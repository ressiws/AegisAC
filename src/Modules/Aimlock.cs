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
 *   target tracking, angle locks, and mechanical adherence across player movement.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at:
 *     http://www.apache.org/licenses/LICENSE-2.0
 * =============================================================================
 */

using System;
using System.Collections.Generic;
using AegisAC.Configuration;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;

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
	public bool Valid;
}

internal sealed class AimlockTrack
{
	public int TargetIndex = -1;
	public int BodyPoint = -1;
	public int StartServerTick;
	public int LastServerTick;
	public int Samples;
	public int HypothesisCount;
	public readonly AimlockLagHypothesis[] Hypotheses = new AimlockLagHypothesis[AimlockDetector.LagSearchRadius * 2 + 1];

	public void Clear()
	{
		TargetIndex = -1;
		BodyPoint = -1;
		StartServerTick = 0;
		LastServerTick = 0;
		Samples = 0;
		HypothesisCount = 0;
		Array.Clear(Hypotheses);
	}
}

internal sealed class AimlockIncident
{
	public required DateTime Time { get; init; }
	public required int OnTargetSamples { get; init; }
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
	private int TrackingTicks => Math.Max(16, (int)MathF.Round(_config.TrackingDurationSeconds * 64.0f));
	private const int RearmTicks = 32;     // 0.5 seconds at 64-tick
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

	private readonly AimlockPlayerData[] _players = new AimlockPlayerData[AimbotDetector.MaxPlayers + 1];

	public void UpdateConfig(AimlockConfig config) => _config = config;

	public long StatEvaluations, StatTracksStarted, StatIncidents, StatDetections;

	public AimlockDetector(
		AimlockConfig config,
		Func<int, PositionFrame?> findFrame,
		Func<int, IPlayer?> getPlayer,
		Func<int, int, bool> areEnemies,
		Action<IPlayer, string, string> announce,
		Action<string> debug)
	{
		_config = config;
		_findFrame = findFrame;
		_getPlayer = getPlayer;
		_areEnemies = areEnemies;
		_announce = announce;
		_debug = debug;

		for (int i = 0; i < _players.Length; i++)
			_players[i] = new AimlockPlayerData();
	}

	public string Stats() =>
		$"evals={StatEvaluations} tracks={StatTracksStarted} incidents={StatIncidents} detections={StatDetections}";

	public void Reset()
	{
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

		var st = _players[id];

		foreach (var cmd in cmds)
		{
			var pb = cmd.CSGOUserCmd;
			int number = (int)cmd.CommandNumber;

			if (st.Commands.Exists(c => c.CommandNumber == number))
				continue;

			if (!pb.Accessor.HasField("base"))
				continue;

			var b = pb.Base;
			if (!b.Accessor.HasField("viewangles"))
				continue;

			QAngle angles = b.Viewangles;
			int idx = pb.Attack1StartHistoryIndex;
			int histCount = pb.InputHistory.Count;

			if (idx >= 0 && idx < histCount)
			{
				var entry = pb.InputHistory.Get(idx);
				if (entry.Accessor.HasField("view_angles"))
				{
					QAngle shotAngles = entry.ViewAngles;
					if (AimMath.IsFinite(shotAngles))
						angles = shotAngles;
				}
			}

			if (!AimMath.IsFinite(angles))
				continue;

			st.Commands.Add(new AimlockCommand
			{
				CommandNumber = number,
				Angles = angles
			});

			if (st.Commands.Count > 128)
				st.Commands.RemoveAt(0);
		}
	}

	private static bool MeetsCoverage(int onTarget, int samples) =>
		samples > 0 && onTarget * 20 >= samples * 19; // 95% on-target coverage

	private static QAngle Bearing(in Vector eye, in Vector target)
	{
		Vector delta = new(target.X - eye.X, target.Y - eye.Y, target.Z - eye.Z);
		float horizontal = MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
		const float rad2Deg = 180.0f / MathF.PI;
		return new QAngle(
			-MathF.Atan2(delta.Z, horizontal) * rad2Deg,
			MathF.Atan2(delta.Y, delta.X) * rad2Deg,
			0.0f);
	}

	private static int EstimateVisualLag(IPlayer player)
	{
		uint pingMs = player.Controller?.Ping ?? 0;
		float rttSeconds = Math.Clamp(pingMs / 1000.0f, 0.0f, 2.0f);
		float interpTicks = 1.0f;
		return Math.Max(0, (int)MathF.Round(rttSeconds * 64.0f + interpTicks));
	}

	private bool MeasureTarget(
		AimlockSample sample,
		in TrackedPosition target,
		int bodyPoint,
		in Vector aimForward,
		out QAngle bearing,
		out float error,
		out float maximumError,
		out float requiredTargetDisplacement)
	{
		bearing = default;
		error = 180.0f;
		maximumError = 0.0f;
		requiredTargetDisplacement = 0.0f;

		if (bodyPoint < 0 || bodyPoint >= BodyHeights.Length)
			return false;

		Vector targetPoint = new(target.Origin.X, target.Origin.Y, target.Origin.Z + BodyHeights[bodyPoint]);
		Vector direction = new(targetPoint.X - sample.EyePosition.X, targetPoint.Y - sample.EyePosition.Y, targetPoint.Z - sample.EyePosition.Z);
		float targetDistance = direction.Length();

		if (!AimMath.IsFinite(targetPoint) || !AimMath.IsFinite(direction) || float.IsNaN(targetDistance) || targetDistance < AimMath.Epsilon)
			return false;

		Vector dirNorm = new(direction.X / targetDistance, direction.Y / targetDistance, direction.Z / targetDistance);
		float dot = Math.Clamp(AimMath.Dot(aimForward, dirNorm), -1.0f, 1.0f);
		const float rad2Deg = 180.0f / MathF.PI;

		error = MathF.Acos(dot) * rad2Deg;
		maximumError = MathF.Atan2(PlayerHalfWidth, targetDistance) * rad2Deg;
		requiredTargetDisplacement = MathF.Atan2(MinimumTargetTravel, targetDistance) * rad2Deg;
		bearing = Bearing(sample.EyePosition, targetPoint);

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
		out float error,
		out float maximumError,
		out float requiredTargetDisplacement)
	{
		bearing = default;
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

		return MeasureTarget(sample, historicalTarget, bodyPoint, aimForward, out bearing, out error, out maximumError, out requiredTargetDisplacement);
	}

	private readonly record struct Candidate(QAngle Bearing, float Error, int TargetIndex, int BodyPoint, int LagTicks, bool Valid);

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

		for (int lagTicks = firstLag; lagTicks <= lastLag; lagTicks++)
		{
			var historicalFrame = _findFrame(sample.ServerTick - lagTicks);
			if (historicalFrame == null)
				continue;

			for (int targetIndex = 0; targetIndex < AimbotDetector.MaxPlayers; targetIndex++)
			{
				if (targetIndex == observerIndex)
					continue;

				var currentTarget = currentFrame.Players[targetIndex];
				var historicalTarget = historicalFrame.Players[targetIndex];

				if (!currentTarget.Valid || !currentTarget.Alive || currentTarget.Teleported || !_areEnemies(observerIndex, targetIndex))
					continue;

				if (!historicalTarget.Valid || !historicalTarget.Alive || historicalTarget.Teleported)
					continue;

				if (historicalTarget.Origin.Distance(sample.EyePosition) < MinimumDistance)
					continue;

				for (int bodyPoint = 0; bodyPoint < BodyHeights.Length; bodyPoint++)
				{
					if (!MeasureTarget(sample, historicalTarget, bodyPoint, aimForward, out var bearing, out var error, out var maximumError, out _) || error > maximumError)
						continue;

					if (matchedTarget < 0)
						matchedTarget = targetIndex;
					else if (matchedTarget != targetIndex)
						ambiguous = true;

					if (error < (best.Valid ? best.Error : 180.0f))
						best = new Candidate(bearing, error, targetIndex, bodyPoint, lagTicks, true);
				}
			}
		}

		if (ambiguous)
			return default;

		return best;
	}

	public void OnSetupMove(IPlayer player, int commandNumber, int currentTick)
	{
		if (!_config.Enabled || !AimbotDetector.IsEligibleHuman(player) || !player.IsAlive)
			return;

		int id = player.PlayerID;
		if (id < 0 || id >= _players.Length)
			return;

		var st = _players[id];
		if (currentTick == st.LastProcessedTick)
			return;

		var pawn = player.PlayerPawn;
		if (pawn is null)
		{
			st.Track.Clear();
			st.Pending = default;
			return;
		}

		Vector? maybeOrigin = pawn.AbsOrigin;
		if (!maybeOrigin.HasValue || !AimMath.IsFinite(maybeOrigin.Value))
		{
			st.Track.Clear();
			st.Pending = default;
			return;
		}

		Vector origin = maybeOrigin.Value;
		Vector eye = pawn.EyePosition ?? new Vector(origin.X, origin.Y, origin.Z + 64.0f);
		var cmd = st.Commands.FindLast(c => c.CommandNumber == commandNumber);
		QAngle angles = cmd != null ? cmd.Angles : pawn.EyeAngles;

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

		for (int index = 0; index < AimbotDetector.MaxPlayers; index++)
		{
			var player = _getPlayer(index);
			if (player == null || !AimbotDetector.IsEligibleHuman(player) || !player.IsAlive)
			{
				_players[index].Reset();
				continue;
			}

			var st = _players[index];
			if (!st.Pending.Valid)
				continue;

			var sample = st.Pending;
			st.Pending = default;

			if (sample.ServerTick > currentTick || currentTick - sample.ServerTick > 2 || sample.ServerTick <= st.LastProcessedTick || _findFrame(sample.ServerTick) == null)
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

	private static AimlockLagHypothesis? BestHypothesis(AimlockTrack track, bool requireEvidence)
	{
		AimlockLagHypothesis? best = null;
		for (int index = 0; index < track.HypothesisCount; index++)
		{
			var hypothesis = track.Hypotheses[index];
			if (!hypothesis.Valid || (requireEvidence && (!MeetsCoverage(hypothesis.OnTargetSamples, track.Samples) || hypothesis.MaximumTargetDisplacement < hypothesis.RequiredTargetDisplacement)))
				continue;

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
					out _, out var error, out var maximumError, out _) && error <= maximumError)
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

			data.Track.Clear();
			data.Track.TargetIndex = candidate.TargetIndex;
			data.Track.BodyPoint = candidate.BodyPoint;
			data.Track.StartServerTick = sample.ServerTick;
			data.Track.LastServerTick = sample.ServerTick;
			data.Track.Samples = 1;

			int firstLag = Math.Max(0, estTicks - LagSearchRadius);
			int lastLag = estTicks + LagSearchRadius;

			for (int lagTicks = firstLag; lagTicks <= lastLag && data.Track.HypothesisCount < data.Track.Hypotheses.Length; lagTicks++)
			{
				var historicalFrame = _findFrame(sample.ServerTick - lagTicks);
				if (historicalFrame == null)
					continue;

				if (!EvaluateTarget(sample, currentFrame, historicalFrame, player.PlayerID, candidate.TargetIndex, candidate.BodyPoint, aimForward,
					out var bearing, out var error, out var maximumError, out var reqDisplacement))
					continue;

				data.Track.Hypotheses[data.Track.HypothesisCount++] = new AimlockLagHypothesis
				{
					StartBearing = bearing,
					MaximumTargetDisplacement = 0.0f,
					RequiredTargetDisplacement = reqDisplacement,
					LagTicks = lagTicks,
					OnTargetSamples = error <= maximumError ? 1 : 0,
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
		if (serverTickDelta < 1 || serverTickDelta > 3)
		{
			if (data.Track.Samples > 16)
				_debug($"[Aimlock] {Name(player)} tracking reset: simulated ticks not consecutive (delta={serverTickDelta}).");
			data.Track.Clear();
			StartTrack();
			return;
		}

		data.Track.Samples += serverTickDelta;
		int validHypotheses = 0;

		for (int index = 0; index < data.Track.HypothesisCount; index++)
		{
			ref var hypothesis = ref data.Track.Hypotheses[index];
			if (!hypothesis.Valid)
				continue;

			var historicalFrame = _findFrame(sample.ServerTick - hypothesis.LagTicks);
			if (historicalFrame == null || !EvaluateTarget(sample, currentFrame, historicalFrame, player.PlayerID, data.Track.TargetIndex, data.Track.BodyPoint, aimForward,
				out var bearing, out var error, out var maximumError, out _))
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
				hypothesis.OnTargetSamples += serverTickDelta;

			hypothesis.MaximumTargetDisplacement = MathF.Max(hypothesis.MaximumTargetDisplacement, displacement);
		}

		if (validHypotheses == 0)
		{
			_debug($"[Aimlock] {Name(player)} tracking reset: fixed target history no longer trustworthy.");
			data.Track.Clear();
			StartTrack();
			return;
		}

		data.Track.LastServerTick = sample.ServerTick;

		int elapsedTicks = sample.ServerTick - data.Track.StartServerTick;
		if (elapsedTicks > 0 && elapsedTicks < TrackingTicks && elapsedTicks % RearmTicks == 0)
		{
			var best = BestHypothesis(data.Track, requireEvidence: false);
			if (best != null)
			{
				_debug($"[Aimlock] {Name(player)} episode: {elapsedTicks / 64.0f:F1}/2.0s, best delay {best.Value.LagTicks}t kept {best.Value.OnTargetSamples}/{data.Track.Samples} on target; target moved {best.Value.MaximumTargetDisplacement:F1}°/{best.Value.RequiredTargetDisplacement:F1}° required.");
			}
		}

		if (elapsedTicks < TrackingTicks)
			return;

		var passing = BestHypothesis(data.Track, requireEvidence: true);
		if (passing == null)
		{
			var best = BestHypothesis(data.Track, requireEvidence: false);
			if (best != null)
			{
				_debug($"[Aimlock] {Name(player)} episode ended without evidence: best delay {best.Value.LagTicks}t kept {best.Value.OnTargetSamples}/{data.Track.Samples} on target; target moved {best.Value.MaximumTargetDisplacement:F1}°/{best.Value.RequiredTargetDisplacement:F1}° required.");
			}
			data.Track.Clear();
			return;
		}

		AddIncident(player, data, passing.Value);
	}

	private void AddIncident(IPlayer player, AimlockPlayerData data, in AimlockLagHypothesis hypothesis)
	{
		StatIncidents++;
		var now = DateTime.UtcNow;
		data.Incidents.RemoveAll(i => now - i.Time >= EvidenceWindow);

		data.Incidents.Add(new AimlockIncident
		{
			Time = now,
			OnTargetSamples = hypothesis.OnTargetSamples,
			TotalSamples = data.Track.Samples,
			Displacement = hypothesis.MaximumTargetDisplacement,
			RequiredDisplacement = hypothesis.RequiredTargetDisplacement,
			TargetId = data.Track.TargetIndex
		});

		_debug($"[Aimlock] {Name(player)} recorded aimlock episode: {hypothesis.OnTargetSamples}/{data.Track.Samples} on-target ticks, {hypothesis.MaximumTargetDisplacement:F1}°/{hypothesis.RequiredTargetDisplacement:F1}° displacement. Evidence: {data.Incidents.Count}/{_config.EvidenceThreshold}.");

		if (data.Incidents.Count >= _config.EvidenceThreshold)
		{
			StatDetections++;
			var lines = new List<string>();
			for (int i = 0; i < data.Incidents.Count; i++)
			{
				var inc = data.Incidents[i];
				lines.Add($"{i + 1}. Locked {inc.OnTargetSamples}/{inc.TotalSamples} ticks ({inc.Displacement:F1}° target travel)");
			}

			string latest = $"Latest aimlock: pinned inside enemy hitbox hull for {hypothesis.OnTargetSamples}/{data.Track.Samples} ticks while target traversed {hypothesis.MaximumTargetDisplacement:F1}° across screen (required: {hypothesis.RequiredTargetDisplacement:F1}°). Evidence {data.Incidents.Count}/{_config.EvidenceThreshold} in 5 min.";

			string evidence = string.Join("\n", lines) + "\n" + latest;

			_announce(player, "AIMLOCK", evidence);
			data.Incidents.Clear();
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
