using System;
using System.Collections.Generic;
using AegisAC.Configuration;
using AegisAC.Modules;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using Xunit;

namespace AegisAC.Tests;

public class TriggerbotDetectionTests
{
    private sealed class TriggerbotTestHarness
    {
        public readonly Dictionary<int, (IPlayer Player, FakePlayerState State)> Players = new();
        public readonly List<(IPlayer Player, string Module, string Evidence)> Detections = new();
        public readonly List<string> DebugLogs = new();
        public readonly NetworkSafetyMonitor NetworkSafety = new();
        public readonly TriggerbotConfig Config = new()
        {
            Enabled = true,
            MaxReactionTimeMs = 90.0f,
            EvidenceThreshold = 4,
            EnableNativeTrace = false // Use deterministic analytical 3-zone hitbox intersection in unit tests
        };
        public readonly AimbotDetector FrameSource;
        public readonly TriggerbotDetector Detector;

        public TriggerbotTestHarness()
        {
            var aimConfig = new AimbotConfig { Enabled = true };
            FrameSource = new AimbotDetector(
                aimConfig,
                id => Players.TryGetValue(id, out var p) ? p.Player : null,
                (_, _, _) => { },
                _ => { },
                NetworkSafety);

            Detector = new TriggerbotDetector(
                Config,
                null,
                id => Players.TryGetValue(id, out var p) ? p.Player : null,
                (a, b) => FrameSource.AreEnemies(a, b),
                (player, module, evidence) => Detections.Add((player, module, evidence)),
                msg => DebugLogs.Add(msg),
                tick => FrameSource.FindFrame(tick),
                NetworkSafety);
        }

        public (IPlayer Player, FakePlayerState State) AddPlayer(
            int id,
            byte team,
            Vector origin,
            bool crouched = false)
        {
            var entry = TestPlayerFactory.CreatePlayer(id, team: team, origin: origin);
            TestPlayerFactory.SetPosition(entry.State, origin, crouched: crouched);
            Players[id] = entry;
            return entry;
        }

        public void StepCommand(
            int tick,
            IPlayer attacker,
            int cmdNum,
            QAngle angles,
            bool isFreshAttack,
            bool attackHeld = false,
            IPlayer? hurtVictim = null,
            string weapon = "ak47",
            DateTime? nowOverride = null)
        {
            FrameSource.OnGameFrame(tick);
            Detector.RecordCommand(attacker, cmdNum, tick, angles, isFreshAttack, attackHeld, isBatchBurst: false, nowOverride: nowOverride);
            Detector.OnSetupMove(attacker, cmdNum, tick);

            if (isFreshAttack || attackHeld)
            {
                Detector.OnWeaponFire(attacker, weapon, tick);
            }

            if (hurtVictim != null)
            {
                Detector.OnPlayerHurt(attacker, hurtVictim, weapon, 35, cmdNum);
            }
        }
    }

    [Fact]
    public void TargetDriven_ZeroAndOneTickReactionDelay_DetectsTriggerbot()
    {
        var h = new TriggerbotTestHarness();
        var (attacker, _) = h.AddPlayer(1, team: 2, origin: new Vector(0f, 0f, 0f));
        var (victim, victimState) = h.AddPlayer(2, team: 3, origin: new Vector(500f, 100f, 0f));

        int tick = 1;
        // Execute 4 holds where attacker holds crosshair static at Yaw 0 and enemy crosses into crosshair (Y=0), firing 1 tick later
        for (int rep = 0; rep < 4; rep++)
        {
            // Enemy off-crosshair for 6 ticks while attacker holds completely still
            TestPlayerFactory.SetPosition(victimState, new Vector(500f, 100f, 0f));
            for (int w = 0; w < 6; w++)
            {
                h.StepCommand(tick, attacker, tick, new QAngle(0f, 0f, 0f), isFreshAttack: false);
                tick++;
            }

            // Enemy crosses into crosshair at tick
            TestPlayerFactory.SetPosition(victimState, new Vector(500f, 0f, 0f));
            h.StepCommand(tick, attacker, tick, new QAngle(0f, 0f, 0f), isFreshAttack: false);
            tick++;

            // 1 tick later (15.6ms): triggerbot fires!
            h.StepCommand(tick, attacker, tick, new QAngle(0f, 0f, 0f), isFreshAttack: true, hurtVictim: victim);
            tick++;
        }

        Assert.Single(h.Detections);
        Assert.Equal("TRIGGERBOT", h.Detections[0].Module);
    }

    [Fact]
    public void AimDriven_ExternalTriggerbotWithTwoToFourTickDelayWhileMovingCrosshair_IsDetected()
    {
        var h = new TriggerbotTestHarness();
        var (attacker, _) = h.AddPlayer(1, team: 2, origin: new Vector(0f, 0f, 0f));
        var (victim, _) = h.AddPlayer(2, team: 3, origin: new Vector(500f, 0f, 0f));

        int tick = 1;
        // Previously AegisAC had a complete blind spot for AimDriven external triggerbots with 2..4 ticks delay!
        // Simulate 5 controlled peeking/micro-adjustment sweeps (1.5 deg/tick) onto the enemy, firing 2 ticks (31.2ms) after contact.
        for (int rep = 0; rep < 5; rep++)
        {
            // Sweep toward enemy from Yaw 9.0 down to 3.0 (outside head hitbox radius ~1.03 deg at 500u)
            float[] approachYaws = [9.0f, 7.5f, 6.0f, 4.5f, 3.0f];
            foreach (float y in approachYaws)
            {
                h.StepCommand(tick, attacker, tick, new QAngle(0f, y, 0f), isFreshAttack: false);
                tick++;
            }

            // Contact tick (tick C): crosshair enters enemy head hitbox at Yaw 0.8 deg (movement = 2.2 deg/tick -> AimDriven)
            h.StepCommand(tick, attacker, tick, new QAngle(0f, 0.8f, 0f), isFreshAttack: false);
            tick++;

            // Tick C+1: still on target at Yaw 0.3 deg
            h.StepCommand(tick, attacker, tick, new QAngle(0f, 0.3f, 0f), isFreshAttack: false);
            tick++;

            // Tick C+2 (2 ticks = 31.2ms reaction delay): external triggerbot clicks!
            h.StepCommand(tick, attacker, tick, new QAngle(0f, 0.0f, 0f), isFreshAttack: true, hurtVictim: victim);
            tick++;
        }

        Assert.NotEmpty(h.Detections);
        Assert.Equal("TRIGGERBOT", h.Detections[0].Module);
    }

    [Fact]
    public void FixedDelayExternalTriggerbot_FiveTicksWithZeroVariance_IsDetectedByLowVarianceBonus()
    {
        var h = new TriggerbotTestHarness();
        var (attacker, _) = h.AddPlayer(1, team: 2, origin: new Vector(0f, 0f, 0f));
        var (victim, victimState) = h.AddPlayer(2, team: 3, origin: new Vector(500f, 100f, 0f));

        int tick = 1;
        // Simulate an external triggerbot configured with a constant 5-tick (~78ms) delay across 5 engagements (variance = 0.0 ticks^2)
        for (int rep = 0; rep < 5; rep++)
        {
            TestPlayerFactory.SetPosition(victimState, new Vector(500f, 100f, 0f));
            for (int w = 0; w < 6; w++)
            {
                h.StepCommand(tick, attacker, tick, new QAngle(0f, 0f, 0f), isFreshAttack: false);
                tick++;
            }

            // Enemy enters crosshair and stays for 5 ticks before constant-delay triggerbot fires
            TestPlayerFactory.SetPosition(victimState, new Vector(500f, 0f, 0f));
            for (int hold = 0; hold < 5; hold++)
            {
                h.StepCommand(tick, attacker, tick, new QAngle(0f, 0f, 0f), isFreshAttack: false);
                tick++;
            }

            h.StepCommand(tick, attacker, tick, new QAngle(0f, 0f, 0f), isFreshAttack: true, hurtVictim: victim);
            tick++;
        }

        Assert.Single(h.Detections);
        Assert.Equal("TRIGGERBOT", h.Detections[0].Module);
        Assert.Contains("FIXED-DELAY CADENCE", h.Detections[0].Evidence);
    }

    [Fact]
    public void LegitimateHumanFastReactionsWithHighVarianceAndFlicks_NeverFalseFlag()
    {
        var h = new TriggerbotTestHarness();
        var (attacker, _) = h.AddPlayer(1, team: 2, origin: new Vector(0f, 0f, 0f));
        var (victim, victimState) = h.AddPlayer(2, team: 3, origin: new Vector(500f, 100f, 0f));

        int tick = 1;
        // 1. Human reactions with natural variance: one lucky pre-anticipated 3-tick reaction followed by normal 9, 13, 8, 11, 10 tick reactions
        int[] humanDelays = [3, 9, 13, 8, 11, 10];
        foreach (int delay in humanDelays)
        {
            TestPlayerFactory.SetPosition(victimState, new Vector(500f, 100f, 0f));
            for (int w = 0; w < 6; w++)
            {
                h.StepCommand(tick, attacker, tick, new QAngle(0f, 0f, 0f), isFreshAttack: false);
                tick++;
            }

            TestPlayerFactory.SetPosition(victimState, new Vector(500f, 0f, 0f));
            for (int hold = 0; hold < delay; hold++)
            {
                h.StepCommand(tick, attacker, tick, new QAngle(0f, 0f, 0f), isFreshAttack: false);
                tick++;
            }

            h.StepCommand(tick, attacker, tick, new QAngle(0f, 0f, 0f), isFreshAttack: true, hurtVictim: victim);
            tick++;
        }

        // 2. High-speed ballistic AWP flicks (> 8.5 deg/tick) clicking 1 tick after crossing target
        for (int rep = 0; rep < 5; rep++)
        {
            h.StepCommand(tick, attacker, tick, new QAngle(0f, 25f, 0f), isFreshAttack: false);
            tick++;
            h.StepCommand(tick, attacker, tick, new QAngle(0f, 12f, 0f), isFreshAttack: false);
            tick++;
            // Enters target at 12 deg/tick velocity (> 8.5 deg/tick ballistic threshold)
            h.StepCommand(tick, attacker, tick, new QAngle(0f, 0f, 0f), isFreshAttack: false);
            tick++;
            h.StepCommand(tick, attacker, tick, new QAngle(0f, 0f, 0f), isFreshAttack: true, hurtVictim: victim);
            tick++;
        }

        Assert.Empty(h.Detections);
    }

    [Fact]
    public void SingleDoubleClickAnomaly_DoesNotContaminateAimDrivenTriggerbotEvaluation()
    {
        var h = new TriggerbotTestHarness();
        var (attacker, _) = h.AddPlayer(1, team: 2, origin: new Vector(0f, 0f, 0f));
        var (victim, _) = h.AddPlayer(2, team: 3, origin: new Vector(500f, 0f, 0f));

        DateTime baseTime = DateTime.UtcNow;
        int tick = 1;

        // 1 accidental mouse switch double-click (2 commands apart) while looking away
        h.StepCommand(tick, attacker, tick, new QAngle(0f, 45f, 0f), isFreshAttack: true, nowOverride: baseTime);
        tick++;
        h.StepCommand(tick, attacker, tick, new QAngle(0f, 45f, 0f), isFreshAttack: true, nowOverride: baseTime.AddMilliseconds(15));
        tick++;

        // Wait 5 ticks, then perform 4 normal AimDriven shots with 5-tick (78ms) human reaction delay.
        // Previously, SyntheticInputAnomalies >= 1 permanently poisoned all future AimDriven hits!
        for (int rep = 0; rep < 4; rep++)
        {
            for (int w = 0; w < 5; w++)
            {
                h.StepCommand(tick, attacker, tick, new QAngle(0f, 10f, 0f), isFreshAttack: false, nowOverride: baseTime.AddSeconds(rep + 1));
                tick++;
            }

            // Controlled move onto target and hold for 5 ticks (above AimDrivenThresholdTicks = 4)
            h.StepCommand(tick, attacker, tick, new QAngle(0f, 8f, 0f), isFreshAttack: false, nowOverride: baseTime.AddSeconds(rep + 1));
            tick++;
            for (int hold = 0; hold < 5; hold++)
            {
                h.StepCommand(tick, attacker, tick, new QAngle(0f, 0f, 0f), isFreshAttack: false, nowOverride: baseTime.AddSeconds(rep + 1));
                tick++;
            }

            h.StepCommand(tick, attacker, tick, new QAngle(0f, 0f, 0f), isFreshAttack: true, hurtVictim: victim, nowOverride: baseTime.AddSeconds(rep + 1));
            tick++;
        }

        Assert.Empty(h.Detections);
    }

    [Fact]
    public void PreFireContinuousSprayIntoDoorway_IsIgnoredWhenEnemyWalksIntoCrosshair()
    {
        var h = new TriggerbotTestHarness();
        var (attacker, _) = h.AddPlayer(1, team: 2, origin: new Vector(0f, 0f, 0f));
        var (victim, victimState) = h.AddPlayer(2, team: 3, origin: new Vector(500f, 100f, 0f));

        int tick = 1;
        // Attacker starts spraying (attackHeld = true) BEFORE enemy walks into doorway
        for (int rep = 0; rep < 5; rep++)
        {
            TestPlayerFactory.SetPosition(victimState, new Vector(500f, 100f, 0f));
            for (int s = 0; s < 5; s++)
            {
                h.StepCommand(tick, attacker, tick, new QAngle(0f, 0f, 0f), isFreshAttack: s == 0, attackHeld: true);
                tick++;
            }

            // Enemy walks into active spray at tick and gets hurt 1 tick later
            TestPlayerFactory.SetPosition(victimState, new Vector(500f, 0f, 0f));
            h.StepCommand(tick, attacker, tick, new QAngle(0f, 0f, 0f), isFreshAttack: false, attackHeld: true);
            tick++;
            h.StepCommand(tick, attacker, tick, new QAngle(0f, 0f, 0f), isFreshAttack: false, attackHeld: true, hurtVictim: victim);
            tick++;
        }

        Assert.Empty(h.Detections);
    }

    [Fact]
    public void CrouchedTargetHitbox_ExcludesStandingHeadHeightAbove54Units()
    {
        Vector rayStart = new(0f, 0f, 64f);
        Vector rayDir = new(1f, 0f, 0f); // Straight horizontal ray at Z = 64
        Vector targetOrigin = new(400f, 0f, 0f);

        // Standing target (top Z = 72) is hit by horizontal ray at Z = 64
        bool hitsStanding = TriggerbotDetector.IntersectsAnatomicalHitbox(rayStart, rayDir, targetOrigin, crouched: false, 4096f, out float standDist, out _);
        Assert.True(hitsStanding);
        Assert.True(standDist >= 380f && standDist <= 400.1f);

        // Crouched target (top Z = 54) is NOT hit by horizontal ray at Z = 64
        bool hitsCrouched = TriggerbotDetector.IntersectsAnatomicalHitbox(rayStart, rayDir, targetOrigin, crouched: true, 4096f, out _, out _);
        Assert.False(hitsCrouched);
    }
}
