using SwiftlyS2.Shared.GameHooks;
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.ProtobufDefinitions;
using AegisAC.Configuration;

using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;

namespace AegisAC.Modules;

/// <summary>
/// Dedicated Movement Automation Detector (Bhop, Nulls / Snap-Tap, and Auto-Strafe / Strafe Optimizer).
/// Reimplemented for SwiftlyS2 based on CS2AC Legacy's bhop.cpp, nulls.cpp, and strafe_optimizer.cpp.
/// </summary>
internal sealed class MovementDetector
{
    public const int MaxPlayers = 64;
    private const int MotionHistorySize = 16;
    private const float TickIntervalSeconds = 1.0f / 64.0f;

    internal struct AngleSample
    {
        public int CommandNumber;
        public QAngle Angles;
    }

    internal sealed class PlayerMovementState
    {
        public readonly AngleSample[] Samples = new AngleSample[MotionHistorySize];
        public int SampleHead;
        public int SampleCount;

        // Bhop / Hyperscroll tracking (ported from CS2AC Legacy bhop.cpp)
        public bool WasAirborne;
        public int AirTicks;
        public int ConsecutivePerfBhops;

        // Airborne Nulls / Snap-Tap (SOCD) tracking (ported from CS2AC Legacy nulls.cpp)
        public int ConsecutivePerfectNulls;
        public int LastLeftRightReleaseCmd = -1;
        public float LastLeftRightReleaseWhen = -1f;
        public ulong LastLeftRightReleasedButton;

        // Airborne Strafe Optimizer tracking (ported from CS2AC Legacy strafe_optimizer.cpp)
        public float YawAccelPercent;

        public DateTime LastMovementAlertTime = DateTime.MinValue;

        public void Reset()
        {
            Array.Clear(Samples);
            SampleHead = 0;
            SampleCount = 0;
            WasAirborne = false;
            AirTicks = 0;
            ConsecutivePerfBhops = 0;
            ConsecutivePerfectNulls = 0;
            LastLeftRightReleaseCmd = -1;
            LastLeftRightReleaseWhen = -1f;
            LastLeftRightReleasedButton = 0;
            YawAccelPercent = 0f;
            LastMovementAlertTime = DateTime.MinValue;
        }

        public void PushSample(int cmdNum, QAngle angles)
        {
            Samples[SampleHead] = new AngleSample { CommandNumber = cmdNum, Angles = angles };
            SampleHead = (SampleHead + 1) % MotionHistorySize;
            if (SampleCount < MotionHistorySize)
            {
                SampleCount++;
            }
        }
    }

    private readonly ISwiftlyCore? _core;
    private readonly ILogger? _logger;
    private MovementConfig _config;
    private readonly NetworkSafetyMonitor? _networkSafety;
    private readonly Action<IPlayer?, string, string, PunishmentType>? _onViolation;
    private readonly PlayerMovementState[] _players = new PlayerMovementState[MaxPlayers + 1];

    public long StatBhopDetections { get; private set; }
    public long StatNullsDetections { get; private set; }
    public long StatStrafeDetections { get; private set; }

    public MovementDetector(
        ISwiftlyCore? core,
        ILogger? logger,
        MovementConfig config,
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
            _players[i] = new PlayerMovementState();
        }
    }

    public void UpdateConfig(MovementConfig config) => _config = config;

    public string Stats() =>
        $"enabled={_config.Enabled} bhop={StatBhopDetections} nulls={StatNullsDetections} strafe={StatStrafeDetections}";

    public void ResetAll()
    {
        StatBhopDetections = 0;
        StatNullsDetections = 0;
        StatStrafeDetections = 0;
        for (int i = 0; i <= MaxPlayers; i++)
        {
            _players[i].Reset();
        }
    }

    public void ResetPlayer(int slot)
    {
        if (slot >= 0 && slot <= MaxPlayers)
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
        if (slot < 0 || slot > MaxPlayers)
        {
            return;
        }

        var pawn = player.PlayerPawn;
        if (pawn == null || !pawn.IsValid || pawn.LifeState != 0)
        {
            return;
        }

        string playerName = player.Controller?.PlayerName ?? $"Player#{slot}";
        bool isAirborne = (pawn.Flags & 1u) == 0;

        if (_config.EnableBhopCheck)
        {
            RecordBhopStep(slot, player, playerName, isAirborne);
        }

        Span<(ulong Button, bool Pressed, float When)> nullsBuffer = stackalloc (ulong, bool, float)[32];

        foreach (var cmd in cmds)
        {
            var pb = cmd.CSGOUserCmd;
            int cmdNum = (int)cmd.CommandNumber;

            if (!pb.Accessor.HasField("base"))
            {
                continue;
            }

            var b = pb.Base;
            if (_config.EnableAutoStrafeCheck && b.Accessor.HasField("viewangles"))
            {
                _players[slot].PushSample(cmdNum, b.Viewangles);
                if (isAirborne)
                {
                    EvaluateStrafeOptimization(slot, player, playerName);
                }
            }

            if (_config.EnableNullsCheck && isAirborne && b.SubtickMoves != null && b.SubtickMoves.Count > 0)
            {
                int count = Math.Min(b.SubtickMoves.Count, 32);
                for (int i = 0; i < count; i++)
                {
                    var step = b.SubtickMoves.Get(i);
                    nullsBuffer[i] = (step.Button, step.Pressed, step.When);
                }
                VerifyAirborneNullsSpan(slot, nullsBuffer.Slice(0, count), player, playerName, cmdNum);
            }
        }
    }

    /// <summary>
    /// Tracks airborne ticks and frame-perfect bunnyhop landing chains (&gt;= 10 consecutive frame-perfect jumps).
    /// Ported from CS2AC Legacy bhop.cpp.
    /// </summary>
    internal bool RecordBhopStep(int slot, IPlayer? player, string playerName, bool isAirborne, bool jumpedOnLandingTick = false)
    {
        if (slot < 0 || slot > MaxPlayers || !_config.Enabled || !_config.EnableBhopCheck)
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
                    StatBhopDetections++;
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
                        StatBhopDetections++;
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



    internal bool VerifyAirborneNullsSpan(
        int slot,
        ReadOnlySpan<(ulong Button, bool Pressed, float When)> steps,
        IPlayer? player = null,
        string playerName = "TestPlayer",
        int commandNumber = -1)
    {
        if (slot < 0 || slot > MaxPlayers || !_config.Enabled || !_config.EnableNullsCheck)
            return false;

        var state = _players[slot];
        int nullsThreshold = Math.Max(16, _config.NullsStreakThreshold);

        for (int i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            // IN_MOVELEFT(512) <-> IN_MOVERIGHT(1024)
            if (step.Button == 512UL || step.Button == 1024UL)
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
                            StatNullsDetections++;
                            string details = $"Nulls / Snap-Tap: {streak} consecutive airborne opposite-direction switches with 0.00ms underlap.";
                            _logger?.LogWarning("[AegisAC] [Movement] {Name} {Details}", playerName, details);
                            _onViolation?.Invoke(player, "Nulls", details, _config.Punishment);
                            return true;
                        }
                    }
                    else if (tickDiff > 0.001f && tickDiff <= 12.0f)
                    {
                        state.ConsecutivePerfectNulls = 0;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Detects airborne auto-strafe / strafe optimizer yaw-acceleration symmetry.
    /// Ported from CS2AC Legacy strafe_optimizer.cpp.
    /// </summary>
    internal bool EvaluateStrafeOptimization(int slot, IPlayer? player, string playerName)
    {
        if (slot < 0 || slot > MaxPlayers || !_config.Enabled || !_config.EnableAutoStrafeCheck)
            return false;

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
            StatStrafeDetections++;
            string details = $"AutoStrafe (Strafe Optimizer): airborne yaw acceleration matched mechanical symmetry ({scorePct:F1}% > 90.0%).";
            _logger?.LogWarning("[AegisAC] [Movement] {Name} {Details}", playerName, details);
            _onViolation?.Invoke(player, "AutoStrafe", details, _config.Punishment);
            return true;
        }

        return false;
    }
}
