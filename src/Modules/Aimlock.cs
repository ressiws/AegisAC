/**
 * =============================================================================
 * Aegis Anti-Cheat (AegisAC) - Module: Aimlock
 *
 * Copyright (C) 2026 swisser & sevon
 * Authors:
 *   - swisser (https://github.com/ressiws)
 *   - sevon   (https://github.com/victor201202)
 *
 * Description:
 *   Server-side aimlock detection module analyzing sustained unnatural
 *   target tracking, wall-occlusion adherence, lag-compensated interpolation
 *   hypotheses, and explicit missing-sample handling.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at:
 *     http://www.apache.org/licenses/LICENSE-2.0
 * =============================================================================
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using AegisAC.Configuration;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Services;
using SwiftlyS2.Shared.Trace;

namespace AegisAC.Modules;

internal struct AimlockSample
{
	public int ServerTick;
	public QAngle Angles;
	public Vector EyePosition;
	public bool Valid;
}

internal struct AimlockLagHypothesis
{
	public QAngle StartBearing;
	public float MaximumTargetDisplacement;
	public float RequiredTargetDisplacement;
	public int LagTicks;
	public int OnTargetSamples;
	public int OccludedOnTargetSamples;
	public bool Valid;
}

internal sealed class AimlockTrack
{
	public int TargetIndex = -1;
	public int BodyPoint = -1;
	public int StartServerTick;
	public int LastServerTick;
	public int Samples;
	public int MissingTicks;
	public int OffTargetStreak;
	public int HypothesisCount;
	public readonly AimlockLagHypothesis[] Hypotheses = new AimlockLagHypothesis[AimlockDetector.LagSearchRadius * 2 + 1];

	public void Clear()
	{
		TargetIndex = -1;
		BodyPoint = -1;
		StartServerTick = 0;
		LastServerTick = 0;
		Samples = 0;
		MissingTicks = 0;
		OffTargetStreak = 0;
		HypothesisCount = 0;
		Array.Clear(Hypotheses);
	}
}

internal sealed class AimlockIncident
{
	public required DateTime Time { get; init; }
	public required int OnTargetSamples { get; init; }
	public required int OccludedSamples { get; init; }
	public required int TotalSamples { get; init; }
	public required float Displacement { get; init; }
	public required float RequiredDisplacement { get; init; }
	public required int TargetId { get; init; }
}

internal sealed class AimlockCommand
{
	public int CommandNumber;
	public QAngle Angles;
}

internal sealed class AimlockPlayerData
{
	public AimlockSample Pending;
	public readonly AimlockTrack Track = new();
	public readonly List<AimlockCommand> Commands = new();
	public int LatchedTarget = -1;
	public int LatchedBodyPoint = -1;
	public int BreakStartTick = -1;
	public int LastProcessedTick = -1;
	public bool Latched;
	public readonly List<AimlockIncident> Incidents = new();

	public void Reset()
	{
		Pending = default;
		Track.Clear();
		Commands.Clear();
		LatchedTarget = -1;
		LatchedBodyPoint = -1;
		BreakStartTick = -1;
		LastProcessedTick = -1;
		Latched = false;
		Incidents.Clear();
	}
}

internal sealed class AimlockDetector
{
	public const int LagSearchRadius = 2;
	public const int MaxMissingTicksPerEpisode = 3;
	private const float MaximumInterpolationTicks = 19.0f;
	private int TrackingTicks => Math.Max(16, (int)MathF.Round(_config.TrackingDurationSeconds * 64.0f));
	private const int RearmTicks = 32; // 0.5 seconds at 64-tick
	private const float PlayerHalfWidth = 16.0f;
	private float MinimumDistance => _config.MinimumDistance;
	private float MinimumTargetTravel => _config.MinimumTargetTravel;
	private static readonly TimeSpan EvidenceWindow = TimeSpan.FromMinutes(5);
	private static readonly float[] BodyHeights = [8.0f, 46.0f, 64.0f]; // Feet, Chest, Head

	private AimlockConfig _config;
	private readonly Func<int, PositionFrame?> _findFrame;
	private readonly Func<int, IPlayer?> _getPlayer;
	private readonly Func<int, int, bool> _areEnemies;
	private readonly Action<IPlayer, string, string> _announce;
	private readonly Action<string> _debug;
	private readonly ITraceManager? _traceManager;
	private readonly Func<Vector, Vector, int, int, bool>? _isOccludedOverride;
	private readonly NetworkSafetyMonitor? _networkSafety;

	private readonly AimlockPlayerData[] _players = new AimlockPlayerData[AimbotDetector.MaxPlayers + 1];
	private readonly Dictionary<ulong, List<AimlockIncident>> _persistentIncidents = new();

	public void UpdateConfig(AimlockConfig config) => _config = config;

	public long StatEvaluations, StatTracksStarted, StatMissingTickResets, StatVisibleRejected, StatIncidents, StatDetections;

	public AimlockDetector(
		AimlockConfig config,
		Func<int, PositionFrame?> findFrame,
		Func<int, IPlayer?> getPlayer,
		Func<int, int, bool> areEnemies,
		Action<IPlayer, string, string> announce,
		Action<string> debug,
		ITraceManager? traceManager = null,
		Func<Vector, Vector, int, int, bool>? isOccludedOverride = null,
		NetworkSafetyMonitor? networkSafety = null)
	{
		_config = config;
		_findFrame = findFrame;
		_getPlayer = getPlayer;
		_areEnemies = areEnemies;
		_announce = announce;
		_debug = debug;
		_traceManager = traceManager;
		_isOccludedOverride = isOccludedOverride;
		_networkSafety = networkSafety;

		for (int i = 0; i < _players.Length; i++)
			_players[i] = new AimlockPlayerData();
	}

	public string Stats() =>
		$"evals={StatEvaluations} tracks={StatTracksStarted} gapResets={StatMissingTickResets} visRej={StatVisibleRejected} incidents={StatIncidents} detections={StatDetections}";

	public void Reset()
	{
		_persistentIncidents.Clear();
		for (int i = 0; i < _players.Length; i++)
			_players[i].Reset();
	}

	public void OnClientDisconnect(int playerId)
	{
		if (playerId >= 0 && playerId < _players.Length)
			_players[playerId].Reset();
	}

	public void OnProcessUsercmds(IPlayer player, List<SwiftlyS2.Shared.GameHooks.IUserCmd> cmds)
	{
		if (!_config.Enabled || !AimbotDetector.IsEligibleHuman(player) || cmds.Count == 0)
			return;

		int id = player.PlayerID;
		if (id < 0 || id >= _players.Length)
			return;

		foreach (var cmd in cmds)
		{
			var pb = cmd.CSGOUserCmd;
			int number = (int)cmd.CommandNumber;

			if (!pb.Accessor.HasField("base"))
				continue;

			var b = pb.Base;
			if (!b.Accessor.HasField("viewangles"))
				continue;

			// Use base.Viewangles for continuous aimlock tracking (matches CS2AC Legacy AimlockModule::OnSetupMove)
			QAngle angles = b.Viewangles;
			RecordCommand(player, number, angles);
		}
	}

	internal void RecordCommand(IPlayer player, int commandNumber, QAngle angles)
	{
		if (!_config.Enabled || !AimbotDetector.IsEligibleHuman(player))
			return;

		int id = player.PlayerID;
		if (id < 0 || id >= _players.Length || !AimMath.IsFinite(angles))
			return;

		var st = _players[id];
		for (int ci = st.Commands.Count - 1; ci >= 0; ci--)
		{
			if (st.Commands[ci].CommandNumber == commandNumber)
				return;
			if (st.Commands[ci].CommandNumber < commandNumber)
				break;
		}

		st.Commands.Add(new AimlockCommand
		{
			CommandNumber = commandNumber,
			Angles = angles
		});

		if (st.Commands.Count > 128)
			st.Commands.RemoveAt(0);
	}

	private static bool MeetsCoverage(int onTarget, int samples) =>
		samples > 0 && onTarget * 20 >= samples * 19; // >= 95% on-target coverage

	private static bool MeetsOcclusionRatio(int occludedOnTarget, int onTarget) =>
		onTarget > 0 && occludedOnTarget * 10 >= onTarget * 7; // >= 70% occluded while locked

	internal static int EstimateVisualLag(IPlayer player)
	{
		float rttSeconds = 0.0f;
		try
		{
			var netChan = player.ServerSideClient?.NetChannel;
			if (netChan != null && float.IsFinite(netChan.EngineLatency) && netChan.EngineLatency >= 0.0f)
				rttSeconds = netChan.EngineLatency;
		}
		catch { }

		if (rttSeconds <= 0.0f)
		{
			uint pingMs = PlayerSnapshotProvider.GetPing(player);
			rttSeconds = pingMs / 1000.0f;
		}

		rttSeconds = Math.Clamp(rttSeconds, 0.0f, 2.0f);

		float interpTicks = 1.0f;
		try
		{
			string? interpStr = player.GetClientConvarValue("cl_interp_ratio");
			if (!string.IsNullOrWhiteSpace(interpStr) &&
				float.TryParse(interpStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedInterp) &&
				float.IsFinite(parsedInterp) &&
				parsedInterp >= 0.0f &&
				parsedInterp <= MaximumInterpolationTicks)
			{
				interpTicks = parsedInterp == 0.0f ? 1.0f : parsedInterp;
			}
		}
		catch { }

		return Math.Max(0, (int)MathF.Round(rttSeconds * 64.0f + interpTicks));
	}

	private bool HasOcclusionCheck => _config.RequireWallOcclusion && (_traceManager != null || _isOccludedOverride != null);

	private bool IsTargetOccluded(IPlayer observer, in Vector eye, in Vector targetPoint, int observerIndex, int targetIndex)
	{
		if (_isOccludedOverride != null)
			return _isOccludedOverride(eye, targetPoint, observerIndex, targetIndex);

		if (_traceManager == null)
			return true;

		try
		{
			Vector dir = targetPoint - eye;
			float dist = dir.Length();
			if (dist <= 32.0f)
				return false;

			Vector normDir = new(dir.X / dist, dir.Y / dist, dir.Z / dist);
			float startOffset = Math.Min(24.0f, dist * 0.25f);
			Vector traceStart = new(eye.X + normDir.X * startOffset, eye.Y + normDir.Y * startOffset, eye.Z + normDir.Z * startOffset);
			Vector traceEnd = new(targetPoint.X - normDir.X * 4.0f, targetPoint.Y - normDir.Y * 4.0f, targetPoint.Z - normDir.Z * 4.0f);
			float traceLen = traceStart.Distance(traceEnd);

			var ignoreList = new List<SwiftlyS2.Shared.SchemaDefinitions.CEntityInstance>(2);
			if (observer.PlayerPawn is { IsValid: true } obsPawn)
				ignoreList.Add(obsPawn);
			var targetPlayer = _getPlayer(targetIndex);
			if (targetPlayer?.PlayerPawn is { IsValid: true } tgtPawn)
				ignoreList.Add(tgtPawn);

			var traceParams = new TraceParams
			{
				InteractWith = MaskTrace.Solid,
				EntitiesToIgnore = ignoreList
			};

			var trace = _traceManager.TraceShapeLine(traceStart, traceEnd, traceParams);
			return trace.DidHit && !trace.StartInSolid && trace.Distance < traceLen - 16.0f;
		}
		catch
		{
			return false;
		}
	}

	private bool MeasureTarget(
		AimlockSample sample,
		in TrackedPosition target,
		int bodyPoint,
		in Vector aimForward,
		out QAngle bearing,
		out Vector targetPoint,
		out float error,
		out float maximumError,
		out float requiredTargetDisplacement)
	{
		bearing = default;
		targetPoint = default;
		error = 180.0f;
		maximumError = 0.0f;
		requiredTargetDisplacement = 0.0f;

		if (bodyPoint < 0 || bodyPoint >= BodyHeights.Length)
			return false;

		float height = AimMath.BodyHeight(bodyPoint, target.Crouched);
		targetPoint = new(target.Origin.X, target.Origin.Y, target.Origin.Z + height);
		Vector direction = new(targetPoint.X - sample.EyePosition.X, targetPoint.Y - sample.EyePosition.Y, targetPoint.Z - sample.EyePosition.Z);
		float targetDistance = direction.Length();

		if (!AimMath.IsFinite(targetPoint) || !AimMath.IsFinite(direction) || float.IsNaN(targetDistance) || targetDistance < AimMath.Epsilon)
			return false;

		Vector dirNorm = new(direction.X / targetDistance, direction.Y / targetDistance, direction.Z / targetDistance);
		float dot = Math.Clamp(AimMath.Dot(aimForward, dirNorm), -1.0f, 1.0f);

		error = MathF.Acos(dot) * AimMath.Rad2Deg;
		maximumError = MathF.Atan2(PlayerHalfWidth, targetDistance) * AimMath.Rad2Deg;
		requiredTargetDisplacement = MathF.Atan2(MinimumTargetTravel, targetDistance) * AimMath.Rad2Deg;
		bearing = AimMath.Bearing(sample.EyePosition, targetPoint);

		return float.IsFinite(error) && float.IsFinite(maximumError) && float.IsFinite(requiredTargetDisplacement) && AimMath.IsFinite(bearing);
	}

	private bool EvaluateTarget(
		AimlockSample sample,
		PositionFrame currentFrame,
		PositionFrame historicalFrame,
		int observerIndex,
		int targetIndex,
		int bodyPoint,
		in Vector aimForward,
		out QAngle bearing,
		out Vector targetPoint,
		out float error,
		out float maximumError,
		out float requiredTargetDisplacement)
	{
		bearing = default;
		targetPoint = default;
		error = 180.0f;
		maximumError = 0.0f;
		requiredTargetDisplacement = 0.0f;

		if (observerIndex < 0 || observerIndex >= AimbotDetector.MaxPlayers || targetIndex < 0 || targetIndex >= AimbotDetector.MaxPlayers)
			return false;

		var observer = currentFrame.Players[observerIndex];
		var currentTarget = currentFrame.Players[targetIndex];
		var historicalTarget = historicalFrame.Players[targetIndex];

		if (!observer.Valid || !observer.Alive || observer.Teleported)
			return false;

		if (!currentTarget.Valid || !currentTarget.Alive || currentTarget.Teleported || !_areEnemies(observerIndex, targetIndex))
			return false;

		if (!historicalTarget.Valid || !historicalTarget.Alive || historicalTarget.Teleported)
			return false;

		float dist = historicalTarget.Origin.Distance(sample.EyePosition);
		if (dist < MinimumDistance)
			return false;

		return MeasureTarget(sample, historicalTarget, bodyPoint, aimForward, out bearing, out targetPoint, out error, out maximumError, out requiredTargetDisplacement);
	}

	private readonly record struct Candidate(QAngle Bearing, Vector TargetPoint, float Error, int TargetIndex, int BodyPoint, int LagTicks, bool Valid);

	private Candidate FindCandidate(AimlockSample sample, PositionFrame currentFrame, int observerIndex, int estimateTicks)
	{
		Candidate best = default;
		if (observerIndex < 0 || observerIndex >= AimbotDetector.MaxPlayers || !AimMath.IsFinite(sample.EyePosition) || !AimMath.IsFinite(sample.Angles))
			return best;

		var observer = currentFrame.Players[observerIndex];
		if (!observer.Valid || !observer.Alive || observer.Teleported)
			return best;

		Vector aimForward = AimMath.Forward(sample.Angles);
		int matchedTarget = -1;
		bool ambiguous = false;

		int firstLag = Math.Max(0, estimateTicks - LagSearchRadius);
		int lastLag = estimateTicks + LagSearchRadius;
		float minDistSq = MinimumDistance * MinimumDistance;

		for (int targetIndex = 0; targetIndex < AimbotDetector.MaxPlayers; targetIndex++)
		{
			if (targetIndex == observerIndex)
				continue;

			var currentTarget = currentFrame.Players[targetIndex];
			if (!currentTarget.Valid || !currentTarget.Alive || currentTarget.Teleported || !_areEnemies(observerIndex, targetIndex))
				continue;

			// Fast angular cone pre-filter: Check all 3 body points
			bool withinCone = false;
			for (int bp = 0; bp < BodyHeights.Length; bp++)
			{
				float bx = currentTarget.Origin.X - sample.EyePosition.X;
				float by = currentTarget.Origin.Y - sample.EyePosition.Y;
				float bz = (currentTarget.Origin.Z + AimMath.BodyHeight(bp, currentTarget.Crouched)) - sample.EyePosition.Z;
				float bDistSq = bx * bx + by * by + bz * bz;

				if (bDistSq < minDistSq)
					continue;

				float bDot = bx * aimForward.X + by * aimForward.Y + bz * aimForward.Z;
				if (bDot <= 0.0f)
					continue;

				if (bDot * bDot >= 0.82f * bDistSq)
				{
					withinCone = true;
					break;
				}
			}

			if (!withinCone)
				continue;

			for (int lagTicks = firstLag; lagTicks <= lastLag; lagTicks++)
			{
				var historicalFrame = _findFrame(sample.ServerTick - lagTicks);
				if (historicalFrame == null)
					continue;

				var historicalTarget = historicalFrame.Players[targetIndex];
				if (!historicalTarget.Valid || !historicalTarget.Alive || historicalTarget.Teleported)
					continue;

				if (historicalTarget.Origin.Distance(sample.EyePosition) < MinimumDistance)
					continue;

				for (int bodyPoint = 0; bodyPoint < BodyHeights.Length; bodyPoint++)
				{
					if (!MeasureTarget(sample, historicalTarget, bodyPoint, aimForward, out var bearing, out var targetPt, out var error, out var maximumError, out _) || error > maximumError)
						continue;

					if (matchedTarget < 0)
						matchedTarget = targetIndex;
					else if (matchedTarget != targetIndex)
						ambiguous = true;

					if (error < (best.Valid ? best.Error : 180.0f))
						best = new Candidate(bearing, targetPt, error, targetIndex, bodyPoint, lagTicks, true);
				}
			}
		}

		if (ambiguous)
			return default;

		return best;
	}

	public void OnSetupMove(IPlayer player, int commandNumber, int currentTick)
	{
		if (!_config.Enabled || !AimbotDetector.IsEligibleHuman(player) || !PlayerSnapshotProvider.IsAlive(player))
			return;

		int id = player.PlayerID;
		if (id < 0 || id >= _players.Length)
			return;

		var st = _players[id];
		if (currentTick == st.LastProcessedTick)
			return;

		if (!PlayerSnapshotProvider.TryGetPawnTransform(player, out _, out var eye, out var pawnEyeAngles, out _, out _, out _, out _, out _))
		{
			st.Track.Clear();
			st.Pending = default;
			return;
		}

		var cmd = st.Commands.FindLast(c => c.CommandNumber == commandNumber);
		QAngle angles = cmd != null ? cmd.Angles : pawnEyeAngles;

		if (!AimMath.IsFinite(angles) || !AimMath.IsFinite(eye))
		{
			st.Track.Clear();
			st.Pending = default;
			return;
		}

		st.Pending = new AimlockSample
		{
			ServerTick = currentTick,
			Angles = angles,
			EyePosition = eye,
			Valid = true
		};
	}

	public void OnGameFrame(int currentTick)
	{
		if (!_config.Enabled)
			return;

		var frame = _findFrame(currentTick);

		for (int index = 0; index < AimbotDetector.MaxPlayers; index++)
		{
			var st = _players[index];
			if (!st.Pending.Valid)
			{
				if (frame != null && (!frame.Players[index].Valid || !frame.Players[index].Alive))
					st.Reset();
				continue;
			}

			var player = _getPlayer(index);
			if (player == null || !AimbotDetector.IsEligibleHuman(player) || !player.IsAlive)
			{
				st.Reset();
				continue;
			}

			var sample = st.Pending;
			st.Pending = default;

			if (sample.ServerTick != currentTick || sample.ServerTick <= st.LastProcessedTick || _findFrame(sample.ServerTick) == null)
			{
				if (st.Track.TargetIndex >= 0 && st.Track.Samples > 16)
					_debug($"[Aimlock] {Name(player)} tracking reset: snapshot mismatch (sampleTick={sample.ServerTick}, currentTick={currentTick}, lastProcessed={st.LastProcessedTick}).");
				st.Track.Clear();
				continue;
			}

			st.LastProcessedTick = sample.ServerTick;
			Evaluate(player, st, sample);
		}
	}

	private AimlockLagHypothesis? BestHypothesis(AimlockTrack track, bool requireEvidence)
	{
		AimlockLagHypothesis? best = null;
		bool checkOcclusion = HasOcclusionCheck;

		for (int index = 0; index < track.HypothesisCount; index++)
		{
			var hypothesis = track.Hypotheses[index];
			if (!hypothesis.Valid)
				continue;

			if (requireEvidence)
			{
				if (!MeetsCoverage(hypothesis.OnTargetSamples, track.Samples) ||
					hypothesis.MaximumTargetDisplacement < hypothesis.RequiredTargetDisplacement)
				{
					continue;
				}

				if (checkOcclusion && !MeetsOcclusionRatio(hypothesis.OccludedOnTargetSamples, hypothesis.OnTargetSamples))
				{
					continue;
				}
			}

			if (best == null || hypothesis.OnTargetSamples > best.Value.OnTargetSamples)
				best = hypothesis;
		}
		return best;
	}

	private void Evaluate(IPlayer player, AimlockPlayerData data, AimlockSample sample)
	{
		StatEvaluations++;
		var currentFrame = _findFrame(sample.ServerTick);
		if (currentFrame == null || !AimMath.IsFinite(sample.Angles) || !AimMath.IsFinite(sample.EyePosition))
		{
			data.Track.Clear();
			return;
		}

		Vector aimForward = AimMath.Forward(sample.Angles);

		if (data.Latched)
		{
			bool stillLocked = false;
			int estTicks = EstimateVisualLag(player);
			int firstLag = Math.Max(0, estTicks - LagSearchRadius);
			int lastLag = estTicks + LagSearchRadius;

			for (int lagTicks = firstLag; lagTicks <= lastLag; lagTicks++)
			{
				var historicalFrame = _findFrame(sample.ServerTick - lagTicks);
				if (historicalFrame == null)
					continue;

				if (EvaluateTarget(sample, currentFrame, historicalFrame, player.PlayerID, data.LatchedTarget, data.LatchedBodyPoint, aimForward,
					out _, out _, out var error, out var maximumError, out _) && error <= maximumError)
				{
					stillLocked = true;
					break;
				}
			}

			if (stillLocked)
			{
				data.BreakStartTick = -1;
				return;
			}

			if (data.BreakStartTick < 0)
				data.BreakStartTick = sample.ServerTick;

			if (sample.ServerTick - data.BreakStartTick < RearmTicks)
				return;

			data.Latched = false;
			data.LatchedTarget = -1;
			data.LatchedBodyPoint = -1;
			data.BreakStartTick = -1;
			_debug($"[Aimlock] {Name(player)} rearmed after half-second tracking break.");
		}

		void StartTrack()
		{
			int estTicks = EstimateVisualLag(player);
			var candidate = FindCandidate(sample, currentFrame, player.PlayerID, estTicks);
			if (!candidate.Valid)
				return;

			bool checkOcclusion = HasOcclusionCheck;
			bool initialOccluded = !checkOcclusion || IsTargetOccluded(player, sample.EyePosition, candidate.TargetPoint, player.PlayerID, candidate.TargetIndex);

			data.Track.Clear();
			data.Track.TargetIndex = candidate.TargetIndex;
			data.Track.BodyPoint = candidate.BodyPoint;
			data.Track.StartServerTick = sample.ServerTick;
			data.Track.LastServerTick = sample.ServerTick;
			data.Track.Samples = 1;
			data.Track.MissingTicks = 0;

			int firstLag = Math.Max(0, estTicks - LagSearchRadius);
			int lastLag = estTicks + LagSearchRadius;

			for (int lagTicks = firstLag; lagTicks <= lastLag && data.Track.HypothesisCount < data.Track.Hypotheses.Length; lagTicks++)
			{
				var historicalFrame = _findFrame(sample.ServerTick - lagTicks);
				if (historicalFrame == null)
					continue;

				if (!EvaluateTarget(sample, currentFrame, historicalFrame, player.PlayerID, candidate.TargetIndex, candidate.BodyPoint, aimForward,
					out var bearing, out _, out var error, out var maximumError, out var reqDisplacement))
					continue;

				bool onTarget = error <= maximumError;
				data.Track.Hypotheses[data.Track.HypothesisCount++] = new AimlockLagHypothesis
				{
					StartBearing = bearing,
					MaximumTargetDisplacement = 0.0f,
					RequiredTargetDisplacement = reqDisplacement,
					LagTicks = lagTicks,
					OnTargetSamples = onTarget ? 1 : 0,
					OccludedOnTargetSamples = (onTarget && initialOccluded) ? 1 : 0,
					Valid = true
				};
			}

			if (data.Track.HypothesisCount == 0)
			{
				data.Track.Clear();
				return;
			}

			StatTracksStarted++;
			_debug($"[Aimlock] {Name(player)} started tracking target #{candidate.TargetIndex} across {data.Track.HypothesisCount} delay hypotheses.");
		}

		if (data.Track.TargetIndex < 0)
		{
			StartTrack();
			return;
		}

		int serverTickDelta = sample.ServerTick - data.Track.LastServerTick;

		// Explicit missing-sample handling:
		// Never credit unobserved intervals as confirmed tracking samples!
		// Allow at most a 1-tick gap (serverTickDelta == 2) up to MaxMissingTicksPerEpisode (3) before resetting the track.
		if (serverTickDelta < 1 || serverTickDelta > 2)
		{
			StatMissingTickResets++;
			if (data.Track.Samples > 16)
				_debug($"[Aimlock] {Name(player)} tracking reset: simulated ticks not consecutive (delta={serverTickDelta}).");
			data.Track.Clear();
			StartTrack();
			return;
		}

		if (serverTickDelta == 2)
		{
			data.Track.MissingTicks++;
			if (data.Track.MissingTicks > MaxMissingTicksPerEpisode)
			{
				StatMissingTickResets++;
				_debug($"[Aimlock] {Name(player)} tracking reset: exceeded maximum missing ticks ({data.Track.MissingTicks}).");
				data.Track.Clear();
				StartTrack();
				return;
			}
		}

		// Credit strictly 1 observed sample for this verified command
		data.Track.Samples += 1;
		int validHypotheses = 0;
		bool anyOnTarget = false;
		bool checkOcclusion = HasOcclusionCheck;
		bool occlusionComputed = false;
		bool isOccludedSample = true;

		for (int index = 0; index < data.Track.HypothesisCount; index++)
		{
			ref var hypothesis = ref data.Track.Hypotheses[index];
			if (!hypothesis.Valid)
				continue;

			var historicalFrame = _findFrame(sample.ServerTick - hypothesis.LagTicks);
			if (historicalFrame == null || !EvaluateTarget(sample, currentFrame, historicalFrame, player.PlayerID, data.Track.TargetIndex, data.Track.BodyPoint, aimForward,
				out var bearing, out var targetPoint, out var error, out var maximumError, out _))
			{
				hypothesis.Valid = false;
				continue;
			}

			float displacement = AimMath.AngularDistance(hypothesis.StartBearing, bearing);
			if (!float.IsFinite(displacement))
			{
				hypothesis.Valid = false;
				continue;
			}

			validHypotheses++;
			if (error <= maximumError)
			{
				anyOnTarget = true;
				hypothesis.OnTargetSamples += 1;
				if (checkOcclusion)
				{
					if (!occlusionComputed)
					{
						isOccludedSample = IsTargetOccluded(player, sample.EyePosition, targetPoint, player.PlayerID, data.Track.TargetIndex);
						occlusionComputed = true;
					}
					if (isOccludedSample)
						hypothesis.OccludedOnTargetSamples += 1;
				}
				else
				{
					hypothesis.OccludedOnTargetSamples += 1;
				}
			}

			hypothesis.MaximumTargetDisplacement = MathF.Max(hypothesis.MaximumTargetDisplacement, displacement);
		}

		if (validHypotheses == 0)
		{
			_debug($"[Aimlock] {Name(player)} tracking reset: fixed target history no longer trustworthy.");
			data.Track.Clear();
			StartTrack();
			return;
		}

		if (anyOnTarget)
		{
			data.Track.OffTargetStreak = 0;
		}
		else
		{
			data.Track.OffTargetStreak++;
			if (data.Track.OffTargetStreak > 3)
			{
				data.Track.Clear();
				StartTrack();
				return;
			}
		}

		data.Track.LastServerTick = sample.ServerTick;

		// Require actual observed samples >= TrackingTicks (never count missing gap ticks toward duration)
		if (data.Track.Samples < TrackingTicks)
			return;

		var passing = BestHypothesis(data.Track, requireEvidence: true);
		if (passing == null)
		{
			var best = BestHypothesis(data.Track, requireEvidence: false);
			if (best != null)
			{
				if (MeetsCoverage(best.Value.OnTargetSamples, data.Track.Samples) &&
					best.Value.MaximumTargetDisplacement >= best.Value.RequiredTargetDisplacement &&
					checkOcclusion &&
					!MeetsOcclusionRatio(best.Value.OccludedOnTargetSamples, best.Value.OnTargetSamples))
				{
					StatVisibleRejected++;
					_debug($"[Aimlock] {Name(player)} tracking ignored: target was visible in line-of-sight ({best.Value.OccludedOnTargetSamples}/{best.Value.OnTargetSamples} occluded).");
				}
				else
				{
					_debug($"[Aimlock] {Name(player)} episode ended without evidence: best delay {best.Value.LagTicks}t kept {best.Value.OnTargetSamples}/{data.Track.Samples} on target; target moved {best.Value.MaximumTargetDisplacement:F1}°/{best.Value.RequiredTargetDisplacement:F1}° required.");
				}
			}
			data.Track.Clear();
			return;
		}

		if (_networkSafety != null && _networkSafety.ShouldSkipDetection(player.PlayerID, out var netEv))
		{
			_debug($"[Aimlock] {Name(player)} tracking episode deferred due to network instability: {netEv.FormatSummary()}");
			data.Track.Clear();
			return;
		}

		AddIncident(player, data, passing.Value);
	}

	private void AddIncident(IPlayer player, AimlockPlayerData data, in AimlockLagHypothesis hypothesis)
	{
		StatIncidents++;
		var now = DateTime.UtcNow;
		ulong steamId = player.SteamID;

		if (steamId > 0 && data.Incidents.Count == 0)
		{
			if (_persistentIncidents.TryGetValue(steamId, out var cached))
			{
				cached.RemoveAll(i => now - i.Time >= EvidenceWindow);
				data.Incidents.AddRange(cached);
			}
		}

		data.Incidents.RemoveAll(i => now - i.Time >= EvidenceWindow);

		data.Incidents.Add(new AimlockIncident
		{
			Time = now,
			OnTargetSamples = hypothesis.OnTargetSamples,
			OccludedSamples = hypothesis.OccludedOnTargetSamples,
			TotalSamples = data.Track.Samples,
			Displacement = hypothesis.MaximumTargetDisplacement,
			RequiredDisplacement = hypothesis.RequiredTargetDisplacement,
			TargetId = data.Track.TargetIndex
		});

		if (steamId > 0)
		{
			_persistentIncidents[steamId] = new List<AimlockIncident>(data.Incidents);
		}

		_debug($"[Aimlock] {Name(player)} recorded aimlock episode: {hypothesis.OnTargetSamples}/{data.Track.Samples} on-target ticks ({hypothesis.OccludedOnTargetSamples} occluded), {hypothesis.MaximumTargetDisplacement:F1}°/{hypothesis.RequiredTargetDisplacement:F1}° displacement. Evidence: {data.Incidents.Count}/{_config.EvidenceThreshold}.");

		if (data.Incidents.Count >= _config.EvidenceThreshold)
		{
			StatDetections++;
			var lines = new List<string>();
			for (int i = 0; i < data.Incidents.Count; i++)
			{
				var inc = data.Incidents[i];
				lines.Add($"{i + 1}. Locked {inc.OnTargetSamples}/{inc.TotalSamples} observed ticks ({inc.OccludedSamples} occluded, {inc.Displacement:F1}° travel)");
			}

			string latest = $"Latest aimlock: pinned inside enemy hitbox hull for {hypothesis.OnTargetSamples}/{data.Track.Samples} verified ticks ({hypothesis.OccludedOnTargetSamples} through solid cover) while target traversed {hypothesis.MaximumTargetDisplacement:F1}° across view (required: {hypothesis.RequiredTargetDisplacement:F1}°). Evidence {data.Incidents.Count}/{_config.EvidenceThreshold} in 5 min.";

			string evidence = string.Join("\n", lines) + "\n" + latest;

			_announce(player, "AIMLOCK", evidence);
			data.Incidents.Clear();
			if (steamId > 0)
				_persistentIncidents.Remove(steamId);

			data.Latched = true;
			data.LatchedTarget = data.Track.TargetIndex;
			data.LatchedBodyPoint = data.Track.BodyPoint;
			data.BreakStartTick = -1;
		}

		data.Track.Clear();
	}

	private static string Name(IPlayer p) =>
		p.Controller?.PlayerName ?? $"#{p.PlayerID}";
}
