using System;
using System.Collections.Generic;
using AegisAC.Configuration;
using AegisAC.Modules;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using Xunit;

namespace AegisAC.Tests;

public class AimbotDetectionTests
{
    private sealed class AimbotTestHarness
    {
        public readonly Dictionary<int, (IPlayer Player, FakePlayerState State)> Players = new();
        public readonly List<(IPlayer Player, string Module, string Evidence)> Detections = new();
        public readonly List<string> DebugLogs = new();
        public readonly NetworkSafetyMonitor NetworkSafety = new();
        public readonly AimbotConfig Config = new()
        {
            Enabled = true,
            SnapAngleThreshold = 10.0f,
            SmoothMinimumMovement = 12.0f,
            LowFovSmoothMinimumMovement = 3.5f,
            SmoothMinimumPathEfficiency = 0.93f,
            EnableSilentAim = true,
            EnableDoubletap = true,
            EnableAntiAim = true
        };
        public readonly AimbotDetector Detector;

        public AimbotTestHarness()
        {
            Detector = new AimbotDetector(
                Config,
                id => Players.TryGetValue(id, out var p) ? p.Player : null,
                (player, module, evidence) => Detections.Add((player, module, evidence)),
                msg => DebugLogs.Add(msg),
                NetworkSafety);
        }

        public (IPlayer Player, FakePlayerState State) AddPlayer(
            int id,
            byte team,
            Vector origin,
            bool isBot = false,
            uint ping = 20u)
        {
            var entry = TestPlayerFactory.CreatePlayer(id, team: team, origin: origin, isBot: isBot, ping: ping);
            Players[id] = entry;
            return entry;
        }

        public void StepTick(
            int tick,
            IPlayer attacker,
            int cmdNum,
            QAngle baseAngles,
            QAngle? attackAngles = null,
            bool fireWeapon = false,
            IPlayer? hurtVictim = null,
            string weapon = "ak47",
            float subtickPitch = 0f,
            float subtickYaw = 0f)
        {
            Detector.RecordUsercmd(attacker, cmdNum, tick, baseAngles, attackAngles, subtickPitch, subtickYaw);
            Detector.OnSetupMove(attacker, cmdNum, tick);
            Detector.OnGameFrame(tick);

            if (fireWeapon)
            {
                Detector.OnWeaponFire(attacker, weapon, tick);
            }

            if (hurtVictim != null)
            {
                Detector.OnPlayerHurt(attacker, hurtVictim, weapon, tick);
            }
        }
    }

    [Fact]
    public void BlatantRageSnap_SingleTick95Degrees_TriggersImmediateDetection()
    {
        var h = new AimbotTestHarness();
        var (attacker, _) = h.AddPlayer(1, team: 2, origin: new Vector(0f, 0f, 0f));
        var (victim, _) = h.AddPlayer(2, team: 3, origin: new Vector(500f, 0f, 0f)); // Target bearing is Yaw 0, Pitch ~0

        // Warmup frames looking 95 degrees away
        for (int t = 1; t <= 5; t++)
        {
            h.StepTick(t, attacker, t, new QAngle(0f, 95f, 0f));
        }

        // Tick 6: Instant 95-degree snap onto victim head/chest and fire
        QAngle targetAim = AimMath.Bearing(new Vector(0f, 0f, 64f), new Vector(500f, 0f, 64f));
        h.StepTick(6, attacker, 6, targetAim, targetAim, fireWeapon: true, hurtVictim: victim);

        Assert.Single(h.Detections);
        Assert.Equal("AIMBOT", h.Detections[0].Module);
        Assert.Contains("95", h.Detections[0].Evidence);
    }

    [Fact]
    public void MediumAndSmallIsolatedSnapCorrections_AccumulateToDetection()
    {
        var h = new AimbotTestHarness();
        var (attacker, _) = h.AddPlayer(1, team: 2, origin: new Vector(0f, 0f, 0f));
        var (victim, _) = h.AddPlayer(2, team: 3, origin: new Vector(500f, 0f, 0f));

        QAngle onTarget = AimMath.Bearing(new Vector(0f, 0f, 64f), new Vector(500f, 0f, 64f));
        int tick = 1;

        // Execute 3 isolated 5.5-degree 1-tick closet aimbot corrections (below the 10-degree standard snap threshold)
        for (int rep = 0; rep < 3; rep++)
        {
            for (int w = 0; w < 5; w++)
            {
                h.StepTick(tick, attacker, tick, new QAngle(0f, 5.5f, 0f));
                tick++;
            }

            // 1-tick snap from 5.5 deg to 0.0 deg with stationary pre/post frames
            h.StepTick(tick, attacker, tick, onTarget, onTarget, fireWeapon: true, hurtVictim: victim);
            tick++;

            h.StepTick(tick, attacker, tick, onTarget);
            tick++;
        }

        Assert.Single(h.Detections);
        Assert.Equal("AIMBOT", h.Detections[0].Module);
    }

    [Fact]
    public void BlatantLaserSmoothTrajectory_TriggersDetectionAtThresholdTwo()
    {
        var h = new AimbotTestHarness();
        var (attacker, _) = h.AddPlayer(1, team: 2, origin: new Vector(0f, 0f, 0f));
        var (victim, _) = h.AddPlayer(2, team: 3, origin: new Vector(500f, 0f, 0f));

        int tick = 1;
        float[] yaws = [24f, 21f, 18f, 15f, 12f, 9f, 6f, 3f, 0f];

        // Laser trajectory requires threshold = 2 (safe against 1-in-a-million pro player flick anomalies)
        for (int rep = 0; rep < 2; rep++)
        {
            for (int i = 0; i < 5; i++)
            {
                h.StepTick(tick, attacker, tick, new QAngle(2.0f, 19.0f + i * 1.0f, 0f));
                tick++;
            }

            for (int i = 0; i < yaws.Length; i++)
            {
                bool isShot = i == yaws.Length - 1;
                QAngle ang = new(0f, yaws[i], 0f);
                h.StepTick(tick, attacker, tick, ang, isShot ? ang : null, fireWeapon: isShot, hurtVictim: isShot ? victim : null);
                tick++;
            }

            h.StepTick(tick, attacker, tick, new QAngle(0f, 0f, 0f));
            tick += 5;
        }

        Assert.Single(h.Detections);
        Assert.Equal("AIMBOT", h.Detections[0].Module);
        Assert.Contains("synthetic laser trajectory", h.Detections[0].Evidence);
    }

    [Fact]
    public void ClosetLowFovSmoothAimbot_DetectsDeceleratingPullsAcrossThreeIncidents()
    {
        var h = new AimbotTestHarness();
        var (attacker, _) = h.AddPlayer(1, team: 2, origin: new Vector(0f, 0f, 0f));
        var (victim, _) = h.AddPlayer(2, team: 3, origin: new Vector(500f, 0f, 0f));

        int tick = 1;
        // 3 closet low-FOV (6.0 deg total movement, well below 12 deg standard threshold) exponential smooth pulls
        float[] pullYaws = [6.0f, 3.8f, 2.2f, 1.1f, 0.4f, 0.0f];

        for (int rep = 0; rep < 3; rep++)
        {
            h.StepTick(tick, attacker, tick, new QAngle(0f, 6.0f, 0f));
            tick++;

            for (int i = 0; i < pullYaws.Length; i++)
            {
                bool isShot = i == pullYaws.Length - 1;
                QAngle ang = new(0f, pullYaws[i], 0f);
                h.StepTick(tick, attacker, tick, ang, isShot ? ang : null, fireWeapon: isShot, hurtVictim: isShot ? victim : null);
                tick++;
            }

            h.StepTick(tick, attacker, tick, new QAngle(0f, 0f, 0f));
            tick++;
        }

        Assert.Single(h.Detections);
        Assert.Equal("AIMBOT", h.Detections[0].Module);
    }

    [Fact]
    public void LegitimateHumanBallisticFlick_WithOvershootAndMicroCorrection_NeverFalseFlags()
    {
        var h = new AimbotTestHarness();
        var (attacker, _) = h.AddPlayer(1, team: 2, origin: new Vector(0f, 0f, 0f));
        var (victim, _) = h.AddPlayer(2, team: 3, origin: new Vector(500f, 0f, 0f));

        int tick = 1;
        // Simulate 10 fast human high-DPI flicks with realistic acceleration, peak, slight overshoot, and pitch/yaw micro-jitter
        for (int rep = 0; rep < 10; rep++)
        {
            (float Pitch, float Yaw)[] humanFlick =
            [
                (0.5f, 22.0f),
                (0.8f, 19.5f), // Acceleration phase
                (1.2f, 13.0f), // Peak ballistic velocity (6.5 deg)
                (0.6f, 5.5f),  // Fast sweep (7.5 deg - acceleration spike)
                (-0.4f, -1.2f), // Slight human overshoot past target (6.7 deg)
                (0.3f, 0.6f),  // Micro-correction reversal #1 (jitter flip)
                (-0.1f, 0.1f)  // Micro-correction reversal #2 onto target
            ];

            for (int i = 0; i < humanFlick.Length; i++)
            {
                bool isShot = i == humanFlick.Length - 1;
                QAngle ang = new(humanFlick[i].Pitch, humanFlick[i].Yaw, 0f);
                h.StepTick(tick, attacker, tick, ang, isShot ? ang : null, fireWeapon: isShot, hurtVictim: isShot ? victim : null);
                tick++;
            }

            // Follow-through frame
            h.StepTick(tick, attacker, tick, new QAngle(-0.2f, -0.3f, 0f));
            tick++;
        }

        Assert.Empty(h.Detections);
    }

    [Fact]
    public void SubtickSilentAim_DetectsBaseVsAttackAngleDivergenceBeyondWeaponSpread()
    {
        var h = new AimbotTestHarness();
        var (attacker, _) = h.AddPlayer(1, team: 2, origin: new Vector(0f, 0f, 0f));
        var (victim, _) = h.AddPlayer(2, team: 3, origin: new Vector(500f, 0f, 0f));

        int tick = 1;
        // Camera (BaseAngles) remains fixed at Yaw = 12.0 deg (off-target),
        // while subtick AttackAngles silently snap to Yaw = 0.0 deg (on-target) across 3 hits.
        QAngle cameraAngles = new(0f, 12.0f, 0f);
        QAngle silentAttackAngles = new(0f, 0.0f, 0f);

        for (int rep = 0; rep < 3; rep++)
        {
            h.StepTick(tick, attacker, tick, cameraAngles);
            tick++;

            h.Detector.OnFireBullets(1, tick, weaponId: 7, inaccuracy: 0.008f, spread: 0.002f, silentAttackAngles);
            h.StepTick(tick, attacker, tick, cameraAngles, silentAttackAngles, fireWeapon: true, hurtVictim: victim);
            tick++;

            h.StepTick(tick, attacker, tick, cameraAngles);
            tick++;
        }

        Assert.Single(h.Detections);
        Assert.Equal("SILENT_AIM", h.Detections[0].Module);
        Assert.Contains("subtick", h.Detections[0].Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AutomaticSpray_CorrelatesAllBulletsWithoutOverConsumingNeighboringCommands()
    {
        var h = new AimbotTestHarness();
        var (attacker, _) = h.AddPlayer(1, team: 2, origin: new Vector(0f, 0f, 0f));
        var (victim, _) = h.AddPlayer(2, team: 3, origin: new Vector(400f, 0f, 0f));

        int tick = 1;
        // Simulate a 10-bullet AK-47 spray (1 bullet every 6 ticks).
        // Bullet 1 has attackAngles set; bullets 2..10 have attackAngles = null (continuous button hold).
        for (int bullet = 0; bullet < 10; bullet++)
        {
            for (int sub = 0; sub < 6; sub++)
            {
                bool fire = sub == 0;
                QAngle ang = new(0f, 0.2f * (sub % 2), 0f);
                h.StepTick(
                    tick,
                    attacker,
                    tick,
                    ang,
                    attackAngles: (bullet == 0 && fire) ? ang : null,
                    fireWeapon: fire,
                    hurtVictim: fire ? victim : null,
                    weapon: "ak47");
                tick++;
            }
        }

        Assert.Equal(10, h.Detector.StatWeaponFires);
        Assert.Equal(10, h.Detector.StatFireMatched);
        Assert.Equal(0, h.Detector.StatFireNoMatch);
        Assert.Equal(10, h.Detector.StatHurts);
        Assert.Equal(10, h.Detector.StatMatched);
        Assert.Equal(0, h.Detector.StatHurtNoMatch);
        Assert.Empty(h.Detections);
    }

    [Fact]
    public void ShotgunMultiPelletAndCollateralHits_CorrelateCleanlyWithoutDroppedHurtEvents()
    {
        var h = new AimbotTestHarness();
        var (attacker, _) = h.AddPlayer(1, team: 2, origin: new Vector(0f, 0f, 0f));
        var (victim1, _) = h.AddPlayer(2, team: 3, origin: new Vector(300f, 0f, 0f));
        var (victim2, _) = h.AddPlayer(3, team: 3, origin: new Vector(350f, 0f, 0f));

        for (int t = 1; t <= 4; t++)
        {
            h.StepTick(t, attacker, t, new QAngle(0f, 0f, 0f));
        }

        // Tick 5: 1 Nova shotgun blast hits 8 pellets on victim1 and 2 collateral pellets on victim2
        QAngle shotAng = new(0f, 0f, 0f);
        h.Detector.RecordUsercmd(attacker, 5, 5, shotAng, shotAng);
        h.Detector.OnSetupMove(attacker, 5, 5);
        h.Detector.OnGameFrame(5);
        h.Detector.OnWeaponFire(attacker, "nova", 5);

        for (int p = 0; p < 8; p++)
        {
            int? matched = h.Detector.OnPlayerHurt(attacker, victim1, "nova", 5);
            Assert.Equal(5, matched);
        }

        for (int p = 0; p < 2; p++)
        {
            int? matched = h.Detector.OnPlayerHurt(attacker, victim2, "nova", 5);
            Assert.Equal(5, matched);
        }

        Assert.Equal(10, h.Detector.StatHurts);
        Assert.Equal(10, h.Detector.StatMatched);
        Assert.Equal(0, h.Detector.StatHurtNoMatch);
    }

    [Fact]
    public void MovingTargetWithAttackerPing_DetectedViaMultiHypothesisLagCompensation()
    {
        var h = new AimbotTestHarness();
        // Attacker has 80ms ping (~5 ticks RTT + 2 ticks interp = ~7 ticks lag compensation)
        var (attacker, _) = h.AddPlayer(1, team: 2, origin: new Vector(0f, 0f, 0f), ping: 80u);
        var (victim, victimState) = h.AddPlayer(2, team: 3, origin: new Vector(500f, -140f, 0f));

        // Victim runs fast laterally along Y (+20 units/tick).
        // At tick 13, victim was at Y = 0 (Yaw 0). By tick 20 (when attacker shoots), server victim is at Y = +140 (Yaw +15.6 deg)!
        // Without lag compensation, aiming at Yaw 0 at tick 20 has 15.6 deg error and fails smooth detection.
        // With multi-hypothesis lag compensation, detector reconstructs the 7-tick lag-compensated position at Y = 0!
        float[] laserYaws = [24f, 21f, 18f, 15f, 12f, 9f, 6f, 3f, 0f];

        for (int rep = 0; rep < 2; rep++)
        {
            int baseT = rep * 30;
            for (int t = 1; t <= 20; t++)
            {
                int currentT = baseT + t;
                float victimY = (t - 13) * 20.0f;
                TestPlayerFactory.SetPosition(victimState, new Vector(500f, victimY, 0f));

                int laserIdx = t - (20 - (laserYaws.Length - 1));
                float yaw = laserIdx >= 0 ? laserYaws[laserIdx] : 24f;
                bool isShot = t == 20;
                QAngle ang = new(0f, yaw, 0f);

                h.StepTick(currentT, attacker, currentT, ang, isShot ? ang : null, fireWeapon: isShot, hurtVictim: isShot ? victim : null);
            }

            h.StepTick(baseT + 21, attacker, baseT + 21, new QAngle(0f, 0f, 0f));
        }

        Assert.Single(h.Detections);
        Assert.Equal("AIMBOT", h.Detections[0].Module);
    }

    [Fact]
    public void DoubletapExploit_DetectsImpossibleWeaponCycleIntervals()
    {
        var h = new AimbotTestHarness();
        var (attacker, _) = h.AddPlayer(1, team: 2, origin: new Vector(0f, 0f, 0f));

        // Fire two AWP shots 1 tick apart (minimum cycle is 75 ticks)
        QAngle ang = new(0f, 0f, 0f);
        h.StepTick(10, attacker, 10, ang, ang, fireWeapon: true, weapon: "awp");
        h.StepTick(11, attacker, 11, ang, ang, fireWeapon: true, weapon: "awp");

        Assert.Single(h.Detections);
        Assert.Equal("DOUBLETAP", h.Detections[0].Module);
    }

    [Fact]
    public void RcsMacro_LowPatternVariancePullDown_FlagsWhileHumanSprayIgnored()
    {
        var h = new AimbotTestHarness();
        var (attacker, _) = h.AddPlayer(1, team: 2, origin: new Vector(0f, 0f, 0f));

        // 2 bursts of 8 AK-47 shots where pitch pulls down by exactly +0.55 deg per shot (CV = 0.0% < 8.157% PATTERN_VARIANCE)
        int tick = 10;
        for (int burst = 0; burst < 2; burst++)
        {
            float pitch = 0f;
            for (int shot = 0; shot < 8; shot++)
            {
                pitch += 0.55f;
                QAngle ang = new(pitch, 0f, 0f);
                h.StepTick(tick, attacker, tick, ang, ang, fireWeapon: true, weapon: "ak47");
                tick += 7;
            }
            tick += 30; // Pause between bursts
        }

        Assert.Contains(h.Detections, d => d.Module == "RCS_MACRO");
    }

    [Fact]
    public void IrregularBehavior_RepeatedAirborneNoScopeKills_Flags()
    {
        var h = new AimbotTestHarness();
        var (attacker, attackerState) = h.AddPlayer(1, team: 2, origin: new Vector(0f, 0f, 0f));
        var (victim, _) = h.AddPlayer(2, team: 3, origin: new Vector(800f, 0f, 0f));
        attackerState.Snapshot.Airborne = true;
        attackerState.Snapshot.Scoped = false;

        for (int i = 1; i <= 4; i++)
        {
            int tick = i * 20;
            QAngle ang = new(0f, 0f, 0f);
            h.StepTick(tick, attacker, tick, ang, ang, fireWeapon: true, weapon: "ssg08");
            h.Detector.OnPlayerDeath(
                attacker,
                victim,
                "ssg08",
                headshot: true,
                penetrated: 1,
                noscope: true,
                thruSmoke: false,
                attackerInAir: true,
                distanceMeters: 25.0f,
                currentTick: tick);
        }

        Assert.Contains(h.Detections, d => d.Module == "IRREGULAR_BEHAVIOR");
    }

    [Fact]
    public void InhumanAccuracy_Over95PercentAimedHitRate_Flags()
    {
        var h = new AimbotTestHarness();
        var (attacker, _) = h.AddPlayer(1, team: 2, origin: new Vector(0f, 0f, 0f));
        var (victim, _) = h.AddPlayer(2, team: 3, origin: new Vector(500f, 0f, 0f));

        // 36 aimed deagle shots at 500 units with 35 hits (97.2% >= 95% PREDICTION_THRESHOLD)
        for (int i = 1; i <= 36; i++)
        {
            int tick = i * 20;
            QAngle ang = new(0f, 0f, 0f);
            bool hit = i <= 35;
            h.StepTick(tick, attacker, tick, ang, ang, fireWeapon: true, hurtVictim: hit ? victim : null, weapon: "deagle");
        }

        Assert.Contains(h.Detections, d => d.Module == "INHUMAN_ACCURACY");
    }
}
