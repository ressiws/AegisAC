using System;
using System.Numerics;
using System.Runtime.InteropServices;
using AegisAC.Modules;
using AegisAC.Utilities;
using AegisAC.Utilities.AntiWallHack;
using Xunit;

namespace AegisAC.Tests;

public class AntiWallHackTests
{
	[Fact]
	public void CornerPredictor_CalculateLead_ZeroVelocity_ReturnsZero()
	{
		var lead = CornerPredictor.CalculateLead(Vector3.Zero, 0f, 0, 0.12f, 32f);
		Assert.Equal(Vector3.Zero, lead);
	}

	[Fact]
	public void CornerPredictor_CalculateLead_ClampsToMaxDistance()
	{
		// Moving fast at 1000 units/s forward
		var velocity = new Vector3(1000f, 0f, 0f);
		var lead = CornerPredictor.CalculateLead(velocity, 0f, 0, 0.5f, 32f);

		float length = lead.Length();
		Assert.True(length <= 32.01f, $"Lead length {length} exceeded max distance 32.0");
	}

	[Fact]
	public void CornerPredictor_CalculateLead_AppliesButtonDirection()
	{
		// Holding IN_FORWARD (bit 3) with yaw 0 should project along +X
		const ulong IN_FORWARD = 1UL << 3;
		var lead = CornerPredictor.CalculateLead(Vector3.Zero, 0f, IN_FORWARD, 0.12f, 32f);

		Assert.True(lead.X > 0f, "Lead should have positive X forward component");
		Assert.Equal(0f, lead.Y, precision: 3);
	}

	[Fact]
	public void CornerPredictor_ClipFraction_HandlesSolidAndInfinity()
	{
		Assert.Equal(0f, CornerPredictor.ClipFraction(10f, 0.5f, startInSolid: true));
		Assert.Equal(0f, CornerPredictor.ClipFraction(10f, float.NaN, startInSolid: false));
		Assert.Equal(0f, CornerPredictor.ClipFraction(10f, float.PositiveInfinity, startInSolid: false));
		Assert.Equal(0f, CornerPredictor.ClipFraction(10f, -0.1f, startInSolid: false));
	}

	[Fact]
	public void CornerPredictor_ClipFraction_AppliesBackoff()
	{
		float scale = CornerPredictor.ClipFraction(20f, 1.0f, startInSolid: false);
		Assert.True(scale > 0.8f && scale <= 1.0f, $"Clip scale was {scale}");
	}

	[Fact]
	public unsafe void TransmitHook_WithholdEntity_ManipulatesMasksCorrectly()
	{
		const int entityIndex = 45;
		int word = entityIndex >> 5;
		uint bit = 1u << (entityIndex & 31);

		uint* primary = stackalloc uint[16];
		uint* suppressed = stackalloc uint[16];

		// Set entity bit in primary transmission mask
		primary[word] = bit;
		suppressed[word] = 0;

		bool withheld = TransmitHook.WithholdEntity(primary, suppressed, entityIndex);
		Assert.True(withheld, "WithholdEntity should return true for valid entity present in primary mask");

		// Primary mask bit must now be cleared
		Assert.Equal(0u, primary[word] & bit);
		// Suppressed mask bit must now be set
		Assert.Equal(bit, suppressed[word] & bit);

		// Second call for already cleared bit should return false
		bool secondCall = TransmitHook.WithholdEntity(primary, suppressed, entityIndex);
		Assert.False(secondCall, "Second withhold call should return false when primary bit is already cleared");
	}

	[Fact]
	public unsafe void TransmitHook_WithholdEntity_RejectsInvalidIndices()
	{
		uint* primary = stackalloc uint[16];
		uint* suppressed = stackalloc uint[16];

		Assert.False(TransmitHook.WithholdEntity(primary, suppressed, 0));
		Assert.False(TransmitHook.WithholdEntity(primary, suppressed, -5));
		Assert.False(TransmitHook.WithholdEntity(primary, suppressed, TransmitHook.MaxEntityCount + 100));
		Assert.False(TransmitHook.WithholdEntity(null, suppressed, 10));
		Assert.False(TransmitHook.WithholdEntity(primary, null, 10));
	}

	[Fact]
	public void PairVisibilityTracker_GracePeriod_PreventsPrematureOcclusion()
	{
		var tracker = new PairVisibilityTracker();
		tracker.Reset();

		int viewer = 1;
		int target = 2;
		int graceTicks = 12;

		// Tick 100: Both players see each other
		tracker.BeginTick();
		Assert.True(tracker.NeedsQuery(viewer, target, 100));
		tracker.CompleteQuery(viewer, target, 100, graceTicks, OcclusionResult.Visible);

		// Deadline for viewer and target should be 100 + 12 = 112
		// Tick 105: Target runs behind a wall
		tracker.BeginTick();
		// In tick 105 <= 112, grace period holds the state
		Assert.False(tracker.NeedsQuery(viewer, target, 105));
		Assert.False(tracker.IsHidden(viewer, target, 105), "Player should remain visible during grace ticks");

		// Tick 115: Grace ticks expired (115 > 112)
		tracker.BeginTick();
		Assert.True(tracker.NeedsQuery(viewer, target, 115));
		tracker.CompleteQuery(viewer, target, 115, graceTicks, OcclusionResult.Hidden);

		// Now enemy should be hidden
		Assert.True(tracker.IsHidden(viewer, target, 115), "Player should be hidden after grace expiration");
	}

	[Fact]
	public void PairVisibilityTracker_Invalidate_ResetsQueryAndSetsDeadline()
	{
		var tracker = new PairVisibilityTracker();
		tracker.Reset();

		int viewer = 3;
		int target = 4;
		int graceTicks = 12;

		tracker.Invalidate(viewer, 200, graceTicks);

		// NeedsQuery is false during immediate invalidation grace period
		Assert.False(tracker.NeedsQuery(viewer, target, 205));
		Assert.False(tracker.IsHidden(viewer, target, 205));

		// After grace period passes, query is needed again
		tracker.BeginTick();
		Assert.True(tracker.NeedsQuery(viewer, target, 220));
	}

	[Fact]
	public void SmokeOcclusionTracker_RayThroughSmoke_IsBlocked()
	{
		var tracker = new SmokeOcclusionTracker();
		var smokeCenter = new Vector3(500f, 500f, 0f);
		tracker.OnSmokeDetonate(smokeCenter, 10.0f);

		// Active smoke at time 15.0s (age = 5.0s, fully formed)
		var rayStart = new Vector3(0f, 500f, 15f);
		var rayEnd = new Vector3(1000f, 500f, 15f);

		bool blocked = tracker.IsRayBlockedBySmoke(rayStart, rayEnd, 15.0f, 145.0f);
		Assert.True(blocked, "Ray directly passing through smoke center should be occluded");
	}

	[Fact]
	public void SmokeOcclusionTracker_RayFarAway_IsNotBlocked()
	{
		var tracker = new SmokeOcclusionTracker();
		var smokeCenter = new Vector3(500f, 500f, 0f);
		tracker.OnSmokeDetonate(smokeCenter, 10.0f);

		// Ray 300 units away from smoke
		var rayStart = new Vector3(0f, 900f, 15f);
		var rayEnd = new Vector3(1000f, 900f, 15f);

		bool blocked = tracker.IsRayBlockedBySmoke(rayStart, rayEnd, 15.0f, 145.0f);
		Assert.False(blocked, "Ray passing outside smoke radius should not be occluded");
	}

	[Fact]
	public void SmokeOcclusionTracker_HeGrenade_DissipatesSmokeTemporarily()
	{
		var tracker = new SmokeOcclusionTracker();
		var smokeCenter = new Vector3(500f, 500f, 0f);
		tracker.OnSmokeDetonate(smokeCenter, 10.0f);

		var rayStart = new Vector3(0f, 500f, 15f);
		var rayEnd = new Vector3(1000f, 500f, 15f);

		// HE grenade detonates inside the smoke at 14.0s
		tracker.OnHeDetonate(smokeCenter + new Vector3(10f, 0f, 0f), 14.0f);

		// During the 3s dissipation window (e.g. 15.0s), smoke is blown open
		bool blockedWhileDissipated = tracker.IsRayBlockedBySmoke(rayStart, rayEnd, 15.0f, 145.0f);
		Assert.False(blockedWhileDissipated, "Smoke cleared by HE grenade should not block vision during dissipation");

		// After dissipation window expires (e.g. 17.5s), smoke should seal back
		bool blockedAfterWindow = tracker.IsRayBlockedBySmoke(rayStart, rayEnd, 17.5f, 145.0f);
		Assert.True(blockedAfterWindow, "Smoke should re-occlude after HE dissipation window");
	}

	[Fact]
	public void SmokeOcclusionTracker_FadingAndExpiredSmoke_DoesNotBlock()
	{
		var tracker = new SmokeOcclusionTracker();
		var smokeCenter = new Vector3(500f, 500f, 0f);
		tracker.OnSmokeDetonate(smokeCenter, 10.0f);

		var rayStart = new Vector3(0f, 500f, 15f);
		var rayEnd = new Vector3(1000f, 500f, 15f);

		// At 10.5s (age 0.5s < 1.2s fade-in), smoke is not yet dense
		Assert.False(tracker.IsRayBlockedBySmoke(rayStart, rayEnd, 10.5f, 145.0f));

		// At 27.0s (age 17.0s > 16.5s fade-out), smoke is dissipated
		Assert.False(tracker.IsRayBlockedBySmoke(rayStart, rayEnd, 27.0f, 145.0f));
	}

	[Fact]
	public void SmokeOcclusionTracker_OnRoundReset_ClearsActiveSmokes()
	{
		var tracker = new SmokeOcclusionTracker();
		tracker.OnSmokeDetonate(new Vector3(100f, 100f, 0f), 5.0f);
		Assert.Equal(1, tracker.ActiveSmokeCount);

		tracker.OnRoundReset();
		Assert.Equal(0, tracker.ActiveSmokeCount);
	}
}
