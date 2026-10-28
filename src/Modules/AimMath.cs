/**
 * =============================================================================
 * Aegis Anti-Cheat (AegisAC) - Module: AimMath
 *
 * Copyright (C) 2026 swisser & sevon
 * Authors:
 *   - swisser (https://github.com/ressiws)
 *   - sevon   (https://github.com/victor201202)
 *
 * Description:
 *   Mathematical and geometric utilities for angle transformations,
 *   vector forward projections, angular distance, and trajectory calculations.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at:
 *     http://www.apache.org/licenses/LICENSE-2.0
 * =============================================================================
 */

using System;
using System.Runtime.CompilerServices;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;

namespace AegisAC;

internal sealed class SyntheticPlayerState
{
	public bool PawnValid { get; set; } = true;
	public Vector Origin { get; set; }
	public Vector EyePosition { get; set; }
	public QAngle EyeAngles { get; set; }
	public int TeamNum { get; set; } = 2;
	public bool Alive { get; set; } = true;
	public bool Crouched { get; set; }
	public bool Airborne { get; set; }
	public bool Scoped { get; set; }
	public uint Ping { get; set; } = 20u;
	public string PlayerName { get; set; } = "Player";
}

internal static class PlayerSnapshotProvider
{
	private static readonly ConditionalWeakTable<IPlayer, SyntheticPlayerState> SyntheticPlayers = new();

	public static void Register(IPlayer player, SyntheticPlayerState state)
	{
		SyntheticPlayers.AddOrUpdate(player, state);
	}

	public static bool HasValidPawn(IPlayer? player)
	{
		if (player == null)
			return false;
		if (player.PlayerPawn is { IsValid: true })
			return true;
		return SyntheticPlayers.TryGetValue(player, out var s) && s.PawnValid;
	}

	public static bool IsAlive(IPlayer? player)
	{
		if (player == null)
			return false;
		if (player.PlayerPawn is { IsValid: true } pawn)
			return pawn.LifeState == 0;
		return SyntheticPlayers.TryGetValue(player, out var s) && s.PawnValid && s.Alive;
	}

	public static int GetTeam(IPlayer? player)
	{
		if (player == null)
			return 0;
		if (player.PlayerPawn is { IsValid: true } pawn)
			return pawn.TeamNum;
		return SyntheticPlayers.TryGetValue(player, out var s) ? s.TeamNum : 0;
	}

	public static uint GetPing(IPlayer? player)
	{
		if (player == null)
			return 0u;
		if (player.Controller != null)
			return player.Controller.Ping;
		return SyntheticPlayers.TryGetValue(player, out var s) ? s.Ping : 0u;
	}

	public static string GetName(IPlayer? player)
	{
		if (player == null)
			return "#null";
		if (player.Controller?.PlayerName is { Length: > 0 } name)
			return name;
		if (SyntheticPlayers.TryGetValue(player, out var s) && !string.IsNullOrEmpty(s.PlayerName))
			return s.PlayerName;
		return $"#{player.PlayerID}";
	}

	public static bool TryGetPawnTransform(
		IPlayer? player,
		out Vector origin,
		out Vector eye,
		out QAngle eyeAngles,
		out int team,
		out bool alive,
		out bool crouched,
		out bool airborne,
		out bool scoped)
	{
		if (player != null && player.PlayerPawn is { IsValid: true } pawn)
		{
			Vector? maybeOrigin = pawn.AbsOrigin;
			if (maybeOrigin.HasValue && AimMath.IsFinite(maybeOrigin.Value))
			{
				origin = maybeOrigin.Value;
				eye = pawn.EyePosition ?? new Vector(origin.X, origin.Y, origin.Z + 64.0f);
				eyeAngles = pawn.EyeAngles;
				team = pawn.TeamNum;
				alive = pawn.LifeState == 0;
				crouched = (eye.Z - origin.Z) < 55.0f || (pawn.Flags & 2u) != 0;
				airborne = (pawn.Flags & 1u) == 0;
				scoped = pawn.IsScoped;
				return true;
			}
		}

		if (player != null && SyntheticPlayers.TryGetValue(player, out var s) && s.PawnValid && AimMath.IsFinite(s.Origin))
		{
			origin = s.Origin;
			eye = s.EyePosition;
			eyeAngles = s.EyeAngles;
			team = s.TeamNum;
			alive = s.Alive;
			crouched = s.Crouched;
			airborne = s.Airborne;
			scoped = s.Scoped;
			return true;
		}

		origin = default;
		eye = default;
		eyeAngles = default;
		team = 0;
		alive = false;
		crouched = false;
		airborne = false;
		scoped = false;
		return false;
	}
}

internal static class AimMath
{
	public const float Deg2Rad = MathF.PI / 180.0f;
	public const float Rad2Deg = 180.0f / MathF.PI;
	public const float Epsilon = 1e-6f;

	public static bool IsFinite(in QAngle a) => float.IsFinite(a.Pitch) && float.IsFinite(a.Yaw) && float.IsFinite(a.Roll);
	public static bool IsFinite(in Vector v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

	public static float NormalizeAngle(float angle)
	{
		if (!float.IsFinite(angle)) return 0.0f;
		float rem = MathF.IEEERemainder(angle, 360.0f);
		if (rem > 180.0f) rem -= 360.0f;
		else if (rem <= -180.0f) rem += 360.0f;
		return rem;
	}

	public static float YawDelta(float fromYaw, float toYaw)
	{
		if (!float.IsFinite(fromYaw) || !float.IsFinite(toYaw)) return float.NaN;
		return NormalizeAngle(toYaw - fromYaw);
	}

	public static float PitchDelta(float fromPitch, float toPitch)
	{
		if (!float.IsFinite(fromPitch) || !float.IsFinite(toPitch)) return float.NaN;
		return toPitch - fromPitch;
	}

	public static Vector Forward(in QAngle a)
	{
		float pitch = a.Pitch * Deg2Rad;
		float yaw = a.Yaw * Deg2Rad;
		float cp = MathF.Cos(pitch);
		return new Vector(cp * MathF.Cos(yaw), cp * MathF.Sin(yaw), -MathF.Sin(pitch));
	}

	public static QAngle Bearing(in Vector eye, in Vector target)
	{
		Vector delta = new(target.X - eye.X, target.Y - eye.Y, target.Z - eye.Z);
		float horizontal = MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
		return new QAngle(
			-MathF.Atan2(delta.Z, horizontal) * Rad2Deg,
			MathF.Atan2(delta.Y, delta.X) * Rad2Deg,
			0.0f);
	}

	public static float Dot(in Vector a, in Vector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

	public static float AngularDistance(in QAngle a, in QAngle b)
	{
		if (!IsFinite(a) || !IsFinite(b)) return float.NaN;
		float dot = Math.Clamp(Dot(Forward(a), Forward(b)), -1.0f, 1.0f);
		return MathF.Acos(dot) * Rad2Deg;
	}

	public static float AimError(in Vector eye, in QAngle angles, in Vector target)
	{
		var dir = target - eye;
		if (!IsFinite(dir) || dir.LengthSquared() < Epsilon) return 0.0f;
		float len = dir.Length();
		dir = new Vector(dir.X / len, dir.Y / len, dir.Z / len);
		float dot = Math.Clamp(Dot(Forward(angles), dir), -1.0f, 1.0f);
		return MathF.Acos(dot) * Rad2Deg;
	}

	private static readonly float[] StandingBodyHeights = [8.0f, 46.0f, 64.0f];
	private static readonly float[] CrouchingBodyHeights = [8.0f, 32.0f, 46.0f];
	public static int BodyPointCount => StandingBodyHeights.Length;
	public static float BodyHeight(int i, bool crouched = false) =>
		crouched ? CrouchingBodyHeights[i] : StandingBodyHeights[i];

	public static float NearestBodyAimError(in Vector eye, in QAngle angles, in Vector feet, bool crouched = false)
	{
		float best = 180.0f;
		var heights = crouched ? CrouchingBodyHeights : StandingBodyHeights;
		for (int i = 0; i < heights.Length; i++)
			best = MathF.Min(best, AimError(eye, angles, new Vector(feet.X, feet.Y, feet.Z + heights[i])));
		return best;
	}

	/// <summary>
	/// Reverse-engineered mathematical baselines from Riot Vanguard &amp; Blizzard Overwatch
	/// adapted for 64-tick server-side CS2 evaluation.
	/// </summary>
	public static class Baselines
	{
		/// <summary>SNAP_THRESHOLD: &gt;= 90 deg in &lt; 16ms (1 tick at 64Hz) -> instant rage aimbot detection.</summary>
		public const float SnapThresholdDegrees = 90.0f;

		/// <summary>MIN_HUMAN_REACTION: 150ms biological simple-reaction floor (~9.6 ticks at 64Hz).</summary>
		public const float MinHumanReactionMs = 150.0f;
		public const int MinHumanReactionTicks = 9;

		/// <summary>MACRO_THRESHOLD: timing variance &lt; 0.01 (normalized seconds^2 / sub-tick cadence variance).</summary>
		public const float MacroVarianceThreshold = 0.01f;

		/// <summary>MIN_CLICK_VARIANCE: &lt; 0.05 mechanical click/trigger cadence variance.</summary>
		public const float MinClickVariance = 0.05f;

		/// <summary>MIN_VARIANCE: &lt; 0.05 bot angular step variance.</summary>
		public const float MinBotVariance = 0.05f;

		/// <summary>PREDICTION_THRESHOLD: &gt; 95% trajectory correlation / aimlock adherence / accuracy.</summary>
		public const float PredictionThreshold = 0.95f;

		/// <summary>RECOIL_THRESHOLD: &lt; 0.80 recoil pitch/yaw residual (i.e. &gt;= 80% recoil compensation).</summary>
		public const float RecoilCompensationThreshold = 0.80f;

		/// <summary>PATTERN_VARIANCE: &lt; 8.157% relative pattern variance across RCS, smooth steps, and strafe/jump patterns.</summary>
		public const float PatternVariancePercent = 8.157f;
		public const float PatternVarianceRatio = 0.08157f;
	}

	/// <summary>
	/// Computes the angular tolerance radius (in degrees) of a target hitbox at a given distance.
	/// Accounts for horizontal bone offsets (head/shoulders/arms) from the central origin axis.
	/// </summary>
	public static float HitboxAngularRadius(float distance, float halfWidthUnits = 9.0f, float minDeg = 1.15f, float maxDeg = 2.75f)
	{
		if (!float.IsFinite(distance) || distance <= Epsilon) return maxDeg;
		float deg = MathF.Atan2(halfWidthUnits, distance) * Rad2Deg;
		return Math.Clamp(deg, minDeg, maxDeg);
	}

	/// <summary>
	/// Zero-allocation 3D slab test checking if a sightline segment [start, end] intersects an active smoke bounding box.
	/// Ported from CS2AC Legacy triggerbot_module.cpp (smokeHalfExtent = 160..320 units).
	/// </summary>
	public static bool SegmentTouchesSmokeBounds(in Vector start, in Vector end, in Vector center, float smokeHalfExtent = 220.0f)
	{
		float minFrac = 0.0f;
		float maxFrac = 1.0f;

		for (int axis = 0; axis < 3; axis++)
		{
			float s = axis == 0 ? start.X : axis == 1 ? start.Y : start.Z;
			float e = axis == 0 ? end.X : axis == 1 ? end.Y : end.Z;
			float c = axis == 0 ? center.X : axis == 1 ? center.Y : center.Z;

			float delta = e - s;
			float minBound = c - smokeHalfExtent;
			float maxBound = c + smokeHalfExtent;

			if (MathF.Abs(delta) < Epsilon)
			{
				if (s < minBound || s > maxBound)
					return false;
				continue;
			}

			float inv = 1.0f / delta;
			float t1 = (minBound - s) * inv;
			float t2 = (maxBound - s) * inv;
			if (t1 > t2)
			{
				(t1, t2) = (t2, t1);
			}

			if (t1 > minFrac) minFrac = t1;
			if (t2 < maxFrac) maxFrac = t2;
			if (minFrac > maxFrac)
				return false;
		}

		return true;
	}
}
