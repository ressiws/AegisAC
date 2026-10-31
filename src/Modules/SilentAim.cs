using System;
using SwiftlyS2.Shared.Natives;
using AegisAC.Configuration;

namespace AegisAC.Modules;

/// <summary>
/// Dedicated Server-Side Silent Aim &amp; Snap-Return Detector.
/// Separates sub-tick silent aim (BaseAngles vs Attack1StartHistoryIndex angles) and post-shot SnapReturn
/// from standard Aimbot trajectory convergence:
/// - Sub-Tick Silent Aim: compares base command camera angles against sub-tick attack history angles,
///   accounting for CMsgTEFireBullets weapon inaccuracy/spread and CSubtickMoveStep mouse travel.
/// - Adjacent Path Excess (CS2AC Legacy triangle-inequality check): rejects false positives when the
///   attack angle lies along the geodesic camera sweep from previous BaseAngles to current BaseAngles.
/// - Snap-Return Silent Aim: catches 1-tick silent snaps onto a target that immediately return to the
///   prior camera direction on the next command.
/// </summary>
internal sealed class SilentAimDetector
{
    public const int MaxPlayers = 64;

    internal readonly record struct SubtickSilentResult(
        bool Triggered,
        bool IsBlatant,
        float DesyncDegrees,
        float BaseErrorDegrees,
        float AttackErrorDegrees,
        float AccumulatedScore);

    internal readonly record struct SnapReturnResult(
        bool Triggered,
        bool IsBlatant,
        float SnapDegrees,
        float SurroundingDegrees,
        float BeforeErrorDegrees,
        float AfterErrorDegrees);

    private SilentAimConfig _config;
    private readonly float[] _scores = new float[MaxPlayers + 1];

    public long StatSubtickEvaluations { get; private set; }
    public long StatSubtickTriggers { get; private set; }
    public long StatSnapReturnEvaluations { get; private set; }
    public long StatSnapReturnTriggers { get; private set; }

    public SilentAimDetector(SilentAimConfig? config = null)
    {
        _config = config ?? new SilentAimConfig();
    }

    public SilentAimConfig Config => _config;

    public void UpdateConfig(SilentAimConfig config) => _config = config;

    public void Reset()
    {
        Array.Clear(_scores);
        StatSubtickEvaluations = 0;
        StatSubtickTriggers = 0;
        StatSnapReturnEvaluations = 0;
        StatSnapReturnTriggers = 0;
    }

    public void OnClientDisconnect(int slot)
    {
        if (slot >= 0 && slot <= MaxPlayers)
        {
            _scores[slot] = 0f;
        }
    }

    public float GetScore(int slot) => slot >= 0 && slot <= MaxPlayers ? _scores[slot] : 0f;

    public string Stats() =>
        $"enabled={_config.Enabled} subtickEvals={StatSubtickEvaluations} subtickHits={StatSubtickTriggers} snapReturnEvals={StatSnapReturnEvaluations} snapReturnHits={StatSnapReturnTriggers}";

    /// <summary>
    /// Evaluates sub-tick Silent Aim divergence between base viewangles and attack history angles.
    /// </summary>
    public SubtickSilentResult EvaluateSubtickSilentAim(
        int attackerId,
        QAngle baseAngles,
        QAngle attackAngles,
        QAngle? prevBaseAngles,
        float subtickPitchTravel,
        float subtickYawTravel,
        bool hasFireBulletsSpread,
        float fireBulletsInaccuracy,
        float fireBulletsSpread,
        in Vector attackerEye,
        bool attackerAirborne,
        in Vector targetOrigin,
        bool targetCrouched,
        float attackError,
        float targetAngularRadius)
    {
        if (!_config.Enabled || !_config.EnableSubtickSilentAim || attackerId < 0 || attackerId > MaxPlayers)
        {
            return default;
        }

        StatSubtickEvaluations++;

        float desync = AimMath.AngularDistance(baseAngles, attackAngles);
        float baseError = AimMath.NearestBodyAimError(attackerEye, baseAngles, targetOrigin, targetCrouched);

        float spreadAllowance = 1.25f;
        if (hasFireBulletsSpread)
        {
            float totalSpread = MathF.Max(0f, fireBulletsInaccuracy + fireBulletsSpread);
            spreadAllowance = MathF.Max(1.0f, MathF.Atan(totalSpread) * (180.0f / MathF.PI));
        }

        float subtickTravel = MathF.Sqrt(
            subtickPitchTravel * subtickPitchTravel +
            subtickYawTravel * subtickYawTravel);

        float totalAllowance = spreadAllowance + subtickTravel + 1.25f;
        float baseCameraStep = prevBaseAngles.HasValue
            ? AimMath.AngularDistance(prevBaseAngles.Value, baseAngles)
            : 0f;

        // Triangle-inequality adjacent path check (ported from CS2AC Legacy silent_aim_module.cpp):
        // If the attack angle lies directly along the geodesic path from previous BaseAngles to current BaseAngles,
        // the player swept across the target during the sub-tick interval and clicked mid-sweep.
        bool adjacentPathSupported = false;
        if (prevBaseAngles.HasValue && baseCameraStep > 0.5f)
        {
            float prevToAttack = AimMath.AngularDistance(prevBaseAngles.Value, attackAngles);
            float adjacentPathExcess = MathF.Max(0f, prevToAttack + desync - baseCameraStep);
            adjacentPathSupported = float.IsFinite(adjacentPathExcess) && adjacentPathExcess <= 1.0f;
        }

        float threshold = MathF.Max(1.0f, _config.ScoreThreshold);
        if (!adjacentPathSupported
            && desync > totalAllowance
            && attackError <= targetAngularRadius + 1.0f
            && baseError >= attackError + MathF.Max(2.0f, desync * 0.55f)
            && baseCameraStep <= MathF.Max(4.0f, desync * 0.35f))
        {
            float excess = desync - totalAllowance;
            float silentPoints = excess >= 20.0f
                ? MathF.Max(2.5f, threshold * 0.5f) // Blatant sub-tick rage silent aim: requires 2 blatant shots for threshold
                : (excess >= 6.0f ? (attackerAirborne ? 5.0f : 4.0f) : (attackerAirborne ? 3.5f : 2.5f));

            _scores[attackerId] = MathF.Min(20f, _scores[attackerId] + silentPoints);
            if (_scores[attackerId] >= threshold)
            {
                StatSubtickTriggers++;
                return new SubtickSilentResult(
                    Triggered: true,
                    IsBlatant: excess >= 20.0f,
                    DesyncDegrees: desync,
                    BaseErrorDegrees: baseError,
                    AttackErrorDegrees: attackError,
                    AccumulatedScore: _scores[attackerId]);
            }
        }
        else if (desync <= spreadAllowance + 0.5f)
        {
            _scores[attackerId] = MathF.Max(0f, _scores[attackerId] - 1.5f);
        }

        return new SubtickSilentResult(
            Triggered: false,
            IsBlatant: false,
            DesyncDegrees: desync,
            BaseErrorDegrees: baseError,
            AttackErrorDegrees: attackError,
            AccumulatedScore: _scores[attackerId]);
    }

    /// <summary>
    /// Evaluates single-command SnapReturn silent aim (flicking onto the target on the shot command and immediately
    /// returning to the prior view angle on the subsequent command).
    /// </summary>
    public SnapReturnResult EvaluateSnapReturn(
        QAngle previousAngles,
        QAngle shotAngles,
        QAngle nextAngles,
        in Vector previousEye,
        in Vector targetOrigin,
        bool targetCrouched,
        float shotError)
    {
        if (!_config.Enabled || !_config.EnableSnapReturn)
        {
            return default;
        }

        StatSnapReturnEvaluations++;

        float surrounding = AimMath.AngularDistance(previousAngles, nextAngles);
        float snap = AimMath.AngularDistance(previousAngles, shotAngles);

        if (float.IsFinite(surrounding)
            && float.IsFinite(snap)
            && surrounding < _config.SnapReturnMaximumSurroundingAngle
            && snap >= _config.SnapReturnMinimumAngle
            && snap > surrounding * 4.0f)
        {
            StatSnapReturnTriggers++;
            float beforeError = AimMath.NearestBodyAimError(previousEye, previousAngles, targetOrigin, targetCrouched);
            bool isBlatant = snap >= 25.0f && surrounding < 1.0f;
            return new SnapReturnResult(
                Triggered: true,
                IsBlatant: isBlatant,
                SnapDegrees: snap,
                SurroundingDegrees: surrounding,
                BeforeErrorDegrees: beforeError,
                AfterErrorDegrees: shotError);
        }

        return default;
    }
}
