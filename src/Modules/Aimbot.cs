/**
 * =============================================================================
 * Aegis Anti-Cheat (AegisAC) - Module: Aimbot, SilentAim & Doubletap
 *
 * Copyright (C) 2026 swisser & sevon
 * Authors:
 *   - swisser (https://github.com/ressiws)
 *   - sevon   (https://github.com/victor201202)
 *
 * Description:
 *   Server-side aimbot detection module analyzing user command viewangle
 *   convergence, single-tick snaps, backward-anchored smooth trajectories,
 *   subtick silent-aim desynchronization, and rapid-fire doubletap exploits
 *   with lag-compensated multi-hypothesis target reconstruction.
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
using AegisAC.Modules;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.ProtobufDefinitions;

namespace AegisAC;

internal sealed class AimCommand
{
	public int CommandNumber;
	public int ClientTick;
	public int ServerTick = -1;
	public QAngle BaseAngles;
	public QAngle Angles;
	public bool HasAttackAngles;
	public float SubtickPitchTravel;
	public float SubtickYawTravel;
	public bool SubtickAnglesValid;
	public Vector EyePosition;
	public bool EyePositionValid;
	public bool Airborne;
	public bool Scoped;
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
	public bool AccuracyCounted { get; set; }
	public bool AccuracyHitCounted { get; set; }
	public bool IrregularCounted { get; set; }
	public HashSet<int> EvaluatedVictims { get; } = new();
}

internal struct IrregularSample
{
	public DateTime Time;
	public bool Success;
	public int Points;
}

internal struct AccuracySample
{
	public DateTime Time;
	public bool Hit;
}

internal struct FireBulletsSample
{
	public int ServerTick;
	public uint WeaponId;
	public float Inaccuracy;
	public float Spread;
	public QAngle Angles;
	public bool Valid;
}

internal struct TrackedPosition
{
	public Vector Origin;
	public Vector Eye;
	public int Team;
	public bool Valid;
	public bool Alive;
	public bool Teleported;
	public bool Crouched;
	public bool Airborne;
	public bool Scoped;
	public int PingMs;
	public float InterpTicks;
}

internal sealed class PositionFrame
{
	public int ServerTick = -1;
	public readonly TrackedPosition[] Players = new TrackedPosition[AimbotDetector.MaxPlayers + 1];
}

internal enum EvidenceType
{
	Convergence,
	SnapReturn,
	SmoothConvergence,
	SilentAim,
	Doubletap
}

internal readonly record struct AimbotIncident(
	DateTime Time,
	EvidenceType Type,
	float Movement,
	float Before,
	float After,
	float Weight = 1.0f);

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
	public readonly List<AimbotIncident> SilentEvidence = new();
	public readonly List<ShotRecord> Shots = new();

	public FireBulletsSample LastFireBullets;
	public float SilentAimScore;
	public int LastWeaponFireTick = -1;
	public string LastWeaponFireName = string.Empty;
	public float DoubletapScore;
	public DateTime LastUntrustedAlertTime = DateTime.MinValue;
	public DateTime LastDoubletapAlertTime = DateTime.MinValue;

	// RCS / No-Recoil spray pattern state (Vanguard RECOIL_THRESHOLD & PATTERN_VARIANCE < 8.157%)
	public readonly QAngle[] SprayShotAngles = new QAngle[32];
	public int SprayShotCount;
	public int LastSprayFireTick = -1;
	public string LastSprayWeapon = string.Empty;
	public float RcsScore;
	public DateTime LastRcsAlertTime = DateTime.MinValue;

	// Irregular Behavior (Airborne / Sniper No-Scope impossible conversion rate, ported from CS2AC Legacy)
	public readonly List<IrregularSample> IrregularHistory = new();
	public DateTime LastIrregularAlertTime = DateTime.MinValue;

	// Inhuman Accuracy (PREDICTION_THRESHOLD > 95% accuracy across >= 35 aimed shots at >= 100u)
	public readonly List<AccuracySample> AccuracyHistory = new();
	public DateTime LastAccuracyAlertTime = DateTime.MinValue;
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
	private const float SilentAimScoreThreshold = 10.0f;
	private const float DoubletapScoreThreshold = 6.0f;
	private const float TeleportDistance = 128.0f;
	private static readonly TimeSpan EvidenceWindow = TimeSpan.FromMinutes(5);

	private const float SmoothMinimumImprovement = 0.90f;
	private const float SmoothMaximumFinalError = 1.15f;
	private const float SmoothMinimumStep = 0.12f;
	private const float SmoothRelativeStep = 0.08f;
	private const int SmoothMinimumSteps = 3;

	private static readonly HashSet<string> BallisticWeapons = new(StringComparer.OrdinalIgnoreCase)
	{
		"ak47","aug","awp","bizon","cz75a","deagle","elite","famas","fiveseven","g3sg1","galilar","glock",
		"hkp2000","m249","m4a1","m4a1_silencer","mac10","mag7","mp5sd","mp7","mp9","negev","nova","p250",
		"p90","revolver","sawedoff","scar20","sg556","ssg08","taser","tec9","ump45","usp_silencer","xm1014",
	};

	// Theoretical minimum server ticks (at 64Hz) between consecutive shots of the same weapon.
	// Derived from CS2 weapon script CycleTime values with a conservative 2-tick safety margin.
	private static readonly Dictionary<string, int> MinimumWeaponCycleTicks = new(StringComparer.OrdinalIgnoreCase)
	{
		["awp"] = 75,
		["ssg08"] = 68,
		["nova"] = 48,
		["mag7"] = 46,
		["sawedoff"] = 46,
		["revolver"] = 24,
		["deagle"] = 13,
		["xm1014"] = 18,
		["scar20"] = 13,
		["g3sg1"] = 13,
		["usp_silencer"] = 9,
		["hkp2000"] = 9,
		["p250"] = 8,
		["fiveseven"] = 8,
		["ak47"] = 5,
		["galilar"] = 5,
		["sg556"] = 5,
		["aug"] = 5,
		["m4a1_silencer"] = 5,
		["m4a1"] = 4,
		["famas"] = 4,
		["mp9"] = 3,
		["mac10"] = 4,
		["mp7"] = 4,
		["mp5sd"] = 4,
		["ump45"] = 5,
		["p90"] = 3,
		["bizon"] = 4,
		["m249"] = 4,
		["negev"] = 3,
	};

	private AimbotConfig _settings;
	private readonly Action<IPlayer, string, string> _announce;
	private readonly Action<string> _debug;
	private readonly Func<int, IPlayer?> _getPlayer;
	private readonly NetworkSafetyMonitor? _networkSafety;

	private readonly PlayerState[] _players = new PlayerState[MaxPlayers + 1];
	private readonly PositionFrame[] _frames = new PositionFrame[PositionHistoryLimit];
	private readonly SilentAimDetector _silentAim = new();
	private int _latestFrameSlot = -1;
	private readonly Dictionary<ulong, (List<AimbotIncident> Snap, List<AimbotIncident> Smooth, List<AimbotIncident> Silent, float SilentScore)> _persistentEvidence = new();

	internal SilentAimDetector SilentAim => _silentAim;

	public void UpdateConfig(AimbotConfig settings, SilentAimConfig? silentAimSettings = null)
	{
		_settings = settings;
		if (silentAimSettings != null)
		{
			_silentAim.UpdateConfig(silentAimSettings);
		}
		else
		{
			_silentAim.UpdateConfig(new SilentAimConfig
			{
				Enabled = settings.Enabled && settings.EnableSilentAim,
				Punishment = settings.Punishment
			});
		}
	}

	public AimbotDetector(
		AimbotConfig settings,
		Func<int, IPlayer?> getPlayer,
		Action<IPlayer, string, string> announce,
		Action<string> debug,
		NetworkSafetyMonitor? networkSafety = null)
	{
		_settings = settings;
		_getPlayer = getPlayer;
		_announce = announce;
		_debug = debug;
		_networkSafety = networkSafety;
		for (int i = 0; i < _players.Length; i++) _players[i] = new PlayerState();
		for (int i = 0; i < _frames.Length; i++) _frames[i] = new PositionFrame();
	}

	internal PlayerState GetState(int slot) => _players[slot];

	public void Reset()
	{
		_persistentEvidence.Clear();
		_silentAim.Reset();
		_latestFrameSlot = -1;
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
		_silentAim.OnClientDisconnect(id);

		for (int i = 0; i < _frames.Length; i++)
			_frames[i].Players[id] = default;
	}

	public static bool IsEligibleHuman(IPlayer? p) =>
		p is { IsValid: true, IsFakeClient: false } && PlayerSnapshotProvider.HasValidPawn(p);

	public bool AreOpponents(int a, int b)
	{
		bool aPlays = a == 2 || a == 3, bPlays = b == 2 || b == 3;
		return aPlays && bPlays && (a != b || _settings.TeammatesAreEnemies);
	}

	public bool AreEnemies(int aId, int bId)
	{
		if (aId < 0 || aId > MaxPlayers || bId < 0 || bId > MaxPlayers)
			return false;

		if (_latestFrameSlot >= 0)
		{
			var frame = _frames[_latestFrameSlot];
			if (frame.ServerTick >= 0 && frame.Players[aId].Valid && frame.Players[bId].Valid)
			{
				return AreOpponents(frame.Players[aId].Team, frame.Players[bId].Team);
			}
		}

		var pA = _getPlayer(aId);
		var pB = _getPlayer(bId);
		if (pA is null || pB is null) return false;
		int teamA = PlayerSnapshotProvider.GetTeam(pA);
		int teamB = PlayerSnapshotProvider.GetTeam(pB);
		return AreOpponents(teamA, teamB);
	}

	public void OnFireBullets(int slot, int serverTick, uint weaponId, float inaccuracy, float spread, QAngle angles)
	{
		if (slot < 0 || slot > MaxPlayers)
			return;

		_players[slot].LastFireBullets = new FireBulletsSample
		{
			ServerTick = serverTick,
			WeaponId = weaponId,
			Inaccuracy = inaccuracy,
			Spread = spread,
			Angles = angles,
			Valid = true
		};
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

			if (IsDuplicateCommand(st, number))
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

			QAngle baseAngles = b.Viewangles;
			QAngle effectiveAngles = baseAngles;
			bool isShot = false;
			bool hasAttackAngles = false;
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
						effectiveAngles = shotAngles;
						isShot = true;
						hasAttackAngles = true;
						StatShotCmds++;
					}
				}
			}

			float subtickPitchTravel = 0f;
			float subtickYawTravel = 0f;
			bool subtickValid = true;
			if (b.SubtickMoves != null && b.SubtickMoves.Count > 0)
			{
				for (int si = 0; si < b.SubtickMoves.Count; si++)
				{
					var step = b.SubtickMoves.Get(si);
					if (!float.IsFinite(step.PitchDelta) || !float.IsFinite(step.YawDelta))
					{
						subtickValid = false;
						break;
					}
					subtickPitchTravel += MathF.Abs(step.PitchDelta);
					subtickYawTravel += MathF.Abs(step.YawDelta);
				}
			}

			st.LastIdx = idx;
			st.LastHistCount = histCount;
			st.LastHasVA = hasVA;

			StoreValidatedCommand(
				player,
				st,
				number,
				b.ClientTick,
				baseAngles,
				effectiveAngles,
				isShot,
				hasAttackAngles,
				subtickPitchTravel,
				subtickYawTravel,
				subtickValid);
		}
	}

	/// <summary>
	/// Direct command ingestion entrypoint for both live hooks and deterministic unit tests.
	/// </summary>
	internal bool RecordUsercmd(
		IPlayer player,
		int commandNumber,
		int clientTick,
		QAngle baseAngles,
		QAngle? attackAngles = null,
		float subtickPitchTravel = 0f,
		float subtickYawTravel = 0f,
		bool subtickAnglesValid = true)
	{
		if (!_settings.Enabled || player.PlayerID < 0 || player.PlayerID > MaxPlayers)
			return false;

		var st = _players[player.PlayerID];
		StatUsercmds++;

		if (IsDuplicateCommand(st, commandNumber))
		{
			StatSkipDup++;
			return false;
		}

		bool hasAttack = attackAngles.HasValue;
		QAngle effective = attackAngles ?? baseAngles;
		if (hasAttack)
		{
			StatShotCmds++;
		}

		return StoreValidatedCommand(
			player,
			st,
			commandNumber,
			clientTick,
			baseAngles,
			effective,
			hasAttack,
			hasAttack,
			subtickPitchTravel,
			subtickYawTravel,
			subtickAnglesValid);
	}

	private static bool IsDuplicateCommand(PlayerState st, int number)
	{
		for (int ci = st.Commands.Count - 1; ci >= 0; ci--)
		{
			if (st.Commands[ci].CommandNumber == number)
				return true;
			if (st.Commands[ci].CommandNumber < number)
				break;
		}
		return false;
	}

	private bool StoreValidatedCommand(
		IPlayer player,
		PlayerState st,
		int number,
		int clientTick,
		QAngle baseAngles,
		QAngle effectiveAngles,
		bool isShot,
		bool hasAttackAngles,
		float subtickPitchTravel,
		float subtickYawTravel,
		bool subtickAnglesValid)
	{
		// Anti-Untrusted deterministic exploit check with 60-second per-player alert latch:
		// Genuine CS2 client clamps pitch to [-89.0°, +89.0°] and roll to 0.0°.
		if (!AimMath.IsFinite(baseAngles) || !AimMath.IsFinite(effectiveAngles) || !subtickAnglesValid
			|| baseAngles.X < -89.05f || baseAngles.X > 89.05f || MathF.Abs(baseAngles.Z) > 1.0f
			|| effectiveAngles.X < -89.05f || effectiveAngles.X > 89.05f || MathF.Abs(effectiveAngles.Z) > 1.0f)
		{
			StatSkipNonFinite++;
			DateTime now = DateTime.UtcNow;
			if ((now - st.LastUntrustedAlertTime).TotalSeconds >= 60.0)
			{
				st.LastUntrustedAlertTime = now;
				StatDetections++;
				_announce(player, "ANTI_UNTRUSTED", $"Invalid viewangles: Pitch {effectiveAngles.X:F2}°, Yaw {effectiveAngles.Y:F2}°, Roll {effectiveAngles.Z:F2}°. Deterministic exploit.");
			}
			return false;
		}

		st.Commands.Add(new AimCommand
		{
			CommandNumber = number,
			ClientTick = clientTick,
			BaseAngles = baseAngles,
			Angles = effectiveAngles,
			HasAttackAngles = hasAttackAngles,
			SubtickPitchTravel = subtickPitchTravel,
			SubtickYawTravel = subtickYawTravel,
			SubtickAnglesValid = subtickAnglesValid,
			IsShot = isShot
		});
		StatStored++;

		if (st.Commands.Count > CommandHistorySize)
			st.Commands.RemoveAt(0);

		return true;
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

		// Capture attacker EyePosition and movement state at the exact SetupMove simulation phase
		if (PlayerSnapshotProvider.TryGetPawnTransform(player, out _, out var eyePos, out _, out _, out _, out _, out bool isAirborne, out bool isScoped))
		{
			cmd.EyePosition = eyePos;
			cmd.EyePositionValid = true;
			cmd.Airborne = isAirborne;
			cmd.Scoped = isScoped;
		}

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

	internal void RecordFrame(int tick, int playerId, TrackedPosition position)
	{
		if (tick < 0 || playerId < 0 || playerId > MaxPlayers)
			return;

		int slot = tick & (PositionHistoryLimit - 1);
		_latestFrameSlot = slot;
		var frame = _frames[slot];
		if (frame.ServerTick != tick)
		{
			frame.ServerTick = tick;
			Array.Clear(frame.Players);
		}
		frame.Players[playerId] = position;
	}

	private void CaptureFrame(int tick)
	{
		int slot = tick & (PositionHistoryLimit - 1);
		_latestFrameSlot = slot;
		var frame = _frames[slot];
		frame.ServerTick = tick;

		int prevSlot = (tick - 1) & (PositionHistoryLimit - 1);
		var previous = _frames[prevSlot];
		bool hasPrevious = previous.ServerTick == tick - 1;

		for (int id = 0; id < MaxPlayers; id++)
		{
			var p = _getPlayer(id);
			if (p == null || !p.IsValid || !PlayerSnapshotProvider.TryGetPawnTransform(
				p,
				out var origin,
				out var eye,
				out _,
				out int team,
				out bool alive,
				out bool crouched,
				out bool airborne,
				out bool scoped))
			{
				frame.Players[id] = default;
				continue;
			}

			bool teleported = false;
			if (hasPrevious && previous.Players[id].Valid)
				teleported = origin.Distance(previous.Players[id].Origin) > TeleportDistance;

			int pingMs = (int)PlayerSnapshotProvider.GetPing(p);

			frame.Players[id] = new TrackedPosition
			{
				Origin = origin,
				Eye = eye,
				Team = team,
				Valid = true,
				Alive = alive,
				Teleported = teleported,
				Crouched = crouched,
				Airborne = airborne,
				Scoped = scoped,
				PingMs = pingMs,
				InterpTicks = 2.0f,
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

		if (weapon.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase))
			weapon = weapon[7..];

		if (!BallisticWeapons.Contains(weapon))
			return;

		StatWeaponFires++;

		var st = _players[player.PlayerID];

		// Evaluate Doubletap / Rapid-Fire exploit before command matching
		if (_settings.EnableDoubletap)
		{
			EvaluateDoubletap(player, st, weapon, currentTick);
		}

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

		// Primary search: unconsumed simulated shot commands within [-1, +1] ticks.
		// IMPORTANT: Do NOT mark non-selected candidates as Consumed = true, so rapid fire
		// or dual-command ticks remain available for subsequent weapon_fire events.
		AimCommand? matchedCandidate = null;
		int candidateCount = 0;
		for (int ci = st.Commands.Count - 1; ci >= 0; ci--)
		{
			var c = st.Commands[ci];
			if (c.IsShot && c.Simulated && !c.Consumed && (long)currentTick - c.ServerTick is >= -1 and <= 1)
			{
				candidateCount++;
				if (matchedCandidate == null || Math.Abs(c.ServerTick - currentTick) < Math.Abs(matchedCandidate.ServerTick - currentTick))
				{
					matchedCandidate = c;
				}
			}
		}

		// Fallback for continuous automatic spray:
		// In CS2 Protobuf usercmds, Attack1StartHistoryIndex is only set on the initial click frame.
		// Subsequent bullets (2..30) of an automatic spray occur while mouse1 is continuously held (idx = -1).
		// Correlate the weapon_fire event to the single closest unconsumed simulated command in [-2, +2] ticks
		// WITHOUT consuming neighboring commands needed for subsequent spray bullets.
		if (matchedCandidate == null)
		{
			for (int ci = st.Commands.Count - 1; ci >= 0; ci--)
			{
				var c = st.Commands[ci];
				if (c.Simulated && !c.Consumed && (long)currentTick - c.ServerTick is >= -2 and <= 2)
				{
					candidateCount++;
					if (matchedCandidate == null || Math.Abs(c.ServerTick - currentTick) < Math.Abs(matchedCandidate.ServerTick - currentTick))
					{
						matchedCandidate = c;
					}
				}
			}
		}

		if (matchedCandidate == null)
		{
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
		command.IsShot = true;

		var shotRecord = new ShotRecord
		{
			Command = command,
			FireTick = currentTick,
			Weapon = weapon
		};
		st.Shots.Add(shotRecord);

		if (st.Shots.Count > 12)
			st.Shots.RemoveAt(0);

		StatFireMatched++;

		EvaluateSprayRecoilPattern(player, st, command, weapon, currentTick);
		RecordAimedShotAndIrregularAttempt(player, st, shotRecord, currentTick);

		_debug($"{Name(player)} weapon_fire matched command {command.CommandNumber}, server tick {command.ServerTick}, fire tick {currentTick}.");
	}

	private static bool IsAutomaticSprayWeapon(string weapon) =>
		weapon.Equals("ak47", StringComparison.OrdinalIgnoreCase)
		|| weapon.Equals("m4a1", StringComparison.OrdinalIgnoreCase)
		|| weapon.Equals("m4a1_silencer", StringComparison.OrdinalIgnoreCase)
		|| weapon.Equals("galilar", StringComparison.OrdinalIgnoreCase)
		|| weapon.Equals("famas", StringComparison.OrdinalIgnoreCase)
		|| weapon.Equals("sg556", StringComparison.OrdinalIgnoreCase)
		|| weapon.Equals("aug", StringComparison.OrdinalIgnoreCase)
		|| weapon.Equals("mp9", StringComparison.OrdinalIgnoreCase)
		|| weapon.Equals("mac10", StringComparison.OrdinalIgnoreCase)
		|| weapon.Equals("mp7", StringComparison.OrdinalIgnoreCase)
		|| weapon.Equals("mp5sd", StringComparison.OrdinalIgnoreCase)
		|| weapon.Equals("ump45", StringComparison.OrdinalIgnoreCase)
		|| weapon.Equals("p90", StringComparison.OrdinalIgnoreCase)
		|| weapon.Equals("bizon", StringComparison.OrdinalIgnoreCase)
		|| weapon.Equals("m249", StringComparison.OrdinalIgnoreCase)
		|| weapon.Equals("negev", StringComparison.OrdinalIgnoreCase);

	private static bool IsSniperWeapon(string weapon) =>
		weapon.Equals("awp", StringComparison.OrdinalIgnoreCase)
		|| weapon.Equals("ssg08", StringComparison.OrdinalIgnoreCase)
		|| weapon.Equals("scar20", StringComparison.OrdinalIgnoreCase)
		|| weapon.Equals("g3sg1", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Evaluates continuous automatic rifle/SMG spray shots against Vanguard RECOIL_THRESHOLD (&gt;= 80% compensation)
	/// and PATTERN_VARIANCE (&lt; 8.157% relative step variance with zero sign flips).
	/// Catches external mouse_event RCS tables while ignoring organic human spray control.
	/// </summary>
	private void EvaluateSprayRecoilPattern(IPlayer player, PlayerState st, AimCommand command, string weapon, int currentTick)
	{
		if (!_settings.EnableRcsCheck || !IsAutomaticSprayWeapon(weapon) || (_networkSafety != null && _networkSafety.ShouldSkipDetection(player.PlayerID, out _)))
		{
			st.SprayShotCount = 0;
			st.LastSprayFireTick = -1;
			return;
		}

		int tickDelta = st.LastSprayFireTick >= 0 ? currentTick - st.LastSprayFireTick : 999;
		if (tickDelta is >= 1 and <= 12 && string.Equals(st.LastSprayWeapon, weapon, StringComparison.OrdinalIgnoreCase))
		{
			if (st.SprayShotCount < st.SprayShotAngles.Length)
			{
				st.SprayShotAngles[st.SprayShotCount++] = command.Angles;
			}
		}
		else
		{
			st.SprayShotCount = 1;
			st.SprayShotAngles[0] = command.Angles;
			st.LastSprayWeapon = weapon;
		}
		st.LastSprayFireTick = currentTick;

		if (st.SprayShotCount < 8)
			return;

		int steps = st.SprayShotCount - 1;
		float sumStep = 0f;
		float sumSqStep = 0f;
		float totalPitchPullDown = 0f;
		int pitchSignReversals = 0;

		for (int i = 1; i < st.SprayShotCount; i++)
		{
			float dp = st.SprayShotAngles[i].X - st.SprayShotAngles[i - 1].X; // Positive X = looking down (recoil pull-down)
			float dy = AimMath.YawDelta(st.SprayShotAngles[i - 1].Y, st.SprayShotAngles[i].Y);
			float stepMag = MathF.Sqrt(dp * dp + dy * dy);

			sumStep += stepMag;
			sumSqStep += stepMag * stepMag;
			totalPitchPullDown += dp;

			if (i >= 2 && i <= 6 && dp < -0.15f)
			{
				pitchSignReversals++;
			}
		}

		float meanStep = sumStep / steps;
		if (meanStep < 0.25f)
			return;

		float variance = MathF.Max(0f, (sumSqStep / steps) - (meanStep * meanStep));
		float cvPercent = (MathF.Sqrt(variance) / meanStep) * 100.0f;

		// Expected vertical recoil pull-down across >= 7 rifle spray intervals is ~3.5..6.5 deg.
		// RECOIL_THRESHOLD (0.80) corresponds to >= 2.8 deg pull-down with PATTERN_VARIANCE < 8.157%.
		bool meetsRecoilComp = totalPitchPullDown >= 2.8f * AimMath.Baselines.RecoilCompensationThreshold;
		bool meetsPatternVariance = cvPercent < AimMath.Baselines.PatternVariancePercent && pitchSignReversals == 0;

		if (meetsRecoilComp && meetsPatternVariance)
		{
			st.RcsScore = MathF.Min(15.0f, st.RcsScore + 2.5f);
			st.SprayShotCount = 0; // Reset burst window so one spray counts once

			DateTime now = DateTime.UtcNow;
			if (st.RcsScore >= 5.0f && (now - st.LastRcsAlertTime).TotalSeconds >= 15.0)
			{
				st.LastRcsAlertTime = now;
				st.RcsScore = 0f;
				StatDetections++;
				_announce(player, "RCS_MACRO", $"Robotic recoil compensation pattern on {weapon}: step CV {cvPercent:F2}% < {AimMath.Baselines.PatternVariancePercent:F3}% (PATTERN_VARIANCE) with {totalPitchPullDown:F2}° pull-down.");
			}
		}
		else if (cvPercent >= 14.0f)
		{
			st.RcsScore = MathF.Max(0f, st.RcsScore - 0.5f);
		}
	}

	private void RecordAimedShotAndIrregularAttempt(IPlayer player, PlayerState st, ShotRecord shotRecord, int currentTick)
	{
		var cmd = shotRecord.Command;
		bool haveAttacker = FindPosition(cmd.ServerTick, player.PlayerID, out var attackerPos);
		bool airborne = cmd.Airborne || (haveAttacker && attackerPos.Airborne);
		bool scoped = cmd.Scoped || (haveAttacker && attackerPos.Scoped);
		bool sniper = IsSniperWeapon(shotRecord.Weapon);
		bool noScope = sniper && !scoped;

		DateTime now = DateTime.UtcNow;

		// 1. Irregular Behavior attempt tracking (Airborne or Sniper No-Scope, ported from CS2AC Legacy)
		if (_settings.EnableIrregularBehaviorCheck && (airborne || noScope))
		{
			st.IrregularHistory.RemoveAll(x => now - x.Time >= EvidenceWindow);
			st.IrregularHistory.Add(new IrregularSample { Time = now, Success = false, Points = 0 });
			shotRecord.IrregularCounted = true;
		}

		// 2. Inhuman Accuracy attempt tracking (PREDICTION_THRESHOLD >= 95% across >= 35 aimed shots at >= 100u)
		if (!_settings.EnableInhumanAccuracyCheck || !haveAttacker || attackerPos.Teleported)
			return;

		Vector eye = cmd.EyePositionValid ? cmd.EyePosition : attackerPos.Eye;
		var frame = FindFrame(cmd.ServerTick);
		if (frame == null)
			return;

		bool aimedNearEnemy = false;
		for (int targetId = 0; targetId < MaxPlayers; targetId++)
		{
			if (targetId == player.PlayerID)
				continue;

			ref readonly var target = ref frame.Players[targetId];
			if (!target.Valid || !target.Alive || target.Teleported || !AreOpponents(attackerPos.Team, target.Team))
				continue;

			float dist = target.Origin.Distance(eye);
			if (!float.IsFinite(dist) || dist < MinimumDistance)
				continue;

			float err = AimMath.NearestBodyAimError(eye, cmd.Angles, target.Origin, target.Crouched);
			float tolerance = MathF.Atan2(32.0f, dist) * AimMath.Rad2Deg;
			if (err <= tolerance)
			{
				aimedNearEnemy = true;
				break;
			}
		}

		if (aimedNearEnemy)
		{
			st.AccuracyHistory.RemoveAll(x => now - x.Time >= EvidenceWindow);
			st.AccuracyHistory.Add(new AccuracySample { Time = now, Hit = false });
			shotRecord.AccuracyCounted = true;
		}
	}

	/// <summary>
	/// Evaluates repeated improbable kills (airborne / sniper no-scope at &gt;= 10m).
	/// Ported from CS2AC Legacy IrregularBehaviorModule.
	/// </summary>
	public void OnPlayerDeath(
		IPlayer attacker,
		IPlayer victim,
		string weapon,
		bool headshot,
		int penetrated,
		bool noscope,
		bool thruSmoke,
		bool attackerInAir,
		float distanceMeters,
		int currentTick)
	{
		if (!_settings.Enabled || !_settings.EnableIrregularBehaviorCheck || !IsEligibleHuman(attacker) || !victim.IsValid || attacker.PlayerID == victim.PlayerID)
			return;

		if (weapon.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase))
			weapon = weapon[7..];

		if (!BallisticWeapons.Contains(weapon) || !float.IsFinite(distanceMeters) || distanceMeters < 10.0f)
			return;

		var st = _players[attacker.PlayerID];
		bool sniper = IsSniperWeapon(weapon);
		bool isNoScope = sniper && noscope;
		bool isAirborne = attackerInAir;

		// Check if the latest correlated shot had airborne/unscoped state
		for (int si = st.Shots.Count - 1; si >= 0; si--)
		{
			var s = st.Shots[si];
			if ((long)currentTick - s.FireTick is >= 0 and <= 2)
			{
				isAirborne = isAirborne || s.Command.Airborne;
				isNoScope = isNoScope || (sniper && !s.Command.Scoped);
				break;
			}
		}

		if (!isAirborne && !isNoScope)
			return;

		int points = (isAirborne ? 1 : 0)
			+ (isNoScope ? 1 : 0)
			+ (isAirborne && isNoScope ? 2 : 0)
			+ (distanceMeters >= 20.0f ? 1 : 0)
			+ (penetrated > 0 ? 1 : 0)
			+ (thruSmoke ? 1 : 0)
			+ 2
			+ (headshot ? 1 : 0);

		DateTime now = DateTime.UtcNow;
		st.IrregularHistory.RemoveAll(x => now - x.Time >= EvidenceWindow);

		// Upgrade the latest pending attempt or append if none was open
		if (st.IrregularHistory.Count > 0 && !st.IrregularHistory[^1].Success && (now - st.IrregularHistory[^1].Time).TotalSeconds <= 1.0)
		{
			st.IrregularHistory[^1] = new IrregularSample { Time = now, Success = true, Points = points };
		}
		else
		{
			st.IrregularHistory.Add(new IrregularSample { Time = now, Success = true, Points = points });
		}

		int attempts = st.IrregularHistory.Count;
		int successes = 0;
		int totalScore = 0;
		for (int i = 0; i < attempts; i++)
		{
			if (st.IrregularHistory[i].Success)
			{
				successes++;
				totalScore += st.IrregularHistory[i].Points;
			}
		}

		if (totalScore >= 16 && successes >= 3 && attempts >= 4 && successes * 2 >= attempts
			&& (now - st.LastIrregularAlertTime).TotalSeconds >= 15.0)
		{
			st.LastIrregularAlertTime = now;
			float rate = (successes * 100.0f) / attempts;
			StatDetections++;
			_announce(attacker, "IRREGULAR_BEHAVIOR", $"Repeated improbable airborne/no-scope kills: {successes}/{attempts} attempts succeeded ({rate:F1}%), score {totalScore}/16.");
			st.IrregularHistory.Clear();
		}
	}

	private void EvaluateDoubletap(IPlayer player, PlayerState st, string weapon, int currentTick)
	{
		if (_networkSafety != null && _networkSafety.ShouldSkipDetection(player.PlayerID, out _))
		{
			st.LastWeaponFireTick = currentTick;
			st.LastWeaponFireName = weapon;
			return;
		}

		if (st.LastWeaponFireTick >= 0 && string.Equals(st.LastWeaponFireName, weapon, StringComparison.OrdinalIgnoreCase))
		{
			int tickDelta = currentTick - st.LastWeaponFireTick;
			if (tickDelta >= 0 && MinimumWeaponCycleTicks.TryGetValue(weapon, out int minTicks))
			{
				// Allow 2 ticks of subtick/command batch jitter around the weapon cycle interval
				if (tickDelta < minTicks - 2)
				{
					float ratio = (float)tickDelta / Math.Max(1, minTicks);
					float points = tickDelta <= 1 ? 6.0f : (ratio <= 0.5f ? 4.0f : 2.5f);
					st.DoubletapScore = MathF.Min(15f, st.DoubletapScore + points);

					DateTime now = DateTime.UtcNow;
					if (st.DoubletapScore >= DoubletapScoreThreshold && (now - st.LastDoubletapAlertTime).TotalSeconds >= 10.0)
					{
						st.LastDoubletapAlertTime = now;
						st.DoubletapScore = 0f;
						StatDetections++;
						_announce(player, "DOUBLETAP", $"Impossible weapon cycle interval on {weapon}: {tickDelta} ticks (minimum {minTicks} ticks). Rapid-fire / tickbase exploit.");
					}
				}
				else
				{
					st.DoubletapScore = MathF.Max(0f, st.DoubletapScore - 0.5f);
				}
			}
		}

		st.LastWeaponFireTick = currentTick;
		st.LastWeaponFireName = weapon;
	}

	public int? OnPlayerHurt(IPlayer attacker, IPlayer victim, string weapon, int currentTick)
	{
		if (!_settings.Enabled || !IsEligibleHuman(attacker) || !victim.IsValid || attacker.PlayerID == victim.PlayerID)
			return null;

		if (weapon.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase))
			weapon = weapon[7..];

		StatHurts++;

		if (!BallisticWeapons.Contains(weapon))
		{
			_debug($"{Name(attacker)} hurt ignored: weapon '{weapon}' is not ballistic.");
			return null;
		}

		var st = _players[attacker.PlayerID];

		// Multi-pellet (shotgun), collateral penetration, and rapid-fire safe correlation:
		// 1. Prefer a shot fired on the exact currentTick matching the weapon;
		// 2. Otherwise fall back to currentTick - 1.
		ShotRecord? matchedShot = null;
		for (int si = st.Shots.Count - 1; si >= 0; si--)
		{
			var s = st.Shots[si];
			if (s.FireTick == currentTick && string.Equals(s.Weapon, weapon, StringComparison.OrdinalIgnoreCase))
			{
				matchedShot = s;
				if (!s.EvaluatedVictims.Contains(victim.PlayerID))
					break;
			}
		}

		if (matchedShot == null)
		{
			for (int si = st.Shots.Count - 1; si >= 0; si--)
			{
				var s = st.Shots[si];
				if ((long)currentTick - s.FireTick is >= 0 and <= 1)
				{
					matchedShot = s;
					if (!s.EvaluatedVictims.Contains(victim.PlayerID))
						break;
				}
			}
		}

		if (matchedShot == null)
		{
			StatHurtNoMatch++;
			_debug($"{Name(attacker)} hurt unmatched at tick {currentTick}.");
			return null;
		}

		var shot = matchedShot;
		shot.HurtSeen = true;
		shot.VictimId = victim.PlayerID;

		// Credit aimed-shot accuracy hit (PREDICTION_THRESHOLD >= 95% across >= 35 aimed shots)
		if (shot.AccuracyCounted && !shot.AccuracyHitCounted && st.AccuracyHistory.Count > 0)
		{
			shot.AccuracyHitCounted = true;
			st.AccuracyHistory[^1] = new AccuracySample { Time = st.AccuracyHistory[^1].Time, Hit = true };
			EvaluateInhumanAccuracy(attacker, st);
		}

		var command = shot.Command;
		StatMatched++;

		// If this exact shot already evaluated this exact victim (e.g., pellets 2..8 of a shotgun blast),
		// return the correlated command number immediately without duplicating evaluation or failing correlation.
		if (!shot.EvaluatedVictims.Add(victim.PlayerID))
		{
			return command.CommandNumber;
		}

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

	private void EvaluateInhumanAccuracy(IPlayer attacker, PlayerState st)
	{
		DateTime now = DateTime.UtcNow;
		st.AccuracyHistory.RemoveAll(x => now - x.Time >= EvidenceWindow);

		int attempts = st.AccuracyHistory.Count;
		if (attempts < 35)
			return;

		int hits = 0;
		for (int i = 0; i < attempts; i++)
		{
			if (st.AccuracyHistory[i].Hit)
				hits++;
		}

		float accuracyRatio = (float)hits / attempts;
		if (accuracyRatio >= AimMath.Baselines.PredictionThreshold && (now - st.LastAccuracyAlertTime).TotalSeconds >= 20.0)
		{
			st.LastAccuracyAlertTime = now;
			StatDetections++;
			_announce(attacker, "INHUMAN_ACCURACY", $"Sustained near-perfect aimed accuracy: {hits}/{attempts} aimed shots hit ({accuracyRatio * 100.0f:F1}% >= {AimMath.Baselines.PredictionThreshold * 100.0f:F0}% PREDICTION_THRESHOLD).");
			st.AccuracyHistory.Clear();
		}
	}

	private readonly record struct SmoothPoint(AimCommand Command, float Error);

	private static AimCommand? FindCommand(PlayerState st, int number) =>
		st.Commands.FindLast(c => c.CommandNumber == number && c.Simulated);

	private static string Name(IPlayer p) => p.Controller?.PlayerName ?? $"#{p.PlayerID}";

	private int EstimateLagTicks(int attackerId, TrackedPosition attackerPos)
	{
		float pingMs = attackerPos.PingMs;
		if (_networkSafety != null)
		{
			float netPing = _networkSafety.GetCurrentPingMs(attackerId);
			if (netPing > 0f)
			{
				pingMs = netPing;
			}
		}

		float interpTicks = attackerPos.InterpTicks > 0f ? attackerPos.InterpTicks : 2.0f;
		int lagTicks = (int)MathF.Round((pingMs * 0.001f * TickRate) + interpTicks);
		return Math.Clamp(lagTicks, 0, 32);
	}

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

		// Gate non-blatant heuristic detections behind NetworkSafetyMonitor during severe packet loss/choke/bursts
		bool networkUnstable = _networkSafety != null && _networkSafety.ShouldSkipDetection(attackerId, out _);

		bool haveTargetAtShot = FindPosition(shot.ServerTick, st.VictimId, out var rawShotTarget);
		bool haveAttacker = FindPosition(shot.ServerTick, attackerId, out var shotAttacker);
		if (!haveTargetAtShot || !haveAttacker)
		{
			if (currentTick <= shot.ServerTick)
				return false;

			_debug($"{Name(attacker)} rejected because target history is missing for server tick {shot.ServerTick}.");
			Clear();
			return true;
		}

		// Prefer the exact EyePosition captured during SetupMove if available
		Vector attackerEyeAtShot = shot.EyePositionValid ? shot.EyePosition : shotAttacker.Eye;

		// Multi-hypothesis lag-compensated target position selection:
		// Find the lag offset (0, estLag +- 2, or 1..12 ticks) where the victim's historical hitbox
		// aligns closest with the damaging shot's viewangles.
		int estLag = EstimateLagTicks(attackerId, shotAttacker);
		Span<int> candidateOffsets = stackalloc int[16];
		int offsetCount = 0;
		candidateOffsets[offsetCount++] = 0;
		if (estLag > 0)
		{
			for (int d = -2; d <= 2; d++)
			{
				int cand = Math.Clamp(estLag + d, 1, 32);
				bool exists = false;
				for (int k = 0; k < offsetCount; k++)
					if (candidateOffsets[k] == cand) exists = true;
				if (!exists)
					candidateOffsets[offsetCount++] = cand;
			}
		}
		for (int extra = 1; extra <= 8 && offsetCount < candidateOffsets.Length; extra++)
		{
			bool exists = false;
			for (int k = 0; k < offsetCount; k++)
				if (candidateOffsets[k] == extra) exists = true;
			if (!exists)
				candidateOffsets[offsetCount++] = extra;
		}

		int bestLagOffset = 0;
		TrackedPosition shotTarget = rawShotTarget;
		float bestLagShotError = AimMath.NearestBodyAimError(attackerEyeAtShot, shot.Angles, rawShotTarget.Origin, rawShotTarget.Crouched);

		for (int i = 1; i < offsetCount; i++)
		{
			int offset = candidateOffsets[i];
			int targetTick = shot.ServerTick - offset;
			if (FindPosition(targetTick, st.VictimId, out var candTarget) && !candTarget.Teleported && candTarget.Alive)
			{
				float err = AimMath.NearestBodyAimError(attackerEyeAtShot, shot.Angles, candTarget.Origin, candTarget.Crouched);
				if (err + 0.05f < bestLagShotError)
				{
					bestLagShotError = err;
					bestLagOffset = offset;
					shotTarget = candTarget;
				}
			}
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

		float distance = shotTarget.Origin.Distance(attackerEyeAtShot);
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
		bool isBlatantSnap = false;
		bool isBlatantLaser = false;
		float incidentWeight = 1.0f;

		int smoothBodyPoint = 0;
		float smoothShotError = 180.0f;
		for (int bp = 0; bp < AimMath.BodyPointCount; bp++)
		{
			var t = new Vector(shotTarget.Origin.X, shotTarget.Origin.Y, shotTarget.Origin.Z + AimMath.BodyHeight(bp, shotTarget.Crouched));
			float e = AimMath.AimError(attackerEyeAtShot, shot.Angles, t);
			if (e < smoothShotError)
			{
				smoothShotError = e;
				smoothBodyPoint = bp;
			}
		}

		// Distance-adaptive angular hitbox tolerance (head ~7u, chest/pelvis ~12u)
		float hitboxRadiusUnits = smoothBodyPoint == 2 ? 7.5f : 12.0f;
		float targetAngularRadius = Math.Clamp(
			MathF.Max(SmoothMaximumFinalError, AimMath.HitboxAngularRadius(distance, hitboxRadiusUnits)),
			1.0f,
			4.5f);

		// 1. Subtick SilentAim Evaluation via dedicated SilentAimDetector module (src/Modules/SilentAim.cs)
		if (_settings.EnableSilentAim && shot.HasAttackAngles && !networkUnstable)
		{
			var prevCmdForSilent = FindCommand(st, shot.CommandNumber - 1);
			bool hasFireSpread = st.LastFireBullets.Valid && Math.Abs(st.LastFireBullets.ServerTick - shot.ServerTick) <= 1;

			var silentResult = _silentAim.EvaluateSubtickSilentAim(
				attackerId,
				shot.BaseAngles,
				shot.Angles,
				prevCmdForSilent?.BaseAngles,
				shot.SubtickPitchTravel,
				shot.SubtickYawTravel,
				hasFireSpread,
				st.LastFireBullets.Inaccuracy,
				st.LastFireBullets.Spread,
				in attackerEyeAtShot,
				shot.Airborne || shotAttacker.Airborne,
				in shotTarget.Origin,
				shotTarget.Crouched,
				smoothShotError,
				targetAngularRadius);

			st.SilentAimScore = silentResult.AccumulatedScore;
			if (silentResult.Triggered)
			{
				suspicious = true;
				matchedRule = EvidenceType.SilentAim;
				ruleMatched = true;
				largestSnap = silentResult.DesyncDegrees;
				bestBefore = silentResult.BaseErrorDegrees;
				bestAfter = silentResult.AttackErrorDegrees;
				if (silentResult.IsBlatant)
					isBlatantSnap = true;
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

			int olderTargetTick = older.ServerTick - bestLagOffset;
			int newerTargetTick = newer.ServerTick - bestLagOffset;

			if (!FindPosition(olderTargetTick, st.VictimId, out var olderTarget)
				|| !FindPosition(newerTargetTick, st.VictimId, out var newerTarget)
				|| !FindPosition(older.ServerTick, attackerId, out var olderAttacker)
				|| !FindPosition(newer.ServerTick, attackerId, out var newerAttacker)
				|| olderTarget.Teleported || newerTarget.Teleported || olderAttacker.Teleported || newerAttacker.Teleported)
			{
				measurementFailure = "matching historical player positions were missing or interrupted by a teleport";
				break;
			}

			Vector olderEye = older.EyePositionValid ? older.EyePosition : olderAttacker.Eye;
			Vector newerEye = newer.EyePositionValid ? newer.EyePosition : newerAttacker.Eye;

			float snap = AimMath.AngularDistance(older.Angles, newer.Angles);
			float before = AimMath.NearestBodyAimError(olderEye, older.Angles, olderTarget.Origin, olderTarget.Crouched);
			float after = AimMath.NearestBodyAimError(newerEye, newer.Angles, newerTarget.Origin, newerTarget.Crouched);

			if (!float.IsFinite(snap) || !float.IsFinite(before) || !float.IsFinite(after))
			{
				measurementFailure = "an angle or target-error measurement was invalid";
				break;
			}

			var fixedPoint = new Vector(
				olderTarget.Origin.X,
				olderTarget.Origin.Y,
				olderTarget.Origin.Z + AimMath.BodyHeight(smoothBodyPoint, olderTarget.Crouched));
			float smoothError = AimMath.AimError(olderEye, older.Angles, fixedPoint);
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
				measuredBefore = before;
				measuredAfter = after;
			}

			// Check pre-step and post-step isolation for medium/small 1-tick snap corrections
			var beforeOlder = FindCommand(st, older.CommandNumber - 1);
			var afterNewer = FindCommand(st, newer.CommandNumber + 1);
			float prevStep = beforeOlder != null ? AimMath.AngularDistance(beforeOlder.Angles, older.Angles) : 0f;
			float nextStep = afterNewer != null ? AimMath.AngularDistance(newer.Angles, afterNewer.Angles) : 0f;
			bool isolatedOneTickSnap = prevStep <= snap * 0.25f && nextStep <= snap * 0.25f;

			// 1. Blatant Rage Snap: >= 45° in 1 tick landing directly on target (or >= 90° within 10°)
			bool isBlatantRageSnap = serverGap <= 1 && clientGap <= 1
				&& ((snap >= 90.0f && after <= 10.0f) || (snap >= 45.0f && after <= targetAngularRadius * 1.35f && before >= 30.0f));

			// 2. Standard Snap Convergence (>= SnapAngleThreshold, e.g. 10°)
			bool isStandardSnap = !networkUnstable
				&& ((snap >= _settings.SnapAngleThreshold && after < before * 0.20f && after <= targetAngularRadius * 1.4f && isolatedOneTickSnap)
					|| (snap >= _settings.SnapAngleThreshold * 1.5f && after < before * 0.12f && after <= targetAngularRadius));

			// 3. Small/Medium Closet 1-Tick Snap (3.5° .. 10.0° isolated 1-tick jump onto target with flat pre/post velocity)
			bool isSmallIsolatedSnap = !networkUnstable
				&& snap >= _settings.LowFovSmoothMinimumMovement
				&& snap < _settings.SnapAngleThreshold
				&& isolatedOneTickSnap
				&& prevStep <= 0.35f
				&& nextStep <= 0.35f
				&& after <= MathF.Min(before * 0.10f, 1.0f);

			bool converged = isBlatantRageSnap || isStandardSnap || isSmallIsolatedSnap;
			bool fresh = !st.HasCountedIncident || newer.CommandNumber > st.LastCountedIncidentCommand;
			if (converged && fresh && !suspicious)
			{
				suspicious = true;
				matchedRule = EvidenceType.Convergence;
				ruleMatched = true;
				if (isBlatantRageSnap)
					isBlatantSnap = true;

				largestSnap = snap;
				bestBefore = before;
				bestAfter = after;
				incidentWeight = isBlatantRageSnap ? 3.0f : 1.0f;
			}
			else if (converged)
				reusedSnap = true;

			newer = older;
		}

		var previous = FindCommand(st, st.PendingShot - 1);
		var next = FindCommand(st, st.PendingShot + 1);
		if (!suspicious && next == null && (long)currentTick - shot.ServerTick <= 1)
			return false;

		if (!networkUnstable && previous != null && next != null
			&& shot.CommandNumber - previous.CommandNumber == 1 && next.CommandNumber - shot.CommandNumber == 1
			&& (long)shot.ClientTick - previous.ClientTick is >= 0 and <= MaxClientTickGap
			&& (long)next.ClientTick - shot.ClientTick is >= 0 and <= MaxClientTickGap
			&& (long)shot.ServerTick - previous.ServerTick is >= 0 and <= 1
			&& (long)next.ServerTick - shot.ServerTick is >= 0 and <= 1)
		{
			bool fresh = !st.HasCountedIncident || shot.CommandNumber > st.LastCountedIncidentCommand;
			if (fresh)
			{
				Vector prevEye = previous.EyePositionValid ? previous.EyePosition : shotAttacker.Eye;
				var snapReturn = _silentAim.EvaluateSnapReturn(
					previous.Angles,
					shot.Angles,
					next.Angles,
					in prevEye,
					in shotTarget.Origin,
					shotTarget.Crouched,
					smoothShotError);

				if (snapReturn.Triggered)
				{
					if (!suspicious)
					{
						matchedRule = EvidenceType.SnapReturn;
						ruleMatched = true;
						largestSnap = snapReturn.SnapDegrees;
						bestBefore = snapReturn.BeforeErrorDegrees;
						bestAfter = snapReturn.AfterErrorDegrees;
						if (snapReturn.IsBlatant)
							isBlatantSnap = true;
					}

					suspicious = true;
				}
			}
		}

		float smoothMovement = 0, smoothBefore = 0, smoothAfter = 0;
		if (!suspicious && !networkUnstable && smoothPoints.Count >= SmoothMinimumSteps + 1)
		{
			smoothPoints.Reverse(); // Chronological order: [0 .. ^1] where ^1 is the shot command

			// Backward-anchored convergence window:
			// Walk backward from the shot command (^1) while target error increases backward
			// and angular steps remain active. This isolates the exact pull onto the target
			// without polluting the path with unrelated camera turns earlier in the 0.5s window.
			int shotIdx = smoothPoints.Count - 1;
			int start = shotIdx;
			while (start > 0)
			{
				float stepDist = AimMath.AngularDistance(smoothPoints[start - 1].Command.Angles, smoothPoints[start].Command.Angles);
				float errPrev = smoothPoints[start - 1].Error;
				float errCurr = smoothPoints[start].Error;

				// Stop walking backward when movement stops or was moving away from the target
				if (stepDist < 0.08f || errPrev < errCurr - 0.15f)
					break;

				start--;
			}

			int segmentCommandCount = shotIdx - start + 1;
			if (segmentCommandCount >= SmoothMinimumSteps + 1)
			{
				float path = 0f;
				float largest = 0f;
				int steps = 0;
				int accelerationSpikes = 0;
				int jitterFlips = 0;
				float sumSteps = 0f;
				float sumSqSteps = 0f;
				float prevStep = float.PositiveInfinity;
				float prevYawDelta = 0f;
				float prevPitchDelta = 0f;

				for (int i = start + 1; i <= shotIdx; i++)
				{
					var c0 = smoothPoints[i - 1].Command.Angles;
					var c1 = smoothPoints[i].Command.Angles;
					float step = AimMath.AngularDistance(c0, c1);
					path += step;
					if (step > largest)
						largest = step;

					if (step >= SmoothMinimumStep)
					{
						steps++;
						sumSteps += step;
						sumSqSteps += step * step;

						// Allow up to 10% + 0.12° tolerance for tick quantization on decelerating smoothers
						if (step > prevStep * 1.10f + 0.12f)
						{
							accelerationSpikes++;
						}
						prevStep = step;

						float dy = AimMath.YawDelta(c1.Y, c0.Y);
						float dp = AimMath.PitchDelta(c1.X, c0.X);
						if (steps >= 2)
						{
							if ((MathF.Abs(dy) >= 0.25f && MathF.Abs(prevYawDelta) >= 0.25f && dy * prevYawDelta < 0f)
								|| (MathF.Abs(dp) >= 0.25f && MathF.Abs(prevPitchDelta) >= 0.25f && dp * prevPitchDelta < 0f))
							{
								jitterFlips++;
							}
						}
						prevYawDelta = dy;
						prevPitchDelta = dp;
					}
				}

				smoothMovement = AimMath.AngularDistance(smoothPoints[start].Command.Angles, smoothPoints[shotIdx].Command.Angles);
				smoothBefore = smoothPoints[start].Error;
				smoothAfter = smoothPoints[shotIdx].Error;

				float efficiency = path > AimMath.Epsilon ? smoothMovement / path : 0.0f;
				float improvement = smoothBefore > AimMath.Epsilon ? 1.0f - smoothAfter / smoothBefore : 0.0f;
				float meanStep = steps > 0 ? sumSteps / steps : 0f;
				float stepVariance = steps > 1 ? MathF.Max(0f, (sumSqSteps / steps) - (meanStep * meanStep)) : 0f;
				float stepCv = meanStep > AimMath.Epsilon ? MathF.Sqrt(stepVariance) / meanStep : 1.0f;

				// Monotonic deceleration (exponential/proportional smoother) OR constant-velocity linear smoother
				// meeting Vanguard MIN_VARIANCE (< 0.05) / PATTERN_VARIANCE (< 8.157%) or step CV <= 0.28 with zero sign reversals
				bool isMechanicalProfile = (accelerationSpikes == 0
					|| stepVariance <= AimMath.Baselines.MinBotVariance
					|| stepCv <= AimMath.Baselines.PatternVarianceRatio
					|| stepCv <= 0.28f) && jitterFlips == 0;

				bool fresh = !st.HasCountedIncident || shot.CommandNumber > st.LastCountedIncidentCommand;

				// 1. Blatant Laser Trajectory: >= 99.0% straight-line efficiency over >= 20° and >= 5 steps with zero jitter flips
				bool isBlatantLaserTrajectory = efficiency >= 0.99f
					&& smoothMovement >= 20.0f
					&& steps >= 5
					&& smoothAfter <= targetAngularRadius
					&& jitterFlips == 0
					&& (accelerationSpikes <= 1 || stepCv <= 0.30f);

				// 2. Standard Smooth Aimbot (>= SmoothMinimumMovement, e.g. 12.0°)
				bool isStandardSmooth = smoothMovement >= _settings.SmoothMinimumMovement
					&& improvement >= SmoothMinimumImprovement
					&& smoothAfter <= targetAngularRadius
					&& efficiency >= _settings.SmoothMinimumPathEfficiency
					&& steps >= SmoothMinimumSteps
					&& isMechanicalProfile;

				// 3. Closet Low-FOV Smooth Aimbot (3.5° .. 12.0° ultra-clean linear/decelerating pull)
				bool isLowFovSmooth = smoothMovement >= _settings.LowFovSmoothMinimumMovement
					&& smoothMovement < _settings.SmoothMinimumMovement
					&& improvement >= 0.92f
					&& smoothAfter <= MathF.Min(1.25f, targetAngularRadius)
					&& efficiency >= MathF.Max(0.965f, _settings.SmoothMinimumPathEfficiency)
					&& steps >= 4
					&& isMechanicalProfile;

				if (fresh && (isBlatantLaserTrajectory || isStandardSmooth || isLowFovSmooth))
				{
					suspicious = true;
					matchedRule = EvidenceType.SmoothConvergence;
					ruleMatched = true;
					largestSnap = smoothMovement;
					bestBefore = smoothBefore;
					bestAfter = smoothAfter;
					if (isBlatantLaserTrajectory)
					{
						isBlatantLaser = true;
						incidentWeight = 3.0f;
					}
				}
				else if (Utils.DebugMode)
				{
					_debug($"{Name(attacker)} smooth curve rejected: movement {smoothMovement:F2}/{_settings.SmoothMinimumMovement:F2}, " +
						   $"error {smoothBefore:F2}->{smoothAfter:F2}/{targetAngularRadius:F2}, path {efficiency * 100:F1}%, steps {steps}/{SmoothMinimumSteps}, spikes {accelerationSpikes}, flips {jitterFlips}, cv {stepCv:F2}.");
				}
			}
		}

		int incidentCommand = shot.CommandNumber;
		Clear();
		if (!suspicious || !ruleMatched)
		{
			// Normal clean human damaging hit: apply gentle decay to oldest non-blatant evidence
			// so long legitimate sessions (DM / retake) do not accumulate coincidences.
			if (st.SmoothEvidence.Count > 0 && smoothShotError > 1.2f)
			{
				st.SmoothEvidence.RemoveAt(0);
			}

			if (Utils.DebugMode)
			{
				if (reusedSnap)
					_debug($"{Name(attacker)} ignored a snap that already counted for an earlier damaging shot.");
				else if (measuredMovements == 0)
					_debug($"{Name(attacker)} damaging shot rejected because {measurementFailure}.");
				else
					_debug($"{Name(attacker)} damaging shot rejected after {measuredMovements} adjacent movements (stopped: {measurementFailure}): largest {largestMeasuredSnap:F2}°, target error {measuredBefore:F2} -> {measuredAfter:F2}.");
			}

			return true;
		}

		st.LastCountedIncidentCommand = incidentCommand;
		st.HasCountedIncident = true;

		ulong steamId = attacker.SteamID;
		var now = DateTime.UtcNow;

		// Reconnect evidence preservation: restore unexpired incidents if player recently disconnected and reconnected
		if (steamId > 0 && st.SnapEvidence.Count == 0 && st.SmoothEvidence.Count == 0 && st.SilentEvidence.Count == 0)
		{
			if (_persistentEvidence.TryGetValue(steamId, out var cached))
			{
				cached.Snap.RemoveAll(i => now - i.Time >= EvidenceWindow);
				cached.Smooth.RemoveAll(i => now - i.Time >= EvidenceWindow);
				cached.Silent.RemoveAll(i => now - i.Time >= EvidenceWindow);
				st.SnapEvidence.AddRange(cached.Snap);
				st.SmoothEvidence.AddRange(cached.Smooth);
				st.SilentEvidence.AddRange(cached.Silent);
				st.SilentAimScore = cached.SilentScore;
			}
		}

		st.SnapEvidence.RemoveAll(i => now - i.Time >= EvidenceWindow);
		st.SmoothEvidence.RemoveAll(i => now - i.Time >= EvidenceWindow);
		st.SilentEvidence.RemoveAll(i => now - i.Time >= EvidenceWindow);

		List<AimbotIncident> incidents = matchedRule switch
		{
			EvidenceType.SmoothConvergence => st.SmoothEvidence,
			EvidenceType.SilentAim => st.SilentEvidence,
			_ => st.SnapEvidence
		};

		incidents.Add(new AimbotIncident(now, matchedRule, largestSnap, bestBefore, bestAfter, incidentWeight));

		if (steamId > 0)
		{
			_persistentEvidence[steamId] = (
				new List<AimbotIncident>(st.SnapEvidence),
				new List<AimbotIncident>(st.SmoothEvidence),
				new List<AimbotIncident>(st.SilentEvidence),
				st.SilentAimScore);
		}

		bool isBlatant = isBlatantSnap || isBlatantLaser || matchedRule == EvidenceType.SilentAim;
		int threshold = isBlatant ? 1 : (matchedRule == EvidenceType.SmoothConvergence ? SmoothDetectionThreshold : SnapDetectionThreshold);

		string blatantTag = isBlatantLaser
			? " [BLATANT LASER: zero-jitter straight trajectory]"
			: (isBlatantSnap ? " [BLATANT SNAP]" : "");

		if (Utils.DebugMode)
			_debug($"{Name(attacker)} counted {matchedRule}{blatantTag} {largestSnap:F2}°, error {bestBefore:F2} -> {bestAfter:F2}, evidence {incidents.Count}/{threshold}.");

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
					EvidenceType.SilentAim => $"{i + 1}. {inc.Movement:F2}° subtick silent-aim desync ({inc.Before:F2}°→{inc.After:F2}°)",
					_ => $"{i + 1}. {inc.Movement:F2}° sudden move ({inc.Before:F2}°→{inc.After:F2}°)",
				});
			}

			string latest = isBlatantLaser
				? $"Latest damaging shot: mathematically synthetic laser trajectory (movement {largestSnap:F2}°, error {bestBefore:F2}° → {bestAfter:F2}°, zero tremor/curvature). Blatant detection: threshold 1."
				: (matchedRule switch
				{
					EvidenceType.SnapReturn => $"Latest damaging shot: aim jumped {largestSnap:F2}° and immediately returned. Evidence {incidents.Count}/{threshold} in 5 min.",
					EvidenceType.SmoothConvergence => $"Latest damaging shot: mechanical {largestSnap:F2}° smooth convergence, enemy distance {bestBefore:F2}° → {bestAfter:F2}°. Evidence {incidents.Count}/{threshold} in 5 min.",
					EvidenceType.SilentAim => $"Latest damaging shot: subtick attack viewangles diverged {largestSnap:F2}° from camera viewangles onto target ({bestBefore:F2}° → {bestAfter:F2}°).",
					_ => $"Latest damaging shot: aim moved {largestSnap:F2}° in one step, enemy distance {bestBefore:F2}° → {bestAfter:F2}°. Evidence {incidents.Count}/{threshold} in 5 min.",
				});

			string violationModule = matchedRule == EvidenceType.SilentAim ? "SILENT_AIM" : "AIMBOT";
			StatDetections++;
			_announce(attacker, violationModule, string.Join("\n", lines) + "\n" + latest);
			st.SnapEvidence.Clear();
			st.SmoothEvidence.Clear();
			st.SilentEvidence.Clear();
			st.SilentAimScore = 0f;
			if (steamId > 0)
				_persistentEvidence.Remove(steamId);
		}

		return true;
	}
}
