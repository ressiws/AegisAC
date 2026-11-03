using System;
using System.Collections.Generic;
using System.Linq;
using AegisAC.Configuration;
using SwiftlyS2.Shared.Players;

namespace AegisAC.Telemetry;

/// <summary>
/// Aegis Cumulative Conviction Engine (ACCE).
/// Integrates cross-module Bayesian suspicion scoring with exponential temporal half-life decay
/// and multi-subsystem entropy synergy multipliers to detect closet cheating suites with mathematical certainty.
/// </summary>
public sealed class ConvictionEngine
{
	public sealed class SuspectIncident
	{
		public string Module { get; set; } = string.Empty;
		public string Detection { get; set; } = string.Empty;
		public string Details { get; set; } = string.Empty;
		public DateTime Timestamp { get; set; }
		public float Weight { get; set; }
	}

	public sealed class SuspectRecord
	{
		public ulong SteamId { get; set; }
		public int Slot { get; set; } = -1;
		public string PlayerName { get; set; } = string.Empty;
		public float DecayedScore { get; set; }
		public DateTime LastIncidentTime { get; set; } = DateTime.MinValue;
		public DateTime FirstIncidentTime { get; set; } = DateTime.MinValue;
		public bool HasBeenConvicted { get; set; }
		public DateTime LastConvictionTime { get; set; } = DateTime.MinValue;

		public readonly Dictionary<string, int> ModuleCounts = new(StringComparer.OrdinalIgnoreCase);
		public readonly Dictionary<string, (DateTime LastTime, int Count)> DetectionCounts = new(StringComparer.OrdinalIgnoreCase);
		public readonly List<SuspectIncident> RecentIncidents = new();

		public void Reset()
		{
			DecayedScore = 0f;
			LastIncidentTime = DateTime.MinValue;
			FirstIncidentTime = DateTime.MinValue;
			HasBeenConvicted = false;
			LastConvictionTime = DateTime.MinValue;
			ModuleCounts.Clear();
			DetectionCounts.Clear();
			RecentIncidents.Clear();
		}
	}

	private ConvictionConfig _config;
	private readonly Dictionary<ulong, SuspectRecord> _suspectsBySteam = new();
	private readonly SuspectRecord[] _suspectsBySlot = new SuspectRecord[65];

	public ConvictionEngine(ConvictionConfig config)
	{
		_config = config;
		for (int i = 0; i < _suspectsBySlot.Length; i++)
		{
			_suspectsBySlot[i] = new SuspectRecord { Slot = i };
		}
	}

	public void UpdateConfig(ConvictionConfig config) => _config = config;

	public void ResetAll()
	{
		_suspectsBySteam.Clear();
		foreach (var s in _suspectsBySlot)
		{
			s.Reset();
		}
	}

	public void ResetPlayer(int slot)
	{
		if (slot >= 0 && slot < _suspectsBySlot.Length)
		{
			_suspectsBySlot[slot].Reset();
		}
	}

	/// <summary>
	/// Returns the calibrated confidence weight of a detection type.
	/// </summary>
	public static float GetDetectionWeight(string module, string detection) => detection.ToUpperInvariant() switch
	{
		"ANTIAIM" or "SPINBOT" => 5.0f,
		"AIMBOT" or "CONVERGENCE" or "SNAPHIT" => 4.5f,
		"SILENTAIM" or "SUBTICKSILENTAIM" => 4.0f,
		"FORGEDCOMMAND" or "WHENREPEAT" => 3.5f,
		"MOUSELESSAIM" => 3.5f,
		"MOUSEGRID" or "MOUSE_GRID" => 3.0f,
		"TRIGGERBOT" or "SYNTHETICINPUT" => 3.0f,
		"AIMLOCK" or "WALLTRACKING" => 3.0f,
		"INHUMAN_ACCURACY" => 3.0f,
		"IRREGULAR_BEHAVIOR" => 2.5f,
		"BHOP" or "HYPERSCROLL" => 2.5f,
		"AUTOSTRAFE" or "STRAFEOPTIMIZER" => 2.5f,
		"NULLS" or "SNAPTAP" => 2.0f,
		"DOUBLETAP" => 2.0f,
		"NAMECHANGER" => 1.5f,
		"DESUBTICK" or "SUBTICKTAMPER" => 1.5f,
		"DLL_INJECTION" or "DLLINJECTION" => 4.0f,
		"CVAR" or "CVARSCANNER" => 3.5f,
		_ => 2.0f
	};

	/// <summary>
	/// Registers evidence and evaluates cross-module cumulative conviction.
	/// </summary>
	public bool NoteEvidence(
		IPlayer player,
		string module,
		string detection,
		string details,
		out bool convicted,
		out string convictionSummary,
		DateTime? nowOverride = null)
	{
		convicted = false;
		convictionSummary = string.Empty;

		if (!_config.Enabled || !player.IsValid)
			return false;

		DateTime now = nowOverride ?? DateTime.UtcNow;
		ulong steamId = player.SteamID;
		int slot = player.PlayerID;
		string name = player.Controller?.PlayerName ?? $"#{slot}";

		SuspectRecord suspect;
		if (steamId > 0)
		{
			if (!_suspectsBySteam.TryGetValue(steamId, out suspect!))
			{
				suspect = new SuspectRecord { SteamId = steamId, Slot = slot, PlayerName = name };
				_suspectsBySteam[steamId] = suspect;
			}
		}
		else if (slot >= 0 && slot < _suspectsBySlot.Length)
		{
			suspect = _suspectsBySlot[slot];
		}
		else
		{
			return false;
		}

		suspect.PlayerName = name;
		suspect.Slot = slot;

		// 1. Exponential Half-Life Temporal Decay
		float halfLifeMs = Math.Max(1.0f, _config.HalfLifeMinutes) * 60_000.0f;
		if (suspect.LastIncidentTime != DateTime.MinValue && now > suspect.LastIncidentTime)
		{
			double elapsedMs = (now - suspect.LastIncidentTime).TotalMilliseconds;
			double decayFactor = Math.Pow(0.5, elapsedMs / halfLifeMs);
			suspect.DecayedScore = (float)(suspect.DecayedScore * decayFactor);
		}

		suspect.LastIncidentTime = now;
		if (suspect.FirstIncidentTime == DateTime.MinValue)
		{
			suspect.FirstIncidentTime = now;
		}

		// 2. Diminishing returns on monotypic repeats: W_eff = Weight * 0.5^repeats
		float baseWeight = GetDetectionWeight(module, detection);
		int repeatCount = 0;
		if (suspect.DetectionCounts.TryGetValue(detection, out var countInfo))
		{
			double repeatAgeMinutes = (now - countInfo.LastTime).TotalMinutes;
			if (repeatAgeMinutes <= _config.HalfLifeMinutes * 2.0)
			{
				repeatCount = countInfo.Count;
			}
		}

		suspect.DetectionCounts[detection] = (now, repeatCount + 1);
		suspect.ModuleCounts[module] = suspect.ModuleCounts.GetValueOrDefault(module, 0) + 1;

		float addedScore = baseWeight * (float)Math.Pow(0.5, repeatCount);
		suspect.DecayedScore += addedScore;

		suspect.RecentIncidents.Add(new SuspectIncident
		{
			Module = module,
			Detection = detection,
			Details = details,
			Timestamp = now,
			Weight = addedScore
		});

		// Prune incidents older than 2x half-life
		TimeSpan maxHistory = TimeSpan.FromMinutes(_config.HalfLifeMinutes * 2.0);
		suspect.RecentIncidents.RemoveAll(i => now - i.Timestamp > maxHistory);

		// 3. Cross-Module Entropy & Synergy Multiplier
		// When anomalies span orthogonal systems (e.g. Combat + Movement + Exploits), synergy increases confidence
		int distinctModules = suspect.ModuleCounts.Count(m => m.Value > 0);
		float synergyMultiplier = 1.0f;
		if (distinctModules >= _config.MinimumDistinctModules)
		{
			synergyMultiplier += 0.35f * (distinctModules - 1);
		}

		float totalConvictionScore = suspect.DecayedScore * synergyMultiplier;

		// 4. Conviction Evaluation
		bool isCooldownActive = suspect.HasBeenConvicted && (now - suspect.LastConvictionTime).TotalMinutes < _config.HalfLifeMinutes;
		if (totalConvictionScore >= _config.ConvictionThreshold && !isCooldownActive)
		{
			convicted = true;
			suspect.HasBeenConvicted = true;
			suspect.LastConvictionTime = now;

			string moduleBreakdown = string.Join(", ", suspect.ModuleCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}[{kv.Value}x]"));
			convictionSummary = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Cumulative Conviction Score {totalConvictionScore:F1}/{_config.ConvictionThreshold:F1} (Synergy {synergyMultiplier:F2}x across {distinctModules} modules: {moduleBreakdown})");
			return true;
		}

		return false;
	}

	/// <summary>
	/// Returns a list of active suspects sorted by decayed conviction score descending.
	/// </summary>
	public List<SuspectRecord> GetActiveSuspects(DateTime? nowOverride = null)
	{
		var list = new List<SuspectRecord>();
		DateTime now = nowOverride ?? DateTime.UtcNow;
		float halfLifeMs = Math.Max(1.0f, _config.HalfLifeMinutes) * 60_000.0f;

		foreach (var s in _suspectsBySteam.Values)
		{
			if (s.LastIncidentTime != DateTime.MinValue)
			{
				double elapsedMs = (now - s.LastIncidentTime).TotalMilliseconds;
				double decay = Math.Pow(0.5, elapsedMs / halfLifeMs);
				float currentScore = (float)(s.DecayedScore * decay);
				if (currentScore >= 0.5f)
				{
					list.Add(s);
				}
			}
		}

		return list.OrderByDescending(s => s.DecayedScore).ToList();
	}
}
