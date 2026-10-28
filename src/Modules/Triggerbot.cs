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
	public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
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
	public int SyntheticAnomalies { get; init; }
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
	public bool WasAttacking;
	public int AngleHoldingTicks;
	public int LastFreshAttackCommand = -1;
	public int SyntheticInputAnomalies;
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
		WasAttacking = false;
		AngleHoldingTicks = 0;
		LastFreshAttackCommand = -1;
		SyntheticInputAnomalies = 0;
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

	private static readonly HashSet<string> BallisticWeapons = new(StringComparer.OrdinalIgnoreCase)
	{
		"ak47","aug","awp","bizon","cz75a","deagle","elite","famas","fiveseven","g3sg1","galilar","glock",
		"hkp2000","m249","m4a1","m4a1_silencer","mac10","mag7","mp5sd","mp7","mp9","negev","nova","p250",
		"p90","sawedoff","scar20","sg556","ssg08","tec9","ump45","usp_silencer","xm1014"
	};

	private TriggerbotConfig _config;
	private readonly ITraceManager _traceManager;
	private readonly Func<int, IPlayer?> _getPlayer;
	private readonly Func<int, int, bool> _areEnemies;
	private readonly Action<IPlayer, string, string> _announce;
	private readonly Action<string> _debug;

	private readonly TriggerPlayerState[] _players = new TriggerPlayerState[AimbotDetector.MaxPlayers + 1];
	private readonly List<TriggerSmoke> _smokes = new();
	private DateTime _smokeStateUnknownUntil = DateTime.MinValue;

	public void UpdateConfig(TriggerbotConfig config) => _config = config;

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
		if (playerId >= 0 && playerId < _players.Length)
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
		if (id < 0 || id >= _players.Length)
			return;

		var st = _players[id];

		foreach (var cmd in cmds)
		{
			var pb = cmd.CSGOUserCmd;
			int number = (int)cmd.CommandNumber;

			bool isDup = false;
			for (int ci = st.Commands.Count - 1; ci >= 0; ci--)
			{
				if (st.Commands[ci].CommandNumber == number)
				{
					isDup = true;
					break;
				}
				if (st.Commands[ci].CommandNumber < number)
					break;
			}
			if (isDup)
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

						// Overwatch reverse-engineered input state machine check:
						// Real physical mouse switches and human hands cannot perform two complete click cycles in <= 2 ticks (<= 31.2ms).
						// External triggerbots, macro scripts, or injected SendInput calls pulse duplicate Down events or rapid synthetic pulses without physical dwell time.
						if (st.LastFreshAttackCommand >= 0)
						{
							int deltaCmds = number - st.LastFreshAttackCommand;
							if (deltaCmds is >= 1 and <= 2)
							{
								st.SyntheticInputAnomalies++;
								_debug($"[Triggerbot] {Name(player)} detected impossible click cadence / synthetic input injection: {deltaCmds} ticks delta (cmd {st.LastFreshAttackCommand} -> {number}). Anomalies: {st.SyntheticInputAnomalies}.");
							}
						}
						st.LastFreshAttackCommand = number;
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
		if (id < 0 || id >= _players.Length)
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
		bool wasAlreadyAttacking = st.WasAttacking;
		st.WasAttacking = isFreshAttack;

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

		float aimMovement = st.LastAnglesValid ? AimMath.AngularDistance(st.LastAngles, viewAngles) : 0.0f;
		if (st.LastAnglesValid && float.IsFinite(aimMovement) && aimMovement <= HeldAngleMaximumDegrees)
		{
			st.AngleHoldingTicks++;
		}
		else
		{
			st.AngleHoldingTicks = 0;
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

			if (targetIndex >= 0)
			{
				// If the weapon was already discharging before target entered crosshair, discard (pre-firing / spray)
				if (wasAlreadyAttacking)
				{
					st.ActiveMode = TriggerContactMode.AimDriven;
				}
				else
				{
					st.ContactStartCommand = commandNumber;
					st.ActiveMode = st.AngleHoldingTicks >= 3 && aimMovement <= HeldAngleMaximumDegrees
						? TriggerContactMode.TargetDriven
						: TriggerContactMode.AimDriven;

					StatAcquisitions++;
					_debug($"[Triggerbot] {Name(player)} acquired target #{targetIndex} (mode: {st.ActiveMode}, cmd: {commandNumber}, holdTicks: {st.AngleHoldingTicks}).");
				}
			}
		}

		if (targetIndex >= 0)
		{
			st.LastContactTick[targetIndex] = currentTick;
		}

		// Prune expired smokes
		var now = DateTime.UtcNow;
		_smokes.RemoveAll(s => s.Expires <= now);

		bool smokeOccluded = now < _smokeStateUnknownUntil;
		if (!smokeOccluded && targetIndex >= 0)
		{
			smokeOccluded = _smokes.Any(s => SegmentTouchesSmokeBounds(eye, hitPoint, s.Center));
		}

		if (isFreshAttack && targetIndex >= 0 && st.ContactStartCommand >= 0)
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

		string normWeapon = weapon.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase) ? weapon[7..] : weapon;
		if (!BallisticWeapons.Contains(normWeapon))
			return;

		int id = player.PlayerID;
		if (id < 0 || id >= _players.Length)
			return;

		var st = _players[id];
		// Discard continuous spray bullets: a triggerbot initiates combat on fresh click, not on bullet #12 of an ongoing spray.
		if (st.WasAttacking && st.Pending.Count > 0)
			return;

		int targetIndex = st.ActiveTarget;
		int contactStart = st.ContactStartCommand;

		if (targetIndex < 0)
		{
			// Check if contact was lost within the last 2 ticks due to weapon recoil punch
			for (int t = 0; t < AimbotDetector.MaxPlayers; t++)
			{
				if (st.LastContactTick[t] >= 0 && currentTick - st.LastContactTick[t] <= 2)
				{
					targetIndex = t;
					contactStart = st.ContactStartCommand >= 0 ? st.ContactStartCommand : st.LastCommandNumber - (currentTick - st.LastContactTick[t]);
					break;
				}
			}
		}

		if (targetIndex < 0 || contactStart < 0)
			return;

		int cmdNum = st.LastCommandNumber >= 0 ? st.LastCommandNumber : currentTick;
		int reactionTicks = Math.Max(0, cmdNum - contactStart);

		bool alreadyPending = false;
		for (int pi = st.Pending.Count - 1; pi >= 0; pi--)
		{
			if (st.Pending[pi].CommandNumber == cmdNum)
			{
				alreadyPending = true;
				break;
			}
		}

		if (!alreadyPending)
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
		if (attackerId < 0 || attackerId >= _players.Length)
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
		if (!BallisticWeapons.Contains(normWeapon) || pendingMatch.ReactionTicks < 0)
			return;

		// Discard pending reactions older than 0.75 seconds to avoid matching delayed hits with obsolete clicks
		if ((DateTime.UtcNow - pendingMatch.CreatedAt).TotalSeconds > 0.75)
			return;

		// Pure Triggerbot operates on TargetDriven mode (holding an angle and reacting to an enemy crossing crosshair).
		// AimDriven reactions are flicks/swipes where the click is intentionally coordinated with arm movement.
		if (pendingMatch.Mode != TriggerContactMode.TargetDriven)
			return;

		// Verify direct line-of-sight at damage resolution to reject wallbang / penetration shots
		if (_config.EnableNativeTrace)
		{
			try
			{
				var victimPawn = victim.PlayerPawn;
				var attackerPawn = attacker.PlayerPawn;
				if (victimPawn is { IsValid: true } && attackerPawn is { IsValid: true })
				{
					Vector attEye = attackerPawn.EyePosition ?? default;
					Vector vicOrigin = victimPawn.AbsOrigin ?? default;
					Vector vicChest = new(vicOrigin.X, vicOrigin.Y, vicOrigin.Z + 46.0f);
					if (AimMath.IsFinite(attEye) && AimMath.IsFinite(vicChest))
					{
						float hurtDist = attEye.Distance(vicChest);
						var hurtTrace = _traceManager.TraceShapeLine(attEye, vicChest, new TraceParams { InteractWith = MaskTrace.Solid });
						if (hurtTrace.DidHit && hurtTrace.Distance < hurtDist - 32.0f)
						{
							_debug($"[Triggerbot] {Name(attacker)} damaging shot ignored: victim was behind world geometry (wallbang).");
							return;
						}
					}
				}
			}
			catch { }
		}

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
				Mode = pendingMatch.Mode,
				SyntheticAnomalies = st.SyntheticInputAnomalies
			});

			_debug($"[Triggerbot] {Name(attacker)} unnatural command delta: {pendingMatch.ReactionTicks} ticks (~{reactionMs:F1}ms cmd interval <= {_config.MaxReactionTimeMs:F0}ms, mode: {pendingMatch.Mode}) on #{victim.PlayerID} dealing {damage} dmg. Evidence: {st.Incidents.Count}/{_config.EvidenceThreshold}.");

			if (st.Incidents.Count >= _config.EvidenceThreshold)
			{
				StatDetections++;

				// Vanguard MACRO_TRESHOLD / MIN_CLICK_VARIANCE verification:
				// Real human reaction cadence exhibits biological variance (>0.05).
				// Mechanical triggerbots and auto-clickers execute with near-zero timing variance (<0.01).
				float meanTicks = 0.0f;
				for (int i = 0; i < st.Incidents.Count; i++)
					meanTicks += st.Incidents[i].ReactionTicks;
				meanTicks /= st.Incidents.Count;

				float varianceTicks = 0.0f;
				for (int i = 0; i < st.Incidents.Count; i++)
				{
					float diff = st.Incidents[i].ReactionTicks - meanTicks;
					varianceTicks += diff * diff;
				}
				varianceTicks /= st.Incidents.Count;

				var lines = new List<string>();
				for (int i = 0; i < st.Incidents.Count; i++)
				{
					var inc = st.Incidents[i];
					lines.Add($"{i + 1}. {inc.ReactionTicks} ticks (~{inc.ReactionMs:F1}ms cmd interval) - {inc.Mode} ({inc.Weapon}, {inc.Damage} dmg)");
				}

				string macroTag = varianceTicks < 0.05f
					? $" (Machine-like cadence: timing variance {varianceTicks:F3} < 0.05 Vanguard benchmark)"
					: "";

				string anomalyTag = st.SyntheticInputAnomalies > 0
					? $" (Overwatch input anomaly: {st.SyntheticInputAnomalies} impossible double-click pulses detected)"
					: "";

				string latest = $"Latest damaging shot: command-to-acquisition delta of {pendingMatch.ReactionTicks} ticks (~{reactionMs:F1}ms cmd interval, mode: {pendingMatch.Mode}){macroTag}{anomalyTag}. Evidence {st.Incidents.Count}/{_config.EvidenceThreshold} in 5 min.";

				string evidence = string.Join("\n", lines) + "\n" + latest;

				_announce(attacker, "TRIGGERBOT", evidence);
				st.Incidents.Clear();
			}
		}
		else
		{
			_debug($"[Triggerbot] {Name(attacker)} normal human reaction: {pendingMatch.ReactionTicks} ticks (~{reactionMs:F1}ms > {_config.MaxReactionTimeMs:F0}ms, mode: {pendingMatch.Mode}) on #{victim.PlayerID}.");
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

		// Fast-pass: Pure geometric cylinder intersection (100% managed C#, zero native calls, zero crash risk)
		int bestTarget = -1;
		float bestDist = float.MaxValue;
		Vector bestPoint = end;

		const float playerRadius = 18.0f; // CS2 player hull radius (16 + 2 tolerance)
		const float radiusSq = playerRadius * playerRadius;

		for (int i = 0; i < AimbotDetector.MaxPlayers; i++)
		{
			var target = _getPlayer(i);
			if (target is not { IsValid: true } || target.PlayerID == player.PlayerID)
				continue;

			if (!target.IsAlive || !_areEnemies(player.PlayerID, target.PlayerID))
				continue;

			var targetPawn = target.PlayerPawn;
			if (targetPawn is null || !targetPawn.IsValid)
				continue;

			Vector? maybeOrigin = targetPawn.AbsOrigin;
			if (!maybeOrigin.HasValue || !AimMath.IsFinite(maybeOrigin.Value))
				continue;

			Vector origin = maybeOrigin.Value;

			float dx = origin.X - eye.X;
			float dy = origin.Y - eye.Y;
			float dz = origin.Z - eye.Z;

			float t = dx * forward.X + dy * forward.Y + dz * forward.Z;
			if (t < 30.0f || t >= bestDist)
				continue;

			float px = eye.X + forward.X * t;
			float py = eye.Y + forward.Y * t;
			float pz = eye.Z + forward.Z * t;

			if (pz < origin.Z - 4.0f || pz > origin.Z + 76.0f)
				continue;

			float latX = px - origin.X;
			float latY = py - origin.Y;
			float latDistSq = latX * latX + latY * latY;

			if (latDistSq <= radiusSq)
			{
				bestDist = t;
				bestTarget = target.PlayerID;
				bestPoint = new Vector(px, py, pz);
			}
		}

		if (bestTarget < 0)
			return (-1, end);

		// Verified line-of-sight check against world geometry (MaskTrace.Solid)
		// Safety rule: Never assume visibility through walls; if line-of-sight cannot be confirmed, safely ignore the candidate target.
		if (!_config.EnableNativeTrace)
		{
			// Native raycasting disabled: visibility cannot be confirmed through potential walls
			return (-1, end);
		}

		try
		{
			var traceParams = new TraceParams
			{
				InteractWith = MaskTrace.Solid
			};

			var trace = _traceManager.TraceShapeLine(eye, bestPoint, traceParams);
			if (trace.DidHit && trace.Distance < bestDist - 16.0f)
			{
				// Occluded by solid world geometry / wall
				return (-1, trace.EndPos);
			}

			// Clear direct line of sight confirmed
			return (bestTarget, bestPoint);
		}
		catch
		{
			// Trace threw or failed: safely ignore unverified targets
			return (-1, end);
		}
	}

	private static bool SegmentTouchesSmokeBounds(in Vector start, in Vector end, in Vector center)
	{
		float minFraction = 0.0f;
		float maxFraction = 1.0f;

		if (!ClipAxis(start.X, end.X, center.X, ref minFraction, ref maxFraction)) return false;
		if (!ClipAxis(start.Y, end.Y, center.Y, ref minFraction, ref maxFraction)) return false;
		if (!ClipAxis(start.Z, end.Z, center.Z, ref minFraction, ref maxFraction)) return false;

		return true;
	}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
	private static bool ClipAxis(float start, float end, float center, ref float minFraction, ref float maxFraction)
	{
		float delta = end - start;
		float min = center - SmokeHalfExtent;
		float max = center + SmokeHalfExtent;

		if (MathF.Abs(delta) < 1e-5f)
		{
			return start >= min && start <= max;
		}

		float first = (min - start) / delta;
		float second = (max - start) / delta;

		if (first > second)
			(first, second) = (second, first);

		minFraction = MathF.Max(minFraction, first);
		maxFraction = MathF.Min(maxFraction, second);

		return minFraction <= maxFraction;
	}

	private static string Name(IPlayer p) => p.Controller?.PlayerName ?? $"#{p.PlayerID}";
}
