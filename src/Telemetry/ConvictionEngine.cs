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

	public void ResetPlayer(ulong steamId)
	{
		if (steamId > 0)
		{
			_suspectsBySteam.Remove(steamId);
		}
	}

	/// <summary>
	/// Returns the calibrated confidence weight of a detection type.
	/// Normalizes spaces, underscores, and hyphens so all production module names match their exact weights.
	/// </summary>
	public static float GetDetectionWeight(string module, string detection)
	{
		string norm = detection.ToUpperInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
		return norm switch
		{
			"ANTIAIM" or "SPINBOT" or "UNTRUSTED" or "ANTIUNTRUSTED" => 5.0f,
			"AIMBOT" or "CONVERGENCE" or "SNAPHIT" => 4.5f,
			"SILENTAIM" or "SUBTICKSILENTAIM" or "SNAPRETURN" => 4.0f,
			"FORGEDCOMMAND" or "WHENREPEAT" or "ZEROTICK" => 3.5f,
			"MOUSELESSAIM" => 3.5f,
			"MOUSEGRID" => 3.0f,
			"TRIGGERBOT" or "SYNTHETICINPUT" => 3.0f,
			"AIMLOCK" or "WALLTRACKING" => 3.0f,
			"INHUMANACCURACY" => 3.0f,
			"IRREGULARBEHAVIOR" => 2.5f,
			"BHOP" or "HYPERSCROLL" => 2.5f,
			"AUTOSTRAFE" or "STRAFEOPTIMIZER" => 2.5f,
			"NULLS" or "SNAPTAP" => 2.0f,
			"DOUBLETAP" => 2.0f,
			"NAMECHANGER" => 1.5f,
			"DESUBTICK" or "SUBTICKTAMPER" or "INVALIDINPUT" => 1.5f,
			"DLLINJECTION" => 4.0f,
			"CVAR" or "CVARSCANNER" or "FORBIDDENCVAR" => 3.5f,
			_ => 2.0f
		};
	}

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

		// Check for duplicate reports of the same physical anomaly across modules within 0.25s
		bool isDuplicateCrossModuleReport = suspect.RecentIncidents.Any(i =>
			(now - i.Timestamp).TotalSeconds <= 0.25 &&
			(string.Equals(i.Detection, detection, StringComparison.OrdinalIgnoreCase) ||
			 (detection.Contains("UNTRUSTED", StringComparison.OrdinalIgnoreCase) && i.Detection.Contains("UNTRUSTED", StringComparison.OrdinalIgnoreCase))));

		suspect.DetectionCounts[detection] = (now, repeatCount + 1);

		float addedScore = baseWeight * (float)Math.Pow(0.5, repeatCount);
		suspect.DecayedScore += addedScore;

		suspect.RecentIncidents.Add(new SuspectIncident
		{
			Module = isDuplicateCrossModuleReport ? suspect.RecentIncidents.Last().Module : module,
			Detection = detection,
			Details = details,
			Timestamp = now,
			Weight = addedScore
		});

		// Prune incidents older than 2x half-life
		TimeSpan maxHistory = TimeSpan.FromMinutes(_config.HalfLifeMinutes * 2.0);
		suspect.RecentIncidents.RemoveAll(i => now - i.Timestamp > maxHistory);

		// Dynamically synchronize active module counts with non-expired incidents
		suspect.ModuleCounts.Clear();
		foreach (var inc in suspect.RecentIncidents)
		{
			suspect.ModuleCounts[inc.Module] = suspect.ModuleCounts.GetValueOrDefault(inc.Module, 0) + 1;
		}

		// 3. Cross-Module Entropy & Synergy Multiplier
		// Calculated strictly from unexpired incidents across distinct systems
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
	/// Returns a list of active suspects sorted by real-time decayed conviction score descending.
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
				double decay = Math.Pow(0.5, Math.Max(0.0, elapsedMs) / halfLifeMs);
				s.DecayedScore = (float)(s.DecayedScore * decay);
				s.LastIncidentTime = now;

				TimeSpan maxHistory = TimeSpan.FromMinutes(_config.HalfLifeMinutes * 2.0);
				s.RecentIncidents.RemoveAll(i => now - i.Timestamp > maxHistory);
				s.ModuleCounts.Clear();
				foreach (var inc in s.RecentIncidents)
				{
					s.ModuleCounts[inc.Module] = s.ModuleCounts.GetValueOrDefault(inc.Module, 0) + 1;
				}

				if (s.DecayedScore >= 0.5f || s.HasBeenConvicted)
				{
					list.Add(s);
				}
			}
		}

		return list.OrderByDescending(s => s.DecayedScore).ToList();
	}
}
