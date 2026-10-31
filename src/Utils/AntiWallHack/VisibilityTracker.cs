using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;
using AegisAC.Configuration;

namespace AegisAC.Utilities.AntiWallHack;

/// <summary>
/// Snapshot of a player state in the current tick.
/// </summary>
internal sealed class PlayerSnapshot
{
	public IPlayer Player { get; set; } = null!;
	public CCSPlayerPawn Pawn { get; set; } = null!;
	public Vector Origin { get; set; }
	public Vector Eye { get; set; }
	public Vector Mins { get; set; }
	public Vector Maxs { get; set; }
	public byte Team { get; set; }
	public Vector Velocity { get; set; }
	public QAngle Angles { get; set; }
	public ulong Buttons { get; set; }
	public List<CEntityInstance> Entities { get; } = new();
	public bool Complete { get; set; }
}

/// <summary>
/// Tracks pairwise player visibility states, applying grace period smoothing
/// to eliminate pop-in / flickering around corners.
/// </summary>
internal sealed class PairVisibilityTracker
{
	private readonly OcclusionResult[,] _values = new OcclusionResult[64, 64];
	private readonly long[,] _deadlines = new long[64, 64];

	public void BeginTick() => Array.Clear(_values);

	public void Reset()
	{
		Array.Clear(_values);
		Array.Clear(_deadlines);
	}

	public void Invalidate(int slot, int tick, int graceTicks)
	{
		for (int other = 0; other < 64; other++)
		{
			_values[slot, other] = _values[other, slot] = OcclusionResult.Unchecked;
			_deadlines[slot, other] = _deadlines[other, slot] = (long)tick + graceTicks;
		}
	}

	public bool NeedsQuery(int viewer, int target, int tick)
	{
		if (_values[viewer, target] != OcclusionResult.Unchecked)
		{
			return false;
		}

		if (tick <= _deadlines[viewer, target])
		{
			_values[viewer, target] = OcclusionResult.GraceHeld;
			return false;
		}

		return true;
	}

	public void CompleteQuery(int viewer, int target, int tick, int graceTicks, OcclusionResult result)
	{
		_values[viewer, target] = result;

		if (result is OcclusionResult.Visible or OcclusionResult.Unknown)
		{
			_deadlines[viewer, target] = Math.Max(_deadlines[viewer, target], (long)tick + graceTicks);
		}

		if (result is OcclusionResult.Visible)
		{
			_deadlines[target, viewer] = Math.Max(_deadlines[target, viewer], (long)tick + graceTicks);
		}
	}

	public bool IsHidden(int viewer, int target, int tick) =>
		_values[viewer, target] == OcclusionResult.Hidden && tick > _deadlines[viewer, target];
}

/// <summary>
/// Manages player snapshot generation, equipment bundle aggregation, and visibility queries.
/// </summary>
internal sealed class VisibilityTracker
{
	private readonly ISwiftlyCore _core;
	private readonly AntiWallHackConfig _config;
	private readonly OcclusionTracer _tracer;
	private readonly PairVisibilityTracker _pairs = new();

	private readonly PlayerSnapshot?[] _players = new PlayerSnapshot?[64];
	private readonly List<CEntityInstance>[] _hiddenBundles = Enumerable.Range(0, 64).Select(_ => new List<CEntityInstance>()).ToArray();
	private readonly bool[] _evaluated = new bool[64];
	private readonly (ulong Session, uint Pawn, byte Team)[] _identities = new (ulong, uint, byte)[64];

	private int _frameTick = -1;
	private bool _warnedButtons;

	public int HiddenPairsCount { get; private set; }

	public VisibilityTracker(ISwiftlyCore core, AntiWallHackConfig config, OcclusionTracer tracer)
	{
		_core = core;
		_config = config;
		_tracer = tracer;
	}

	public void Reset()
	{
		_frameTick = -1;
		Array.Clear(_players);
		Array.Clear(_identities);
		Array.Clear(_evaluated);
		_pairs.Reset();
		foreach (var list in _hiddenBundles)
		{
			list.Clear();
		}
		_tracer.Reset();
		HiddenPairsCount = 0;
	}

	public void BeginFrame(int tick)
	{
		if (_frameTick == tick)
		{
			return;
		}

		if (tick < _frameTick)
		{
			Reset();
		}

		_frameTick = tick;
		Array.Clear(_players);
		Array.Clear(_evaluated);
		foreach (var list in _hiddenBundles)
		{
			list.Clear();
		}

		_pairs.BeginTick();
		_tracer.BeginTick();
		HiddenPairsCount = 0;

		foreach (var player in _core.PlayerManager.GetAllValidPlayers())
		{
			int slot = player.PlayerID;
			var pawn = player.PlayerPawn;

			if (slot is < 0 or >= 64 || !IsAlive(player, pawn) || pawn == null || pawn.TeamNum is not (2 or 3))
			{
				continue;
			}

			if (pawn.AbsOrigin is not Vector origin || pawn.EyePosition is not Vector eye || pawn.Collision is not { } bounds || pawn.Identity is not { } identity)
			{
				continue;
			}

			if (!IsFinite(origin) || !IsFinite(eye) || !IsFinite(bounds.Mins) || !IsFinite(bounds.Maxs) || !IsFinite(pawn.AbsVelocity)
				|| !float.IsFinite(pawn.EyeAngles.Pitch) || !float.IsFinite(pawn.EyeAngles.Yaw) || !float.IsFinite(pawn.EyeAngles.Roll)
				|| bounds.Mins.X >= bounds.Maxs.X || bounds.Mins.Y >= bounds.Maxs.Y || bounds.Mins.Z >= bounds.Maxs.Z)
			{
				continue;
			}

			var currentIdentity = (player.SessionId, identity.EntityHandle.Raw, pawn.TeamNum);
			if (_identities[slot] != currentIdentity)
			{
				_identities[slot] = currentIdentity;
				_pairs.Invalidate(slot, tick, _config.VisibleGraceTicks);
			}

			var snapshot = new PlayerSnapshot
			{
				Player = player,
				Pawn = pawn,
				Origin = origin,
				Eye = eye,
				Mins = bounds.Mins,
				Maxs = bounds.Maxs,
				Team = pawn.TeamNum,
				Velocity = pawn.AbsVelocity,
				Angles = pawn.EyeAngles,
				Buttons = ReadButtons(pawn)
			};

			snapshot.Entities.Add(pawn);

			var seen = new HashSet<nint>();
			var scene = pawn.CBodyComponent?.SceneNode;
			snapshot.Complete = scene != null && CollectChildren(scene.Child, snapshot.Entities, seen, 0);

			if (pawn.WeaponServices is { } weaponServices)
			{
				foreach (var handle in weaponServices.MyWeapons)
				{
					if (handle.Value is { IsValid: true } weapon)
					{
						snapshot.Entities.Add(weapon);
					}
				}
			}
			else
			{
				snapshot.Complete = false;
			}

			_players[slot] = snapshot;
		}

		CollectOwnedEquipment();

		for (int i = 0; i < 64; i++)
		{
			if (_players[i] == null && _identities[i] != default)
			{
				_pairs.Invalidate(i, tick, _config.VisibleGraceTicks);
				_identities[i] = default;
			}
		}
	}

	public void EvaluateViewer(int slot, int tick)
	{
		var viewer = _players[slot];
		if (!IsEligibleViewer(viewer) || _evaluated[slot])
		{
			return;
		}

		_evaluated[slot] = true;

		for (int offset = 0; offset < 64; offset++)
		{
			int other = (offset + (tick & 63) + slot) & 63;
			var target = _players[other];

			if (target == null || other == slot || target.Team == viewer!.Team)
			{
				continue;
			}

			if (_pairs.NeedsQuery(slot, other, tick))
			{
				var result = target.Complete && IsAlive(target.Player, target.Pawn)
					? _tracer.CheckVisibility(viewer!, target, _config.MaxTracesPerTick)
					: OcclusionResult.Unknown;

				_pairs.CompleteQuery(slot, other, tick, _config.VisibleGraceTicks, result);
			}
		}
	}

	public List<CEntityInstance>? GetHiddenEntities(int slot, int tick)
	{
		var viewer = _players[slot];
		if (!IsEligibleViewer(viewer) || !_evaluated[slot])
		{
			return null;
		}

		var result = _hiddenBundles[slot];
		result.Clear();

		for (int other = 0; other < 64; other++)
		{
			var target = _players[other];
			if (target == null || target.Team == viewer!.Team || !_pairs.IsHidden(slot, other, tick)
				|| !IsAlive(target.Player, target.Pawn)
				|| target.Entities.Any(e => !e.IsValid || e.Index == 0 || e.Index >= TransmitHook.MaxEntityCount))
			{
				continue;
			}

			result.AddRange(target.Entities);
			HiddenPairsCount++;
		}

		return result;
	}

	private void CollectOwnedEquipment()
	{
		var owners = new Dictionary<uint, PlayerSnapshot>();
		foreach (var snapshot in _players)
		{
			if (snapshot != null)
			{
				owners[snapshot.Pawn.Index] = snapshot;
			}
		}

		foreach (var entity in _core.EntitySystem.GetAllEntitiesByClass<CBaseEntity>())
		{
			if (entity is not CEconEntity || !entity.IsValid || !entity.OwnerEntity.IsValid)
			{
				continue;
			}

			if (owners.TryGetValue(entity.OwnerEntity.EntityIndex, out var owner) && entity != owner.Pawn)
			{
				owner.Entities.Add(entity);
			}
		}
	}

	private static bool CollectChildren(CGameSceneNode? node, List<CEntityInstance> entities, HashSet<nint> seen, int depth)
	{
		while (node != null)
		{
			if (depth > 16 || seen.Count >= 128 || !seen.Add(node.Address))
			{
				return false;
			}

			if (node.Owner is not { IsValid: true } entity)
			{
				return false;
			}

			if (entity is CCSPlayerPawn or CCSPlayerController)
			{
				return false;
			}

			entities.Add(entity);

			if (!CollectChildren(node.Child, entities, seen, depth + 1))
			{
				return false;
			}

			node = node.NextSibling;
		}

		return true;
	}

	private static bool IsAlive(IPlayer player, CCSPlayerPawn? pawn) =>
		player.IsValid && pawn is { IsValid: true } && pawn.Health > 0 && pawn.LifeState == 0 && !pawn.IsSpawning
		&& player.Controller is { IsValid: true } controller
		&& pawn.Controller.Value?.Address == controller.Address
		&& controller.PlayerPawn.Value?.Address == pawn.Address;

	private static bool IsEligibleViewer(PlayerSnapshot? viewer) =>
		viewer != null && IsAlive(viewer.Player, viewer.Pawn)
		&& !viewer.Player.IsFakeClient
		&& !viewer.Player.ServerSideClient.IsHLTV
		&& viewer.Player.ServerSideClient.DeltaTick >= 0;

	private static bool IsFinite(Vector v) =>
		float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

	private ulong ReadButtons(CCSPlayerPawn pawn)
	{
		if (!_config.CornerPrediction)
		{
			return 0;
		}

		try
		{
			if (pawn.MovementServices?.Buttons is { } buttons)
			{
				return buttons.ButtonStates[0];
			}
		}
		catch
		{
			// Fall back to velocity only
		}

		if (!_warnedButtons)
		{
			_warnedButtons = true;
			_core.Logger.LogWarning("[AegisAC-AWH] Movement buttons unavailable; corner prediction using velocity only.");
		}

		return 0;
	}
}
