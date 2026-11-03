using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Convars;
using SwiftlyS2.Shared.Natives;
using AegisAC.Configuration;
using AegisAC.Utilities.AntiWallHack;

namespace AegisAC.Modules;

/// <summary>
/// Server-side Anti-WallHack (Fog of War / Entity Occlusion) detector module.
/// Intercepts network entity transmission via ISource2GameEntities::CheckTransmit,
/// suppressing enemy player pawns, scene children, and weapons when occluded behind
/// solid world geometry.
/// </summary>
internal sealed class AntiWallHackDetector : IDisposable
{
	private readonly ISwiftlyCore _core;
	private AntiWallHackConfig _config;
	private readonly SmokeOcclusionTracker _smokeTracker = new();
	private readonly OcclusionTracer _tracer;
	private readonly VisibilityTracker _tracker;
	private TransmitHook? _hook;

	private IConVar? _dontTransmitCVar;
	private string? _previousDontTransmitVal;
	private bool _faulted;
	private bool _roundEnded;
	private bool _disposed;

	public bool IsActive => _config.Enabled && _hook?.IsHooked == true && !_faulted;
	public int HiddenPairs => _tracker.HiddenPairsCount;
	public int TracesThisTick => _tracer.TracesThisTick;
	public int BudgetSkipsThisTick => _tracer.BudgetSkipsThisTick;
	public int WithheldEntitiesThisTick { get; private set; }
	public bool TeammatesAreEnemies
	{
		get => _tracker.TeammatesAreEnemies;
		set => _tracker.TeammatesAreEnemies = value;
	}

	public AntiWallHackDetector(ISwiftlyCore core, AntiWallHackConfig config)
	{
		_core = core;
		_config = config;
		_tracer = new OcclusionTracer(core, config, _smokeTracker);
		_tracker = new VisibilityTracker(core, config, _tracer);

		if (_config.Enabled)
		{
			try
			{
				ConfigureTransmitCVar();
				_hook = new TransmitHook(core, OnPostTransmit, OnHookFailure);
				if (_hook.IsHooked)
				{
					_core.Logger.LogInformation("[AegisAC-AWH] AntiWallHack network occlusion hook installed successfully (ShadowMode={ShadowMode}).", _config.ShadowMode);
				}
				else
				{
					_core.Logger.LogWarning("[AegisAC-AWH] AntiWallHack hook could not be initialized (unmanaged function not found).");
				}
			}
			catch (Exception ex)
			{
				_core.Logger.LogWarning(ex, "[AegisAC-AWH] AntiWallHack initialization failed; network occlusion disabled.");
				_hook = null;
			}
		}
	}

	public void UpdateConfig(AntiWallHackConfig config)
	{
		bool wasEnabled = _config.Enabled;
		_config = config;
		_tracer.UpdateConfig(config);
		_tracker.UpdateConfig(config);

		if (!_config.Enabled && wasEnabled)
		{
			_hook?.Dispose();
			_hook = null;
			RestoreTransmitCVar();
			_tracker.Reset();
		}
		else if (_config.Enabled && !wasEnabled && _hook == null)
		{
			try
			{
				ConfigureTransmitCVar();
				_hook = new TransmitHook(_core, OnPostTransmit, OnHookFailure);
				if (_hook.IsHooked)
				{
					_core.Logger.LogInformation("[AegisAC-AWH] AntiWallHack hook installed upon reload (ShadowMode={ShadowMode}).", _config.ShadowMode);
				}
			}
			catch (Exception ex)
			{
				_core.Logger.LogWarning(ex, "[AegisAC-AWH] AntiWallHack initialization failed on reload.");
			}
		}
	}

	public void OnRoundStart()
	{
		_roundEnded = false;
		_smokeTracker.OnRoundReset();
		_tracker.Reset();
	}

	public void OnRoundEnd()
	{
		_roundEnded = true;
		_smokeTracker.OnRoundReset();
		_tracker.Reset();
	}

	public void ResetPlayer(int slot)
	{
		_tracker.ResetPlayer(slot);
	}

	public void ResetAll()
	{
		_smokeTracker.OnRoundReset();
		_tracker.Reset();
	}

	public void OnMapLoad()
	{
		_roundEnded = false;
		ConfigureTransmitCVar();
		_smokeTracker.OnRoundReset();
		_tracker.Reset();
	}

	public void OnSmokeDetonate(Vector center, float gameTime) =>
		_smokeTracker.OnSmokeDetonate(new System.Numerics.Vector3(center.X, center.Y, center.Z), gameTime);

	public void OnHeDetonate(Vector center, float gameTime) =>
		_smokeTracker.OnHeDetonate(new System.Numerics.Vector3(center.X, center.Y, center.Z), gameTime);

	public void OnWeaponFired(int slot, int currentTick) =>
		_tracker.OnWeaponFired(slot, currentTick);

	public void OnGameFrame(int tick)
	{
		// Main tick progression if needed outside CheckTransmit
	}

	private void ConfigureTransmitCVar()
	{
		try
		{
			if (string.Equals(_config.TransmitCVarMode, "preserve", StringComparison.OrdinalIgnoreCase))
			{
				return;
			}

			_dontTransmitCVar = _core.ConVar.FindAsString("sv_enable_donttransmit");
			if (_dontTransmitCVar != null)
			{
				_previousDontTransmitVal ??= _dontTransmitCVar.ValueAsString;
				string desired = string.IsNullOrWhiteSpace(_config.TransmitCVarMode) ? "1" : _config.TransmitCVarMode.Trim();
				_dontTransmitCVar.ValueAsString = desired;
			}
		}
		catch (Exception ex)
		{
			_core.Logger.LogDebug(ex, "[AegisAC-AWH] sv_enable_donttransmit not configurable.");
		}
	}

	private void RestoreTransmitCVar()
	{
		if (_dontTransmitCVar != null && _previousDontTransmitVal != null)
		{
			try
			{
				_dontTransmitCVar.ValueAsString = _previousDontTransmitVal;
			}
			catch { }
		}
	}

	private unsafe void OnPostTransmit(nint infos, int count)
	{
		if (_faulted || !_config.Enabled || infos == 0 || count is < 1 or > 64)
		{
			return;
		}

		if (_roundEnded)
		{
			_tracker.Reset();
			return;
		}

		int tick = _core.Engine.GlobalVars.TickCount;
		_tracker.BeginFrame(tick);

		if (_tracker.ActivePlayerCount < 2)
		{
			return;
		}

		// First pass: evaluate viewer visibility for all transmitting players
		for (int offset = 0; offset < count; offset++)
		{
			int index = (offset + (tick & int.MaxValue) % count) % count;
			nint infoPtr = ((nint*)infos)[index];

			if (TransmitHook.TryReadInfo(infoPtr, out int recipient, out _, out _))
			{
				_tracker.EvaluateViewer(recipient, tick);
			}
		}

		// When running in Shadow Mode, we compute occlusion and metrics for observation without withholding packets
		if (_config.ShadowMode)
		{
			return;
		}

		// Second pass: apply suppression masks to entities hidden from each viewer
		int withheldCount = 0;
		for (int i = 0; i < count; i++)
		{
			nint infoPtr = ((nint*)infos)[i];
			if (!TransmitHook.TryReadInfo(infoPtr, out int slot, out nint primary, out nint suppressed))
			{
				continue;
			}

			var hiddenList = _tracker.GetHiddenEntities(slot, tick);
			if (hiddenList == null || hiddenList.Count == 0)
			{
				continue;
			}

			uint* primaryPtr = (uint*)primary;
			uint* suppressedPtr = (uint*)suppressed;

			for (int e = 0; e < hiddenList.Count; e++)
			{
				var entity = hiddenList[e];
				if (entity.IsValid)
				{
					if (TransmitHook.WithholdEntity(primaryPtr, suppressedPtr, (int)entity.Index))
					{
						withheldCount++;
					}
				}
			}
		}
		WithheldEntitiesThisTick = withheldCount;
	}

	private void OnHookFailure(Exception error)
	{
		if (!_faulted)
		{
			_core.Logger.LogError(error, "[AegisAC-AWH] AntiWallHack CheckTransmit failure. Pausing occlusion to protect server stability.");
		}
		_faulted = true;
		_tracker.Reset();
	}

	public string Stats()
	{
		string hookState = _hook?.IsHooked == true ? (_faulted ? "FAULTED" : (_config.ShadowMode ? "SHADOW" : "ACTIVE")) : "DISABLED";
		return $"AntiWallHack: Enabled={_config.Enabled}, ShadowMode={_config.ShadowMode}, State={hookState}, HiddenPairs={HiddenPairs}, Withheld={WithheldEntitiesThisTick}, DontTx={_dontTransmitCVar?.ValueAsString ?? "N/A"}, Traces={TracesThisTick}, Skips={BudgetSkipsThisTick}, Smokes={_smokeTracker.ActiveSmokeCount}, GraceTicks={_config.VisibleGraceTicks}, CornerPred={_config.CornerPrediction}, TargetPred={_config.TargetPrediction}, SmokeOcc={_config.SmokeOcclusion}, CombatRev={_config.CombatReveal}";
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;

		_hook?.Dispose();
		_tracker.Reset();
		RestoreTransmitCVar();
	}
}
