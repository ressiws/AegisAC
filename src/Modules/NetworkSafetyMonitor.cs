/**
 * =============================================================================
 * Aegis Anti-Cheat (AegisAC) - Module: NetworkSafetyMonitor
 *
 * Copyright (C) 2026 swisser & sevon
 * Authors:
 *   - swisser (https://github.com/ressiws)
 *   - sevon   (https://github.com/victor201202)
 *
 * Description:
 *   Shared per-player network quality monitor (adapted from CS2AC Legacy's
 *   NetworkSafetyMonitor). Tracks ping, ping jitter, incoming/outgoing packet
 *   loss, incoming/outgoing choke, and usercmd sequence discontinuities over a
 *   5-second sliding window to defer or veto network-sensitive detections.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at:
 *     http://www.apache.org/licenses/LICENSE-2.0
 * =============================================================================
 */

using System;
using System.Collections.Generic;
using SwiftlyS2.Shared.Engine;
using SwiftlyS2.Shared.Players;

namespace AegisAC.Modules;

internal readonly record struct NetworkSafetySample(
	DateTime Time,
	float PingMilliseconds,
	float IncomingLoss,
	float OutgoingLoss,
	float IncomingChoke,
	float OutgoingChoke,
	bool Valid);

internal readonly record struct NetworkSafetyEvidence(
	float PingMilliseconds,
	float JitterMilliseconds,
	float IncomingLoss,
	float OutgoingLoss,
	float IncomingChoke,
	float OutgoingChoke,
	int CommandGaps,
	int CommandBursts,
	int UnavailableSamples,
	bool Vetoed)
{
	public string FormatSummary() =>
		$"ping={PingMilliseconds:F1}ms jitter={JitterMilliseconds:F1}ms loss={IncomingLoss * 100.0f:F1}/{OutgoingLoss * 100.0f:F1}% " +
		$"choke={IncomingChoke * 100.0f:F1}/{OutgoingChoke * 100.0f:F1}% gaps={CommandGaps} bursts={CommandBursts}";
}

internal sealed class NetworkSafetyMonitor
{
	public const float MaximumPingMilliseconds = 150.0f;
	public const float MaximumJitterMilliseconds = 25.0f;
	public const float MaximumLoss = 0.02f;
	public const float MaximumChoke = 0.02f;
	public const int MaximumBurstCommandsPerTick = 5;
	private static readonly TimeSpan NetworkWindow = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(100);

	private sealed class PlayerNetworkState
	{
		public readonly List<NetworkSafetySample> Samples = new(64);
		public readonly List<DateTime> CommandGaps = new(64);
		public readonly List<DateTime> CommandBursts = new(64);
		public DateTime NextSampleTime = DateTime.MinValue;
		public int LastCommandNumber = -1;
		public int LastClientTick = -1;

		public void Reset()
		{
			Samples.Clear();
			CommandGaps.Clear();
			CommandBursts.Clear();
			NextSampleTime = DateTime.MinValue;
			LastCommandNumber = -1;
			LastClientTick = -1;
		}
	}

	private readonly PlayerNetworkState[] _players = new PlayerNetworkState[AimbotDetector.MaxPlayers + 1];

	public NetworkSafetyMonitor()
	{
		for (int i = 0; i < _players.Length; i++)
			_players[i] = new PlayerNetworkState();
	}

	public static bool ShouldVeto(
		float pingMilliseconds,
		float jitterMilliseconds,
		float incomingLoss,
		float outgoingLoss,
		float incomingChoke,
		float outgoingChoke,
		int commandBursts,
		int unavailableSamples)
	{
		return pingMilliseconds >= MaximumPingMilliseconds
			|| jitterMilliseconds >= MaximumJitterMilliseconds
			|| incomingLoss >= MaximumLoss
			|| outgoingLoss >= MaximumLoss
			|| incomingChoke >= MaximumChoke
			|| outgoingChoke >= MaximumChoke;
	}

	public void Reset()
	{
		for (int i = 0; i < _players.Length; i++)
			_players[i].Reset();
	}

	public void ResetAll() => Reset();

	public void OnClientDisconnect(int playerId)
	{
		if (playerId >= 0 && playerId < _players.Length)
			_players[playerId].Reset();
	}

	public void ResetPlayer(int playerId) => OnClientDisconnect(playerId);

	public bool ShouldSkipDetection(int playerId, out NetworkSafetyEvidence evidence, DateTime? nowOverride = null)
	{
		evidence = Evaluate(playerId, nowOverride);
		return evidence.Vetoed;
	}

	public float GetCurrentPingMs(int playerId)
	{
		if (playerId < 0 || playerId >= _players.Length)
			return 0f;

		var samples = _players[playerId].Samples;
		for (int i = samples.Count - 1; i >= 0; i--)
		{
			if (samples[i].Valid)
				return samples[i].PingMilliseconds;
		}
		return 0f;
	}

	private static void Prune(PlayerNetworkState data, DateTime now)
	{
		DateTime cutoff = now - NetworkWindow;
		data.Samples.RemoveAll(s => s.Time < cutoff);
		data.CommandGaps.RemoveAll(t => t < cutoff);
		data.CommandBursts.RemoveAll(t => t < cutoff);
	}

	public void SamplePlayer(IPlayer player, DateTime? nowOverride = null)
	{
		if (!AimbotDetector.IsEligibleHuman(player))
			return;

		int id = player.PlayerID;
		if (id < 0 || id >= _players.Length)
			return;

		DateTime now = nowOverride ?? DateTime.UtcNow;
		var data = _players[id];
		if (data.NextSampleTime != DateTime.MinValue && now < data.NextSampleTime)
			return;

		data.NextSampleTime = now + SampleInterval;
		var sample = CaptureSample(player, now);
		RecordSample(id, sample);
	}

	public void RecordSample(int playerId, NetworkSafetySample sample)
	{
		if (playerId < 0 || playerId >= _players.Length)
			return;

		var data = _players[playerId];
		data.Samples.Add(sample);
		while (data.Samples.Count > 64)
			data.Samples.RemoveAt(0);

		Prune(data, sample.Time);
	}

	public void OnProcessUsercmds(int playerId, int count, int firstCmd, int lastCmd, DateTime? nowOverride = null)
	{
		if (playerId < 0 || playerId >= _players.Length || count <= 0)
			return;

		DateTime now = nowOverride ?? DateTime.UtcNow;
		var data = _players[playerId];
		Prune(data, now);

		if (count > MaximumBurstCommandsPerTick)
		{
			data.CommandBursts.Add(now);
			if (data.CommandBursts.Count > 128)
				data.CommandBursts.RemoveAt(0);
		}

		if (data.LastCommandNumber >= 0 && firstCmd > data.LastCommandNumber + 1)
		{
			data.CommandGaps.Add(now);
			if (data.CommandGaps.Count > 256)
				data.CommandGaps.RemoveAt(0);
		}

		if (lastCmd > data.LastCommandNumber)
			data.LastCommandNumber = lastCmd;
	}

	public void OnProcessUsercmds(int playerId, ReadOnlySpan<(int CommandNumber, int ClientTick)> commands, DateTime? nowOverride = null)
	{
		if (playerId < 0 || playerId >= _players.Length || commands.Length == 0)
			return;

		DateTime now = nowOverride ?? DateTime.UtcNow;
		var data = _players[playerId];
		Prune(data, now);

		if (commands.Length > MaximumBurstCommandsPerTick)
		{
			data.CommandBursts.Add(now);
			if (data.CommandBursts.Count > 128)
				data.CommandBursts.RemoveAt(0);
		}

		bool discontinuity = false;
		for (int i = 0; i < commands.Length; i++)
		{
			var (cmdNum, clientTick) = commands[i];
			if (cmdNum <= data.LastCommandNumber)
			{
				discontinuity = discontinuity || cmdNum < data.LastCommandNumber;
				continue;
			}

			discontinuity = discontinuity
				|| (data.LastCommandNumber >= 0 && cmdNum > data.LastCommandNumber + 1)
				|| (clientTick >= 0 && data.LastClientTick >= 0 && (clientTick > data.LastClientTick + 1 || clientTick < data.LastClientTick));

			data.LastCommandNumber = cmdNum;
			if (clientTick >= 0)
				data.LastClientTick = clientTick;
		}

		if (discontinuity)
		{
			data.CommandGaps.Add(now);
			if (data.CommandGaps.Count > 256)
				data.CommandGaps.RemoveAt(0);
		}
	}

	public NetworkSafetyEvidence Evaluate(int playerId, DateTime? nowOverride = null)
	{
		if (playerId < 0 || playerId >= _players.Length)
			return new NetworkSafetyEvidence(0, 0, 0, 0, 0, 0, 0, 0, 1, true);

		DateTime now = nowOverride ?? DateTime.UtcNow;
		var data = _players[playerId];
		Prune(data, now);

		int commandGaps = data.CommandGaps.Count;
		int commandBursts = data.CommandBursts.Count;

		if (data.Samples.Count == 0)
		{
			// No samples recorded yet: veto only if severe command bursts are present
			bool burstVeto = commandBursts >= 2;
			return new NetworkSafetyEvidence(0, 0, 0, 0, 0, 0, commandGaps, commandBursts, 0, burstVeto);
		}

		float maxPing = 0.0f;
		float maxInLoss = 0.0f;
		float maxOutLoss = 0.0f;
		float maxInChoke = 0.0f;
		float maxOutChoke = 0.0f;
		float prevPing = 0.0f;
		float totalVariation = 0.0f;
		int validSamples = 0;
		int variationSamples = 0;
		int unavailableSamples = 0;

		for (int i = 0; i < data.Samples.Count; i++)
		{
			var s = data.Samples[i];
			if (!s.Valid)
			{
				unavailableSamples++;
				continue;
			}

			maxPing = MathF.Max(maxPing, s.PingMilliseconds);
			maxInLoss = MathF.Max(maxInLoss, s.IncomingLoss);
			maxOutLoss = MathF.Max(maxOutLoss, s.OutgoingLoss);
			maxInChoke = MathF.Max(maxInChoke, s.IncomingChoke);
			maxOutChoke = MathF.Max(maxOutChoke, s.OutgoingChoke);

			if (validSamples > 0)
			{
				totalVariation += MathF.Abs(s.PingMilliseconds - prevPing);
				variationSamples++;
			}

			prevPing = s.PingMilliseconds;
			validSamples++;
		}

		float jitter = variationSamples > 0 ? totalVariation / variationSamples : 0.0f;
		bool vetoed = ShouldVeto(maxPing, jitter, maxInLoss, maxOutLoss, maxInChoke, maxOutChoke, commandBursts, unavailableSamples);

		return new NetworkSafetyEvidence(
			maxPing,
			jitter,
			maxInLoss,
			maxOutLoss,
			maxInChoke,
			maxOutChoke,
			commandGaps,
			commandBursts,
			unavailableSamples,
			vetoed);
	}

	private static NetworkSafetySample CaptureSample(IPlayer player, DateTime now)
	{
		try
		{
			var netChannel = player.ServerSideClient?.NetChannel;
			if (netChannel != null)
			{
				if (netChannel.IsTimingOut)
					return new NetworkSafetySample(now, 999.0f, 1.0f, 1.0f, 1.0f, 1.0f, false);

				float pingMs = netChannel.EngineLatency * 1000.0f;
				if (!float.IsFinite(pingMs) || pingMs <= 0.0f)
					pingMs = player.Controller?.Ping ?? 0u;

				float inLoss = netChannel.GetAvgLoss(NetworkFlow.Incoming);
				float outLoss = netChannel.GetAvgLoss(NetworkFlow.Outgoing);
				float inChoke = netChannel.GetAvgChoke(NetworkFlow.Incoming);
				float outChoke = netChannel.GetAvgChoke(NetworkFlow.Outgoing);

				bool valid = float.IsFinite(pingMs) && pingMs >= 0.0f
					&& float.IsFinite(inLoss) && inLoss is >= 0.0f and <= 1.0f
					&& float.IsFinite(outLoss) && outLoss is >= 0.0f and <= 1.0f
					&& float.IsFinite(inChoke) && inChoke is >= 0.0f and <= 1.0f
					&& float.IsFinite(outChoke) && outChoke is >= 0.0f and <= 1.0f;

				return new NetworkSafetySample(now, pingMs, inLoss, outLoss, inChoke, outChoke, valid);
			}
		}
		catch
		{
			// Fallback to Controller.Ping if NetChannel vtable call is unavailable
		}

		float fallbackPing = player.Controller?.Ping ?? 0u;
		return new NetworkSafetySample(now, fallbackPing, 0.0f, 0.0f, 0.0f, 0.0f, float.IsFinite(fallbackPing) && fallbackPing >= 0.0f);
	}
}
