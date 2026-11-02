using System;
using System.Collections.Generic;
using System.Numerics;

namespace AegisAC.Utilities.AntiWallHack;

/// <summary>
/// Tracks active volumetric smoke grenades and HE grenade blast dissipations
/// to occlude enemy players through smoke clouds, blocking Smoke-ESP and No-Smoke cheats.
/// </summary>
internal sealed class SmokeOcclusionTracker
{
	private const float SmokeLifetimeSeconds = 18.5f;
	private const float SmokeFadeInSeconds = 1.2f;
	private const float SmokeFadeOutSeconds = 16.5f;
	private const float HeDissipationDuration = 3.0f;
	private const float HeClearRadiusSq = 250f * 250f;

	private sealed class ActiveSmoke
	{
		public Vector3 Center { get; set; }
		public float DetonateTime { get; set; }
		public float DissipatedUntil { get; set; }
	}

	private readonly List<ActiveSmoke> _smokes = new(16);
	private readonly object _lock = new();

	public int ActiveSmokeCount
	{
		get
		{
			lock (_lock)
			{
				return _smokes.Count;
			}
		}
	}

	public void OnSmokeDetonate(Vector3 center, float gameTime)
	{
		if (!float.IsFinite(center.X) || !float.IsFinite(center.Y) || !float.IsFinite(center.Z) || !float.IsFinite(gameTime))
		{
			return;
		}

		lock (_lock)
		{
			// Clean up expired smokes
			_smokes.RemoveAll(s => gameTime - s.DetonateTime > SmokeLifetimeSeconds || gameTime < s.DetonateTime);

			if (_smokes.Count < 32)
			{
				_smokes.Add(new ActiveSmoke
				{
					Center = center + new Vector3(0f, 0f, 15f), // Center slightly above impact
					DetonateTime = gameTime,
					DissipatedUntil = 0f
				});
			}
		}
	}

	public void OnHeDetonate(Vector3 center, float gameTime)
	{
		if (!float.IsFinite(center.X) || !float.IsFinite(center.Y) || !float.IsFinite(center.Z) || !float.IsFinite(gameTime))
		{
			return;
		}

		lock (_lock)
		{
			for (int i = 0; i < _smokes.Count; i++)
			{
				var smoke = _smokes[i];
				if (Vector3.DistanceSquared(center, smoke.Center) <= HeClearRadiusSq)
				{
					// HE grenade blows open / clears smoke cloud for ~3 seconds
					smoke.DissipatedUntil = Math.Max(smoke.DissipatedUntil, gameTime + HeDissipationDuration);
				}
			}
		}
	}

	public void OnRoundReset()
	{
		lock (_lock)
		{
			_smokes.Clear();
		}
	}

	/// <summary>
	/// Tests whether a line segment (ray) between two positions intersects an active, dense smoke grenade volume.
	/// </summary>
	public bool IsRayBlockedBySmoke(Vector3 start, Vector3 end, float gameTime, float smokeRadius)
	{
		if (!float.IsFinite(start.X) || !float.IsFinite(end.X) || !float.IsFinite(gameTime) || smokeRadius <= 0f)
		{
			return false;
		}

		Vector3 rayVec = end - start;
		float rayLengthSq = rayVec.LengthSquared();
		if (rayLengthSq < 0.001f)
		{
			return false;
		}

		float radiusSq = smokeRadius * smokeRadius;

		lock (_lock)
		{
			for (int i = 0; i < _smokes.Count; i++)
			{
				var smoke = _smokes[i];
				float age = gameTime - smoke.DetonateTime;

				// Smoke must be fully formed and not fading out
				if (age < SmokeFadeInSeconds || age > SmokeFadeOutSeconds)
				{
					continue;
				}

				// Smoke must not be currently dissipated by an HE grenade
				if (gameTime < smoke.DissipatedUntil)
				{
					continue;
				}

				// Check closest distance from smoke center to line segment (start -> end)
				Vector3 toCenter = smoke.Center - start;
				float t = Vector3.Dot(toCenter, rayVec) / rayLengthSq;
				t = Math.Clamp(t, 0f, 1f);

				Vector3 closestPoint = start + t * rayVec;
				float distSq = Vector3.DistanceSquared(closestPoint, smoke.Center);

				if (distSq <= radiusSq)
				{
					return true;
				}
			}
		}

		return false;
	}
}
