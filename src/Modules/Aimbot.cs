/**
 * =============================================================================
 * Aegis Anti-Cheat (AegisAC) - Module: Aimbot
 *
 * Copyright (C) 2026 swisser & sevon
 * Authors:
 *   - swisser (https://github.com/ressiws)
 *   - sevon   (https://github.com/victor201202)
 *
 * Description:
 *   Server-side aimbot detection module analyzing user command viewangle
 *   convergence, single-tick snaps, and smooth trajectory envelopes
 *   correlated with weapon fire and damage events.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at:
 *     http://www.apache.org/licenses/LICENSE-2.0
 * =============================================================================
 */

using AegisAC.Configuration;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;

namespace AegisAC;

internal sealed class AimCommand
{
	public int CommandNumber;
	public int ClientTick;
	public int ServerTick = -1;
	public QAngle Angles;
	public bool IsShot;
	public bool Simulated;
	public bool Consumed;
}

internal sealed class ShotRecord
{
	public required AimCommand Command { get; init; }
	public required int FireTick { get; init; }
	public required string Weapon { get; init; }

	public int VictimId { get; set; } = -1;
	public bool HurtSeen { get; set; }
}


internal struct TrackedPosition
{
	public Vector Origin;
	public Vector Eye;
	public int Team;
	public bool Valid;
	public bool Alive;
	public bool Teleported;
}

internal sealed class PositionFrame
{
	public int ServerTick = -1;
	public readonly TrackedPosition[] Players = new TrackedPosition[AimbotDetector.MaxPlayers + 1];
}

internal enum EvidenceType { Convergence, SnapReturn, SmoothConvergence }

internal readonly record struct AimbotIncident(DateTime Time, EvidenceType Type, float Movement, float Before, float After);

internal sealed class PlayerState
{
	public readonly List<AimCommand> Commands = new();
	public int PendingShot = -1;
	public int VictimId = -1;
	public int LastCountedIncidentCommand;
	public bool Pending;
	public bool HasCountedIncident;
	public int LastIdx;
	public int LastHistCount;
	public bool LastHasVA;
	public readonly List<AimbotIncident> SnapEvidence = new();
	public readonly List<AimbotIncident> SmoothEvidence = new();
	public readonly List<ShotRecord> Shots = new();
}


internal sealed class AimbotDetector
{
	public const int MaxPlayers = 64;

	public long StatUsercmds, StatShotCmds, StatSetupMoves, StatSetupMiss, StatFrames, StatHurts, StatHurtNoMatch, StatMatched, StatEvaluations, StatDetections;

	public long StatWeaponFires, StatFireMatched, StatFireNoMatch;

	public long StatStored, StatSkipDup, StatSkipNoBase, StatSkipNoViewangles, StatSkipNonFinite;

	public string LastFireDiag = "n/a";

	public string LastSetupMissDiag = "n/a";

	public long StatAgeLtNeg1, StatAgeEqNeg1, StatAgeEq0, StatAgeEq1, StatAgeGt1, StatAgeNoSim, StatAgeNone;

	public string Stats() =>
	$"usercmds={StatUsercmds} shotCmds={StatShotCmds} stored={StatStored} skipDup={StatSkipDup} skipNoBase={StatSkipNoBase} skipNoVA={StatSkipNoViewangles} skipNonFinite={StatSkipNonFinite} setupMoves={StatSetupMoves} setupMiss={StatSetupMiss} frames={StatFrames} " +
	$"weaponFire={StatWeaponFires} fireMatched={StatFireMatched} fireNoMatch={StatFireNoMatch} " +
	$"hurts={StatHurts} hurtsNoMatch={StatHurtNoMatch} matched={StatMatched} evaluations={StatEvaluations} detections={StatDetections} lastFire=[{LastFireDiag}] lastSetupMiss=[{LastSetupMissDiag}] " +
	$"ageHist=[lt-1:{StatAgeLtNeg1} -1:{StatAgeEqNeg1} 0:{StatAgeEq0} 1:{StatAgeEq1} gt1:{StatAgeGt1} noSim:{StatAgeNoSim} none:{StatAgeNone}]";

	private const int CommandHistorySize = 128;
	private const int PositionHistoryLimit = 128;
	private const int TickRate = 64;
	private const int SnapWindowTicks = (int)(TickRate * 0.5f);
	private const int MaxClientTickGap = 2;
	private const float MinimumDistance = 100.0f;
	private const int SnapDetectionThreshold = 3;
	private const int SmoothDetectionThreshold = 3;
	private const float TeleportDistance = 128.0f; // Position delta between consecutive ticks considered a teleport
	private static readonly TimeSpan EvidenceWindow = TimeSpan.FromMinutes(5);

	private const float SmoothMinimumImprovement = 0.94f;
	private const float SmoothMaximumFinalError = 1.0f;
	private const float SmoothMinimumStep = 0.15f;
	private const float SmoothRelativeStep = 0.10f;
	private const int SmoothMinimumSteps = 3;

	private static readonly HashSet<string> BallisticWeapons = new()
	{
		"ak47","aug","awp","bizon","cz75a","deagle","elite","famas","fiveseven","g3sg1","galilar","glock",
		"hkp2000","m249","m4a1","m4a1_silencer","mac10","mag7","mp5sd","mp7","mp9","negev","nova","p250",
		"p90","revolver","sawedoff","scar20","sg556","ssg08","taser","tec9","ump45","usp_silencer","xm1014",
	};

	private AimbotConfig _settings;
	private readonly Action<IPlayer, string, string> _announce;
	private readonly Action<string> _debug;
	private readonly Func<int, IPlayer?> _getPlayer;

	private readonly PlayerState[] _players = new PlayerState[MaxPlayers + 1];
	private readonly PositionFrame[] _frames = new PositionFrame[PositionHistoryLimit];

	public void UpdateConfig(AimbotConfig settings) => _settings = settings;

	public AimbotDetector(AimbotConfig settings, Func<int, IPlayer?> getPlayer, Action<IPlayer, string, string> announce, Action<string> debug)
	{
		_settings = settings;
		_getPlayer = getPlayer;
		_announce = announce;
		_debug = debug;
		for (int i = 0; i < _players.Length; i++) _players[i] = new PlayerState();
		for (int i = 0; i < _frames.Length; i++) _frames[i] = new PositionFrame();
	}

	public void Reset()
	{
		for (int i = 0; i < _players.Length; i++) _players[i] = new PlayerState();
		for (int i = 0; i < _frames.Length; i++)
		{
			_frames[i].ServerTick = -1;
			Array.Clear(_frames[i].Players);
		}
	}

	public void OnClientDisconnect(int id)
	{
		if (id < 0 || id > MaxPlayers)
			return;

		_players[id] = new PlayerState();

		for (int i = 0; i < _frames.Length; i++)
			_frames[i].Players[id] = default;
	}

	public static bool IsEligibleHuman(IPlayer? p) =>
		p is { IsValid: true, IsFakeClient: false } && p.PlayerPawn is { IsValid: true };

	public bool AreOpponents(int a, int b)
	{
		bool aPlays = a == 2 || a == 3, bPlays = b == 2 || b == 3;
		return aPlays && bPlays && (a != b || _settings.TeammatesAreEnemies);
	}

	public bool AreEnemies(int aId, int bId)
	{
		var pA = _getPlayer(aId);
		var pB = _getPlayer(bId);
		if (pA is null || pB is null) return false;
		int teamA = pA.PlayerPawn?.TeamNum ?? 0;
		int teamB = pB.PlayerPawn?.TeamNum ?? 0;
		return AreOpponents(teamA, teamB);
	}

	public void OnProcessUsercmds(IPlayer player, List<SwiftlyS2.Shared.GameHooks.IUserCmd> cmds)
	{
		if (!_settings.Enabled || !IsEligibleHuman(player) || cmds.Count == 0)
			return;

		var st = _players[player.PlayerID];

		foreach (var cmd in cmds)
		{
			var pb = cmd.CSGOUserCmd;
			int number = (int)cmd.CommandNumber;

			StatUsercmds++;

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
			{
				StatSkipDup++;
				continue;
			}

			if (!pb.Accessor.HasField("base"))
			{
				StatSkipNoBase++;
				continue;
			}

			var b = pb.Base;

			if (!b.Accessor.HasField("viewangles"))
			{
				StatSkipNoViewangles++;
				continue;
			}

			QAngle angles = b.Viewangles;
			bool isShot = false;
			int idx = pb.Attack1StartHistoryIndex;
			int histCount = pb.InputHistory.Count;
			bool hasVA = false;

			if (idx >= 0 && idx < histCount)
			{
				var entry = pb.InputHistory.Get(idx);
				hasVA = entry.Accessor.HasField("view_angles");

				if (hasVA)
				{
					QAngle shotAngles = entry.ViewAngles;
					if (AimMath.IsFinite(shotAngles))
					{
						angles = shotAngles;
						isShot = true;
						StatShotCmds++;
					}
				}
			}

			st.LastIdx = idx;
			st.LastHistCount = histCount;
			st.LastHasVA = hasVA;

			if (!AimMath.IsFinite(angles))
			{
				StatSkipNonFinite++;
				continue;
			}

			st.Commands.Add(new AimCommand { CommandNumber = number, ClientTick = b.ClientTick, Angles = angles, IsShot = isShot });
			StatStored++;

			if (st.Commands.Count > CommandHistorySize)
				st.Commands.RemoveAt(0);
		}
	}

	public void OnSetupMove(IPlayer player, int commandNumber, int currentTick)
	{
		if (!_settings.Enabled || !IsEligibleHuman(player))
			return;

		var st = _players[player.PlayerID];
		var cmd = st.Commands.FindLast(c => c.CommandNumber == commandNumber);

		if (cmd == null)
		{
			StatSetupMiss++;
			LastSetupMissDiag = $"cmd {commandNumber}, stored latest {(st.Commands.Count > 0 ? st.Commands[^1].CommandNumber : -1)}";
			return;
		}

		cmd.ServerTick = currentTick;
		cmd.Simulated = true;
		StatSetupMoves++;

		if (st.Pending)
			Evaluate(player, st, currentTick);
	}

	public void OnGameFrame(int currentTick)
	{
		CaptureFrame(currentTick);
		StatFrames++;

		if (!_settings.Enabled)
			return;

		for (int id = 0; id < MaxPlayers; id++)
		{
			var st = _players[id];

			st.Shots.RemoveAll(s => (long)currentTick - s.FireTick > 2);

			if (!st.Pending)
				continue;

			var p = _getPlayer(id);
			if (!IsEligibleHuman(p))
			{
				_players[id] = new PlayerState();
				continue;
			}

			Evaluate(p!, st, currentTick);
		}
	}

	private void CaptureFrame(int tick)
	{
		int slot = tick & (PositionHistoryLimit - 1);
		var frame = _frames[slot];
		frame.ServerTick = tick;

		int prevSlot = (tick - 1) & (PositionHistoryLimit - 1);
		var previous = _frames[prevSlot];
		bool hasPrevious = previous.ServerTick == tick - 1;

		for (int id = 0; id < MaxPlayers; id++)
		{
			var p = _getPlayer(id);
			var pawn = p?.PlayerPawn;

			if (p == null || !p.IsValid || pawn == null || !pawn.IsValid)
			{
				frame.Players[id] = default;
				continue;
			}

			Vector? maybeOrigin = pawn.AbsOrigin;
			if (!maybeOrigin.HasValue || !AimMath.IsFinite(maybeOrigin.Value))
			{
				frame.Players[id] = default;
				continue;
			}

			Vector origin = maybeOrigin.Value;
			var eye = pawn.EyePosition ?? new Vector(origin.X, origin.Y, origin.Z + 64.0f);

			bool teleported = false;
			if (hasPrevious && previous.Players[id].Valid)
				teleported = origin.Distance(previous.Players[id].Origin) > TeleportDistance;

			frame.Players[id] = new TrackedPosition
			{
				Origin = origin,
				Eye = eye,
				Team = pawn.TeamNum,
				Valid = true,
				Alive = pawn.LifeState == 0,
				Teleported = teleported,
			};
		}
	}

	public PositionFrame? FindFrame(int tick)
	{
		if (tick < 0) return null;
		int slot = tick & (PositionHistoryLimit - 1);
		var frame = _frames[slot];
		return frame.ServerTick == tick ? frame : null;
	}

	private bool FindPosition(int tick, int id, out TrackedPosition pos)
	{
		if (tick < 0 || id < 0 || id > MaxPlayers)
		{
			pos = default;
			return false;
		}

		int slot = tick & (PositionHistoryLimit - 1);
		var frame = _frames[slot];
		if (frame.ServerTick == tick)
		{
			pos = frame.Players[id];
			return pos.Valid;
		}

		pos = default;
		return false;
	}


	public void OnWeaponFire(IPlayer player, string weapon, int currentTick)
	{
		if (!_settings.Enabled || !IsEligibleHuman(player))
			return;

		if (weapon.StartsWith("weapon_", StringComparison.Ordinal))
			weapon = weapon[7..];

		if (!BallisticWeapons.Contains(weapon))
			return;

		StatWeaponFires++;

		var st = _players[player.PlayerID];

		var newestShotCmd = st.Commands.LastOrDefault(c => c.IsShot);
		if (newestShotCmd == null)
			StatAgeNone++;
		else if (!newestShotCmd.Simulated)
			StatAgeNoSim++;
		else
		{
			long age = (long)currentTick - newestShotCmd.ServerTick;
			if (age < -1) StatAgeLtNeg1++;
			else if (age == -1) StatAgeEqNeg1++;
			else if (age == 0) StatAgeEq0++;
			else if (age == 1) StatAgeEq1++;
			else StatAgeGt1++;
		}

		AimCommand? matchedCandidate = null;
		int candidateCount = 0;
		for (int ci = st.Commands.Count - 1; ci >= 0; ci--)
		{
			var c = st.Commands[ci];
			if (c.IsShot && c.Simulated && !c.Consumed && (long)currentTick - c.ServerTick is >= -1 and <= 1)
			{
				candidateCount++;
				if (candidateCount == 1)
				{
					matchedCandidate = c;
				}
				else
				{
					c.Consumed = true;
				}
			}
		}

		if (candidateCount != 1 || matchedCandidate == null)
		{
			if (matchedCandidate != null)
				matchedCandidate.Consumed = true;

			StatFireNoMatch++;

			AimCommand? lastShotCmd = null;
			int histShot = 0;
			for (int ci = st.Commands.Count - 1; ci >= 0; ci--)
			{
				if (st.Commands[ci].IsShot)
				{
					histShot++;
					lastShotCmd ??= st.Commands[ci];
				}
			}

			LastFireDiag = lastShotCmd == null
				? $"tick={currentTick} cands={candidateCount} histShot=0 lastShot=none"
				: $"tick={currentTick} cands={candidateCount} histShot={histShot} lastShot=cmd{lastShotCmd.CommandNumber} sim={lastShotCmd.Simulated} serverTick={lastShotCmd.ServerTick} age={(lastShotCmd.ServerTick < 0 ? "NA" : $"{currentTick - lastShotCmd.ServerTick}")}";

			_debug($"{Name(player)} weapon_fire unmatched: {candidateCount} candidate commands at tick {currentTick} (lastCmd idx={st.LastIdx} histCount={st.LastHistCount} hasVA={st.LastHasVA} recentShotCmds={histShot}/{st.Commands.Count}).");
			return;
		}

		var command = matchedCandidate;
		command.Consumed = true;

		st.Shots.Add(new ShotRecord
		{
			Command = command,
			FireTick = currentTick,
			Weapon = weapon
		});

		if (st.Shots.Count > 8)
			st.Shots.RemoveAt(0);

		StatFireMatched++;

		_debug($"{Name(player)} weapon_fire matched command {command.CommandNumber}, server tick {command.ServerTick}, fire tick {currentTick}.");
	}

	public int? OnPlayerHurt(IPlayer attacker, IPlayer victim, string weapon, int currentTick)
	{
		if (!_settings.Enabled || !IsEligibleHuman(attacker) || !victim.IsValid || attacker.PlayerID == victim.PlayerID)
			return null;

		if (weapon.StartsWith("weapon_", StringComparison.Ordinal))
			weapon = weapon[7..];

		StatHurts++;

		if (!BallisticWeapons.Contains(weapon))
		{
			_debug($"{Name(attacker)} hurt ignored: weapon '{weapon}' is not ballistic.");
			return null;
		}

		var st = _players[attacker.PlayerID];

		ShotRecord? matchedShot = null;
		int shotCount = 0;
		for (int si = st.Shots.Count - 1; si >= 0; si--)
		{
			var s = st.Shots[si];
			if (!s.HurtSeen && (long)currentTick - s.FireTick is >= 0 and <= 1)
			{
				shotCount++;
				if (shotCount == 1)
					matchedShot = s;
			}
		}

		if (shotCount != 1 || matchedShot == null)
		{
			StatHurtNoMatch++;
			_debug($"{Name(attacker)} hurt unmatched: {shotCount} registered shots at tick {currentTick}.");
			return null;
		}

		var shot = matchedShot;
		shot.HurtSeen = true;
		shot.VictimId = victim.PlayerID;

		var command = shot.Command;

		StatMatched++;

		if (st.Pending)
		{
			Evaluate(attacker, st, command.ServerTick);

			if (st.Pending)
			{
				st.Pending = false;
				st.PendingShot = -1;
				st.VictimId = -1;
			}
		}

		st.PendingShot = command.CommandNumber;
		st.VictimId = victim.PlayerID;
		st.Pending = true;

		_debug($"{Name(attacker)} correlated damage with shot command {command.CommandNumber}, server tick {command.ServerTick}, fire tick {shot.FireTick}.");

		Evaluate(attacker, st, command.ServerTick);
		return command.CommandNumber;
	}

	private readonly record struct SmoothPoint(AimCommand Command, float Error);

	private static AimCommand? FindCommand(PlayerState st, int number) =>
		st.Commands.FindLast(c => c.CommandNumber == number && c.Simulated);

	private bool MeetsSmoothThresholds(float movement, float improvement, float finalError, float efficiency, int steps, bool shrinking) =>
		movement >= _settings.SmoothMinimumMovement && improvement >= SmoothMinimumImprovement && finalError <= SmoothMaximumFinalError
		&& efficiency >= _settings.SmoothMinimumPathEfficiency && steps >= SmoothMinimumSteps && shrinking;

	private static string Name(IPlayer p) => p.Controller?.PlayerName ?? $"#{p.PlayerID}";

	private bool Evaluate(IPlayer attacker, PlayerState st, int currentTick)
	{
		if (!st.Pending)
			return true;

		StatEvaluations++;
		int attackerId = attacker.PlayerID;

		void Clear()
		{
			st.PendingShot = -1;
			st.VictimId = -1;
			st.Pending = false;
		}

		var shot = st.Commands.Find(c => c.CommandNumber == st.PendingShot);
		if (shot == null || !shot.Simulated || st.VictimId < 0 || st.VictimId >= MaxPlayers)
		{
			Clear();
			return true;
		}

		bool haveTarget = FindPosition(shot.ServerTick, st.VictimId, out var shotTarget);
		bool haveAttacker = FindPosition(shot.ServerTick, attackerId, out var shotAttacker);
		if (!haveTarget || !haveAttacker)
		{
			if (currentTick <= shot.ServerTick)
				return false;

			_debug($"{Name(attacker)} rejected because target history is missing for server tick {shot.ServerTick}.");
			Clear();

			return true;
		}

		if (shotTarget.Teleported || shotAttacker.Teleported)
		{
			_debug($"{Name(attacker)} rejected: teleport.");
			Clear();
			return true;
		}

		if (!AreOpponents(shotAttacker.Team, shotTarget.Team))
		{
			_debug($"{Name(attacker)} rejected: not an enemy.");
			Clear();
			return true;
		}

		float distance = shotTarget.Origin.Distance(shotAttacker.Eye);
		if (!float.IsFinite(distance) || distance < MinimumDistance)
		{
			_debug($"{Name(attacker)} rejected: distance {distance:F1} below {MinimumDistance:F0}.");
			Clear();

			return true;
		}

		bool suspicious = false;
		float largestSnap = 0, bestBefore = 0, bestAfter = 0;
		float largestMeasuredSnap = -1, measuredBefore = 0, measuredAfter = 0;
		int measuredMovements = 0;
		string measurementFailure = "no continuous adjacent command history was available";
		bool reusedSnap = false;
		var matchedRule = EvidenceType.Convergence;
		bool ruleMatched = false;

		int smoothBodyPoint = 0;
		float smoothShotError = 180.0f;
		for (int bp = 0; bp < AimMath.BodyPointCount; bp++)
		{
			var t = new Vector(shotTarget.Origin.X, shotTarget.Origin.Y, shotTarget.Origin.Z + AimMath.BodyHeight(bp));
			float e = AimMath.AimError(shotAttacker.Eye, shot.Angles, t);
			if (e < smoothShotError)
			{
				smoothShotError = e;
				smoothBodyPoint = bp;
			}
		}

		var smoothPoints = new List<SmoothPoint> { new(shot, smoothShotError) };

		AimCommand newer = shot;
		while (true)
		{
			var older = FindCommand(st, newer.CommandNumber - 1);
			if (older == null)
			{
				measurementFailure = $"the previous simulated command was missing (looked for cmd {newer.CommandNumber - 1} of {st.Commands.Count} stored)";
				break;
			}

			long serverGap = (long)newer.ServerTick - older.ServerTick;
			long clientGap = (long)newer.ClientTick - older.ClientTick;
			if (clientGap < 0 || clientGap > MaxClientTickGap || serverGap < 0 || serverGap > 1)
			{
				measurementFailure = $"the history was discontinuous (cmd {newer.CommandNumber}->{older.CommandNumber}, " +
					$"client {newer.ClientTick}->{older.ClientTick} gap {clientGap} outside 0..{MaxClientTickGap}, " +
					$"server {newer.ServerTick}->{older.ServerTick} gap {serverGap} outside 0..1)";
				break;
			}

			if ((long)shot.ServerTick - older.ServerTick > SnapWindowTicks)
			{
				measurementFailure = "the preceding command was outside the 0.5-second snap window";
				break;
			}

			if (!FindPosition(older.ServerTick, st.VictimId, out var olderTarget)
				|| !FindPosition(newer.ServerTick, st.VictimId, out var newerTarget)
				|| !FindPosition(older.ServerTick, attackerId, out var olderAttacker)
				|| !FindPosition(newer.ServerTick, attackerId, out var newerAttacker)
				|| olderTarget.Teleported || newerTarget.Teleported || olderAttacker.Teleported || newerAttacker.Teleported)
			{
				measurementFailure = "matching historical player positions were missing or interrupted by a teleport";
				break;
			}

			float snap = AimMath.AngularDistance(older.Angles, newer.Angles);
			float before = AimMath.NearestBodyAimError(olderAttacker.Eye, older.Angles, olderTarget.Origin);
			float after = AimMath.NearestBodyAimError(newerAttacker.Eye, newer.Angles, newerTarget.Origin);

			if (!float.IsFinite(snap) || !float.IsFinite(before) || !float.IsFinite(after))
			{
				measurementFailure = "an angle or target-error measurement was invalid";
				break;
			}

			var fixedPoint = new Vector(olderTarget.Origin.X, olderTarget.Origin.Y, olderTarget.Origin.Z + AimMath.BodyHeight(smoothBodyPoint));
			float smoothError = AimMath.AimError(olderAttacker.Eye, older.Angles, fixedPoint);
			if (!float.IsFinite(smoothError))
			{
				measurementFailure = "a fixed-body target-error measurement was invalid";
				break;
			}

			smoothPoints.Add(new(older, smoothError));
			measuredMovements++;
			if (snap > largestMeasuredSnap)
			{
				largestMeasuredSnap = snap;
				measuredBefore = before; measuredAfter = after;
			}

			// Vanguard SNAP_TRESHOLD benchmark: >= 90.0° in <16ms (1 tick) terminating on-target (blatant rage snap)
			bool isBlatantRageSnap = snap >= 90.0f && serverGap <= 1 && clientGap <= 1 && after <= 10.0f;
			bool converged = isBlatantRageSnap
				|| (snap >= _settings.SnapAngleThreshold && after < before * 0.2f)
				|| (snap >= (_settings.SnapAngleThreshold * 0.6f) && after < before * 0.1f);
			bool fresh = !st.HasCountedIncident || newer.CommandNumber > st.LastCountedIncidentCommand;
			if (converged && fresh)
			{
				suspicious = true;
				matchedRule = EvidenceType.Convergence;
				ruleMatched = true;
				if (snap > largestSnap)
				{
					largestSnap = snap;
					bestBefore = before;
					bestAfter = after;
				}
			}
			else if (converged)
				reusedSnap = true;

			newer = older;
		}

		var previous = FindCommand(st, st.PendingShot - 1);
		var next = FindCommand(st, st.PendingShot + 1);
		if (!suspicious && next == null && (long)currentTick - shot.ServerTick <= 1)
			return false;

		if (previous != null && next != null
			&& shot.CommandNumber - previous.CommandNumber == 1 && next.CommandNumber - shot.CommandNumber == 1
			&& (long)shot.ClientTick - previous.ClientTick is >= 0 and <= MaxClientTickGap
			&& (long)next.ClientTick - shot.ClientTick is >= 0 and <= MaxClientTickGap
			&& (long)shot.ServerTick - previous.ServerTick is >= 0 and <= 1
			&& (long)next.ServerTick - shot.ServerTick is >= 0 and <= 1)
		{
			float surrounding = AimMath.AngularDistance(previous.Angles, next.Angles);
			float snap = AimMath.AngularDistance(previous.Angles, shot.Angles);
			bool fresh = !st.HasCountedIncident || shot.CommandNumber > st.LastCountedIncidentCommand;

			// SnapReturn detection (silent aim flicking away on shot tick and instantly returning).
			// Threshold is set to >= 10.0° to eliminate physical mechanical mouse-click sensor noise (0.2° to 1.0°).
			if (fresh && float.IsFinite(surrounding) && float.IsFinite(snap) && surrounding < 5.0f && snap >= 10.0f && snap > surrounding * 4.0f)
			{
				if (!suspicious)
				{
					matchedRule = EvidenceType.SnapReturn;
					ruleMatched = true;
					largestSnap = snap;
				}

				suspicious = true;
			}
		}

		float smoothMovement = 0, smoothBefore = 0, smoothAfter = 0;
		if (!suspicious && smoothPoints.Count >= SmoothMinimumSteps + 1)
		{
			smoothPoints.Reverse();
			var movements = new List<float>(smoothPoints.Count - 1);

			for (int i = 1; i < smoothPoints.Count; i++)
				movements.Add(AimMath.AngularDistance(smoothPoints[i - 1].Command.Angles, smoothPoints[i].Command.Angles));

			int start = 0;
			for (int i = 1; i < movements.Count; i++)
				if (movements[i] > movements[start])
					start = i;

			float largest = movements[start];

			float path = 0;
			for (int i = start; i < movements.Count; i++)
				path += movements[i];

			smoothMovement = AimMath.AngularDistance(smoothPoints[start].Command.Angles, smoothPoints[^1].Command.Angles);
			smoothBefore = smoothPoints[start].Error;
			smoothAfter = smoothPoints[^1].Error;

			float efficiency = path > AimMath.Epsilon ? smoothMovement / path : 0.0f;
			float meaningfulMinimum = MathF.Max(SmoothMinimumStep, largest * SmoothRelativeStep);
			float previousMeaningful = float.PositiveInfinity;
			bool shrinking = true;
			int steps = 0;

			for (int i = start; i < movements.Count; i++)
			{
				if (movements[i] < meaningfulMinimum)
					continue;

				if (movements[i] >= previousMeaningful)
					shrinking = false;

				previousMeaningful = movements[i];
				steps++;
			}

			float improvement = smoothBefore > AimMath.Epsilon ? 1.0f - smoothAfter / smoothBefore : 0.0f;
			bool fresh = !st.HasCountedIncident || shot.CommandNumber > st.LastCountedIncidentCommand;
			if (fresh && MeetsSmoothThresholds(smoothMovement, improvement, smoothAfter, efficiency, steps, shrinking))
			{
				suspicious = true;
				matchedRule = EvidenceType.SmoothConvergence;
				ruleMatched = true;
				largestSnap = smoothMovement;
				bestBefore = smoothBefore;
				bestAfter = smoothAfter;
			}
			else
			{
				_debug($"{Name(attacker)} smooth curve rejected: movement {smoothMovement:F2}/{_settings.SmoothMinimumMovement:F2}, " +
					   $"error {smoothBefore:F2}->{smoothAfter:F2}/{SmoothMaximumFinalError:F2}, path {efficiency * 100:F1}%, steps {steps}/{SmoothMinimumSteps}, shrinking {shrinking}.");
			}
		}

		int incidentCommand = shot.CommandNumber;
		Clear();
		if (!suspicious || !ruleMatched)
		{
			if (reusedSnap)
				_debug($"{Name(attacker)} ignored a snap that already counted for an earlier damaging shot.");

			else if (measuredMovements == 0)
				_debug($"{Name(attacker)} damaging shot rejected because {measurementFailure}.");

			else
				_debug($"{Name(attacker)} damaging shot rejected after {measuredMovements} adjacent movements (stopped: {measurementFailure}): largest {largestMeasuredSnap:F2}°, target error {measuredBefore:F2} -> {measuredAfter:F2}.");

			return true;
		}

		st.LastCountedIncidentCommand = incidentCommand;
		st.HasCountedIncident = true;

		var now = DateTime.UtcNow;
		st.SnapEvidence.RemoveAll(i => now - i.Time >= EvidenceWindow);
		st.SmoothEvidence.RemoveAll(i => now - i.Time >= EvidenceWindow);

		var incidents = matchedRule == EvidenceType.SmoothConvergence ? st.SmoothEvidence : st.SnapEvidence;
		incidents.Add(new AimbotIncident(now, matchedRule, largestSnap, bestBefore, bestAfter));
		int threshold = matchedRule == EvidenceType.SmoothConvergence ? SmoothDetectionThreshold : SnapDetectionThreshold;

		_debug($"{Name(attacker)} counted {matchedRule} {largestSnap:F2}°, error {bestBefore:F2} -> {bestAfter:F2}, evidence {incidents.Count}/{threshold}.");

		if (incidents.Count >= threshold)
		{
			var lines = new List<string>();
			for (int i = 0; i < incidents.Count; i++)
			{
				var inc = incidents[i];
				lines.Add(inc.Type switch
				{
					EvidenceType.SnapReturn => $"{i + 1}. {inc.Movement:F2}° snap-return",
					EvidenceType.SmoothConvergence => $"{i + 1}. {inc.Movement:F2}° smooth move ({inc.Before:F2}°→{inc.After:F2}°)",
					_ => $"{i + 1}. {inc.Movement:F2}° sudden move ({inc.Before:F2}°→{inc.After:F2}°)",
				});
			}

			string latest = matchedRule switch
			{
				EvidenceType.SnapReturn => $"Latest damaging shot: aim jumped {largestSnap:F2}° and immediately returned. Evidence {incidents.Count}/{threshold} in 5 min.",
				EvidenceType.SmoothConvergence => $"Latest damaging shot: unusually clean {largestSnap:F2}° curve, enemy distance {bestBefore:F2}° → {bestAfter:F2}°. Evidence {incidents.Count}/{threshold} in 5 min.",
				_ => $"Latest damaging shot: aim moved {largestSnap:F2}° in one step, enemy distance {bestBefore:F2}° → {bestAfter:F2}°. Evidence {incidents.Count}/{threshold} in 5 min.",
			};

			StatDetections++;
			_announce(attacker, "AIMBOT", string.Join("\n", lines) + "\n" + latest);
			st.SnapEvidence.Clear();
			st.SmoothEvidence.Clear();
		}

		return true;
	}
}