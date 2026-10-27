/**
 * =============================================================================
 * Aegis Anti-Cheat (AegisAC) - Module: Triggerbot
 *
 * Copyright (C) 2026 swisser & sevon
 * Authors:
 *   - swisser (https://github.com/ressiws)
 *   - sevon   (https://github.com/victor201202)
 *
 * Description:
 *   Server-side triggerbot detection module analyzing crosshair enemy
 *   acquisition, sub-tick reaction timings, raycast line-of-sight,
 *   and smoke occlusion volumes.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at:
 *     http://www.apache.org/licenses/LICENSE-2.0
 * =============================================================================
 */

using System;
using System.Collections.Generic;
using System.Linq;
using AegisAC.Configuration;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Services;
using SwiftlyS2.Shared.Trace;

namespace AegisAC.Modules;

internal enum TriggerContactMode
{
	AimDriven,    // Aim moved onto target
	TargetDriven  // Held angle / target stepped into crosshair
}

internal sealed class TriggerPendingAttack
{
	public required int CommandNumber { get; init; }
	public required int TargetIndex { get; init; }
	public required int ReactionTicks { get; init; }
	public required TriggerContactMode Mode { get; init; }
}

internal sealed class TriggerIncident
{
	public required DateTime Time { get; init; }
	public required int ReactionTicks { get; init; }
	public required float ReactionMs { get; init; }
	public required int TargetId { get; init; }
	public required string Weapon { get; init; }
	public required int Damage { get; init; }
	public required TriggerContactMode Mode { get; init; }
}

internal sealed class TriggerSmoke
{
	public required Vector Center { get; init; }
	public required DateTime Expires { get; init; }
}

internal sealed class TriggerCommand
{
	public required int CommandNumber { get; init; }
	public required QAngle Angles { get; init; }
	public required bool IsFreshAttack { get; init; }
}

internal sealed class TriggerPlayerState
{
	public int LastCommandNumber = -1;
	public int ActiveTarget = -1;
	public int ContactStartCommand = -1;
	public TriggerContactMode ActiveMode = TriggerContactMode.TargetDriven;
	public QAngle LastAngles;
	public bool LastAnglesValid;
	public readonly int[] LastContactTick = new int[AimbotDetector.MaxPlayers + 1];
	public readonly List<TriggerCommand> Commands = new();
	public readonly List<TriggerPendingAttack> Pending = new();
	public readonly List<TriggerIncident> Incidents = new();

	public TriggerPlayerState()
	{
		Array.Fill(LastContactTick, -1);
	}

	public void Reset()
	{
		LastCommandNumber = -1;
		ActiveTarget = -1;
		ContactStartCommand = -1;
		ActiveMode = TriggerContactMode.TargetDriven;
		LastAnglesValid = false;
		Array.Fill(LastContactTick, -1);
		Commands.Clear();
		Pending.Clear();
		Incidents.Clear();
	}
}

internal sealed class TriggerbotDetector
{
	private const int PendingLimit = 8;
	private const int FreshContactTicks = 64; // ~1 second at 64-tick
	private const float HeldAngleMaximumDegrees = 0.5f;
	private const float TraceLength = 8192.0f;
	private const float SmokeHalfExtent = 320.0f;
	private static readonly TimeSpan EvidenceWindow = TimeSpan.FromMinutes(5);
	private static readonly TimeSpan SmokeLifetime = TimeSpan.FromMilliseconds(22500);

	private static readonly HashSet<string> ExcludedWeapons = new(StringComparer.OrdinalIgnoreCase)
	{
		"revolver", "taser", "knife", "bayonet"
	};

	private readonly TriggerbotConfig _config;
	private readonly ITraceManager _traceManager;
	private readonly Func<int, IPlayer?> _getPlayer;
	private readonly Func<int, int, bool> _areEnemies;
	private readonly Action<IPlayer, string, string> _announce;
	private readonly Action<string> _debug;

	private readonly TriggerPlayerState[] _players = new TriggerPlayerState[AimbotDetector.MaxPlayers + 1];
	private readonly List<TriggerSmoke> _smokes = new();
	private DateTime _smokeStateUnknownUntil = DateTime.MinValue;

	public long StatContactChecks, StatAcquisitions, StatPendingAttacks, StatHurtsEvaluated, StatDetections;

	public TriggerbotDetector(
		TriggerbotConfig config,
		ITraceManager traceManager,
		Func<int, IPlayer?> getPlayer,
		Func<int, int, bool> areEnemies,
		Action<IPlayer, string, string> announce,
		Action<string> debug)
	{
		_config = config;
		_traceManager = traceManager;
		_getPlayer = getPlayer;
		_areEnemies = areEnemies;
		_announce = announce;
		_debug = debug;

		for (int i = 0; i < _players.Length; i++)
			_players[i] = new TriggerPlayerState();
	}

	public string Stats() =>
		$"checks={StatContactChecks} acq={StatAcquisitions} pending={StatPendingAttacks} hurts={StatHurtsEvaluated} detections={StatDetections} smokes={_smokes.Count}";

	public void Reset()
	{
		for (int i = 0; i < _players.Length; i++)
			_players[i].Reset();

		_smokes.Clear();
		_smokeStateUnknownUntil = DateTime.UtcNow + SmokeLifetime;
	}

	public void OnClientDisconnect(int playerId)
	{
		if (playerId >= 1 && playerId < _players.Length)
			_players[playerId].Reset();
	}

	public void OnSmokeDetonate(Vector center)
	{
		if (!AimMath.IsFinite(center))
		{
			_smokeStateUnknownUntil = DateTime.UtcNow + SmokeLifetime;
			return;
		}

		_smokes.Add(new TriggerSmoke { Center = center, Expires = DateTime.UtcNow + SmokeLifetime });
		_debug($"[Triggerbot] Smoke safety volume added at {center.X:F1}, {center.Y:F1}, {center.Z:F1}.");
	}

	public void OnProcessUsercmds(IPlayer player, List<SwiftlyS2.Shared.GameHooks.IUserCmd> cmds)
	{
		if (!_config.Enabled || !AimbotDetector.IsEligibleHuman(player) || cmds.Count == 0)
			return;

		int id = player.PlayerID;
		if (id < 1 || id >= _players.Length)
			return;

		var st = _players[id];

		foreach (var cmd in cmds)
		{
			var pb = cmd.CSGOUserCmd;
			int number = (int)cmd.CommandNumber;

			if (st.Commands.Exists(c => c.CommandNumber == number))
				continue;

			if (!pb.Accessor.HasField("base"))
				continue;

			var b = pb.Base;
			if (!b.Accessor.HasField("viewangles"))
				continue;

			QAngle angles = b.Viewangles;
			bool isFreshAttack = false;
			int idx = pb.Attack1StartHistoryIndex;
			int histCount = pb.InputHistory.Count;

			if (idx >= 0 && idx < histCount)
			{
				var entry = pb.InputHistory.Get(idx);
				if (entry.Accessor.HasField("view_angles"))
				{
					QAngle shotAngles = entry.ViewAngles;
					if (AimMath.IsFinite(shotAngles))
					{
						angles = shotAngles;
						isFreshAttack = true;
					}
				}
			}

			if (!AimMath.IsFinite(angles))
				continue;

			st.Commands.Add(new TriggerCommand
			{
				CommandNumber = number,
				Angles = angles,
				IsFreshAttack = isFreshAttack
			});

			if (st.Commands.Count > 128)
				st.Commands.RemoveAt(0);
		}
	}

	public void OnSetupMove(IPlayer player, int commandNumber, int currentTick)
	{
		if (!_config.Enabled || !AimbotDetector.IsEligibleHuman(player) || !player.IsAlive)
			return;

		int id = player.PlayerID;
		if (id < 1 || id >= _players.Length)
			return;

		var pawn = player.PlayerPawn;
		if (pawn is null)
			return;

		var st = _players[id];
		var cmd = st.Commands.FindLast(c => c.CommandNumber == commandNumber);
		if (cmd == null && commandNumber <= st.LastCommandNumber)
			return;

		QAngle viewAngles = cmd != null ? cmd.Angles : pawn.EyeAngles;
		bool isFreshAttack = cmd != null && cmd.IsFreshAttack;

		if (commandNumber <= st.LastCommandNumber)
			return;

		if (st.LastCommandNumber >= 0 && commandNumber - st.LastCommandNumber > 16)
		{
			st.ActiveTarget = -1;
			st.ContactStartCommand = -1;
		}

		st.LastCommandNumber = commandNumber;

		if (!AimMath.IsFinite(viewAngles))
		{
			st.ActiveTarget = -1;
			st.ContactStartCommand = -1;
			return;
		}

		Vector origin = pawn.AbsOrigin ?? default;
		Vector eye = pawn.EyePosition ?? new Vector(origin.X, origin.Y, origin.Z + 64.0f);
		if (!AimMath.IsFinite(eye))
			return;

		StatContactChecks++;
		var (targetIndex, hitPoint) = TraceCrosshair(player, eye, viewAngles);

		if (targetIndex != st.ActiveTarget)
		{
			st.ActiveTarget = targetIndex;
			st.ContactStartCommand = -1;

			if (targetIndex >= 1)
			{
				st.ContactStartCommand = commandNumber;
				float aimMovement = st.LastAnglesValid ? AimMath.AngularDistance(st.LastAngles, viewAngles) : 0.0f;
				st.ActiveMode = float.IsFinite(aimMovement) && aimMovement > HeldAngleMaximumDegrees
					? TriggerContactMode.AimDriven
					: TriggerContactMode.TargetDriven;

				StatAcquisitions++;
				_debug($"[Triggerbot] {Name(player)} acquired target #{targetIndex} (mode: {st.ActiveMode}, cmd: {commandNumber}).");
			}
		}

		if (targetIndex >= 1)
		{
			st.LastContactTick[targetIndex] = currentTick;
		}

		// Prune expired smokes
		var now = DateTime.UtcNow;
		_smokes.RemoveAll(s => s.Expires <= now);

		bool smokeOccluded = now < _smokeStateUnknownUntil;
		if (!smokeOccluded && targetIndex >= 1)
		{
			smokeOccluded = _smokes.Any(s => SegmentTouchesSmokeBounds(eye, hitPoint, s.Center));
		}

		if (isFreshAttack && targetIndex >= 1 && st.ContactStartCommand >= 0)
		{
			int reactionTicks = commandNumber - st.ContactStartCommand;
			if (!smokeOccluded)
			{
				st.Pending.Add(new TriggerPendingAttack
				{
					CommandNumber = commandNumber,
					TargetIndex = targetIndex,
					ReactionTicks = reactionTicks,
					Mode = st.ActiveMode
				});

				while (st.Pending.Count > PendingLimit)
					st.Pending.RemoveAt(0);

				StatPendingAttacks++;
				_debug($"[Triggerbot] {Name(player)} attack pressed: reaction {reactionTicks} ticks on target #{targetIndex}.");
			}
			else
			{
				_debug($"[Triggerbot] {Name(player)} attack ignored due to smoke occlusion volume.");
			}
		}

		st.LastAngles = viewAngles;
		st.LastAnglesValid = true;
	}

	public void OnWeaponFire(IPlayer player, string weapon, int currentTick)
	{
		if (!_config.Enabled || !AimbotDetector.IsEligibleHuman(player))
			return;

		int id = player.PlayerID;
		if (id < 1 || id >= _players.Length)
			return;

		var st = _players[id];
		int targetIndex = st.ActiveTarget;
		int contactStart = st.ContactStartCommand;

		if (targetIndex < 1)
		{
			// Check if contact was lost within the last 2 ticks due to weapon recoil punch
			for (int t = 1; t <= AimbotDetector.MaxPlayers; t++)
			{
				if (st.LastContactTick[t] >= 0 && currentTick - st.LastContactTick[t] <= 2)
				{
					targetIndex = t;
					contactStart = st.ContactStartCommand >= 0 ? st.ContactStartCommand : st.LastCommandNumber - (currentTick - st.LastContactTick[t]);
					break;
				}
			}
		}

		if (targetIndex < 1 || contactStart < 0)
			return;

		int cmdNum = st.LastCommandNumber >= 0 ? st.LastCommandNumber : currentTick;
		int reactionTicks = Math.Max(0, cmdNum - contactStart);

		if (!st.Pending.Any(p => p.CommandNumber == cmdNum))
		{
			st.Pending.Add(new TriggerPendingAttack
			{
				CommandNumber = cmdNum,
				TargetIndex = targetIndex,
				ReactionTicks = reactionTicks,
				Mode = st.ActiveMode
			});

			while (st.Pending.Count > PendingLimit)
				st.Pending.RemoveAt(0);

			StatPendingAttacks++;
			_debug($"[Triggerbot] {Name(player)} weapon_fire registered reaction: {reactionTicks} ticks on target #{targetIndex}.");
		}
	}

	public void OnPlayerHurt(IPlayer attacker, IPlayer victim, string weapon, int damage, int? commandNumber)
	{
		if (!_config.Enabled || !AimbotDetector.IsEligibleHuman(attacker) || !victim.IsValid || damage <= 0)
			return;

		int attackerId = attacker.PlayerID;
		if (attackerId < 1 || attackerId >= _players.Length)
			return;

		var st = _players[attackerId];
		TriggerPendingAttack? pendingMatch = null;
		if (commandNumber.HasValue)
		{
			pendingMatch = st.Pending.FirstOrDefault(p => p.CommandNumber == commandNumber.Value);
		}

		if (pendingMatch == null)
		{
			pendingMatch = st.Pending.LastOrDefault(p => p.TargetIndex == victim.PlayerID);
		}

		if (pendingMatch == null)
			return;

		st.Pending.Remove(pendingMatch);
		StatHurtsEvaluated++;

		if (pendingMatch.TargetIndex != victim.PlayerID)
			return;

		string normWeapon = weapon.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase) ? weapon[7..] : weapon;
		if (ExcludedWeapons.Contains(normWeapon) || pendingMatch.ReactionTicks < 0)
			return;

		float reactionMs = pendingMatch.ReactionTicks * (1000.0f / 64.0f);
		var now = DateTime.UtcNow;
		st.Incidents.RemoveAll(i => now - i.Time >= EvidenceWindow);

		if (reactionMs <= _config.MaxReactionTimeMs)
		{
			st.Incidents.Add(new TriggerIncident
			{
				Time = now,
				ReactionTicks = pendingMatch.ReactionTicks,
				ReactionMs = reactionMs,
				TargetId = victim.PlayerID,
				Weapon = normWeapon,
				Damage = damage,
				Mode = pendingMatch.Mode
			});

			_debug($"[Triggerbot] {Name(attacker)} unnatural trigger reaction: {pendingMatch.ReactionTicks} ticks ({reactionMs:F1}ms <= {_config.MaxReactionTimeMs:F0}ms, mode: {pendingMatch.Mode}) on #{victim.PlayerID} dealing {damage} dmg. Evidence: {st.Incidents.Count}/{_config.EvidenceThreshold}.");

			if (st.Incidents.Count >= _config.EvidenceThreshold)
			{
				StatDetections++;
				var lines = new List<string>();
				for (int i = 0; i < st.Incidents.Count; i++)
				{
					var inc = st.Incidents[i];
					lines.Add($"{i + 1}. {inc.ReactionTicks} ticks ({inc.ReactionMs:F1}ms) - {inc.Mode} ({inc.Weapon}, {inc.Damage} dmg)");
				}

				string latest = $"Latest damaging shot: fired {pendingMatch.ReactionTicks} ticks ({reactionMs:F1}ms) after crosshair target acquisition (mode: {pendingMatch.Mode}). Evidence {st.Incidents.Count}/{_config.EvidenceThreshold} in 5 min.";

				string evidence = string.Join("\n", lines) + "\n" + latest;

				_announce(attacker, "TRIGGERBOT", evidence);
				st.Incidents.Clear();
			}
		}
		else
		{
			_debug($"[Triggerbot] {Name(attacker)} normal human reaction: {pendingMatch.ReactionTicks} ticks ({reactionMs:F1}ms > {_config.MaxReactionTimeMs:F0}ms, mode: {pendingMatch.Mode}) on #{victim.PlayerID}.");
		}
	}

	private (int TargetIndex, Vector HitPoint) TraceCrosshair(IPlayer player, in Vector eye, in QAngle angles)
	{
		var pawn = player.PlayerPawn;
		if (pawn is null)
			return (-1, Vector.Zero);

		Vector forward = AimMath.Forward(angles);
		Vector end = new(
			eye.X + forward.X * TraceLength,
			eye.Y + forward.Y * TraceLength,
			eye.Z + forward.Z * TraceLength);

		var traceParams = new TraceParams
		{
			InteractWith = MaskTrace.Solid | MaskTrace.Hitbox | MaskTrace.Player |
			               MaskTrace.CsgoTeam1 | MaskTrace.CsgoTeam2 |
			               MaskTrace.CsgoMonster | MaskTrace.CsgoMoveable | MaskTrace.Window,
			EntitiesToIgnore = [pawn]
		};

		var trace = _traceManager.TraceShapeLine(eye, end, traceParams);
		float maxDistance = (trace.DidHit && trace.Distance > 0.0f) ? trace.Distance : TraceLength;

		// 1. Direct native trace hit check
		if (trace.DidHit && trace.Entity != null)
		{
			uint hitIndex = trace.Entity.Index;
			nint hitAddress = trace.Entity.Address;

			for (int i = 1; i <= AimbotDetector.MaxPlayers; i++)
			{
				var target = _getPlayer(i);
				if (target is not { IsValid: true } || target.PlayerID == player.PlayerID)
					continue;

				if (!target.IsAlive || !_areEnemies(player.PlayerID, target.PlayerID))
					continue;

				var targetPawn = target.PlayerPawn;
				var targetCtrl = target.Controller;

				bool directHit = false;
				if (targetPawn != null && (targetPawn.Index == hitIndex || targetPawn.Address == hitAddress))
					directHit = true;
				else if (targetCtrl != null && (targetCtrl.Index == hitIndex || targetCtrl.Address == hitAddress))
					directHit = true;
				else if (target.PlayerID == (int)hitIndex)
					directHit = true;

				if (directHit)
					return (i, trace.EndPos);
			}
		}

		// 2. Geometric hull/cylinder intersection bounded by wall line-of-sight distance
		int bestTarget = -1;
		float bestDist = float.MaxValue;
		Vector bestPoint = trace.DidHit ? trace.EndPos : end;

		for (int i = 1; i <= AimbotDetector.MaxPlayers; i++)
		{
			var target = _getPlayer(i);
			if (target is not { IsValid: true } || target.PlayerID == player.PlayerID)
				continue;

			if (!target.IsAlive || !_areEnemies(player.PlayerID, target.PlayerID))
				continue;

			var targetPawn = target.PlayerPawn;
			if (targetPawn is null)
				continue;

			Vector origin = targetPawn.AbsOrigin ?? default;
			if (!AimMath.IsFinite(origin))
				continue;

			float dx = origin.X - eye.X;
			float dy = origin.Y - eye.Y;
			float dz = origin.Z - eye.Z;

			float t = dx * forward.X + dy * forward.Y + dz * forward.Z;
			if (t < 30.0f || t >= maxDistance)
				continue;

			float px = eye.X + forward.X * t;
			float py = eye.Y + forward.Y * t;
			float pz = eye.Z + forward.Z * t;

			if (pz < origin.Z - 4.0f || pz > origin.Z + 76.0f)
				continue;

			float latX = px - origin.X;
			float latY = py - origin.Y;
			float latDistSq = latX * latX + latY * latY;

			const float playerRadius = 18.0f; // CS2 player hull radius (16 + 2 tolerance)
			if (latDistSq <= playerRadius * playerRadius)
			{
				if (t < bestDist)
				{
					bestDist = t;
					bestTarget = i;
					bestPoint = new Vector(px, py, pz);
				}
			}
		}

		if (bestTarget >= 1)
			return (bestTarget, bestPoint);

		return (-1, trace.EndPos);
	}

	private static bool SegmentTouchesSmokeBounds(in Vector start, in Vector end, in Vector center)
	{
		float minFraction = 0.0f;
		float maxFraction = 1.0f;

		float[] starts = [start.X, start.Y, start.Z];
		float[] ends = [end.X, end.Y, end.Z];
		float[] centers = [center.X, center.Y, center.Z];

		for (int axis = 0; axis < 3; axis++)
		{
			float delta = ends[axis] - starts[axis];
			float min = centers[axis] - SmokeHalfExtent;
			float max = centers[axis] + SmokeHalfExtent;

			if (MathF.Abs(delta) < 1e-5f)
			{
				if (starts[axis] < min || starts[axis] > max)
					return false;
				continue;
			}

			float first = (min - starts[axis]) / delta;
			float second = (max - starts[axis]) / delta;

			if (first > second)
				(first, second) = (second, first);

			minFraction = MathF.Max(minFraction, first);
			maxFraction = MathF.Min(maxFraction, second);

			if (minFraction > maxFraction)
				return false;
		}

		return true;
	}

	private static string Name(IPlayer p) => p.Controller?.PlayerName ?? $"#{p.PlayerID}";
}
