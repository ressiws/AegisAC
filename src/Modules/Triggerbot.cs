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
 *   acquisition, sub-tick reaction distributions, decaying evidence scores,
 *   timing variance, anatomical hitbox bounds, line-of-sight, smoke occlusion,
 *   and network stability.
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
	public required float AcquisitionSpeedDegPerTick { get; init; }
	public required float FireSpeedDegPerTick { get; init; }
	public required float MeanContactSpeedDegPerTick { get; init; }
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
	public required float PointsAwarded { get; init; }
	public required float ResultingScore { get; init; }
}

internal sealed class TriggerSmoke
{
	public required Vector Center { get; init; }
	public required DateTime Expires { get; init; }
}

internal sealed class TriggerCommand
{
	public required int CommandNumber { get; init; }
	public required int ClientTick { get; init; }
	public required QAngle Angles { get; init; }
	public required bool IsFreshAttack { get; init; }
	public required bool AttackHeld { get; init; }
}

internal sealed class TriggerPlayerState
{
	public int LastCommandNumber = -1;
	public int ActiveTarget = -1;
	public int ContactStartCommand = -1;
	public TriggerContactMode ActiveMode = TriggerContactMode.TargetDriven;
	public float AcquisitionSpeedDegPerTick;
	public float ContactAccumulatedSpeed;
	public int ContactTickCount;
	public QAngle LastAngles;
	public bool LastAnglesValid;
	public bool WasAttacking;
	public int AngleHoldingTicks;
	public int LastFreshAttackCommand = -1;
	public DateTime LastAnomalyAnnounce = DateTime.MinValue;
	public float EvidenceScore;
	public readonly Queue<DateTime> SyntheticAnomalyTimes = new();
	public readonly int[] LastContactTick = new int[AimbotDetector.MaxPlayers + 1];
	public readonly List<TriggerCommand> Commands = new();
	public readonly List<TriggerPendingAttack> Pending = new();
	public readonly List<TriggerIncident> Incidents = new();
	public readonly List<(DateTime Time, int ReactionTicks, TriggerContactMode Mode)> ReactionHistory = new();

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
		AcquisitionSpeedDegPerTick = 0.0f;
		ContactAccumulatedSpeed = 0.0f;
		ContactTickCount = 0;
		LastAnglesValid = false;
		WasAttacking = false;
		AngleHoldingTicks = 0;
		LastFreshAttackCommand = -1;
		LastAnomalyAnnounce = DateTime.MinValue;
		EvidenceScore = 0.0f;
		SyntheticAnomalyTimes.Clear();
		Array.Fill(LastContactTick, -1);
		Commands.Clear();
		Pending.Clear();
		Incidents.Clear();
		ReactionHistory.Clear();
	}
}

internal sealed class TriggerbotDetector
{
	private const int HistoryLimit = 30;
	private const int PendingLimit = 8;
	private const int FreshContactTicks = 5; // ~78ms edge-jitter guard at 64-tick
	private const float HeldAngleMaximumDegrees = 0.65f;
	private const float TraceLength = 8192.0f;
	private const float SmokeHalfExtent = 320.0f;
	private const float MaxEvidenceScore = 25.0f;
	private static readonly TimeSpan EvidenceWindow = TimeSpan.FromMinutes(5);
	private static readonly TimeSpan SyntheticWindow = TimeSpan.FromSeconds(60);
	private static readonly TimeSpan SmokeLifetime = TimeSpan.FromMilliseconds(22500);

	private static readonly HashSet<string> BallisticWeapons = new(StringComparer.OrdinalIgnoreCase)
	{
		"ak47","aug","awp","bizon","cz75a","deagle","elite","famas","fiveseven","g3sg1","galilar","glock",
		"hkp2000","m249","m4a1","m4a1_silencer","mac10","mag7","mp5sd","mp7","mp9","negev","nova","p250",
		"p90","sawedoff","scar20","sg556","ssg08","tec9","ump45","usp_silencer","xm1014"
	};

	private TriggerbotConfig _config;
	private readonly ITraceManager? _traceManager;
	private readonly Func<int, IPlayer?> _getPlayer;
	private readonly Func<int, int, bool> _areEnemies;
	private readonly Func<int, PositionFrame?>? _findFrame;
	private readonly NetworkSafetyMonitor? _networkSafety;
	private readonly Action<IPlayer, string, string> _announce;
	private readonly Action<string> _debug;

	private readonly TriggerPlayerState[] _players = new TriggerPlayerState[AimbotDetector.MaxPlayers + 1];
	private readonly Dictionary<ulong, (float Score, List<TriggerIncident> Incidents, List<(DateTime Time, int ReactionTicks, TriggerContactMode Mode)> History)> _persistentState = new();
	private readonly List<TriggerSmoke> _smokes = new();
	private DateTime _smokeStateUnknownUntil = DateTime.MinValue;

	public void UpdateConfig(TriggerbotConfig config) => _config = config;

	public long StatContactChecks, StatAcquisitions, StatPendingAttacks, StatHurtsEvaluated, StatNormalDecay, StatNetworkVetoes, StatDetections;

	public TriggerbotDetector(
		TriggerbotConfig config,
		ITraceManager? traceManager,
		Func<int, IPlayer?> getPlayer,
		Func<int, int, bool> areEnemies,
		Action<IPlayer, string, string> announce,
		Action<string> debug,
		Func<int, PositionFrame?>? findFrame = null,
		NetworkSafetyMonitor? networkSafety = null)
	{
		_config = config;
		_traceManager = traceManager;
		_getPlayer = getPlayer;
		_areEnemies = areEnemies;
		_announce = announce;
		_debug = debug;
		_findFrame = findFrame;
		_networkSafety = networkSafety;

		for (int i = 0; i < _players.Length; i++)
			_players[i] = new TriggerPlayerState();
	}

	public string Stats() =>
		$"checks={StatContactChecks} acq={StatAcquisitions} pending={StatPendingAttacks} hurts={StatHurtsEvaluated} " +
		$"decay={StatNormalDecay} netVeto={StatNetworkVetoes} detections={StatDetections} smokes={_smokes.Count}";

	public void Reset()
	{
		_persistentState.Clear();
		for (int i = 0; i < _players.Length; i++)
			_players[i].Reset();

		_smokes.Clear();
		// Only apply initial smoke lockout when native trace is active on a live map load
		_smokeStateUnknownUntil = DateTime.MinValue;
	}

	public void OnMapReloadWithUncertainSmokes()
	{
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

	/// <summary>
	/// Zero-allocation query checking if a line segment [start, end] passes through any active smoke volume.
	/// Shared across Triggerbot, Aimlock, and SilentAim.
	/// </summary>
	public bool IsSmokeOccluded(in Vector start, in Vector end)
	{
		DateTime now = DateTime.UtcNow;
		if (now < _smokeStateUnknownUntil)
			return true;

		for (int i = _smokes.Count - 1; i >= 0; i--)
		{
			if (_smokes[i].Expires <= now)
			{
				_smokes.RemoveAt(i);
				continue;
			}

			if (SegmentTouchesSmokeBounds(start, end, _smokes[i].Center))
				return true;
		}

		return false;
	}

	public void OnProcessUsercmds(IPlayer player, List<SwiftlyS2.Shared.GameHooks.IUserCmd> cmds)
	{
		if (!_config.Enabled || !AimbotDetector.IsEligibleHuman(player) || cmds.Count == 0)
			return;

		int id = player.PlayerID;
		if (id < 0 || id >= _players.Length)
			return;

		bool batchBurst = cmds.Count > NetworkSafetyMonitor.MaximumBurstCommandsPerTick;
		var st = _players[id];

		foreach (var cmd in cmds)
		{
			var pb = cmd.CSGOUserCmd;
			int number = (int)cmd.CommandNumber;

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

			bool attackHeld = isFreshAttack;
			try
			{
				if (b.Accessor.HasField("buttons_pb"))
					attackHeld = attackHeld || (b.ButtonsPb.Buttonstate1 & 1UL) != 0;
			}
			catch { }

			RecordCommand(player, number, b.ClientTick, angles, isFreshAttack, attackHeld, batchBurst);
		}
	}

	/// <summary>
	/// Records a processed usercmd directly (supports both live hook processing and deterministic unit testing).
	/// </summary>
	internal void RecordCommand(
		IPlayer player,
		int commandNumber,
		int clientTick,
		QAngle angles,
		bool isFreshAttack,
		bool attackHeld = false,
		bool isBatchBurst = false,
		DateTime? nowOverride = null)
	{
		if (!_config.Enabled || !AimbotDetector.IsEligibleHuman(player))
			return;

		int id = player.PlayerID;
		if (id < 0 || id >= _players.Length)
			return;

		var st = _players[id];

		for (int ci = st.Commands.Count - 1; ci >= 0; ci--)
		{
			if (st.Commands[ci].CommandNumber == commandNumber)
				return;
			if (st.Commands[ci].CommandNumber < commandNumber)
				break;
		}

		if (!AimMath.IsFinite(angles) || angles.X < -89.05f || angles.X > 89.05f || MathF.Abs(angles.Z) > 1.0f)
			return;

		DateTime now = nowOverride ?? DateTime.UtcNow;

		if (isFreshAttack)
		{
			while (st.SyntheticAnomalyTimes.Count > 0 && (now - st.SyntheticAnomalyTimes.Peek()) >= SyntheticWindow)
				st.SyntheticAnomalyTimes.Dequeue();

			// Overwatch input state-machine hypothesis:
			// Physical microswitches and human fingers cannot complete two full click cycles in <= 2 commands (<= 31.2ms).
			// Exclude packet-choke command bursts and network-vetoed windows.
			if (!isBatchBurst && st.LastFreshAttackCommand >= 0)
			{
				int deltaCmds = commandNumber - st.LastFreshAttackCommand;
				if (deltaCmds is >= 1 and <= 2)
				{
					var net = _networkSafety?.Evaluate(id, now) ?? default;
					if (!net.Vetoed)
					{
						st.SyntheticAnomalyTimes.Enqueue(now);
						_debug($"[Triggerbot] {Name(player)} detected synthetic input cadence: {deltaCmds} ticks delta (cmd {st.LastFreshAttackCommand} -> {commandNumber}). Window anomalies: {st.SyntheticAnomalyTimes.Count}.");

						if (st.SyntheticAnomalyTimes.Count >= 5 && (now - st.LastAnomalyAnnounce) >= TimeSpan.FromMinutes(2))
						{
							st.LastAnomalyAnnounce = now;
							StatDetections++;
							_announce(player, "SYNTHETIC_INPUT", $"Input state-machine anomaly: {st.SyntheticAnomalyTimes.Count} sub-2-tick duplicate click transitions within 60s (mechanical debounce violation).");
							st.SyntheticAnomalyTimes.Clear();
						}
					}
				}
			}

			st.LastFreshAttackCommand = commandNumber;
		}

		st.Commands.Add(new TriggerCommand
		{
			CommandNumber = commandNumber,
			ClientTick = clientTick,
			Angles = angles,
			IsFreshAttack = isFreshAttack,
			AttackHeld = attackHeld || isFreshAttack
		});

		if (st.Commands.Count > 128)
			st.Commands.RemoveAt(0);
	}

	public void OnSetupMove(IPlayer player, int commandNumber, int currentTick)
	{
		if (!_config.Enabled || !AimbotDetector.IsEligibleHuman(player) || !PlayerSnapshotProvider.IsAlive(player))
			return;

		int id = player.PlayerID;
		if (id < 0 || id >= _players.Length)
			return;

		if (!PlayerSnapshotProvider.TryGetPawnTransform(player, out _, out var eye, out var pawnEyeAngles, out _, out _, out _, out _, out _))
			return;

		var st = _players[id];
		var cmd = st.Commands.FindLast(c => c.CommandNumber == commandNumber);
		if (cmd == null && commandNumber <= st.LastCommandNumber)
			return;

		QAngle viewAngles = cmd != null ? cmd.Angles : pawnEyeAngles;
		bool isFreshAttack = cmd != null && cmd.IsFreshAttack;
		bool attackHeld = cmd != null ? cmd.AttackHeld : isFreshAttack;
		bool wasAlreadyAttacking = st.WasAttacking;
		st.WasAttacking = attackHeld;

		if (commandNumber <= st.LastCommandNumber)
			return;

		// Reset active contact if command stream jumped (discontinuity > 1 command, matching CS2AC Legacy)
		if (st.LastCommandNumber >= 0 && commandNumber - st.LastCommandNumber > 2)
		{
			st.ActiveTarget = -1;
			st.ContactStartCommand = -1;
			st.ContactAccumulatedSpeed = 0.0f;
			st.ContactTickCount = 0;
		}

		st.LastCommandNumber = commandNumber;

		if (!AimMath.IsFinite(viewAngles))
		{
			st.ActiveTarget = -1;
			st.ContactStartCommand = -1;
			return;
		}

		float aimMovement = st.LastAnglesValid ? AimMath.AngularDistance(st.LastAngles, viewAngles) : 0.0f;
		if (!float.IsFinite(aimMovement))
			aimMovement = 0.0f;

		if (st.LastAnglesValid && aimMovement <= HeldAngleMaximumDegrees)
			st.AngleHoldingTicks++;
		else
			st.AngleHoldingTicks = 0;

		if (!AimMath.IsFinite(eye))
			return;

		StatContactChecks++;
		var (targetIndex, hitPoint) = TraceCrosshair(player, eye, viewAngles, currentTick);

		if (targetIndex != st.ActiveTarget)
		{
			st.ActiveTarget = targetIndex;
			st.ContactStartCommand = -1;
			st.ContactAccumulatedSpeed = 0.0f;
			st.ContactTickCount = 0;

			if (targetIndex >= 0)
			{
				int previousContact = st.LastContactTick[targetIndex];
				bool isFreshTargetContact = previousContact < 0 || (long)currentTick - previousContact >= FreshContactTicks;

				// Pre-fire / active-spray guard:
				// Only suppress acquisition if the player was already holding fire on the tick before contact,
				// or clicked within the immediately preceding 2 commands (blind jitter-click spam).
				bool isPrefiring = wasAlreadyAttacking;

				if (!isPrefiring && isFreshTargetContact)
				{
					st.ContactStartCommand = commandNumber;
					st.AcquisitionSpeedDegPerTick = aimMovement;
					st.ContactAccumulatedSpeed = aimMovement;
					st.ContactTickCount = 1;
					st.ActiveMode = st.AngleHoldingTicks >= 2 && aimMovement <= HeldAngleMaximumDegrees
						? TriggerContactMode.TargetDriven
						: TriggerContactMode.AimDriven;

					StatAcquisitions++;
					if (Utils.DebugMode)
						_debug($"[Triggerbot] {Name(player)} acquired target #{targetIndex} (mode: {st.ActiveMode}, cmd: {commandNumber}, holdTicks: {st.AngleHoldingTicks}, speed: {aimMovement:F2}°/t).");
				}
			}
		}
		else if (targetIndex >= 0 && st.ContactStartCommand >= 0)
		{
			st.ContactAccumulatedSpeed += aimMovement;
			st.ContactTickCount++;
		}

		if (targetIndex >= 0)
		{
			st.LastContactTick[targetIndex] = currentTick;
		}

		// Prune expired smokes without closure allocation
		var now = DateTime.UtcNow;
		for (int si = _smokes.Count - 1; si >= 0; si--)
		{
			if (_smokes[si].Expires <= now)
				_smokes.RemoveAt(si);
		}

		bool smokeOccluded = now < _smokeStateUnknownUntil;
		if (!smokeOccluded && targetIndex >= 0)
		{
			for (int si = 0; si < _smokes.Count; si++)
			{
				if (SegmentTouchesSmokeBounds(eye, hitPoint, _smokes[si].Center))
				{
					smokeOccluded = true;
					break;
				}
			}
		}

		if (isFreshAttack && targetIndex >= 0 && st.ContactStartCommand >= 0)
		{
			int reactionTicks = commandNumber - st.ContactStartCommand;
			float meanContactSpeed = st.ContactTickCount > 0
				? st.ContactAccumulatedSpeed / st.ContactTickCount
				: aimMovement;

			if (!smokeOccluded && reactionTicks >= 0)
			{
				st.Pending.Add(new TriggerPendingAttack
				{
					CommandNumber = commandNumber,
					TargetIndex = targetIndex,
					ReactionTicks = reactionTicks,
					Mode = st.ActiveMode,
					AcquisitionSpeedDegPerTick = st.AcquisitionSpeedDegPerTick,
					FireSpeedDegPerTick = aimMovement,
					MeanContactSpeedDegPerTick = meanContactSpeed
				});

				while (st.Pending.Count > PendingLimit)
					st.Pending.RemoveAt(0);

				StatPendingAttacks++;
				if (Utils.DebugMode)
					_debug($"[Triggerbot] {Name(player)} attack pressed: reaction {reactionTicks} ticks on target #{targetIndex} (mode={st.ActiveMode}, acqSpeed={st.AcquisitionSpeedDegPerTick:F2}°/t, fireSpeed={aimMovement:F2}°/t).");
			}
			else if (Utils.DebugMode && smokeOccluded)
			{
				_debug($"[Triggerbot] {Name(player)} attack ignored due to smoke occlusion volume.");
			}

			// Clear ContactStartCommand so subsequent commands in the same contact don't record stale reactions
			st.ContactStartCommand = -1;
		}

		st.LastAngles = viewAngles;
		st.LastAnglesValid = true;
	}

	public void OnWeaponFire(IPlayer player, string weapon, int currentTick)
	{
		// Intentional no-op:
		// Triggerbot initiation is strictly evaluated on fresh clicks (cmd.IsFreshAttack in OnSetupMove).
		// Creating pending attacks from weapon_fire during automatic fire cycles leads to false positives
		// when holding spray through doorways or corners.
	}

	public void OnPlayerHurt(IPlayer attacker, IPlayer victim, string weapon, int damage, int? commandNumber, DateTime? nowOverride = null)
	{
		if (!_config.Enabled || !AimbotDetector.IsEligibleHuman(attacker) || !victim.IsValid || damage <= 0)
			return;

		int attackerId = attacker.PlayerID;
		if (attackerId < 0 || attackerId >= _players.Length || attackerId == victim.PlayerID)
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
		if (!BallisticWeapons.Contains(normWeapon) || normWeapon.Equals("revolver", StringComparison.OrdinalIgnoreCase) || normWeapon.Equals("taser", StringComparison.OrdinalIgnoreCase) || pendingMatch.ReactionTicks < 0)
			return;

		DateTime now = nowOverride ?? DateTime.UtcNow;

		// Discard pending reactions older than 0.75 seconds to avoid matching delayed hits with obsolete clicks
		if ((now - pendingMatch.CreatedAt).TotalSeconds > 0.75 && nowOverride == null)
			return;

		// Shared Network Safety Gate (ported from CS2AC Legacy):
		// Defer timing-sensitive scoring when the player experiences high ping, jitter, loss, choke, or command bursts.
		if (_networkSafety != null)
		{
			var net = _networkSafety.Evaluate(attackerId, now);
			if (net.Vetoed)
			{
				StatNetworkVetoes++;
				if (Utils.DebugMode)
					_debug($"[Triggerbot] {Name(attacker)} {pendingMatch.ReactionTicks}-tick shot deferred by NetworkSafety ({net.FormatSummary()}).");
				return;
			}
		}

		// Verify direct line-of-sight at damage resolution to reject wallbang / penetration shots
		if (_config.EnableNativeTrace && _traceManager != null)
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
						Vector dir = new(vicChest.X - attEye.X, vicChest.Y - attEye.Y, vicChest.Z - attEye.Z);
						float hurtDist = dir.Length();
						if (hurtDist > 32.0f)
						{
							Vector normDir = new(dir.X / hurtDist, dir.Y / hurtDist, dir.Z / hurtDist);
							float startOffset = Math.Min(24.0f, hurtDist * 0.25f);
							Vector traceStart = new(attEye.X + normDir.X * startOffset, attEye.Y + normDir.Y * startOffset, attEye.Z + normDir.Z * startOffset);
							Vector traceEnd = new(vicChest.X - normDir.X * 4.0f, vicChest.Y - normDir.Y * 4.0f, vicChest.Z - normDir.Z * 4.0f);
							float traceLen = traceStart.Distance(traceEnd);

							var traceParams = new TraceParams
							{
								InteractWith = MaskTrace.Solid,
								EntitiesToIgnore = new List<SwiftlyS2.Shared.SchemaDefinitions.CEntityInstance> { attackerPawn, victimPawn }
							};

							var hurtTrace = _traceManager.TraceShapeLine(traceStart, traceEnd, traceParams);
							if (hurtTrace.DidHit && !hurtTrace.StartInSolid && hurtTrace.Distance < traceLen - 16.0f)
							{
								_debug($"[Triggerbot] {Name(attacker)} damaging shot ignored: victim was behind world geometry (wallbang).");
								return;
							}
						}
					}
				}
			}
			catch { }
		}

		ulong steamId = attacker.SteamID;
		if (steamId > 0 && st.Incidents.Count == 0 && st.ReactionHistory.Count == 0 && st.EvidenceScore <= 0.0f)
		{
			if (_persistentState.TryGetValue(steamId, out var cached))
			{
				cached.Incidents.RemoveAll(i => now - i.Time >= EvidenceWindow);
				cached.History.RemoveAll(h => now - h.Time >= EvidenceWindow);
				st.Incidents.AddRange(cached.Incidents);
				st.ReactionHistory.AddRange(cached.History);
				st.EvidenceScore = cached.Incidents.Count > 0 ? cached.Score : 0.0f;
			}
		}

		st.Incidents.RemoveAll(i => now - i.Time >= EvidenceWindow);
		st.ReactionHistory.RemoveAll(h => now - h.Time >= EvidenceWindow);

		st.ReactionHistory.Add((now, pendingMatch.ReactionTicks, pendingMatch.Mode));
		while (st.ReactionHistory.Count > HistoryLimit)
			st.ReactionHistory.RemoveAt(0);

		// Compute reaction-time variance across sub-human reaction window (<= MIN_HUMAN_REACTION = 150ms / 9 ticks)
		float varianceTicks = ComputeFastReactionVariance(st.ReactionHistory, out int fastSampleCount, out float meanFastTicks, out float normalizedVariance, out float cvPercent);
		bool isMacroVariance = fastSampleCount >= 4 && normalizedVariance <= AimMath.Baselines.MacroVarianceThreshold;
		bool lowVarianceConsistency = fastSampleCount >= 4 &&
			(varianceTicks <= 0.65f || normalizedVariance <= AimMath.Baselines.MinClickVariance || cvPercent <= AimMath.Baselines.PatternVariancePercent) &&
			meanFastTicks <= AimMath.Baselines.MinHumanReactionTicks;

		float reactionMs = pendingMatch.ReactionTicks * (1000.0f / 64.0f);
		int maxUnnaturalTicks = Math.Max(1, (int)MathF.Floor(_config.MaxReactionTimeMs / (1000.0f / 64.0f)));
		float pointsDelta = EvaluateShotPoints(pendingMatch, maxUnnaturalTicks, lowVarianceConsistency, isMacroVariance);

		if (pointsDelta <= 0.0f)
		{
			if (pointsDelta < 0.0f && st.EvidenceScore > 0.0f)
			{
				st.EvidenceScore = MathF.Max(0.0f, st.EvidenceScore + pointsDelta);
				StatNormalDecay++;
				if (steamId > 0)
					_persistentState[steamId] = (st.EvidenceScore, new List<TriggerIncident>(st.Incidents), new List<(DateTime, int, TriggerContactMode)>(st.ReactionHistory));
			}

			if (Utils.DebugMode)
				_debug($"[Triggerbot] {Name(attacker)} normal reaction: {pendingMatch.ReactionTicks}t (~{reactionMs:F1}ms, mode={pendingMatch.Mode}, score={st.EvidenceScore:F1}).");
			return;
		}

		st.EvidenceScore = MathF.Min(MaxEvidenceScore, st.EvidenceScore + pointsDelta);
		float targetScoreThreshold = MathF.Max(2.5f, _config.EvidenceThreshold * 2.5f);

		st.Incidents.Add(new TriggerIncident
		{
			Time = now,
			ReactionTicks = pendingMatch.ReactionTicks,
			ReactionMs = reactionMs,
			TargetId = victim.PlayerID,
			Weapon = normWeapon,
			Damage = damage,
			Mode = pendingMatch.Mode,
			PointsAwarded = pointsDelta,
			ResultingScore = st.EvidenceScore
		});

		if (steamId > 0)
		{
			_persistentState[steamId] = (st.EvidenceScore, new List<TriggerIncident>(st.Incidents), new List<(DateTime, int, TriggerContactMode)>(st.ReactionHistory));
		}

		if (Utils.DebugMode)
			_debug($"[Triggerbot] {Name(attacker)} suspicious reaction: {pendingMatch.ReactionTicks}t (~{reactionMs:F1}ms, mode={pendingMatch.Mode}, +{pointsDelta:F1}pts, var={varianceTicks:F2}t², normVar={normalizedVariance:F4}, cv={cvPercent:F1}%). Score: {st.EvidenceScore:F1}/{targetScoreThreshold:F1}.");

		if (st.EvidenceScore >= targetScoreThreshold && st.Incidents.Count >= Math.Max(2, _config.EvidenceThreshold - 1))
		{
			StatDetections++;

			var lines = new List<string>();
			for (int i = 0; i < st.Incidents.Count; i++)
			{
				var inc = st.Incidents[i];
				lines.Add($"{i + 1}. {inc.ReactionTicks} ticks (~{inc.ReactionMs:F1}ms) - {inc.Mode} (+{inc.PointsAwarded:F1}pts, {inc.Weapon}, {inc.Damage} dmg)");
			}

			string macroTag = lowVarianceConsistency
				? $" [FIXED-DELAY CADENCE: variance {varianceTicks:F2}t² (normVar={normalizedVariance:F4}, CV={cvPercent:F1}% < {AimMath.Baselines.PatternVariancePercent:F2}%) across {fastSampleCount} hits, mean {meanFastTicks:F1}t]"
				: "";

			string latest = $"Latest damaging shot: {pendingMatch.ReactionTicks} ticks (~{reactionMs:F1}ms, mode: {pendingMatch.Mode}){macroTag}. Rolling score {st.EvidenceScore:F1}/{targetScoreThreshold:F1} across {st.ReactionHistory.Count} evaluated contacts.";
			string evidence = string.Join("\n", lines) + "\n" + latest;

			_announce(attacker, "TRIGGERBOT", evidence);
			st.EvidenceScore = 0.0f;
			st.Incidents.Clear();
			st.ReactionHistory.Clear();
			if (steamId > 0)
				_persistentState.Remove(steamId);
		}
	}

	private static float ComputeFastReactionVariance(
		List<(DateTime Time, int ReactionTicks, TriggerContactMode Mode)> history,
		out int sampleCount,
		out float meanTicks,
		out float normalizedVariance,
		out float cvPercent)
	{
		sampleCount = 0;
		meanTicks = 0.0f;
		normalizedVariance = 999.0f;
		cvPercent = 999.0f;
		float sum = 0.0f;

		for (int i = 0; i < history.Count; i++)
		{
			int rt = history[i].ReactionTicks;
			if (rt is >= 0 and <= 10)
			{
				sum += rt;
				sampleCount++;
			}
		}

		if (sampleCount < 2)
			return 999.0f;

		meanTicks = sum / sampleCount;
		float varSum = 0.0f;
		for (int i = 0; i < history.Count; i++)
		{
			int rt = history[i].ReactionTicks;
			if (rt is >= 0 and <= 10)
			{
				float diff = rt - meanTicks;
				varSum += diff * diff;
			}
		}

		float varianceTicks = varSum / sampleCount;
		float denom = MathF.Max(1.0f, meanTicks * meanTicks);
		normalizedVariance = varianceTicks / denom;
		cvPercent = (MathF.Sqrt(varianceTicks) / MathF.Max(1.0f, meanTicks)) * 100.0f;
		return varianceTicks;
	}

	private static float EvaluateShotPoints(
		TriggerPendingAttack attack,
		int maxUnnaturalTicks,
		bool lowVarianceConsistency,
		bool isMacroVariance)
	{
		int rt = attack.ReactionTicks;
		float macroBonus = isMacroVariance ? 0.5f : 0.0f;

		if (attack.Mode == TriggerContactMode.TargetDriven)
		{
			if (rt <= 1)
				return (lowVarianceConsistency ? 3.5f : 3.0f) + macroBonus;
			if (rt <= 3)
				return (lowVarianceConsistency ? 3.0f : 2.5f) + macroBonus;
			if (rt <= maxUnnaturalTicks)
				return (lowVarianceConsistency ? 2.5f : 2.0f) + macroBonus;
			if (rt <= AimMath.Baselines.MinHumanReactionTicks && lowVarianceConsistency)
				return 2.0f + macroBonus;
			if (rt >= AimMath.Baselines.MinHumanReactionTicks)
				return -3.0f; // Normal human reaction (>= 150ms floor) decays score
			return -1.0f;
		}
		else // AimDriven (moving crosshair, peeking, micro-adjusting)
		{
			// Distinguish high-speed ballistic flicks from controlled aim / external triggerbots
			bool isFastBallisticFlick = attack.AcquisitionSpeedDegPerTick > 8.5f && attack.FireSpeedDegPerTick > 4.5f;

			if (rt <= 1)
			{
				if (!isFastBallisticFlick)
					return (lowVarianceConsistency ? 3.0f : 2.5f) + macroBonus;
				return lowVarianceConsistency ? 2.0f + macroBonus : 0.0f;
			}

			if (rt <= maxUnnaturalTicks)
			{
				// External triggerbots with 2-5 ticks (31-78ms) pipeline/humanizer delay during normal mouse movement:
				// If the crosshair dwelled on or tracked the target (controlled speed) or exhibits low timing variance, score it!
				bool controlledTrackingOrPeek = attack.MeanContactSpeedDegPerTick <= 5.0f && attack.FireSpeedDegPerTick <= 4.0f;
				if (lowVarianceConsistency)
					return 2.5f + macroBonus;
				if (controlledTrackingOrPeek)
					return 2.0f;
				return 0.0f;
			}

			if (rt <= AimMath.Baselines.MinHumanReactionTicks && lowVarianceConsistency)
				return 2.0f + macroBonus;

			if (rt >= 7)
				return -2.5f; // Normal reaction decays score

			return 0.0f;
		}
	}

	private (int TargetIndex, Vector HitPoint) TraceCrosshair(IPlayer player, in Vector eye, in QAngle angles, int currentTick)
	{
		var pawn = player.PlayerPawn;

		Vector forward = AimMath.Forward(angles);
		Vector end = new(
			eye.X + forward.X * TraceLength,
			eye.Y + forward.Y * TraceLength,
			eye.Z + forward.Z * TraceLength);

		int bestTarget = -1;
		float bestDist = float.MaxValue;
		Vector bestPoint = end;

		int attackerId = player.PlayerID;
		PositionFrame? snapshotFrame = _findFrame?.Invoke(currentTick);

		for (int i = 0; i < AimbotDetector.MaxPlayers; i++)
		{
			if (i == attackerId)
				continue;

			Vector origin;
			bool crouched = false;

			if (snapshotFrame != null)
			{
				ref readonly var tp = ref snapshotFrame.Players[i];
				if (!tp.Valid || !tp.Alive || !_areEnemies(attackerId, i))
					continue;
				origin = tp.Origin;
				crouched = tp.Crouched;
			}
			else
			{
				var target = _getPlayer(i);
				if (target is null || !target.IsValid || !PlayerSnapshotProvider.IsAlive(target) || !_areEnemies(attackerId, target.PlayerID))
					continue;

				if (!PlayerSnapshotProvider.TryGetPawnTransform(target, out origin, out _, out _, out _, out _, out crouched, out _, out _))
					continue;
			}

			if (!IntersectsAnatomicalHitbox(eye, forward, origin, crouched, bestDist, out float hitDist, out Vector hitPt))
				continue;

			bestDist = hitDist;
			bestTarget = i;
			bestPoint = hitPt;
		}

		if (bestTarget < 0)
			return (-1, end);

		// Verified line-of-sight check against world geometry (MaskTrace.Solid)
		if (_config.EnableNativeTrace && _traceManager != null)
		{
			try
			{
				float startOffset = Math.Min(24.0f, bestDist * 0.25f);
				Vector traceStart = new(eye.X + forward.X * startOffset, eye.Y + forward.Y * startOffset, eye.Z + forward.Z * startOffset);
				Vector traceEnd = new(bestPoint.X - forward.X * 4.0f, bestPoint.Y - forward.Y * 4.0f, bestPoint.Z - forward.Z * 4.0f);
				float traceLen = traceStart.Distance(traceEnd);

				var traceParams = new TraceParams
				{
					InteractWith = MaskTrace.Solid,
					EntitiesToIgnore = pawn is { IsValid: true }
						? new List<SwiftlyS2.Shared.SchemaDefinitions.CEntityInstance> { pawn }
						: new List<SwiftlyS2.Shared.SchemaDefinitions.CEntityInstance>()
				};

				var trace = _traceManager.TraceShapeLine(traceStart, traceEnd, traceParams);
				if (trace.DidHit && !trace.StartInSolid && trace.Distance < traceLen - 16.0f)
				{
					return (-1, trace.EndPos);
				}
			}
			catch { }
		}

		return (bestTarget, bestPoint);
	}

	/// <summary>
	/// Evaluates ray intersection against a crouch-aware 3-zone anatomical hitbox model (head sphere, torso/arms cylinder, legs cylinder).
	/// Prevents false early contacts in empty air beside the head and over crouched players while catching torso/limb shots.
	/// </summary>
	internal static bool IntersectsAnatomicalHitbox(
		in Vector eye,
		in Vector forward,
		in Vector origin,
		bool crouched,
		float maxDistance,
		out float hitDistance,
		out Vector hitPoint)
	{
		hitDistance = float.MaxValue;
		hitPoint = default;

		float vxySq = forward.X * forward.X + forward.Y * forward.Y;
		float t;
		if (vxySq > 1e-4f)
		{
			t = ((origin.X - eye.X) * forward.X + (origin.Y - eye.Y) * forward.Y) / vxySq;
		}
		else
		{
			float centerZ = crouched ? 32.0f : 46.0f;
			t = MathF.Abs(forward.Z) > 1e-4f ? (origin.Z + centerZ - eye.Z) / forward.Z : 0.0f;
		}

		if (t < 20.0f || t >= maxDistance)
			return false;

		float px = eye.X + forward.X * t;
		float py = eye.Y + forward.Y * t;
		float pz = eye.Z + forward.Z * t;
		float relZ = pz - origin.Z;

		float topZ = crouched ? 54.0f : 72.0f;
		if (relZ < -2.0f || relZ > topZ)
			return false;

		float latX = px - origin.X;
		float latY = py - origin.Y;
		float latDistSq = latX * latX + latY * latY;

		float headStartZ = crouched ? 40.0f : 56.0f;
		float torsoStartZ = crouched ? 18.0f : 28.0f;

		float allowedRadius;
		if (relZ >= headStartZ)
		{
			// Head & neck zone: tighter 9.0u radius to prevent early contact in empty air beside the ears
			allowedRadius = 9.0f;
		}
		else if (relZ >= torsoStartZ)
		{
			// Torso, shoulders, and weapon-holding arms
			allowedRadius = 17.5f;
		}
		else
		{
			// Legs / pelvis
			allowedRadius = 14.0f;
		}

		if (latDistSq > allowedRadius * allowedRadius)
			return false;

		hitDistance = t;
		hitPoint = new Vector(px, py, pz);
		return true;
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
