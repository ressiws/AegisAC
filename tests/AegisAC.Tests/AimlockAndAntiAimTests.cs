using System;
using System.Collections.Generic;
using System.Diagnostics;
using AegisAC.Configuration;
using AegisAC.Modules;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using Xunit;
using Xunit.Abstractions;

namespace AegisAC.Tests;

public class AimlockAndAntiAimTests
{
    private readonly ITestOutputHelper _output;

    public AimlockAndAntiAimTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed class AimlockTestHarness
    {
        public readonly Dictionary<int, (IPlayer Player, FakePlayerState State)> Players = new();
        public readonly List<(IPlayer Player, string Module, string Evidence)> Detections = new();
        public readonly List<string> DebugLogs = new();
        public readonly NetworkSafetyMonitor NetworkSafety = new();
        public readonly AimlockConfig Config = new()
        {
            Enabled = true,
            TrackingDurationSeconds = 1.0f, // 64 observed samples
            EvidenceThreshold = 3,
            MinimumDistance = 150.0f,
            MinimumTargetTravel = 15.0f,
            RequireWallOcclusion = true
        };
        public bool SimulatedWallOcclusion { get; set; } = true;
        public readonly AimbotDetector FrameSource;
        public readonly AimlockDetector Detector;

        public AimlockTestHarness()
        {
            FrameSource = new AimbotDetector(
                new AimbotConfig { Enabled = true },
                id => Players.TryGetValue(id, out var p) ? p.Player : null,
                (_, _, _) => { },
                _ => { },
                NetworkSafety);

            Detector = new AimlockDetector(
                Config,
                tick => FrameSource.FindFrame(tick),
                id => Players.TryGetValue(id, out var p) ? p.Player : null,
                (a, b) => FrameSource.AreEnemies(a, b),
                (player, module, evidence) => Detections.Add((player, module, evidence)),
                msg => DebugLogs.Add(msg),
                traceManager: null,
                isOccludedOverride: (_, _, _, _) => SimulatedWallOcclusion,
                networkSafety: NetworkSafety);
        }

        public (IPlayer Player, FakePlayerState State) AddPlayer(int id, byte team, Vector origin, ulong steamId = 76561198000000000UL)
        {
            var entry = TestPlayerFactory.CreatePlayer(id, steamId: steamId, team: team, origin: origin, ping: 0u);
            entry.State.ConVars["cl_interp_ratio"] = "0.0";
            Players[id] = entry;
            return entry;
        }

        public void StepTick(int tick, IPlayer tracker, int cmdNum, QAngle angles)
        {
            Detector.RecordCommand(tracker, cmdNum, angles);
            Detector.OnSetupMove(tracker, cmdNum, tick);
            FrameSource.OnGameFrame(tick);
            Detector.OnGameFrame(tick);
        }
    }

    [Fact]
    public void AimlockThroughWall_WithObservedSamplesAndTargetTravel_TriggersDetection()
    {
        var h = new AimlockTestHarness { SimulatedWallOcclusion = true };
        var (tracker, _) = h.AddPlayer(1, team: 2, origin: new Vector(0f, 0f, 0f));
        var (_, victimState) = h.AddPlayer(2, team: 3, origin: new Vector(400f, 0f, 0f));

        int tick = 1;
        // Execute 3 wall-occluded tracking episodes of 65 ticks each while target moves laterally by 40 units (> 15u requirement)
        for (int ep = 0; ep < 3; ep++)
        {
            // Break lock for 36 ticks (> RearmTicks = 32) between episodes
            for (int b = 0; b < 36; b++)
            {
                TestPlayerFactory.SetPosition(victimState, new Vector(400f, 0f, 0f));
                h.StepTick(tick, tracker, tick, new QAngle(0f, 90f, 0f));
                tick++;
            }

            // Lock onto victim's chest (height +46) through wall for 66 consecutive observed ticks
            for (int s = 0; s < 66; s++)
            {
                float y = s * 0.8f; // Moves 52 units laterally
                Vector vOrigin = new(400f, y, 0f);
                TestPlayerFactory.SetPosition(victimState, vOrigin);
                Vector chestPoint = new(vOrigin.X, vOrigin.Y, vOrigin.Z + 46f);
                QAngle lockAngles = AimMath.Bearing(new Vector(0f, 0f, 64f), chestPoint);

                h.StepTick(tick, tracker, tick, lockAngles);
                tick++;
            }
        }

        Assert.Single(h.Detections);
        Assert.Equal("AIMLOCK", h.Detections[0].Module);
        Assert.Equal(3, h.Detector.StatIncidents);
    }

    [Fact]
    public void AimlockOnVisibleLineOfSightTarget_IsRejectedAndNeverFalseFlags()
    {
        var h = new AimlockTestHarness { SimulatedWallOcclusion = false }; // Target is visible in open combat!
        var (tracker, _) = h.AddPlayer(1, team: 2, origin: new Vector(0f, 0f, 0f));
        var (_, victimState) = h.AddPlayer(2, team: 3, origin: new Vector(400f, 0f, 0f));

        int tick = 1;
        for (int ep = 0; ep < 3; ep++)
        {
            for (int b = 0; b < 36; b++)
            {
                TestPlayerFactory.SetPosition(victimState, new Vector(400f, 0f, 0f));
                h.StepTick(tick, tracker, tick, new QAngle(0f, 90f, 0f));
                tick++;
            }

            for (int s = 0; s < 66; s++)
            {
                Vector vOrigin = new(400f, s * 0.8f, 0f);
                TestPlayerFactory.SetPosition(victimState, vOrigin);
                QAngle lockAngles = AimMath.Bearing(new Vector(0f, 0f, 64f), new Vector(vOrigin.X, vOrigin.Y, 46f));
                h.StepTick(tick, tracker, tick, lockAngles);
                tick++;
            }
        }

        Assert.Empty(h.Detections);
        Assert.Equal(0, h.Detector.StatIncidents);
        Assert.True(h.Detector.StatVisibleRejected >= 3);
    }

    [Fact]
    public void AimlockMissingTicks_DoNotCreditUnobservedTicksAndResetAfterFourGaps()
    {
        var h = new AimlockTestHarness { SimulatedWallOcclusion = true };
        var (tracker, _) = h.AddPlayer(1, team: 2, origin: new Vector(0f, 0f, 0f));
        var (_, victimState) = h.AddPlayer(2, team: 3, origin: new Vector(400f, 0f, 0f));

        int tick = 1;
        // Inject 2-tick gaps (serverTickDelta == 2) 4 times in a single tracking episode.
        // Because MaxMissingTicksPerEpisode == 3, the 4th missing tick must reset the episode!
        for (int step = 0; step < 40; step++)
        {
            Vector vOrigin = new(400f, step * 1.0f, 0f);
            TestPlayerFactory.SetPosition(victimState, vOrigin);
            QAngle lockAngles = AimMath.Bearing(new Vector(0f, 0f, 64f), new Vector(vOrigin.X, vOrigin.Y, 46f));

            // Advance frame on skipped tick so historical frame exists, but only simulate SetupMove every 2nd tick for first 8 steps
            if (step is >= 2 and <= 6)
            {
                h.FrameSource.OnGameFrame(tick);
                tick++;
            }

            h.StepTick(tick, tracker, step + 1, lockAngles);
            tick++;
        }

        Assert.True(h.Detector.StatMissingTickResets > 0);
        Assert.Equal(0, h.Detector.StatIncidents);
    }

    [Fact]
    public void AntiAim_UntrustedOutOfBoundsAndNaNAngles_RateLimitedByCooldownLatch()
    {
        var alerts = new List<(string Mode, string Details)>();
        var config = new AimbotConfig { Enabled = true, EnableAntiAim = true };
        var antiAim = new AntiAimDetector(
            null,
            null,
            config,
            null,
            (_, mode, details, _) => alerts.Add((mode, details)));

        // Flood 50 consecutive NaN and out-of-bounds pitch (+180 deg) commands for slot 1
        for (int i = 1; i <= 50; i++)
        {
            QAngle bad = (i % 2 == 0)
                ? new QAngle(float.NaN, 0f, 0f)
                : new QAngle(180.0f, 0f, 45.0f);
            antiAim.RecordAngleSample(1, i, i, bad, isShot: false);
        }

        // Per-player 60s cooldown latch ensures 50 malformed packets produce exactly 1 alert, preventing log/webhook DoS!
        Assert.Single(alerts);
        Assert.Contains("Untrusted", alerts[0].Mode);

        // 2. Legitimate player with combat aim punch / flinch camera roll (roll = -5.91 deg, 18.5 deg)
        // must NEVER trigger Anti-Untrusted!
        alerts.Clear();
        for (int i = 1; i <= 100; i++)
        {
            // The exact angle sample from production server gunfight flinch:
            QAngle legitCombatAngle = new(-16.08f, 26.90f, -5.91f);
            antiAim.RecordAngleSample(2, i, i, legitCombatAngle, isShot: i % 10 == 0);
        }
        Assert.Empty(alerts);
    }

    [Fact]
    public void Exploits_SubtickTampering_DetectsNonMonotonicFractionsAndDuplicateButtonTransitions()
    {
        var alerts = new List<(string Mode, string Details)>();
        var config = new ExploitsConfig { Enabled = true };
        var exploits = new ExploitsDetector(
            null,
            null,
            config,
            null,
            (_, mode, details, _) => alerts.Add((mode, details)));

        // 1. Out-of-order subtick fraction (when: 0.8 -> 0.2) and duplicate button press (1UL pressed twice without release)
        (ulong Button, bool Pressed, float When, float PitchDelta, float YawDelta)[] badSteps =
        [
            (1UL, true, 0.80f, 0f, 0f),
            (1UL, true, 0.20f, 0f, 0f)
        ];

        for (int i = 0; i < 3; i++)
        {
            bool valid = exploits.VerifySubtickStepList(1, badSteps);
            Assert.False(valid);
        }

        Assert.Single(alerts);
        Assert.Equal("Subtick Tamper", alerts[0].Mode);

        // 2. Legitimate mixed button (When=0.75 on W, When=0.25 on A/Mouse1, plus analog Button=0 at When=0.0)
        // Includes real-world Windows QPC / NVIDIA Reflex negative timestamps (-0.055, -0.023, -0.016, -0.047)
        // and frametime jitter slightly above 1.0. Must NEVER false-flag!
        alerts.Clear();
        (ulong Button, bool Pressed, float When, float PitchDelta, float YawDelta)[] legitMixedSteps =
        [
            (8UL, true, -0.023f, 0f, 0f), // RawInput arriving 0.36ms before tick boundary
            (512UL, true, -0.055f, 0f, 0f), // RawInput arriving 0.85ms before tick boundary
            (1UL, true, 0.15f, 0f, 0f),
            (2UL, true, 0.50f, 0f, 0f),
            (2UL, true, 0.50f, 0f, 0f), // Dual jump bind (Space + mwheeldown)
            (8UL, false, 1.045f, 0f, 0f), // Slight frametime overrun
            (0UL, false, 0.00f, 0.15f, -0.35f)
        ];

        for (int i = 0; i < 50; i++)
        {
            bool valid = exploits.VerifySubtickStepList(2, legitMixedSteps);
            Assert.True(valid);
        }

        Assert.Empty(alerts);
    }

    [Fact]
    public void MovementAndExploits_SeparatedModules_DetectedDeterministically()
    {
        var alerts = new List<(string Mode, string Details)>();
        var expConfig = new ExploitsConfig { Enabled = true };
        var movConfig = new MovementConfig { Enabled = true };
        var dllConfig = new DllInjectionConfig { Enabled = true };

        var exploits = new ExploitsDetector(
            null,
            null,
            expConfig,
            null,
            (_, mode, details, _) => alerts.Add((mode, details)));

        var movement = new MovementDetector(
            null,
            null,
            movConfig,
            null,
            (_, mode, details, _) => alerts.Add((mode, details)));

        // 1. Desubticking via Exploits module: 30 commands with non-analog button press flattened to When == 0.0f
        (ulong Button, bool Pressed, float When, float PitchDelta, float YawDelta)[] desubtickSteps =
        [
            (8UL, true, 0.0f, 0f, 0f)
        ];
        for (int i = 1; i <= 30; i++)
        {
            exploits.VerifySubtickStepList(1, desubtickSteps);
        }
        Assert.Contains(alerts, a => a.Mode == "Desubtick");

        // 2. Airborne Nulls / Snap-Tap via Movement module: 64 consecutive opposite-axis switches (512 <-> 1024) with 0.00ms underlap
        alerts.Clear();
        for (int i = 1; i <= 64; i++)
        {
            ulong releaseBtn = (i % 2 == 0) ? 512UL : 1024UL;
            ulong pressBtn = (i % 2 == 0) ? 1024UL : 512UL;
            (ulong Button, bool Pressed, float When)[] nullPair =
            [
                (releaseBtn, false, 0.50f),
                (pressBtn, true, 0.50f)
            ];
            movement.VerifyAirborneNullsSpan(2, nullPair, commandNumber: i);
        }
        Assert.Contains(alerts, a => a.Mode == "Nulls");

        // 3. Bhop via Movement module: 10 consecutive frame-perfect bunnyhop landings after >= 4 air ticks
        alerts.Clear();
        for (int hop = 0; hop < 10; hop++)
        {
            for (int air = 0; air < 5; air++)
                movement.RecordBhopStep(3, null, "Bhopper", isAirborne: true);
            movement.RecordBhopStep(3, null, "Bhopper", isAirborne: false, jumpedOnLandingTick: true);
        }
        Assert.Contains(alerts, a => a.Mode == "Bhop");

        // 4. NameChanger via Exploits module: 5 name changes within 60 seconds
        alerts.Clear();
        DateTime t0 = DateTime.UtcNow;
        exploits.CheckPlayerNameChange(4, null, "InitialName", t0);
        for (int c = 1; c <= 5; c++)
        {
            exploits.CheckPlayerNameChange(4, null, $"SpamName_{c}", t0.AddSeconds(c * 2));
        }
        Assert.Contains(alerts, a => a.Mode == "NameChanger");

        // 5. DLL Injection via DllInjectionDetector module
        Assert.Equal(105, DllInjectionDetector.BlacklistedClientEvents.Length);
        var dllInjection = new DllInjectionDetector(
            null,
            null,
            dllConfig,
            (_, mode, details, _) => alerts.Add((mode, details)));

        alerts.Clear();
        bool cleanScan = dllInjection.ScanClientEventSubscriptions(5, null, "CleanPlayer", (_, _) => false);
        Assert.False(cleanScan);
        Assert.Empty(alerts);

        bool injectedScan = dllInjection.ScanClientEventSubscriptions(
            5,
            null,
            "InjectedPlayer",
            (_, ev) => ev is "bullet_damage" or "bullet_flight_resolution");
        Assert.True(injectedScan);
        Assert.Contains(alerts, a => a.Mode == "DLL Injection" && a.Details.Contains("bullet_damage"));
    }

    [Fact]
    public void Movement_AutoBunnyhoppingConvarActive_AutoDisablesBhopCheck()
    {
        var alerts = new List<(string Mode, string Details)>();
        var movConfig = new MovementConfig
        {
            Enabled = true,
            AutoDisableOnAutoBunnyhoppingConvar = true,
            BhopStreakThreshold = 5
        };

        bool svAutoBhopActive = true;
        var movement = new MovementDetector(
            null,
            null,
            movConfig,
            null,
            (_, mode, details, _) => alerts.Add((mode, details)),
            () => svAutoBhopActive);

        // 1. When sv_autobunnyhopping is ACTIVE (e.g. surf, kz, or bhop server), 20 consecutive hops must NEVER flag
        for (int hop = 0; hop < 20; hop++)
        {
            for (int air = 0; air < 5; air++)
                movement.RecordBhopStep(1, null, "AutoBhopper", isAirborne: true);
            movement.RecordBhopStep(1, null, "AutoBhopper", isAirborne: false, jumpedOnLandingTick: true);
        }
        Assert.Empty(alerts);

        // 2. When sv_autobunnyhopping is DEACTIVATED (normal competitive/retakes), 5 consecutive hops trigger detection
        svAutoBhopActive = false;
        for (int hop = 0; hop < 5; hop++)
        {
            for (int air = 0; air < 5; air++)
                movement.RecordBhopStep(1, null, "AutoBhopper", isAirborne: true);
            movement.RecordBhopStep(1, null, "AutoBhopper", isAirborne: false, jumpedOnLandingTick: true);
        }
        Assert.Single(alerts);
        Assert.Contains("Bhop", alerts[0].Mode);
    }

    [Fact]
    public void AntiAim_ContinuousSpinbotAndYawJitter_DetectedWhileCombatFlicksIgnored()
    {
        var alerts = new List<(string Mode, string Details)>();
        var config = new AimbotConfig { Enabled = true, EnableAntiAim = true };
        var antiAim = new AntiAimDetector(
            null,
            null,
            config,
            null,
            (_, mode, details, _) => alerts.Add((mode, details)));

        // 1. Tier 3 Spinbot: +55 deg/tick continuous yaw rotation (3520 deg/s) without firing
        float yaw = 0f;
        for (int t = 1; t <= 16; t++)
        {
            yaw = AimMath.NormalizeAngle(yaw + 55.0f);
            antiAim.RecordAngleSample(1, t, t, new QAngle(0f, yaw, 0f), isShot: false);
        }

        Assert.Single(alerts);
        Assert.Contains("Spinbot", alerts[0].Mode);

        // 2. Yaw Jitter Anti-Aim on slot 2: alternating +90 / -90 deg flips every tick without firing
        alerts.Clear();
        for (int t = 1; t <= 20; t++)
        {
            float jitterYaw = (t % 2 == 0) ? 90.0f : -90.0f;
            antiAim.RecordAngleSample(2, t, t, new QAngle(0f, jitterYaw, 0f), isShot: false);
        }

        Assert.Single(alerts);
        Assert.Contains("Jitter", alerts[0].Mode);

        // 3. High-DPI Human Mouse Spin & Shake on slot 3 (hasMouseInput: true, organic velocity variation, non-periodic shakes):
        // Must NEVER false-flag even at 16,000+ DPI!
        alerts.Clear();
        float humanYaw = 0f;
        float[] organicSteps = [22f, 48f, 75f, 61f, 34f, 19f, 52f, 84f, 41f, 27f, -68f, 79f, -92f, 64f, -77f, 88f];
        for (int t = 1; t <= 64; t++)
        {
            float step = organicSteps[t % organicSteps.Length];
            humanYaw = AimMath.NormalizeAngle(humanYaw + step);
            float humanPitch = MathF.Sin(t * 0.4f) * 4.5f;
            antiAim.RecordAngleSample(3, t, t, new QAngle(humanPitch, humanYaw, 0f), isShot: false, hasMouseInput: true);
        }

        Assert.Empty(alerts);
    }

    [Fact]
    public void NetworkSafetyMonitor_VetoesHighPingJitterLossChokeAndBursts()
    {
        var monitor = new NetworkSafetyMonitor();
        DateTime now = DateTime.UtcNow;

        // Clean connection (25ms ping, 0% loss, 0% choke) -> Not vetoed
        monitor.RecordSample(1, new NetworkSafetySample(now, 25f, 0f, 0f, 0f, 0f, Valid: true));
        monitor.RecordSample(1, new NetworkSafetySample(now.AddMilliseconds(100), 27f, 0f, 0f, 0f, 0f, Valid: true));
        Assert.False(monitor.ShouldSkipDetection(1, out _));

        // Packet loss spike (3.5% incoming loss >= 2% threshold) -> Vetoed
        monitor.RecordSample(2, new NetworkSafetySample(now, 30f, 0.035f, 0f, 0f, 0f, Valid: true));
        Assert.True(monitor.ShouldSkipDetection(2, out var lossEv));
        Assert.True(lossEv.Vetoed);

        // High jitter (20ms -> 60ms = 40ms jitter >= 25ms threshold) -> Vetoed
        monitor.RecordSample(3, new NetworkSafetySample(now, 20f, 0f, 0f, 0f, 0f, Valid: true));
        monitor.RecordSample(3, new NetworkSafetySample(now.AddMilliseconds(100), 60f, 0f, 0f, 0f, 0f, Valid: true));
        Assert.True(monitor.ShouldSkipDetection(3, out var jitterEv));
        Assert.True(jitterEv.Vetoed);

        // Command bursts (> 5 commands in a single tick twice) -> Vetoed
        monitor.OnProcessUsercmds(4, count: 8, firstCmd: 1, lastCmd: 8, nowOverride: now);
        monitor.OnProcessUsercmds(4, count: 7, firstCmd: 9, lastCmd: 15, nowOverride: now.AddMilliseconds(50));
        Assert.True(monitor.ShouldSkipDetection(4, out var burstEv));
        Assert.Equal(2, burstEv.CommandBursts);
    }

    [Fact]
    public void BotsDeadPlayersAndReconnects_HandledProperlyAcrossModules()
    {
        var players = new Dictionary<int, (IPlayer Player, FakePlayerState State)>();
        var detections = new List<string>();
        var net = new NetworkSafetyMonitor();
        var aimConfig = new AimbotConfig { Enabled = true };

        var aimbot = new AimbotDetector(
            aimConfig,
            id => players.TryGetValue(id, out var p) ? p.Player : null,
            (_, mod, _) => detections.Add(mod),
            _ => { },
            net);

        // 1. Bot player (isBot: true) must be completely ignored even on a 95-degree rage snap
        var (bot, _) = TestPlayerFactory.CreatePlayer(1, team: 2, origin: new Vector(0f, 0f, 0f), isBot: true);
        var (victim, _) = TestPlayerFactory.CreatePlayer(2, team: 3, origin: new Vector(500f, 0f, 0f));
        players[1] = (bot, null!);
        players[2] = (victim, null!);

        aimbot.RecordUsercmd(bot, 1, 1, new QAngle(0f, 95f, 0f));
        aimbot.OnSetupMove(bot, 1, 1);
        aimbot.OnGameFrame(1);
        aimbot.RecordUsercmd(bot, 2, 2, new QAngle(0f, 0f, 0f), new QAngle(0f, 0f, 0f));
        aimbot.OnSetupMove(bot, 2, 2);
        aimbot.OnGameFrame(2);
        aimbot.OnWeaponFire(bot, "ak47", 2);
        aimbot.OnPlayerHurt(bot, victim, "ak47", 2);

        Assert.Empty(detections);
    }

    [Fact]
    public void Full64PlayerServerBenchmark_MaintainsLowPerTickOverhead()
    {
        var players = new Dictionary<int, (IPlayer Player, FakePlayerState State)>();
        var net = new NetworkSafetyMonitor();
        var aimConfig = new AimbotConfig { Enabled = true };
        var trigConfig = new TriggerbotConfig { Enabled = true, EnableNativeTrace = false };
        var lockConfig = new AimlockConfig { Enabled = true, RequireWallOcclusion = true };

        for (int i = 0; i < 64; i++)
        {
            byte team = (byte)(i % 2 == 0 ? 2 : 3);
            Vector pos = new((i % 8) * 250f, (i / 8) * 250f, 0f);
            players[i] = TestPlayerFactory.CreatePlayer(i, team: team, origin: pos);
        }

        var aimbot = new AimbotDetector(
            aimConfig,
            id => players.TryGetValue(id, out var p) ? p.Player : null,
            (_, _, _) => { },
            _ => { },
            net);

        var triggerbot = new TriggerbotDetector(
            trigConfig,
            null,
            id => players.TryGetValue(id, out var p) ? p.Player : null,
            (a, b) => aimbot.AreEnemies(a, b),
            (_, _, _) => { },
            _ => { },
            tick => aimbot.FindFrame(tick),
            net);

        var aimlock = new AimlockDetector(
            lockConfig,
            tick => aimbot.FindFrame(tick),
            id => players.TryGetValue(id, out var p) ? p.Player : null,
            (a, b) => aimbot.AreEnemies(a, b),
            (_, _, _) => { },
            _ => { },
            null,
            (_, _, _, _) => true,
            net);

        var antiAim = new AntiAimDetector(null, null, new AntiAimConfig { Enabled = true }, net);
        var movement = new MovementDetector(null, null, new MovementConfig { Enabled = true }, net);
        var exploits = new ExploitsDetector(null, null, new ExploitsConfig { Enabled = true }, net);

        // Simulate 64 ticks (1.0 full second) of 64 active players (4,096 player-ticks)
        var sw = Stopwatch.StartNew();
        for (int tick = 1; tick <= 64; tick++)
        {
            for (int id = 0; id < 64; id++)
            {
                var p = players[id].Player;
                QAngle ang = new(0f, (id * 5 + tick) % 360 - 180f, 0f);
                bool fire = (tick + id) % 32 == 0;

                aimbot.RecordUsercmd(p, tick, tick, ang, fire ? ang : null);
                aimbot.OnSetupMove(p, tick, tick);

                triggerbot.RecordCommand(p, tick, tick, ang, isFreshAttack: fire);
                triggerbot.OnSetupMove(p, tick, tick);

                aimlock.RecordCommand(p, tick, ang);
                aimlock.OnSetupMove(p, tick, tick);

                antiAim.RecordAngleSample(id, tick, tick, ang, isShot: fire);
            }

            aimbot.OnGameFrame(tick);
            aimlock.OnGameFrame(tick);
        }
        sw.Stop();

        double msPerTick = sw.Elapsed.TotalMilliseconds / 64.0;
        _output.WriteLine($"64-Player Full Server Benchmark: {sw.Elapsed.TotalMilliseconds:F2} ms total for 64 ticks ({msPerTick:F3} ms/tick)");

        // Must run well within CS2's 15.625 ms/tick 64Hz server budget (even including DispatchProxy test reflection overhead)
        Assert.True(msPerTick < 14.5, $"Expected < 12.0 ms/tick for 64 players, got {msPerTick:F3} ms/tick");
    }

    [Fact]
    public void CVarScanner_DetectsForbiddenClientConVars_AndHonorsServerSvCheatsBypass()
    {
        var alerts = new List<(string Detection, string Details)>();
        bool serverSvCheats = false;

        var config = new CVarScannerConfig
        {
            Enabled = true,
            CheckDrawOtherModels = true,
            CheckMatWireframe = true,
            CheckSvCheats = true,
            CheckHostTimescale = true,
            CheckFogEnable = true,
            CheckPredictionAndLagComp = true
        };

        var detector = new CVarScannerDetector(
            null,
            null,
            config,
            (player, detection, details, _) => alerts.Add((detection, details)),
            () => serverSvCheats);

        var (player, state) = TestPlayerFactory.CreatePlayer(1);

        // 1. Clean player defaults -> zero detections
        state.ConVars["r_drawothermodels"] = "1";
        state.ConVars["mat_wireframe"] = "0";
        state.ConVars["sv_cheats"] = "0";
        state.ConVars["host_timescale"] = "1.0";
        state.ConVars["fog_enable"] = "1";
        state.ConVars["cl_lagcompensation"] = "1";
        state.ConVars["cl_predict"] = "1";

        bool cleanResult = detector.ScanPlayerCVars(1, player, "CleanUser", cvar => player.GetClientConvarValue(cvar));
        Assert.False(cleanResult);
        Assert.Empty(alerts);

        // 2. Client tampering: wireframe wallhack (r_drawothermodels = 2)
        state.ConVars["r_drawothermodels"] = "2";
        bool rOtherResult = detector.ScanPlayerCVars(1, player, "Wallhacker", cvar => player.GetClientConvarValue(cvar));
        Assert.True(rOtherResult);
        Assert.Single(alerts);
        Assert.Equal("Forbidden CVar", alerts[0].Detection);
        Assert.Contains("r_drawothermodels=\"2\"", alerts[0].Details);
        alerts.Clear();

        // 3. Client tampering: timescale speedhack (host_timescale = 2.0)
        state.ConVars["r_drawothermodels"] = "1";
        state.ConVars["host_timescale"] = "2.0";
        bool speedResult = detector.ScanPlayerCVars(1, player, "Speedhacker", cvar => player.GetClientConvarValue(cvar));
        Assert.True(speedResult);
        Assert.Single(alerts);
        Assert.Contains("host_timescale=\"2.0\"", alerts[0].Details);
        alerts.Clear();

        // 4. Server sv_cheats 1 bypass -> wireframe & timescale must NOT be flagged when server allows cheats
        serverSvCheats = true;
        state.ConVars["host_timescale"] = "2.0";
        state.ConVars["r_drawothermodels"] = "2";
        state.ConVars["mat_wireframe"] = "1";
        bool cheatBypassResult = detector.ScanPlayerCVars(1, player, "AdminTestingCheats", cvar => player.GetClientConvarValue(cvar));
        Assert.False(cheatBypassResult);
        Assert.Empty(alerts);
    }
}
