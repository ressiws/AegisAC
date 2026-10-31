using SwiftlyS2.Shared.GameHooks;
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;
using AegisAC.Configuration;

using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.ProtobufDefinitions;

namespace AegisAC.Modules;

/// <summary>
/// Dedicated Server-Side Anti-Aim and Spinbot Detector.
/// Reimplemented for SwiftlyS2 based on CS2AC Legacy's antiaim_module.cpp:
/// - Deterministic ANTI_UNTRUSTED angle &amp; NaN/Inf validation with a 60-second per-player alert latch
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
        public int UntrustedDetections;
        public int SpinDetections;
        public int JitterDetections;

        public DateTime LastUntrustedAlertTime = DateTime.MinValue;
        public DateTime LastSpinAlertTime = DateTime.MinValue;

        public void Reset()
        {
            Array.Clear(Samples);
            SampleHead = 0;
            SampleCount = 0;
            LastCommandNumber = -1;
            LastServerTick = -1;
            SpinScore = 0f;
            SustainedSpinSeconds = 0f;
            UntrustedDetections = 0;
            SpinDetections = 0;
            JitterDetections = 0;
            LastUntrustedAlertTime = DateTime.MinValue;
            LastSpinAlertTime = DateTime.MinValue;
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
    public long StatJitterDetections { get; private set; }

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

    public string Stats() =>
        $"enabled={_config.Enabled} untrusted={StatUntrustedDetections} spin={StatSpinDetections} jitter={StatJitterDetections}";

    internal PlayerState GetState(int slot) => _players[slot];

    public void ResetAll()
    {
        StatUntrustedDetections = 0;
        StatSpinDetections = 0;
        StatJitterDetections = 0;
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
        var state = _players[slot];
        bool isAirborne = (pawn.Flags & 1u) == 0;

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
                        if (!VerifyAngles(slot, player, playerName, entry.ViewAngles, "input_history viewangles", maxPitch: 95.0f))
                        {
                            break;
                        }
                    }
                }
            }
        }
    }

    internal bool RecordAngleSample(
        int slot,
        int serverTick,
        int cmdNum,
        QAngle angles,
        bool isShot = false,
        IPlayer? player = null,
        string playerName = "TestPlayer",
        bool isAirborne = false,
        bool hasMouseInput = true)
    {
        if (slot < 0 || slot > MaxPlayers || !_config.Enabled)
        {
            return false;
        }

        if (_config.EnableUntrustedCheck && !VerifyAngles(slot, player, playerName, angles, "setup_move"))
        {
            return true;
        }

        var state = _players[slot];
        state.LastCommandNumber = cmdNum;
        state.LastServerTick = serverTick;

        state.PushSample(new AngleSample
        {
            ServerTick = serverTick,
            CommandNumber = cmdNum,
            Angles = angles,
            IsShot = isShot,
            HasMouseInput = hasMouseInput
        });

        return _config.EnableSpinbotCheck && EvaluateMotion(slot, player, playerName);
    }

    private bool VerifyAngles(int slot, IPlayer? player, string playerName, QAngle angles, string context, float maxPitch = 89.5f)
    {
        if (!AimMath.IsFinite(angles))
        {
            FlagUntrusted(slot, player, playerName, $"Non-finite {context} (X={angles.X}, Y={angles.Y}, Z={angles.Z})");
            return false;
        }

        if (angles.X < -maxPitch || angles.X > maxPitch)
        {
            FlagUntrusted(slot, player, playerName, $"Extreme pitch out of bounds ({angles.X:F2}° in {context})");
            return false;
        }

        if (MathF.Abs(angles.Z) > 50.0f)
        {
            FlagUntrusted(slot, player, playerName, $"Extreme roll angle ({angles.Z:F2}° in {context})");
            return false;
        }

        return true;
    }

    private void FlagUntrusted(int slot, IPlayer? player, string playerName, string reason)
    {
        var state = _players[slot];
        DateTime now = DateTime.UtcNow;
        state.UntrustedDetections++;

        bool isDeterministicNonFinite = reason.Contains("Non-finite", StringComparison.OrdinalIgnoreCase);
        if ((isDeterministicNonFinite || state.UntrustedDetections >= 2) && (now - state.LastUntrustedAlertTime).TotalSeconds >= 60.0)
        {
            state.LastUntrustedAlertTime = now;
            state.UntrustedDetections = 0;
            StatUntrustedDetections++;

            string details = $"Anti-Untrusted exploit detected: {reason}. (#{state.UntrustedDetections})";
            _logger?.LogWarning("[AegisAC] [AntiAim] {Name} {Details}", playerName, details);
            _onViolation?.Invoke(player, "Untrusted", details, _config.Punishment);
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

}
