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

using SwiftlyS2.Shared.Natives;

namespace AegisAC;

internal static class AimMath
{
	private const float Deg2Rad = MathF.PI / 180.0f;
	private const float Rad2Deg = 180.0f / MathF.PI;
	public const float Epsilon = 1e-6f;

	public static bool IsFinite(in QAngle a) => float.IsFinite(a.Pitch) && float.IsFinite(a.Yaw) && float.IsFinite(a.Roll);
	public static bool IsFinite(in Vector v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

	public static Vector Forward(in QAngle a)
	{
		float pitch = a.Pitch * Deg2Rad;
		float yaw = a.Yaw * Deg2Rad;
		float cp = MathF.Cos(pitch);
		return new Vector(cp * MathF.Cos(yaw), cp * MathF.Sin(yaw), -MathF.Sin(pitch));
	}

	private static float Dot(in Vector a, in Vector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

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

	private static readonly float[] BodyHeights = { 8.0f, 46.0f, 64.0f };
	public static int BodyPointCount => BodyHeights.Length;
	public static float BodyHeight(int i) => BodyHeights[i];

	public static float NearestBodyAimError(in Vector eye, in QAngle angles, in Vector feet)
	{
		float best = 180.0f;
		foreach (float h in BodyHeights)
			best = MathF.Min(best, AimError(eye, angles, new Vector(feet.X, feet.Y, feet.Z + h)));
		return best;
	}
}
