using System;
using System.Numerics;
using SwiftlyS2.Shared.Natives;

namespace AegisAC.Utilities.AntiWallHack;

/// <summary>
/// Predicts eye position when a player peeks around corners, ensuring enemies behind
/// corners are transmitted before the peeker gains line-of-sight to eliminate visual pop-in.
/// </summary>
internal static class CornerPredictor
{
	public static Vector3 CalculateLead(Vector3 velocity, float yaw, ulong buttons, float seconds, float maxDistance)
	{
		if (!float.IsFinite(velocity.X) || !float.IsFinite(velocity.Y) || !float.IsFinite(velocity.Z)
			|| !float.IsFinite(yaw) || !float.IsFinite(seconds) || !float.IsFinite(maxDistance)
			|| seconds <= 0f || maxDistance <= 0f)
		{
			return Vector3.Zero;
		}

		seconds = Math.Min(seconds, 0.20f);
		maxDistance = Math.Min(maxDistance, 64.0f);
		Vector3 lead = velocity * seconds;

		// Input buttons: IN_FORWARD(8), IN_BACK(16), IN_MOVERIGHT(1024 / 0x400), IN_MOVELEFT(512 / 0x200)
		int forward = ((buttons & 8UL) != 0 ? 1 : 0) - ((buttons & 16UL) != 0 ? 1 : 0);
		int side = ((buttons & 1024UL) != 0 ? 1 : 0) - ((buttons & 512UL) != 0 ? 1 : 0);

		if (forward != 0 || side != 0)
		{
			float angleRad = yaw * (MathF.PI / 180.0f);
			Vector2 dir = Vector2.Normalize(new Vector2(
				MathF.Cos(angleRad) * forward + MathF.Sin(angleRad) * side,
				MathF.Sin(angleRad) * forward - MathF.Cos(angleRad) * side));

			float currentDistance = Math.Min(maxDistance, Math.Max(Math.Min(16.0f, maxDistance), new Vector2(lead.X, lead.Y).Length()));
			lead.X = dir.X * currentDistance;
			lead.Y = dir.Y * currentDistance;
		}

		float totalLen = lead.Length();
		if (!float.IsFinite(totalLen))
		{
			return Vector3.Zero;
		}

		if (totalLen > maxDistance)
		{
			lead *= (maxDistance / totalLen);
		}

		return lead;
	}

	public static float ClipFraction(float distance, float fraction, bool startInSolid)
	{
		if (!float.IsFinite(distance) || distance <= 0f || startInSolid || !float.IsFinite(fraction) || fraction < 0f || fraction > 1f)
		{
			return 0f;
		}

		return fraction >= 1.0f ? 1.0f : Math.Max(0f, fraction - (2.0f / distance));
	}
}
