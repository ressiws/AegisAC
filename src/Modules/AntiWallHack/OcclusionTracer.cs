using System;
using System.Collections.Generic;
using System.Linq;
using Vector3 = System.Numerics.Vector3;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.SchemaDefinitions;
using SwiftlyS2.Shared.Trace;
using AegisAC.Configuration;

namespace AegisAC.Modules.AntiWallHack;

internal enum OcclusionResult : byte
{
	Unchecked = 0,
	Hidden = 1,
	Visible = 2,
	Unknown = 3,
	GraceHeld = 4
}

/// <summary>
/// Performs multi-sample bounding box and corner-peek ray tracing against solid map geometry.
/// Optimized with sample reordering (testing previous hit rays first) and tick budgets.
/// </summary>
internal sealed class OcclusionTracer
{
	private const float DefaultPadding = 24.0f;
	private readonly ISwiftlyCore _core;
	private readonly AntiWallHackConfig _config;

	private readonly Vector[][] _samplePoints = Enumerable.Range(0, 64).Select(_ => new Vector[12]).ToArray();
	private readonly bool[] _samplesReady = new bool[64];
	private readonly int[,] _preferredSample = new int[64, 64];

	private readonly TraceParams _traceParams = new()
	{
		InteractWith = MaskTrace.Solid,
		InteractExclude = MaskTrace.Player | MaskTrace.Hitbox | MaskTrace.Trigger | MaskTrace.PlayerClip | MaskTrace.NpcClip | MaskTrace.Window | MaskTrace.CarriedObject | MaskTrace.CarriedWeapon,
		ShouldHitEntity = IsSolidOccluder
	};

	public int TracesThisTick { get; private set; }
	public int BudgetSkipsThisTick { get; private set; }

	public OcclusionTracer(ISwiftlyCore core, AntiWallHackConfig config)
	{
		_core = core;
		_config = config;
	}

	private static bool IsSolidOccluder(CEntityInstance entity) =>
		entity.IsValid && entity is not CCSPlayerPawn && entity is not CCSPlayerController && entity is not CEconEntity && entity.DesignerName != "player";

	public void Reset()
	{
		Array.Clear(_preferredSample);
		BeginTick();
	}

	public void BeginTick()
	{
		Array.Clear(_samplesReady);
		TracesThisTick = 0;
		BudgetSkipsThisTick = 0;
	}

	public OcclusionResult CheckVisibility(
		PlayerSnapshot viewer,
		PlayerSnapshot target,
		int maxTracesPerTick)
	{
		int viewerSlot = viewer.Player.PlayerID;
		int targetSlot = target.Player.PlayerID;

		if (!_samplesReady[targetSlot])
		{
			BuildTargetSamples(targetSlot, target);
		}

		var points = _samplePoints[targetSlot];

		// Compute viewer corner-peek lead if enabled
		Vector eyeLead = new();
		if (_config.CornerPrediction && TracesThisTick < maxTracesPerTick)
		{
			Vector3 vel3 = new(viewer.Velocity.X, viewer.Velocity.Y, viewer.Velocity.Z);
			Vector3 lead3 = CornerPredictor.CalculateLead(vel3, viewer.Angles.Yaw, viewer.Buttons, _config.CornerPredictionSeconds, 32.0f);
			if (lead3.Length() > 0.05f)
			{
				_traceParams.EntitiesToIgnore.Clear();
				_traceParams.EntitiesToIgnore.AddRange(viewer.Entities);
				_traceParams.OwnersToIgnore.Clear();
				_traceParams.OwnersToIgnore.Add(viewer.Pawn);

				Vector leadWanted = new(lead3.X, lead3.Y, lead3.Z);
				Vector leadTarget = viewer.Eye + leadWanted;

				TracesThisTick++;
				var leadHit = _core.Trace.TraceShapeLine(viewer.Eye, leadTarget, _traceParams);
				float scale = CornerPredictor.ClipFraction(lead3.Length(), leadHit.Fraction, leadHit.StartInSolid);
				eyeLead = leadWanted * scale;
			}
		}

		_traceParams.EntitiesToIgnore.Clear();
		_traceParams.EntitiesToIgnore.AddRange(viewer.Entities);
		_traceParams.EntitiesToIgnore.AddRange(target.Entities);

		_traceParams.OwnersToIgnore.Clear();
		_traceParams.OwnersToIgnore.Add(viewer.Pawn);
		_traceParams.OwnersToIgnore.Add(target.Pawn);

		int sampleCount = (eyeLead.X * eyeLead.X + eyeLead.Y * eyeLead.Y + eyeLead.Z * eyeLead.Z) > 1.0f ? 24 : 12;
		int preferred = _preferredSample[viewerSlot, targetSlot];
		if (preferred >= sampleCount)
		{
			preferred %= 12;
		}

		// Iterate through points, checking preferred (previously clear) ray first
		for (int order = 0; order < sampleCount; order++)
		{
			int sampleIndex = order == 0 ? preferred : (order <= preferred ? order - 1 : order);
			bool useLead = sampleIndex >= 12;

			if (TracesThisTick >= maxTracesPerTick)
			{
				BudgetSkipsThisTick++;
				return OcclusionResult.Unknown;
			}

			Vector rayStart = viewer.Eye + (useLead ? eyeLead : new Vector());
			Vector rayEnd = points[sampleIndex % 12];

			if (!float.IsFinite(rayStart.X) || !float.IsFinite(rayEnd.X))
			{
				return OcclusionResult.Unknown;
			}

			TracesThisTick++;
			var hit = _core.Trace.TraceShapeLine(rayStart, rayEnd, _traceParams);

			// If clear ray reached target without starting in solid geometry
			if (!hit.StartInSolid && float.IsFinite(hit.Fraction) && hit.Fraction >= 0.99f && hit.Fraction <= 1.0f)
			{
				_preferredSample[viewerSlot, targetSlot] = sampleIndex;
				return OcclusionResult.Visible;
			}

			// If ray hit an entity that should be ignored, mark as uncertain
			if (hit.Entity is { } hitEntity && (!IsSolidOccluder(hitEntity) || _traceParams.EntitiesToIgnore.Any(i => i.Address == hitEntity.Address)))
			{
				return OcclusionResult.Unknown;
			}
		}

		return OcclusionResult.Hidden;
	}

	private void BuildTargetSamples(int slot, PlayerSnapshot target)
	{
		var pts = _samplePoints[slot];
		float pad = DefaultPadding;
		Vector lo = target.Mins - new Vector(pad, pad, 0f);
		Vector hi = target.Maxs + new Vector(pad, pad, pad);

		target.Angles.ToDirectionVectors(out var forward, out _, out _);

		// 0: Eye
		pts[0] = target.Eye;
		// 1: Torso / Center of mass
		pts[1] = target.Origin + (lo + hi) * 0.5f;

		// 2..9: 8 bounding box corners
		for (int i = 0; i < 8; i++)
		{
			pts[i + 2] = target.Origin + new Vector(
				(i & 1) == 0 ? lo.X : hi.X,
				(i & 2) == 0 ? lo.Y : hi.Y,
				(i & 4) == 0 ? lo.Z + 4f : hi.Z);
		}

		// 10: Weapon barrel forward (64 units forward from eye)
		pts[10] = target.Eye + forward * 64.0f;
		// 11: Hip forward (40 units forward from waist)
		pts[11] = target.Origin + new Vector(0f, 0f, 32f) + forward * 40.0f;

		_samplesReady[slot] = true;
	}
}
