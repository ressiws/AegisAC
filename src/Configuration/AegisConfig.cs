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

	[Description("Minimum single-tick snap angle in degrees required to trigger convergence aimbot detection (calibrated P99+ margin).")]
	public float SnapAngleThreshold { get; set; } = 26.0f;

	[Description("Minimum total angular movement in degrees required for smooth aimbot trajectory detection.")]
	public float SmoothMinimumMovement { get; set; } = 12.0f;

	[Description("Minimum path efficiency ratio (0.0 to 1.0) required for smooth aimbot trajectory detection.")]
	public float SmoothMinimumPathEfficiency { get; set; } = 0.96f;
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

	[Description("Number of unnatural triggerbot reaction incidents within 5 minutes required to punish (calibrated default: 4).")]
	public int EvidenceThreshold { get; set; } = 4;

	[Description("Maximum reaction time in milliseconds considered an unnatural triggerbot reaction (calibrated default: 90ms).")]
	public float MaxReactionTimeMs { get; set; } = 90.0f;

	[Description("Verify direct world line-of-sight to crosshair targets using native physics raycasts. When disabled or if line-of-sight cannot be confirmed, targets behind potential geometry are safely ignored to eliminate wallbang false positives.")]
	public bool EnableNativeTrace { get; set; } = true;
}

/// <summary>
/// Configuration for the Aimlock detection module.
/// </summary>
public sealed class AimlockConfig
{
	[Description("Enable or disable the aimlock detection module.")]
	public bool Enabled { get; set; } = true;

	[Description("Punishment applied when aimlock is detected: 'none', 'kick', or 'ban'.")]
	[JsonConverter(typeof(JsonStringEnumConverter))]
	public PunishmentType Punishment { get; set; } = PunishmentType.None;

	[Description("Number of sustained aimlock tracking incidents within 5 minutes required to punish (default: 3).")]
	public int EvidenceThreshold { get; set; } = 3;

	[Description("Duration in seconds of uninterrupted mechanical tracking required for an aimlock episode (calibrated default: 1.95s).")]
	public float TrackingDurationSeconds { get; set; } = 1.95f;

	[Description("Minimum distance in units between player and target to evaluate tracking (default: 200.0 units from legacy).")]
	public float MinimumDistance { get; set; } = 200.0f;

	[Description("Minimum traversal in units the target must move across view to eliminate stationary false positives (default: 128.0 units from legacy).")]
	public float MinimumTargetTravel { get; set; } = 128.0f;
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
	[Description("Unique identifier for this server instance in Discord alerts and logs.")]
	public string ServerId { get; set; } = "cs2-server-1";

	[Description("Global debug mode for Aegis Anti-Cheat. Enables diagnostic console messages and in-game debug announcements.")]
	public bool Debug { get; set; } = false;

	[Description("Server console command template executed for kicking. Available placeholders: {userid}, {steamid}, {name}, {reason}.")]
	public string KickCommand { get; set; } = "sw_kick #{userid} \"[AegisAC] {reason}\"";

	[Description("Server console command template executed for banning. Available placeholders: {userid}, {steamid}, {name}, {reason}.")]
	public string BanCommand { get; set; } = "banid 0 {steamid}; writeid; kickid {userid} \"[AegisAC] {reason}\"";

	[Description("Aimbot detector module configuration.")]
	public AimbotConfig Aimbot { get; set; } = new();

	[Description("Triggerbot detector module configuration.")]
	public TriggerbotConfig Triggerbot { get; set; } = new();

	[Description("Aimlock detector module configuration.")]
	public AimlockConfig Aimlock { get; set; } = new();

	[Description("Discord webhook notifications configuration.")]
	public DiscordConfig Discord { get; set; } = new();
}
