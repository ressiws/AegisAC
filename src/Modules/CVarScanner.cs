using System;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;
using AegisAC.Configuration;

namespace AegisAC.Modules;

/// <summary>
/// Dedicated Server-Side Client ConVar Scanner and Integrity Detector.
/// - Queries client console variables via IPlayer.GetClientConvarValue
/// - Verifies that clients do not tamper with protected visual, rendering, or timescale settings
///   (e.g., r_drawothermodels 2 for wireframe wallhack, mat_wireframe, host_timescale speedhacks, sv_cheats)
/// - Automatically bypasses cheat-flagged convars when server has sv_cheats 1 active
/// - Runs on a staggered background schedule (15s initial delay, 90s interval, at most 1 player per tick)
///   to ensure zero frame-time overhead and zero tickrate impact.
/// </summary>
internal sealed class CVarScannerDetector
{
	public const int MaxPlayers = 64;

	private readonly ISwiftlyCore? _core;
	private readonly ILogger? _logger;
	private CVarScannerConfig _config;
	private readonly Action<IPlayer?, string, string, PunishmentType>? _onViolation;
	private readonly Func<bool>? _isSvCheatsActive;
	private readonly DateTime[] _nextScanTimes = new DateTime[MaxPlayers + 1];
	private readonly bool[] _flaggedThisSession = new bool[MaxPlayers + 1];

	public long StatScansCompleted { get; private set; }
	public long StatDetections { get; private set; }

	public CVarScannerDetector(
		ISwiftlyCore? core,
		ILogger? logger,
		CVarScannerConfig config,
		Action<IPlayer?, string, string, PunishmentType>? onViolation = null,
		Func<bool>? isSvCheatsActive = null)
	{
		_core = core;
		_logger = logger;
		_config = config;
		_onViolation = onViolation;
		_isSvCheatsActive = isSvCheatsActive;
	}

	public void UpdateConfig(CVarScannerConfig config) => _config = config;

	public void ResetAll()
	{
		Array.Clear(_nextScanTimes);
		Array.Clear(_flaggedThisSession);
		StatScansCompleted = 0;
		StatDetections = 0;
	}

	public void ResetPlayer(int slot)
	{
		if (slot >= 0 && slot <= MaxPlayers)
		{
			_nextScanTimes[slot] = DateTime.MinValue;
			_flaggedThisSession[slot] = false;
		}
	}

	public string Stats() =>
		$"enabled={_config.Enabled} scans={StatScansCompleted} detections={StatDetections} interval={_config.ScanIntervalSeconds:F0}s";

	internal bool IsSvCheatsActive()
	{
		if (_isSvCheatsActive != null)
			return _isSvCheatsActive();

		if (_core != null)
		{
			try
			{
				if (_core.ConVar.Find<bool>("sv_cheats")?.Value == true)
					return true;
			}
			catch { }
		}

		return false;
	}

	/// <summary>
	/// Staggered background scan (at most 1 player per server frame)
	/// querying client convars via SwiftlyS2's IPlayer.GetClientConvarValue.
	/// </summary>
	public void OnGameFrame()
	{
		if (!_config.Enabled || _core == null)
		{
			return;
		}

		DateTime now = DateTime.UtcNow;
		double initialDelay = Math.Max(1.0, _config.InitialScanDelaySeconds);
		double interval = Math.Max(10.0, _config.ScanIntervalSeconds);

		for (int slot = 0; slot < MaxPlayers; slot++)
		{
			var player = _core.PlayerManager.GetPlayer(slot);
			if (player == null || !AimbotDetector.IsEligibleHuman(player))
			{
				continue;
			}

			if (_nextScanTimes[slot] == DateTime.MinValue)
			{
				_nextScanTimes[slot] = now.AddSeconds(initialDelay);
				continue;
			}

			if (now < _nextScanTimes[slot])
			{
				continue;
			}

			_nextScanTimes[slot] = now.AddSeconds(interval);
			string playerName = player.Controller?.PlayerName ?? $"Player#{slot}";
			ScanPlayerCVars(slot, player, playerName, cvar => player.GetClientConvarValue(cvar));
			break; // Scan at most 1 player per frame (zero tick-time spike)
		}
	}

	internal bool ScanPlayerCVars(
		int slot,
		IPlayer? player,
		string playerName,
		Func<string, string?> getClientCvar)
	{
		if (!_config.Enabled || slot < 0 || slot > MaxPlayers)
		{
			return false;
		}

		StatScansCompleted++;
		bool serverCheats = IsSvCheatsActive();
		var violations = new StringBuilder();

		// 1. r_drawothermodels: classic wireframe wallhack (value 2). Normal is 1 (or 0).
		if (_config.CheckDrawOtherModels && !serverCheats)
		{
			string? val = getClientCvar("r_drawothermodels");
			if (!string.IsNullOrWhiteSpace(val))
			{
				string trimmed = val.Trim();
				if (trimmed == "2")
				{
					AppendViolation(violations, "r_drawothermodels", trimmed, "expected != 2 (wireframe wallhack)");
				}
			}
		}

		// 2. mat_wireframe: full geometry wireframe (values != 0).
		if (_config.CheckMatWireframe && !serverCheats)
		{
			string? val = getClientCvar("mat_wireframe");
			if (!string.IsNullOrWhiteSpace(val))
			{
				string trimmed = val.Trim();
				if (trimmed != "0" && trimmed != "0.0")
				{
					AppendViolation(violations, "mat_wireframe", trimmed, "expected 0 (geometry wireframe)");
				}
			}
		}

		// 3. sv_cheats on client: client reporting 1 while server is 0
		if (_config.CheckSvCheats && !serverCheats)
		{
			string? val = getClientCvar("sv_cheats");
			if (!string.IsNullOrWhiteSpace(val))
			{
				string trimmed = val.Trim();
				if (trimmed != "0" && trimmed != "0.0" && trimmed.ToLowerInvariant() != "false")
				{
					AppendViolation(violations, "sv_cheats", trimmed, "expected 0 on secure server");
				}
			}
		}

		// 4. host_timescale: speedhack / slow-motion tampering. Must be ~1.0.
		if (_config.CheckHostTimescale && !serverCheats)
		{
			string? val = getClientCvar("host_timescale");
			if (!string.IsNullOrWhiteSpace(val))
			{
				if (float.TryParse(val.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float timescale))
				{
					if (MathF.Abs(timescale - 1.0f) > 0.05f)
					{
						AppendViolation(violations, "host_timescale", val.Trim(), "expected 1.0 (timescale speedhack)");
					}
				}
			}
		}

		// 5. fog_enable: disabling fog (value 0). Default is 1.
		if (_config.CheckFogEnable && !serverCheats)
		{
			string? val = getClientCvar("fog_enable");
			if (!string.IsNullOrWhiteSpace(val))
			{
				string trimmed = val.Trim();
				if (trimmed == "0" || trimmed == "0.0" || trimmed.ToLowerInvariant() == "false")
				{
					AppendViolation(violations, "fog_enable", trimmed, "expected 1 (no-fog exploit)");
				}
			}
		}

		// 6. cl_lagcompensation and cl_predict: must be enabled (1)
		if (_config.CheckPredictionAndLagComp)
		{
			string? lagComp = getClientCvar("cl_lagcompensation");
			if (!string.IsNullOrWhiteSpace(lagComp))
			{
				string trimmed = lagComp.Trim();
				if (trimmed == "0" || trimmed == "0.0" || trimmed.ToLowerInvariant() == "false")
				{
					AppendViolation(violations, "cl_lagcompensation", trimmed, "expected 1 (disabled lag compensation)");
				}
			}

			string? predict = getClientCvar("cl_predict");
			if (!string.IsNullOrWhiteSpace(predict))
			{
				string trimmed = predict.Trim();
				if (trimmed == "0" || trimmed == "0.0" || trimmed.ToLowerInvariant() == "false")
				{
					AppendViolation(violations, "cl_predict", trimmed, "expected 1 (disabled client prediction)");
				}
			}
		}

		if (violations.Length == 0)
		{
			return false;
		}

		if (_flaggedThisSession[slot])
		{
			return false;
		}

		_flaggedThisSession[slot] = true;
		StatDetections++;
		string details = $"Forbidden Client CVar: {violations}";
		_logger?.LogWarning("[AegisAC] [CVarScanner] {Name} {Details}", playerName, details);
		_onViolation?.Invoke(player, "Forbidden CVar", details, _config.Punishment);
		return true;
	}

	private static void AppendViolation(StringBuilder sb, string cvar, string actual, string reason)
	{
		if (sb.Length > 0)
		{
			sb.Append("; ");
		}
		sb.Append($"{cvar}=\"{actual}\" ({reason})");
	}
}
