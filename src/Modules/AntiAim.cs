using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.NetMessages;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.ProtobufDefinitions;
using AegisAC.Configuration;

namespace AegisAC.Modules;

/// <summary>
/// Server-side Anti-Aim, Spinbot, and UserCmd / Subtick Integrity detector.
/// Reimplemented for SwiftlyS2 based on CS2AC Legacy's antiaim_module and command verifier:
/// - Deterministic ANTI_UNTRUSTED angle &amp; NaN/Inf validation with a 60-second per-player alert latch
/// - Subtick move step integrity (When in [0, 1], monotonic ordering, duplicate button state transitions)
/// - Multi-tier continuous Spinbot detection (720 deg/s, 1440 deg/s, 2880 deg/s with directional consistency)
/// - Rapid alternating Yaw Jitter anti-aim detection
/// </summary>
internal sealed class AntiAimDetector
{
    public const int MaxPlayers = 64;
    private const int MotionHistorySize = 32;
    private const float TickIntervalSeconds = 1.0f / 64.0f;

    internal struct AngleSample
    {
        public int ServerTick;
        public int CommandNumber;
        public QAngle Angles;
        public bool IsShot;
        public bool HasMouseInput;
    }

    internal sealed class PlayerState
    {
        public readonly AngleSample[] Samples = new AngleSample[MotionHistorySize];
        public int SampleHead;
        public int SampleCount;
        public int LastCommandNumber = -1;
        public int LastServerTick = -1;

        public float SpinScore;
        public float SustainedSpinSeconds;
        public float SubtickTamperScore;
        public DateTime LastSubtickIncidentTime = DateTime.MinValue;
        public int UntrustedDetections;
        public int SpinDetections;
        public int SubtickDetections;

        public DateTime LastUntrustedAlertTime = DateTime.MinValue;
        public DateTime LastSpinAlertTime = DateTime.MinValue;
        public DateTime LastSubtickAlertTime = DateTime.MinValue;

        // Subtick InvalidInput & Desubtick tracking (ported from CS2AC Legacy subtick.cpp)
        public readonly Queue<DateTime> InvalidCommandTimes = new();
        public int SubtickEvaluatedCommands;
        public int SubtickZeroWhenCommands;

        // Airborne Strafe Optimizer tracking (ported from CS2AC Legacy strafe_optimizer.cpp)
        public float YawAccelPercent;

        // Bhop / Hyperscroll & Airborne Nulls tracking (ported from CS2AC Legacy bhop.cpp & nulls.cpp)
        public bool WasAirborne;
        public int AirTicks;
        public int ConsecutivePerfBhops;
        public int ConsecutivePerfectNulls;
        public int LastLeftRightReleaseCmd = -1;
        public float LastLeftRightReleaseWhen = -1f;
        public ulong LastLeftRightReleasedButton;

        // NameChanger tracking (ported from CS2AC Legacy namechanger_module.cpp)
        public string LastKnownName = string.Empty;
        public readonly Queue<DateTime> NameChangeTimes = new();
        public DateTime LastMovementAlertTime = DateTime.MinValue;

        public void Reset()
        {
            Array.Clear(Samples);
            SampleHead = 0;
            SampleCount = 0;
            LastCommandNumber = -1;
            LastServerTick = -1;
            SpinScore = 0f;
            SustainedSpinSeconds = 0f;
            SubtickTamperScore = 0f;
            LastSubtickIncidentTime = DateTime.MinValue;
            UntrustedDetections = 0;
            SpinDetections = 0;
            SubtickDetections = 0;
            LastUntrustedAlertTime = DateTime.MinValue;
            LastSpinAlertTime = DateTime.MinValue;
            LastSubtickAlertTime = DateTime.MinValue;
            InvalidCommandTimes.Clear();
            SubtickEvaluatedCommands = 0;
            SubtickZeroWhenCommands = 0;
            YawAccelPercent = 0f;
            WasAirborne = false;
            AirTicks = 0;
            ConsecutivePerfBhops = 0;
            ConsecutivePerfectNulls = 0;
            LastLeftRightReleaseCmd = -1;
            LastLeftRightReleaseWhen = -1f;
            LastLeftRightReleasedButton = 0;
            LastKnownName = string.Empty;
            NameChangeTimes.Clear();
            LastMovementAlertTime = DateTime.MinValue;
        }

        public void PushSample(AngleSample sample)
        {
            Samples[SampleHead] = sample;
            SampleHead = (SampleHead + 1) % MotionHistorySize;
            if (SampleCount < MotionHistorySize)
            {
                SampleCount++;
            }
        }
    }

    private readonly ISwiftlyCore? _core;
    private readonly ILogger? _logger;
    private AntiAimConfig _config;
    private readonly NetworkSafetyMonitor? _networkSafety;
    private readonly Action<IPlayer?, string, string, PunishmentType>? _onViolation;
    private readonly PlayerState[] _players = new PlayerState[MaxPlayers + 1];

    public long StatUntrustedDetections { get; private set; }
    public long StatSpinDetections { get; private set; }
    public long StatSubtickDetections { get; private set; }
    public long StatMovementDetections { get; private set; }

    public AntiAimDetector(
        ISwiftlyCore? core,
        ILogger? logger,
        AntiAimConfig config,
        NetworkSafetyMonitor? networkSafety = null,
        Action<IPlayer?, string, string, PunishmentType>? onViolation = null)
    {
        _core = core;
        _logger = logger;
        _config = config;
        _networkSafety = networkSafety;
        _onViolation = onViolation;

        for (int i = 0; i <= MaxPlayers; i++)
        {
            _players[i] = new PlayerState();
        }
    }

    public AntiAimDetector(
        ISwiftlyCore? core,
        ILogger? logger,
        AimbotConfig legacyConfig,
        NetworkSafetyMonitor? networkSafety = null,
        Action<IPlayer?, string, string, PunishmentType>? onViolation = null)
        : this(
            core,
            logger,
            new AntiAimConfig
            {
                Enabled = legacyConfig.Enabled && legacyConfig.EnableAntiAim,
                Punishment = legacyConfig.Punishment
            },
            networkSafety,
            onViolation)
    {
    }

    public void UpdateConfig(AntiAimConfig config) => _config = config;

    public void UpdateConfig(AimbotConfig legacyConfig) => _config = new AntiAimConfig
    {
        Enabled = legacyConfig.Enabled && legacyConfig.EnableAntiAim,
        Punishment = legacyConfig.Punishment
    };

    public string Stats() =>
        $"enabled={_config.Enabled} untrusted={StatUntrustedDetections} spin={StatSpinDetections} subtick={StatSubtickDetections} movement={StatMovementDetections}";

    internal PlayerState GetState(int slot) => _players[slot];

    public void ResetAll()
    {
        StatUntrustedDetections = 0;
        StatSpinDetections = 0;
        StatSubtickDetections = 0;
        StatMovementDetections = 0;
        for (int i = 0; i <= MaxPlayers; i++)
        {
            _players[i].Reset();
        }
    }

    public void ResetPlayer(int slot)
    {
        if (ValidSlot(slot))
        {
            _players[slot].Reset();
        }
    }

    public void OnProcessUsercmds(IPlayer player, List<IUserCmd> cmds, int serverTick)
    {
        if (!_config.Enabled || !AimbotDetector.IsEligibleHuman(player) || cmds.Count == 0)
        {
            return;
        }

        int slot = player.PlayerID;
        if (!ValidSlot(slot))
        {
            return;
        }

        var pawn = player.PlayerPawn;
        if (pawn == null || !pawn.IsValid || pawn.LifeState != 0)
        {
            return;
        }

        string playerName = player.Controller?.PlayerName ?? $"Player#{slot}";
        var state = _players[slot];
        bool isAirborne = (pawn.Flags & 1u) == 0;

        if (_config.EnableNameChangerCheck)
        {
            CheckPlayerNameChange(slot, player, playerName);
        }
        if (_config.EnableMovementChecks)
        {
            RecordBhopStep(slot, player, playerName, isAirborne);
        }

        foreach (var cmd in cmds)
        {
            var pb = cmd.CSGOUserCmd;
            int cmdNum = (int)cmd.CommandNumber;
            if (cmdNum == state.LastCommandNumber)
            {
                continue;
            }

            if (!pb.Accessor.HasField("base"))
            {
                continue;
            }

            var b = pb.Base;
            bool isShot = pb.Attack1StartHistoryIndex >= 0;
            bool hasMouseInput = (b.Accessor.HasField("mousedx") && b.Mousedx != 0)
                || (b.Accessor.HasField("mousedy") && b.Mousedy != 0);

            if (b.Accessor.HasField("viewangles"))
            {
                QAngle baseAngles = b.Viewangles;
                if (_config.EnableUntrustedCheck && !VerifyAngles(slot, player, playerName, baseAngles, "base viewangles"))
                {
                    state.LastCommandNumber = cmdNum;
                    continue;
                }

                RecordAngleSample(slot, serverTick, cmdNum, baseAngles, isShot, player, playerName, isAirborne, hasMouseInput);
            }

            if (_config.EnableUntrustedCheck && pb.InputHistory != null)
            {
                for (int i = 0; i < pb.InputHistory.Count; i++)
                {
                    var entry = pb.InputHistory.Get(i);
                    if (entry.Accessor.HasField("view_angles"))
                    {
                        if (!VerifyAngles(slot, player, playerName, entry.ViewAngles, "input_history viewangles"))
                        {
                            break;
                        }
                    }
                }
            }

            var subtickMoves = b.SubtickMoves;
            if (_config.EnableSubtickIntegrityCheck && subtickMoves != null && subtickMoves.Count > 0)
            {
                VerifySubtickMoves(slot, player, playerName, cmdNum, isAirborne, subtickMoves);
            }

            if (_config.EnableSubtickIntegrityCheck && b.Accessor.HasField("buttons_pb"))
            {
                ulong changedMask = b.ButtonsPb.Buttonstate2 | b.ButtonsPb.Buttonstate3;
                VerifyMovementButtonConsistency(slot, player, playerName, changedMask, subtickMoves);
            }
        }
    }

    /// <summary>
    /// Detects rapid client name-changing scripts (&gt;= 5 name changes within 60 seconds).
    /// Ported from CS2AC Legacy NameChangerModule.
    /// </summary>
    internal bool CheckPlayerNameChange(int slot, IPlayer? player, string currentName, DateTime? nowOverride = null)
    {
        if (!ValidSlot(slot) || string.IsNullOrEmpty(currentName))
            return false;

        var state = _players[slot];
        if (string.IsNullOrEmpty(state.LastKnownName))
        {
            state.LastKnownName = currentName;
            return false;
        }

        if (string.Equals(state.LastKnownName, currentName, StringComparison.Ordinal))
            return false;

        state.LastKnownName = currentName;
        DateTime now = nowOverride ?? DateTime.UtcNow;

        while (state.NameChangeTimes.Count > 0 && (now - state.NameChangeTimes.Peek()).TotalSeconds >= 60.0)
            state.NameChangeTimes.Dequeue();

        state.NameChangeTimes.Enqueue(now);
        if (state.NameChangeTimes.Count >= 5)
        {
            string details = $"Rapid name-changer detected: {state.NameChangeTimes.Count} visible name changes within 60 seconds.";
            _logger?.LogWarning("[AegisAC] [AntiAim] {Name} {Details}", currentName, details);
            _onViolation?.Invoke(player, "NameChanger", details, _config.Punishment);
            state.NameChangeTimes.Clear();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Tracks airborne ticks and frame-perfect bunnyhop landing chains (&gt;= 10 consecutive frame-perfect jumps).
    /// Ported from CS2AC Legacy bhop.cpp.
    /// </summary>
    internal bool RecordBhopStep(int slot, IPlayer? player, string playerName, bool isAirborne, bool jumpedOnLandingTick = false)
    {
        if (!ValidSlot(slot) || !_config.Enabled || !_config.EnableMovementChecks)
            return false;

        int bhopThreshold = Math.Max(3, _config.BhopStreakThreshold);
        var state = _players[slot];
        if (isAirborne)
        {
            state.AirTicks++;
            if (state.WasAirborne && jumpedOnLandingTick && state.AirTicks >= 4)
            {
                state.ConsecutivePerfBhops++;
                if (state.ConsecutivePerfBhops >= bhopThreshold)
                {
                    state.ConsecutivePerfBhops = 0;
                    StatMovementDetections++;
                    string details = $"{bhopThreshold} consecutive frame-perfect bunnyhops without ground friction loss.";
                    _logger?.LogWarning("[AegisAC] [Movement] {Name} {Details}", playerName, details);
                    _onViolation?.Invoke(player, "Bhop", details, _config.Punishment);
                    return true;
                }
            }
            state.WasAirborne = true;
        }
        else
        {
            if (state.WasAirborne && state.AirTicks >= 4)
            {
                if (jumpedOnLandingTick)
                {
                    state.ConsecutivePerfBhops++;
                    if (state.ConsecutivePerfBhops >= bhopThreshold)
                    {
                        state.ConsecutivePerfBhops = 0;
                        StatMovementDetections++;
                        string details = $"{bhopThreshold} consecutive frame-perfect bunnyhops without ground friction loss.";
                        _logger?.LogWarning("[AegisAC] [Movement] {Name} {Details}", playerName, details);
                        _onViolation?.Invoke(player, "Bhop", details, _config.Punishment);
                        return true;
                    }
                }
                else
                {
                    state.ConsecutivePerfBhops = 0;
                }
            }
            state.AirTicks = 0;
            state.WasAirborne = false;
        }

        return false;
    }

    /// <summary>
    /// Verifies that movement button state transitions in buttons_pb (W/A/S/D/Jump/Duck) are backed by subtick_moves.
    /// Ported from CS2AC Legacy subtick.cpp (VerifyCommand / InvalidInput).
    /// </summary>
    private void VerifyMovementButtonConsistency(
        int slot,
        IPlayer? player,
        string playerName,
        ulong changedButtonsMask,
        IProtobufRepeatedFieldSubMessageType<CSubtickMoveStep>? subtickMoves)
    {
        // IN_JUMP(2) | IN_DUCK(4) | IN_FORWARD(8) | IN_BACK(16) | IN_MOVELEFT(512) | IN_MOVERIGHT(1024)
        const ulong MovementMask = 2UL | 4UL | 8UL | 16UL | 512UL | 1024UL;
        ulong expected = changedButtonsMask & MovementMask;
        if (expected == 0)
            return;

        if (subtickMoves != null)
        {
            for (int i = 0; i < subtickMoves.Count; i++)
            {
                var step = subtickMoves.Get(i);
                if (step.Button != 0)
                {
                    expected &= ~step.Button;
                }
                else
                {
                    if (step.AnalogForwardDelta != 0f)
                        expected &= ~(8UL | 16UL);
                    if (step.AnalogLeftDelta != 0f)
                        expected &= ~(512UL | 1024UL);
                }
            }
        }

        if (expected != 0)
        {
            var state = _players[slot];
            DateTime now = DateTime.UtcNow;
            while (state.InvalidCommandTimes.Count > 0 && (now - state.InvalidCommandTimes.Peek()).TotalSeconds > 5.0)
                state.InvalidCommandTimes.Dequeue();

            state.InvalidCommandTimes.Enqueue(now);
            if (state.InvalidCommandTimes.Count >= 8 && (now - state.LastSubtickAlertTime).TotalSeconds >= 15.0)
            {
                state.LastSubtickAlertTime = now;
                int count = state.InvalidCommandTimes.Count;
                state.InvalidCommandTimes.Clear();
                string details = $"Invalid Input: {count} commands within 5.0s modified movement buttons without subtick timing records.";
                _logger?.LogWarning("[AegisAC] [AntiAim] {Name} {Details}", playerName, details);
                _onViolation?.Invoke(player, "Invalid Input", details, _config.Punishment);
            }
        }
    }

    /// <summary>
    /// Direct angle &amp; motion verification entrypoint used by both live hooks and deterministic unit tests.
    /// </summary>
    internal bool RecordAngleSample(
        int slot,
        int serverTick,
        int commandNumber,
        QAngle angles,
        bool isShot,
        IPlayer? player = null,
        string playerName = "TestPlayer",
        bool isAirborne = false,
        bool hasMouseInput = false)
    {
        if (!ValidSlot(slot) || !_config.Enabled)
        {
            return false;
        }

        if (_config.EnableUntrustedCheck && !VerifyAngles(slot, player, playerName, angles, "setup_move"))
        {
            return true;
        }

        var state = _players[slot];
        if (commandNumber == state.LastCommandNumber && serverTick == state.LastServerTick)
        {
            return false;
        }

        state.LastCommandNumber = commandNumber;
        state.LastServerTick = serverTick;
        state.PushSample(new AngleSample
        {
            ServerTick = serverTick,
            CommandNumber = commandNumber,
            Angles = angles,
            IsShot = isShot,
            HasMouseInput = hasMouseInput
        });

        if (_config.EnableMovementChecks && isAirborne && EvaluateStrafeOptimization(slot, player, playerName))
        {
            return true;
        }

        return _config.EnableSpinbotCheck && EvaluateMotion(slot, player, playerName);
    }

    /// <summary>
    /// Detects airborne auto-strafe / strafe optimizer yaw-acceleration symmetry.
    /// Ported from CS2AC Legacy strafe_optimizer.cpp.
    /// </summary>
    internal bool EvaluateStrafeOptimization(int slot, IPlayer? player, string playerName)
    {
        var state = _players[slot];
        if (state.SampleCount < 5)
            return false;

        Span<AngleSample> hist = stackalloc AngleSample[5];
        for (int i = 0; i < 5; i++)
        {
            int idx = (state.SampleHead - 5 + i + MotionHistorySize) % MotionHistorySize;
            hist[i] = state.Samples[idx];
        }

        for (int i = 1; i < 5; i++)
        {
            if (hist[i].CommandNumber != hist[i - 1].CommandNumber + 1)
                return false;
        }

        static float YawSpeed(in AngleSample prev, in AngleSample curr) =>
            AimMath.YawDelta(prev.Angles.Y, curr.Angles.Y) / TickIntervalSeconds;

        float speed1 = YawSpeed(hist[0], hist[1]);
        float speed2 = YawSpeed(hist[1], hist[2]);
        float speed3 = YawSpeed(hist[2], hist[3]);
        float speed4 = YawSpeed(hist[3], hist[4]);

        bool switchedDirection = (speed3 < 0f) != (speed4 < 0f) && MathF.Abs(speed3) > 10f && MathF.Abs(speed4) > 10f;
        if (!switchedDirection)
            return false;

        float accel2Ago = MathF.Abs((speed2 - speed1) / TickIntervalSeconds);
        float lastAccel = MathF.Abs((speed3 - speed2) / TickIntervalSeconds);
        float currAccel = MathF.Abs((speed4 - speed3) / TickIntervalSeconds);

        float diff = MathF.Abs(currAccel - accel2Ago);
        bool matched = false;
        if (diff < 64.0f)
        {
            float avg = (currAccel + accel2Ago) * 0.5f;
            if (avg < 128.0f && (lastAccel - avg) > 128.0f)
            {
                matched = true;
            }
        }

        state.YawAccelPercent = state.YawAccelPercent * 0.95f + (matched ? 0.05f : 0.0f);
        if (state.YawAccelPercent > 0.90f && (DateTime.UtcNow - state.LastMovementAlertTime).TotalSeconds >= 15.0)
        {
            state.LastMovementAlertTime = DateTime.UtcNow;
            float scorePct = state.YawAccelPercent * 100.0f;
            state.YawAccelPercent = 0f;
            string details = $"AutoStrafe (Strafe Optimizer): airborne yaw acceleration matched mechanical symmetry ({scorePct:F1}% > 90.0%).";
            _logger?.LogWarning("[AegisAC] [Movement] {Name} {Details}", playerName, details);
            _onViolation?.Invoke(player, "AutoStrafe", details, _config.Punishment);
            return true;
        }

        return false;
    }

    internal bool VerifySubtickStepList(
        int slot,
        ReadOnlySpan<(ulong Button, bool Pressed, float When, float PitchDelta, float YawDelta)> steps,
        IPlayer? player = null,
        string playerName = "TestPlayer",
        int commandNumber = -1,
        bool isAirborne = false)
    {
        if (!ValidSlot(slot) || !_config.Enabled || !_config.EnableSubtickIntegrityCheck)
        {
            return true;
        }

        var state = _players[slot];
        ulong prevButton = 0;
        float prevSameButtonWhen = 0.0f;
        ulong seenButtonsMask = 0;
        ulong lastPressedMask = 0;
        bool hasNonAnalogButton = false;
        bool allButtonWhenZero = true;
        int nullsThreshold = Math.Max(16, _config.NullsStreakThreshold);

        // Track pressed movement buttons carrying pitch/yaw deltas for CS2AC Legacy's HasExcessiveSubtickMovesWithAngles check
        Span<(ulong Button, float When)> pressedAngleButtons = stackalloc (ulong, float)[Math.Min(steps.Length, 32)];
        int pressedAngleButtonCount = 0;
        int aliasSpamMatches = 0;

        for (int i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            if (!float.IsFinite(step.When) || !float.IsFinite(step.PitchDelta) || !float.IsFinite(step.YawDelta))
            {
                FlagUntrusted(slot, player, playerName, "Non-finite subtick float parameter");
                return false;
            }

            // CS2 subtick timing:
            // Source 2 client calculates `when = (input_time - tick_start_time) / tick_interval`.
            // Because of Windows high-precision QPC timers, NVIDIA Reflex, and frame pacing,
            // legitimate clients frequently produce values slightly outside [0, 1] (e.g. -0.060 to 1.060).
            // CS2AC Legacy does NOT check when < 0.0. Only extreme impossible exploit values (e.g. teleports or server crashes)
            // outside [-5.0, 5.0] are considered malicious tampering.
            if (MathF.Abs(step.When) > 5.0f)
            {
                FlagSubtickTamper(slot, player, playerName, $"Extreme exploit subtick fraction (when={step.When:F2})", 5.0f);
                return false;
            }

            if (step.Button != 0)
            {
                hasNonAnalogButton = true;
                if (step.When > 0.0001f)
                {
                    allButtonWhenZero = false;
                }

                // Airborne Nulls / Snap-Tap check on IN_MOVELEFT(512) <-> IN_MOVERIGHT(1024)
                if (_config.EnableMovementChecks && isAirborne && commandNumber >= 0 && (step.Button == 512UL || step.Button == 1024UL))
                {
                    if (!step.Pressed)
                    {
                        state.LastLeftRightReleaseCmd = commandNumber;
                        state.LastLeftRightReleaseWhen = step.When;
                        state.LastLeftRightReleasedButton = step.Button;
                    }
                    else if (state.LastLeftRightReleaseCmd >= 0 && state.LastLeftRightReleasedButton != 0 && state.LastLeftRightReleasedButton != step.Button)
                    {
                        float tickDiff = (commandNumber - state.LastLeftRightReleaseCmd) + (step.When - state.LastLeftRightReleaseWhen);
                        if (MathF.Abs(tickDiff) <= 0.0005f)
                        {
                            state.ConsecutivePerfectNulls++;
                            if (state.ConsecutivePerfectNulls >= nullsThreshold && (DateTime.UtcNow - state.LastMovementAlertTime).TotalSeconds >= 15.0)
                            {
                                state.LastMovementAlertTime = DateTime.UtcNow;
                                int streak = state.ConsecutivePerfectNulls;
                                state.ConsecutivePerfectNulls = 0;
                                StatMovementDetections++;
                                string details = $"Nulls / Snap-Tap: {streak} consecutive airborne opposite-direction switches with 0.00ms underlap.";
                                _logger?.LogWarning("[AegisAC] [Movement] {Name} {Details}", playerName, details);
                                _onViolation?.Invoke(player, "Nulls", details, _config.Punishment);
                                return false;
                            }
                        }
                        else if (tickDiff > 0.001f && tickDiff <= 12.0f)
                        {
                            state.ConsecutivePerfectNulls = 0;
                        }
                    }
                }

                // CS2AC Legacy HasExcessiveSubtickMovesWithAngles (subtick.cpp lines 50-103):
                // Ignore IN_ATTACK(1), IN_ATTACK2(2048), IN_USE(32), IN_RELOAD(8192), IN_TURNLEFT(128), IN_TURNRIGHT(256)
                const ulong IgnoredAliasMask = 1UL | 2048UL | 32UL | 8192UL | 128UL | 256UL;
                if ((step.Button & IgnoredAliasMask) == 0 && (step.PitchDelta != 0f || step.YawDelta != 0f))
                {
                    if (step.Pressed && pressedAngleButtonCount < pressedAngleButtons.Length)
                    {
                        pressedAngleButtons[pressedAngleButtonCount++] = (step.Button, step.When);
                    }
                    else if (!step.Pressed)
                    {
                        for (int k = 0; k < pressedAngleButtonCount; k++)
                        {
                            if (pressedAngleButtons[k].Button == step.Button && MathF.Abs(pressedAngleButtons[k].When - step.When) <= 0.0001f)
                            {
                                aliasSpamMatches++;
                                if (aliasSpamMatches >= 2)
                                {
                                    FlagSubtickTamper(slot, player, playerName, $"Subtick pitch/yaw alias spam (button=0x{step.Button:X} pressed & released at when={step.When:F3} with angle delta)", 2.5f);
                                    return false;
                                }
                                break;
                            }
                        }
                    }
                }
            }

            if (step.Button != 0 && (step.Button & (step.Button - 1)) == 0)
            {
                if ((seenButtonsMask & step.Button) != 0)
                {
                    bool wasPressed = (lastPressedMask & step.Button) != 0;
                    if (wasPressed == step.Pressed)
                    {
                        // Only flag if the exact same button repeats the same state while going BACKWARD in time within the same command
                        // (e.g. when: 0.80 -> 0.20). Normal same-tick duplicate presses/releases (from dual binds or rapid clicks) are ignored here.
                        if (step.Button == prevButton && step.When + 0.0005f < prevSameButtonWhen)
                        {
                            FlagSubtickTamper(slot, player, playerName, $"Non-monotonic duplicate subtick button transition (button=0x{step.Button:X}, when={step.When:F3} < prev={prevSameButtonWhen:F3})", 2.5f);
                            return false;
                        }
                    }
                }

                prevButton = step.Button;
                prevSameButtonWhen = step.When;
                seenButtonsMask |= step.Button;
                if (step.Pressed)
                {
                    lastPressedMask |= step.Button;
                }
                else
                {
                    lastPressedMask &= ~step.Button;
                }
            }
        }

        // Desubticking check (ported from CS2AC Legacy subtick.cpp):
        // If >= 90% of 30 commands containing non-analog subtick button transitions have When == 0.0, flag Desubtick.
        if (_config.EnableDesubtickCheck && hasNonAnalogButton)
        {
            state.SubtickEvaluatedCommands++;
            if (allButtonWhenZero)
                state.SubtickZeroWhenCommands++;

            if (state.SubtickEvaluatedCommands >= 30)
            {
                float zeroRatio = (float)state.SubtickZeroWhenCommands / state.SubtickEvaluatedCommands;
                state.SubtickEvaluatedCommands = 0;
                state.SubtickZeroWhenCommands = 0;

                if (zeroRatio >= 0.90f && (DateTime.UtcNow - state.LastSubtickAlertTime).TotalSeconds >= 15.0)
                {
                    state.LastSubtickAlertTime = DateTime.UtcNow;
                    StatSubtickDetections++;
                    string details = $"Desubticking: {zeroRatio * 100.0f:F1}% of non-analog subtick commands had fractional timing flattened to 0.0.";
                    _logger?.LogWarning("[AegisAC] [AntiAim] {Name} {Details}", playerName, details);
                    _onViolation?.Invoke(player, "Desubtick", details, _config.Punishment);
                    return false;
                }
            }
        }

        state.SubtickTamperScore = MathF.Max(0f, state.SubtickTamperScore - 0.50f);
        return true;
    }

    private void VerifySubtickMoves(
        int slot,
        IPlayer? player,
        string playerName,
        int commandNumber,
        bool isAirborne,
        IProtobufRepeatedFieldSubMessageType<CSubtickMoveStep> subtickMoves)
    {
        int count = Math.Min(subtickMoves.Count, 32);
        Span<(ulong Button, bool Pressed, float When, float PitchDelta, float YawDelta)> buffer = stackalloc (ulong, bool, float, float, float)[count];

        for (int i = 0; i < count; i++)
        {
            var step = subtickMoves.Get(i);
            buffer[i] = (step.Button, step.Pressed, step.When, step.PitchDelta, step.YawDelta);
        }

        VerifySubtickStepList(slot, buffer, player, playerName, commandNumber, isAirborne);
    }

    private bool VerifyAngles(int slot, IPlayer? player, string playerName, QAngle angles, string context)
    {
        if (!AimMath.IsFinite(angles))
        {
            FlagUntrusted(slot, player, playerName, $"Non-finite {context} (NaN/Inf)");
            return false;
        }

        // Pitch limits:
        // base viewangles are capped at +/- 89.5 deg.
        // input_history viewangles include weapon punch and damage flinch, which can legitimately push pitch to +/- 95 deg.
        bool isInputHistory = context.Contains("input_history", StringComparison.OrdinalIgnoreCase);
        float maxPitch = isInputHistory ? 95.0f : 89.5f;

        // Roll limits:
        // In Source 2 CS2, taking damage, aim punch, and grenade shake legitimately apply camera roll up to 10-25 degrees (e.g. roll=-5.91).
        // Roll is only untrusted if it exceeds physical engine limits (MathF.Abs(roll) > 50.0 deg), matching CS2AC Legacy.
        const float MaxRoll = 50.0f;

        if (angles.X < -maxPitch || angles.X > maxPitch || MathF.Abs(angles.Z) > MaxRoll || MathF.Abs(angles.Y) > 360.5f)
        {
            FlagUntrusted(slot, player, playerName, $"Out-of-bounds {context} (pitch={angles.X:F2}, yaw={angles.Y:F2}, roll={angles.Z:F2})");
            return false;
        }

        return true;
    }

    private void FlagUntrusted(int slot, IPlayer? player, string playerName, string reason)
    {
        var state = _players[slot];
        DateTime now = DateTime.UtcNow;

        // Per-player 60-second cooldown latch so continuous out-of-bounds packets do not spam logs/webhooks
        if ((now - state.LastUntrustedAlertTime).TotalSeconds < 60.0)
        {
            return;
        }

        state.LastUntrustedAlertTime = now;
        state.UntrustedDetections++;
        StatUntrustedDetections++;

        string details = $"ANTI_UNTRUSTED: {reason} (#{state.UntrustedDetections})";
        _logger?.LogWarning("[AegisAC] [AntiAim] {Name} {Details}", playerName, details);
        _onViolation?.Invoke(player, "AntiAim (Untrusted)", details, _config.Punishment);
    }

    private void FlagSubtickTamper(int slot, IPlayer? player, string playerName, string reason, float points)
    {
        var state = _players[slot];
        DateTime now = DateTime.UtcNow;

        // Temporal score decay: if more than 5.0 seconds passed since last incident, decay the score
        // to ensure isolated or harmless timing variances over a 45-minute match never accumulate to detection.
        if (state.LastSubtickIncidentTime != DateTime.MinValue)
        {
            double elapsed = (now - state.LastSubtickIncidentTime).TotalSeconds;
            if (elapsed > 5.0)
            {
                float decay = (float)(elapsed * 0.5); // decay 1.0 point every 2 seconds
                state.SubtickTamperScore = MathF.Max(0f, state.SubtickTamperScore - decay);
            }
        }
        state.LastSubtickIncidentTime = now;

        state.SubtickTamperScore = MathF.Min(20f, state.SubtickTamperScore + points);

        if (state.SubtickTamperScore >= 6.0f && (now - state.LastSubtickAlertTime).TotalSeconds >= 15.0)
        {
            state.LastSubtickAlertTime = now;
            state.SubtickTamperScore = 0f;
            state.SubtickDetections++;
            StatSubtickDetections++;

            string details = $"Subtick Tamper: {reason} (#{state.SubtickDetections})";
            _logger?.LogWarning("[AegisAC] [AntiAim] {Name} {Details}", playerName, details);
            _onViolation?.Invoke(player, "Subtick Tamper", details, _config.Punishment);
        }
    }

    private bool EvaluateMotion(int slot, IPlayer? player, string playerName)
    {
        var state = _players[slot];
        if (state.SampleCount < 10)
        {
            return false;
        }

        if (_networkSafety != null && _networkSafety.ShouldSkipDetection(slot, out _))
        {
            return false;
        }

        // Collect chronological samples
        int count = Math.Min(state.SampleCount, 16);
        Span<AngleSample> window = stackalloc AngleSample[count];
        for (int i = 0; i < count; i++)
        {
            int idx = (state.SampleHead - count + i + MotionHistorySize) % MotionHistorySize;
            window[i] = state.Samples[idx];
        }

        float totalAbsYaw = 0f;
        float sumSqYawStep = 0f;
        float totalAbsPitch = 0f;
        float signedYawSum = 0f;
        int totalTicks = 0;
        int activeSpinSteps = 0;
        int jitterFlipSteps = 0;
        int mouseInputSteps = 0;
        float prevYawStep = 0f;
        int stepCount = count - 1;

        for (int i = 1; i < count; i++)
        {
            int tickDelta = window[i].ServerTick - window[i - 1].ServerTick;
            if (tickDelta <= 0 || tickDelta > 2)
            {
                // Discontinuity in command stream
                state.SustainedSpinSeconds = 0f;
                return false;
            }

            if (window[i].IsShot || window[i - 1].IsShot)
            {
                // Ignore combat flicks accompanied by weapon fire
                state.SustainedSpinSeconds = 0f;
                return false;
            }

            if (window[i].HasMouseInput)
            {
                mouseInputSteps++;
            }

            float yawStep = AimMath.YawDelta(window[i].Angles.Y, window[i - 1].Angles.Y);
            float absYawStep = MathF.Abs(yawStep);
            float absPitchStep = MathF.Abs(window[i].Angles.X - window[i - 1].Angles.X);

            totalAbsYaw += absYawStep;
            sumSqYawStep += absYawStep * absYawStep;
            totalAbsPitch += absPitchStep;
            signedYawSum += yawStep;
            totalTicks += tickDelta;

            if (absYawStep >= 15.0f)
            {
                activeSpinSteps++;
            }

            if (i >= 2 && absYawStep >= 65.0f && MathF.Abs(prevYawStep) >= 65.0f
                && ((yawStep * prevYawStep) < 0f || (absYawStep >= 175.0f && MathF.Abs(prevYawStep) >= 175.0f)))
            {
                jitterFlipSteps++;
            }

            prevYawStep = yawStep;
        }

        if (totalTicks < 8 || stepCount <= 0)
        {
            return false;
        }

        float durationSeconds = totalTicks * TickIntervalSeconds;
        float degPerSecond = totalAbsYaw / MathF.Max(durationSeconds, 0.001f);
        float directionalConsistency = totalAbsYaw > 1e-3f ? MathF.Abs(signedYawSum) / totalAbsYaw : 0f;

        float meanYawStep = totalAbsYaw / stepCount;
        float yawStepVariance = MathF.Max(0f, (sumSqYawStep / stepCount) - (meanYawStep * meanYawStep));
        float yawStepCv = meanYawStep > 1e-3f ? MathF.Sqrt(yawStepVariance) / meanYawStep : 1.0f;

        // Check exact periodic Yaw Jitter pattern (period 2, 3, or 5 within 0.50 deg tolerance, ported from CS2AC Legacy antiaim_module.cpp).
        // A human shaking a high-DPI mouse left/right has random turnaround angles (never repeating within 0.50 deg every 2/3/5 ticks).
        bool hasExactPeriodicJitter = false;
        ReadOnlySpan<int> candidatePeriods = [2, 3, 5];
        foreach (int period in candidatePeriods)
        {
            int required = Math.Min(count, Math.Max(8, period * 4));
            if (count < required)
            {
                continue;
            }

            bool repeats = true;
            for (int i = count - required; i < count - period; i++)
            {
                if (MathF.Abs(AimMath.YawDelta(window[i + period].Angles.Y, window[i].Angles.Y)) > 0.50f)
                {
                    repeats = false;
                    break;
                }
            }

            if (repeats)
            {
                hasExactPeriodicJitter = true;
                break;
            }
        }

        float points = 0f;
        string? mode = null;

        if (jitterFlipSteps >= 6 && totalAbsYaw >= 450.0f && (hasExactPeriodicJitter || mouseInputSteps == 0))
        {
            // Rapid alternating yaw jitter anti-aim with exact periodic angle repetition or zero physical mouse input
            state.SustainedSpinSeconds = 0f;
            points = 2.5f;
            mode = "Yaw Jitter Anti-Aim";
        }
        else if (activeSpinSteps >= count - 3 && directionalConsistency >= 0.90f)
        {
            // Distinguish robotic Spinbot (zero physical mouse input OR constant angular step CV <= 8.157% with flat pitch)
            // from a human spinning a high-DPI physical mouse on a mousepad (which has mouseInputSteps > 0, organic hand
            // acceleration/deceleration CV > 8.157%, and/or vertical pitch arc).
            bool isRoboticSpin = mouseInputSteps == 0
                || (yawStepCv <= AimMath.Baselines.PatternVarianceRatio && totalAbsPitch <= 0.15f);

            if (isRoboticSpin)
            {
                if (degPerSecond >= 2880.0f && totalAbsYaw >= 540.0f)
                {
                    points = 4.0f;
                    mode = "Spinbot (Tier 3 Extreme)";
                }
                else if (degPerSecond >= 1440.0f && totalAbsYaw >= 360.0f)
                {
                    points = 2.5f;
                    mode = "Spinbot (Tier 2 Fast)";
                }
                else if (degPerSecond >= 720.0f && totalAbsYaw >= 270.0f && count >= 14)
                {
                    points = 1.5f;
                    mode = "Spinbot (Tier 1 Sustained)";
                }
            }
            else if (degPerSecond >= 720.0f)
            {
                // If physical mouse input is present with organic velocity variation, require >= 10.0 continuous seconds
                // of uninterrupted unidirectional spinning (matching CS2AC Legacy fastSpinSeconds = 10.0f), which is
                // physically impossible on a mousepad without lifting the mouse.
                state.SustainedSpinSeconds += TickIntervalSeconds;
                if (state.SustainedSpinSeconds >= 10.0f)
                {
                    points = 8.0f;
                    mode = "Spinbot (Sustained 10s)";
                    state.SustainedSpinSeconds = 0f;
                }
            }
        }
        else
        {
            state.SustainedSpinSeconds = MathF.Max(0f, state.SustainedSpinSeconds - TickIntervalSeconds * 2.0f);
        }

        if (points > 0f && mode != null)
        {
            state.SpinScore = MathF.Min(20f, state.SpinScore + points);
            DateTime now = DateTime.UtcNow;

            if (state.SpinScore >= 8.0f && (now - state.LastSpinAlertTime).TotalSeconds >= 10.0)
            {
                state.LastSpinAlertTime = now;
                state.SpinScore = 0f;
                state.SpinDetections++;
                StatSpinDetections++;

                string details = $"{mode}: {degPerSecond:F0} deg/s, total={totalAbsYaw:F0} deg, consistency={directionalConsistency:F2}, flips={jitterFlipSteps} (#{state.SpinDetections})";
                _logger?.LogWarning("[AegisAC] [AntiAim] {Name} detected for {Details}", playerName, details);
                _onViolation?.Invoke(player, mode, details, _config.Punishment);
                return true;
            }
        }
        else
        {
            state.SpinScore = MathF.Max(0f, state.SpinScore - 0.25f);
        }

        return false;
    }

    private static bool ValidSlot(int slot) => slot >= 0 && slot <= MaxPlayers;
}
