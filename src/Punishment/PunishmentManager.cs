using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;
using AegisAC.Configuration;

namespace AegisAC.Punishment;

/// <summary>
/// Manages player punishments (kick, ban, none) triggered by detection modules.
/// </summary>
public sealed class PunishmentManager
{
	private readonly ISwiftlyCore _core;
	private AegisConfig _config;

	public PunishmentManager(ISwiftlyCore core, AegisConfig config)
	{
		_core = core;
		_config = config;
	}

	public void UpdateConfig(AegisConfig config) => _config = config;

	/// <summary>
	/// Applies punishment to a detected player based on the configured punishment type.
	/// Guarantees execution on the server game thread.
	/// </summary>
	public void Punish(IPlayer player, PunishmentType punishment, string moduleName, string detectionReason)
	{
		if (punishment == PunishmentType.None || !player.IsValid)
			return;

		if (!_core.IsGameThread)
		{
			_core.Scheduler.NextTick(() => Punish(player, punishment, moduleName, detectionReason));
			return;
		}

		string name = player.Controller?.PlayerName ?? $"#{player.PlayerID}";
		string reason = $"{moduleName}: {detectionReason}";

		switch (punishment)
		{
			case PunishmentType.Kick:
				ExecuteKick(player, name, reason);
				break;

			case PunishmentType.Ban:
				ExecuteBan(player, name, reason);
				break;
		}
	}

	private void ExecuteKick(IPlayer player, string name, string reason)
	{
		if (!string.IsNullOrWhiteSpace(_config.KickCommand))
		{
			string cmd = FormatCommand(_config.KickCommand, player, name, reason);
			_core.Engine.ExecuteCommand(cmd);
		}
		else
		{
			player.Kick($"[AegisAC] {reason}", default);
		}

		Utils.Log($" \x04[AegisAC]\x01 Punished \x07{name}\x01 (Action: \x0FKICK\x01, Reason: {reason})");
	}

	private void ExecuteBan(IPlayer player, string name, string reason)
	{
		if (!string.IsNullOrWhiteSpace(_config.BanCommand))
		{
			string cmd = FormatCommand(_config.BanCommand, player, name, reason);
			_core.Engine.ExecuteCommand(cmd);
		}
		else
		{
			_core.Engine.ExecuteCommand($"sw_ban #{player.UserID} 0 \"[AegisAC] {Sanitize(reason)}\"");
			player.Kick($"[AegisAC] {reason}", default);
		}

		Utils.Log($" \x04[AegisAC]\x01 Punished \x07{name}\x01 (Action: \x0FBAN\x01, Reason: {reason})");
	}

	private static string Sanitize(string input)
	{
		if (string.IsNullOrEmpty(input))
			return "";
		return input
			.Replace("\"", "")
			.Replace(";", "")
			.Replace("'", "")
			.Replace("\n", " ")
			.Replace("\r", " ")
			.Trim();
	}

	private static string FormatCommand(string template, IPlayer player, string name, string reason)
	{
		string sanitizedName = Sanitize(name);
		string sanitizedReason = Sanitize(reason);
		int userId = player.UserID;
		int slot = player.PlayerID;
		ulong steamId = player.SteamID;

		return template
			.Replace("{userid}", userId.ToString())
			.Replace("{slot}", slot.ToString())
			.Replace("{steamid}", steamId.ToString())
			.Replace("{name}", sanitizedName)
			.Replace("{reason}", sanitizedReason);
	}
}
