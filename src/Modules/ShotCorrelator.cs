/**
 * =============================================================================
 * Aegis Anti-Cheat (AegisAC) - Module: ShotCorrelator
 *
 * Copyright (C) 2026 swisser & sevon
 * Authors:
 *   - swisser (https://github.com/ressiws)
 *   - sevon   (https://github.com/victor201202)
 *
 * Description:
 *   Correlates user commands, simulated ticks, weapon firing events,
 *   and damage outcomes to maintain sub-tick timing accuracy.
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

namespace AegisAC.Modules;

public sealed record ShotRecord(int PlayerSlot, int CommandNumber, int ServerTick, int FireTick, string Weapon, int VictimSlot = -1);

public sealed class ShotCorrelator
{
	private const int ShotMatchTicks = 1;
	private const int ShotLifetimeTicks = 2;
	private const int MaxCommands = 32;
	private const int MaxShots = 8;

	private sealed class CommandRecord
	{
		public int CommandNumber { get; init; }
		public int ServerTick { get; set; } = -1;
		public bool Simulated { get; set; }
		public bool Consumed { get; set; }
	}

	private sealed class PlayerHistory
	{
		public readonly List<CommandRecord> Commands = [];
		public readonly List<ShotRecord> Shots = [];
	}

	private readonly Dictionary<int, PlayerHistory> _players = [];

	private PlayerHistory GetPlayer(int slot)
	{
		if (!_players.TryGetValue(slot, out var history))
		{
			history = new PlayerHistory();
			_players.Add(slot, history);
		}

		return history;
	}

	public void RecordCommand(int slot, int commandNumber)
	{
		if (slot <= 0 || commandNumber < 0)
			return;

		var history = GetPlayer(slot);

		if (history.Commands.Any(c => c.CommandNumber == commandNumber))
			return;

		history.Commands.Add(new CommandRecord
		{
			CommandNumber = commandNumber
		});

		if (history.Commands.Count > MaxCommands)
			history.Commands.RemoveAt(0);
	}

	public void MarkSimulated(int slot, int commandNumber, int serverTick)
	{
		if (!_players.TryGetValue(slot, out var history))
			return;

		var command = history.Commands.LastOrDefault(c => c.CommandNumber == commandNumber);

		if (command is null)
			return;

		command.ServerTick = serverTick;
		command.Simulated = true;
	}

	public ShotRecord? OnWeaponFire(int slot, string weapon, int currentTick)
	{
		if (slot <= 0 || !IsBallisticWeapon(weapon))
			return null;

		var history = GetPlayer(slot);

		var candidates = history.Commands
			.Where(c =>
				c.Simulated &&
				!c.Consumed &&
				currentTick - c.ServerTick is >= 0 and <= ShotMatchTicks)
			.ToArray();

		if (candidates.Length != 1)
		{
			foreach (var candidate in candidates)
				candidate.Consumed = true;

			return null;
		}

		var match = candidates[0];
		match.Consumed = true;

		var shot = new ShotRecord(
			slot,
			match.CommandNumber,
			match.ServerTick,
			currentTick,
			weapon);

		history.Shots.Add(shot);

		if (history.Shots.Count > MaxShots)
			history.Shots.RemoveAt(0);

		return shot;
	}

	public ShotRecord? OnPlayerHurt(int attackerSlot, int victimSlot, int currentTick)
	{
		if (attackerSlot <= 0 || victimSlot <= 0 || attackerSlot == victimSlot || !_players.TryGetValue(attackerSlot, out var history))
		{
			return null;
		}

		var candidates = history.Shots
			.Where(s =>
				s.VictimSlot == -1 &&
				currentTick - s.FireTick is >= 0 and <= ShotLifetimeTicks)
			.ToArray();

		if (candidates.Length != 1)
			return null;

		var shot = candidates[0];

		var matched = shot with { VictimSlot = victimSlot };
		history.Shots[history.Shots.IndexOf(shot)] = matched;

		return matched;
	}

	public void Prune(int currentTick)
	{
		foreach (var history in _players.Values)
		{
			history.Commands.RemoveAll(c => c.Simulated && currentTick - c.ServerTick > ShotLifetimeTicks);
			history.Shots.RemoveAll(s => currentTick - s.FireTick > ShotLifetimeTicks);
		}
	}

	public void ResetPlayer(int slot)
	{
		_players.Remove(slot);
	}

	public void Reset()
	{
		_players.Clear();
	}

	private static bool IsBallisticWeapon(string weapon)
	{
		weapon = weapon.StartsWith("weapon_", StringComparison.Ordinal) ? weapon[7..] : weapon;

		return weapon is
			"ak47" or "aug" or "awp" or "bizon" or "cz75a" or
			"deagle" or "elite" or "famas" or "fiveseven" or
			"g3sg1" or "galilar" or "glock" or "hkp2000" or
			"m249" or "m4a1" or "m4a1_silencer" or "mac10" or
			"mag7" or "mp5sd" or "mp7" or "mp9" or "negev" or
			"nova" or "p250" or "p90" or "revolver" or
			"sawedoff" or "scar20" or "sg556" or "ssg08" or
			"tec9" or "ump45" or "usp_silencer" or "xm1014";
	}
}
