using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Convars;
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
	private readonly OcclusionTracer _tracer;
	private readonly VisibilityTracker _tracker;
	private readonly TransmitHook? _hook;

	private IConVar? _dontTransmitCVar;
	private string? _previousDontTransmitVal;
	private bool _faulted;
	private bool _roundEnded;
	private bool _disposed;

	public bool IsActive => _config.Enabled && _hook?.IsHooked == true && !_faulted;
	public int HiddenPairs => _tracker.HiddenPairsCount;
	public int TracesThisTick => _tracer.TracesThisTick;
	public int BudgetSkipsThisTick => _tracer.BudgetSkipsThisTick;

	public AntiWallHackDetector(ISwiftlyCore core, AntiWallHackConfig config)
	{
		_core = core;
		_config = config;
		_tracer = new OcclusionTracer(core, config);
		_tracker = new VisibilityTracker(core, config, _tracer);

		try
		{
			ConfigureTransmitCVar();
			_hook = new TransmitHook(core, OnPostTransmit, OnHookFailure);
			if (_hook.IsHooked)
			{
				_core.Logger.LogInformation("[AegisAC-AWH] AntiWallHack network occlusion hook installed successfully.");
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

	public void UpdateConfig(AntiWallHackConfig config)
	{
		_config = config;
	}

	public void OnRoundStart()
	{
		_roundEnded = false;
		_tracker.Reset();
	}

	public void OnRoundEnd()
	{
		_roundEnded = true;
		_tracker.Reset();
	}

	public void ResetPlayer(int slot)
	{
		_tracker.Reset();
	}

	public void ResetAll()
	{
		_tracker.Reset();
	}

	public void OnGameFrame(int tick)
	{
		// Main tick progression if needed outside CheckTransmit
	}

	private void ConfigureTransmitCVar()
	{
		try
		{
			_dontTransmitCVar = _core.ConVar.FindAsString("sv_enable_donttransmit");
			if (_dontTransmitCVar != null)
			{
				_previousDontTransmitVal ??= _dontTransmitCVar.ValueAsString;
				_dontTransmitCVar.ValueAsString = "0";
			}
		}
		catch (Exception ex)
		{
			_core.Logger.LogDebug(ex, "[AegisAC-AWH] sv_enable_donttransmit not configurable.");
		}
	}

	private unsafe void OnPostTransmit(nint infos, int count)
	{
		if (_faulted || !_config.Enabled || infos == 0 || count is < 1 or > 64)
		{
			return;
		}

		var rules = _core.EntitySystem.GetGameRules();
		if (rules == null || rules.WarmupPeriod || rules.FreezePeriod || _roundEnded)
		{
			_tracker.Reset();
			return;
		}

		int tick = _core.Engine.GlobalVars.TickCount;
		_tracker.BeginFrame(tick);

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

		// Second pass: apply suppression masks to entities hidden from each viewer
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
					TransmitHook.WithholdEntity(primaryPtr, suppressedPtr, (int)entity.Index);
				}
			}
		}
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
		string hookState = _hook?.IsHooked == true ? (_faulted ? "FAULTED" : "ACTIVE") : "UNAVAILABLE";
		return $"AntiWallHack: Enabled={_config.Enabled}, State={hookState}, HiddenPairs={HiddenPairs}, Traces={TracesThisTick}, Skips={BudgetSkipsThisTick}, GraceTicks={_config.VisibleGraceTicks}, CornerPrediction={_config.CornerPrediction}";
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;

		_hook?.Dispose();
		_tracker.Reset();

		if (_dontTransmitCVar != null && _previousDontTransmitVal != null)
		{
			try
			{
				_dontTransmitCVar.ValueAsString = _previousDontTransmitVal;
			}
			catch { }
		}
	}
}
