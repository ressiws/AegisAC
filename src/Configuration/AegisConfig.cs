using System.ComponentModel;
using System.Text.Json.Serialization;

namespace AegisAC.Configuration;

/// <summary>
/// Supported punishment actions when a detection module flags a player.
/// </summary>
public enum PunishmentType
{
	None,
	Kick,
	Ban
}

/// <summary>
/// Configuration for the Aimbot detection module.
/// </summary>
public sealed class AimbotConfig
{
	[Description("Enable or disable the aimbot detection module.")]
	public bool Enabled { get; set; } = true;

	[Description("Punishment applied when aimbot is detected: 'none', 'kick', or 'ban'.")]
	[JsonConverter(typeof(JsonStringEnumConverter))]
	public PunishmentType Punishment { get; set; } = PunishmentType.None;

	[Description("Treat teammates as enemies (useful for deathmatch or FFA servers).")]
	public bool TeammatesAreEnemies { get; set; } = false;
}

/// <summary>
/// Configuration for the Triggerbot detection module.
/// </summary>
public sealed class TriggerbotConfig
{
	[Description("Enable or disable the triggerbot detection module.")]
	public bool Enabled { get; set; } = true;

	[Description("Punishment applied when triggerbot is detected: 'none', 'kick', or 'ban'.")]
	[JsonConverter(typeof(JsonStringEnumConverter))]
	public PunishmentType Punishment { get; set; } = PunishmentType.None;

	[Description("Reaction score threshold required to flag triggerbot (default: 10).")]
	public int DetectionThreshold { get; set; } = 10;
}

/// <summary>
/// Configuration for Discord webhook integration.
/// </summary>
public sealed class DiscordConfig
{
	[Description("Enable or disable Discord webhook alert notifications.")]
	public bool Enabled { get; set; } = false;

	[Description("Discord webhook URL where detection embeds are posted.")]
	public string WebhookUrl { get; set; } = "";
}

/// <summary>
/// Root configuration for Aegis Anti-Cheat.
/// </summary>
public sealed class AegisConfig
{
	[Description("Global debug mode for Aegis Anti-Cheat. Enables diagnostic console messages and in-game debug announcements.")]
	public bool Debug { get; set; } = false;

	[Description("Server console command template executed for kicking. Available placeholders: {userid}, {steamid}, {name}, {reason}.")]
	public string KickCommand { get; set; } = "kickid {userid} \"[AegisAC] {reason}\"";

	[Description("Server console command template executed for banning. Available placeholders: {userid}, {steamid}, {name}, {reason}.")]
	public string BanCommand { get; set; } = "banid 0 {steamid}; writeid; kickid {userid} \"[AegisAC] {reason}\"";

	[Description("Aimbot detector module configuration.")]
	public AimbotConfig Aimbot { get; set; } = new();

	[Description("Triggerbot detector module configuration.")]
	public TriggerbotConfig Triggerbot { get; set; } = new();

	[Description("Discord webhook notifications configuration.")]
	public DiscordConfig Discord { get; set; } = new();
}
