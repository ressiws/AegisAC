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
/// Configuration for the Aimbot, RCS, Doubletap, Inhuman Accuracy, and Irregular Behavior detection module.
/// </summary>
public sealed class AimbotConfig
{
	[Description("Enable or disable the aimbot detection module.")]
	public bool Enabled { get; set; } = true;

	[Description("Punishment applied when aimbot is detected: 'none', 'kick', or 'ban'. Keep 'none' during calibration.")]
	[JsonConverter(typeof(JsonStringEnumConverter))]
	public PunishmentType Punishment { get; set; } = PunishmentType.None;

	[Description("Treat teammates as enemies (useful for deathmatch or FFA servers).")]
	public bool TeammatesAreEnemies { get; set; } = false;

	[Description("Minimum single-tick snap angle in degrees required to trigger convergence aimbot detection (calibrated P99+ margin).")]
	public float SnapAngleThreshold { get; set; } = 26.0f;

	[Description("Minimum total angular movement in degrees required for standard smooth aimbot trajectory detection.")]
	public float SmoothMinimumMovement { get; set; } = 12.0f;

	[Description("Minimum total angular movement in degrees required for low-FOV closet smooth aimbot trajectory detection.")]
	public float LowFovSmoothMinimumMovement { get; set; } = 3.5f;

	[Description("Minimum path efficiency ratio (0.0 to 1.0) required for smooth aimbot trajectory detection.")]
	public float SmoothMinimumPathEfficiency { get; set; } = 0.96f;

	[Description("Enable No-Recoil / RCS macro spray pattern detection (RECOIL_THRESHOLD >= 0.80 & PATTERN_VARIANCE < 8.157%).")]
	public bool EnableRcsCheck { get; set; } = true;

	[Description("Enable sustained inhuman accuracy detection (PREDICTION_THRESHOLD >= 95% hit rate across >= 35 aimed shots at >= 100u).")]
	public bool EnableInhumanAccuracyCheck { get; set; } = true;

	[Description("Enable irregular combat behavior detection (repeated airborne and sniper no-scope kills at >= 10m).")]
	public bool EnableIrregularBehaviorCheck { get; set; } = true;

	[Description("Enable doubletap / rapid-fire exploit detection comparing weapon fire intervals against minimum weapon cycle times.")]
	public bool EnableDoubletap { get; set; } = true;

	[Description("Enable sub-tick silent-aim evaluation within the Aimbot pipeline (also configurable via the dedicated SilentAim section).")]
	public bool EnableSilentAim { get; set; } = true;

	[Description("Enable anti-aim evaluation (also configurable via the dedicated AntiAim section).")]
	public bool EnableAntiAim { get; set; } = true;
}

/// <summary>
/// Configuration for the dedicated Silent Aim &amp; Snap-Return detection module.
/// </summary>
public sealed class SilentAimConfig
{
	[Description("Enable or disable the Silent Aim & Snap-Return detection module.")]
	public bool Enabled { get; set; } = true;

	[Description("Punishment applied when Silent Aim is detected: 'none', 'kick', or 'ban'. Keep 'none' during calibration.")]
	[JsonConverter(typeof(JsonStringEnumConverter))]
	public PunishmentType Punishment { get; set; } = PunishmentType.None;

	[Description("Accumulated weighted point threshold required to trigger a sub-tick Silent Aim detection (default: 10.0).")]
	public float ScoreThreshold { get; set; } = 10.0f;

	[Description("Enable sub-tick Silent Aim detection comparing base viewangles against attack1_start_history_index angles, weapon spread, and sub-tick mouse travel.")]
	public bool EnableSubtickSilentAim { get; set; } = true;

	[Description("Enable post-shot Snap-Return detection catching 1-tick silent snaps that return to the prior view angle on the next command.")]
	public bool EnableSnapReturn { get; set; } = true;

	[Description("Minimum 1-tick snap angle in degrees required for Snap-Return detection (default: 6.0).")]
	public float SnapReturnMinimumAngle { get; set; } = 6.0f;

	[Description("Maximum angular difference in degrees between pre-shot and post-shot camera angles for Snap-Return detection (default: 3.5).")]
	public float SnapReturnMaximumSurroundingAngle { get; set; } = 3.5f;
}

/// <summary>
/// Configuration for the Triggerbot &amp; Synthetic Input detection module.
/// </summary>
public sealed class TriggerbotConfig
{
	[Description("Enable or disable the triggerbot detection module.")]
	public bool Enabled { get; set; } = true;

	[Description("Punishment applied when triggerbot is detected: 'none', 'kick', or 'ban'. Keep 'none' during calibration.")]
	[JsonConverter(typeof(JsonStringEnumConverter))]
	public PunishmentType Punishment { get; set; } = PunishmentType.None;

	[Description("Number of unnatural triggerbot reaction incidents within 5 minutes required to punish (calibrated default: 4, equivalent to score 10).")]
	public int EvidenceThreshold { get; set; } = 4;

	[Description("Maximum reaction time in milliseconds considered an unnatural triggerbot reaction (calibrated default: 90ms).")]
	public float MaxReactionTimeMs { get; set; } = 90.0f;

	[Description("Verify direct world line-of-sight and hitboxes to crosshair targets using native physics raycasts.")]
	public bool EnableNativeTrace { get; set; } = true;

	[Description("Enable isolated sub-2-tick synthetic click-release-click injection detection (SYNTHETIC_INPUT).")]
	public bool EnableSyntheticInputCheck { get; set; } = true;

	[Description("Exclude shots fired through active smoke grenade volumes from triggerbot reaction scoring.")]
	public bool EnableSmokeCheck { get; set; } = true;
}

/// <summary>
/// Configuration for the Aimlock detection module.
/// </summary>
public sealed class AimlockConfig
{
	[Description("Enable or disable the aimlock detection module.")]
	public bool Enabled { get; set; } = true;

	[Description("Punishment applied when aimlock is detected: 'none', 'kick', or 'ban'. Keep 'none' during calibration.")]
	[JsonConverter(typeof(JsonStringEnumConverter))]
	public PunishmentType Punishment { get; set; } = PunishmentType.None;

	[Description("Number of sustained aimlock tracking incidents within 5 minutes required to punish (default: 3).")]
	public int EvidenceThreshold { get; set; } = 3;

	[Description("Duration in seconds of uninterrupted mechanical tracking required for an aimlock episode (default: 1.0s for wall tracking).")]
	public float TrackingDurationSeconds { get; set; } = 1.0f;

	[Description("Minimum distance in units between player and target to evaluate tracking (default: 150.0 units).")]
	public float MinimumDistance { get; set; } = 150.0f;

	[Description("Minimum traversal in units the target must move across view to eliminate stationary false positives (default: 128.0 units).")]
	public float MinimumTargetTravel { get; set; } = 128.0f;

	[Description("Require world geometry occlusion for aimlock tracking episodes so normal visible combat tracking is never false-flagged.")]
	public bool RequireWallOcclusion { get; set; } = true;
}

/// <summary>
/// Configuration for the Anti-Aim and Spinbot detection module.
/// </summary>
public sealed class AntiAimConfig
{
	[Description("Enable or disable the Anti-Aim and Spinbot detection module.")]
	public bool Enabled { get; set; } = true;

	[Description("Punishment applied when Anti-Aim or Spinbot is detected: 'none', 'kick', or 'ban'. Keep 'none' during calibration.")]
	[JsonConverter(typeof(JsonStringEnumConverter))]
	public PunishmentType Punishment { get; set; } = PunishmentType.None;

	[Description("Enable deterministic ANTI_UNTRUSTED viewangle bounds checking (Pitch in [-89.5, +89.5], Roll in [-50, +50], finite NaN/Inf).")]
	public bool EnableUntrustedCheck { get; set; } = true;

	[Description("Enable multi-tier continuous Spinbot (720/1440/2880 deg/s) and alternating Yaw Jitter detection.")]
	public bool EnableSpinbotCheck { get; set; } = true;
}

/// <summary>
/// Configuration for the Movement Automation detection module (Bhop, Nulls / Snap-Tap, AutoStrafe).
/// </summary>
public sealed class MovementConfig
{
	[Description("Enable or disable the Movement automation detection module.")]
	public bool Enabled { get; set; } = true;

	[Description("Punishment applied when Movement automation is detected: 'none', 'kick', or 'ban'. Keep 'none' during calibration.")]
	[JsonConverter(typeof(JsonStringEnumConverter))]
	public PunishmentType Punishment { get; set; } = PunishmentType.None;

	[Description("Enable frame-perfect bunnyhop landing chain detection.")]
	public bool EnableBhopCheck { get; set; } = true;

	[Description("Automatically disable Bhop detection when the server has sv_autobunnyhopping 1 active.")]
	public bool AutoDisableOnAutoBunnyhoppingConvar { get; set; } = true;

	[Description("Consecutive frame-perfect bunnyhop landings required to flag Bhop automation (default: 10).")]
	public int BhopStreakThreshold { get; set; } = 10;

	[Description("Enable airborne Nulls / Snap-Tap (SOCD) opposite-direction switch detection.")]
	public bool EnableNullsCheck { get; set; } = true;

	[Description("Consecutive airborne 0.00ms opposite-direction strafe transitions (A <-> D) required to flag Nulls / Snap-Tap (default: 64).")]
	public int NullsStreakThreshold { get; set; } = 64;

	[Description("Enable airborne AutoStrafe / Strafe Optimizer mechanical symmetry detection.")]
	public bool EnableAutoStrafeCheck { get; set; } = true;
}

/// <summary>
/// Configuration for the Exploits and Input Integrity detection module (Subtick Tamper, Desubtick, Invalid Input, NameChanger).
/// </summary>
public sealed class ExploitsConfig
{
	[Description("Enable or disable the Exploits and Input Integrity detection module.")]
	public bool Enabled { get; set; } = true;

	[Description("Punishment applied when Exploits / Input tampering is detected: 'none', 'kick', or 'ban'. Keep 'none' during calibration.")]
	[JsonConverter(typeof(JsonStringEnumConverter))]
	public PunishmentType Punishment { get; set; } = PunishmentType.None;

	[Description("Enable CSubtickMoveStep extreme fraction and timestamp tampering verification.")]
	public bool EnableSubtickTamperCheck { get; set; } = true;

	[Description("Enable Desubtick detection when >= 90% of subtick moves have fractional timing flattened to 0.0.")]
	public bool EnableDesubtickCheck { get; set; } = true;

	[Description("Enable Invalid Input verification checking movement buttons against subtick records.")]
	public bool EnableInvalidInputCheck { get; set; } = true;

	[Description("Enable NameChanger spam detection (>= 5 visible name changes within 60 seconds).")]
	public bool EnableNameChangerCheck { get; set; } = true;
}

/// <summary>
/// Configuration for the Client ConVar verification / integrity scanner module.
/// </summary>
public sealed class CVarScannerConfig
{
	[Description("Enable or disable the Client ConVar verification scanner module.")]
	public bool Enabled { get; set; } = true;

	[Description("Punishment applied when forbidden client CVars are detected: 'none', 'kick', or 'ban'. Keep 'none' during calibration.")]
	[JsonConverter(typeof(JsonStringEnumConverter))]
	public PunishmentType Punishment { get; set; } = PunishmentType.None;

	[Description("Initial delay in seconds after a player connects before running their first CVar query scan (default: 15.0s).")]
	public float InitialScanDelaySeconds { get; set; } = 15.0f;

	[Description("Interval in seconds between recurring CVar query scans per player (default: 90.0s).")]
	public float ScanIntervalSeconds { get; set; } = 90.0f;

	[Description("Enable verification of r_drawothermodels (flags wireframe wallhack value '2').")]
	public bool CheckDrawOtherModels { get; set; } = true;

	[Description("Enable verification of mat_wireframe (flags values other than '0').")]
	public bool CheckMatWireframe { get; set; } = true;

	[Description("Enable verification of sv_cheats (flags client reporting '1' on secure servers).")]
	public bool CheckSvCheats { get; set; } = true;

	[Description("Enable verification of host_timescale (flags speedhack timescale values != 1.0).")]
	public bool CheckHostTimescale { get; set; } = true;

	[Description("Enable verification of fog_enable (flags disabling fog '0').")]
	public bool CheckFogEnable { get; set; } = true;

	[Description("Enable verification of client prediction and lag compensation settings (cl_lagcompensation, cl_predict).")]
	public bool CheckPredictionAndLagComp { get; set; } = true;
}

/// <summary>
/// Configuration for the Client Event Subscription / DLL Injection detection module.
/// </summary>
public sealed class DllInjectionConfig
{
	[Description("Enable or disable the Client Event Subscription (DLL Injection) scanner module.")]
	public bool Enabled { get; set; } = true;

	[Description("Punishment applied when blacklisted cheat event subscriptions are detected: 'none', 'kick', or 'ban'. Keep 'none' during calibration.")]
	[JsonConverter(typeof(JsonStringEnumConverter))]
	public PunishmentType Punishment { get; set; } = PunishmentType.None;

	[Description("Initial delay in seconds after a player connects before running their first event subscription scan (default: 10.0s).")]
	public float InitialScanDelaySeconds { get; set; } = 10.0f;

	[Description("Interval in seconds between recurring event subscription scans per player (default: 120.0s).")]
	public float ScanIntervalSeconds { get; set; } = 120.0f;
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
/// Configuration for Incident Telemetry JSON dump logging.
/// </summary>
public sealed class TelemetryConfig
{
	[Description("Enable logging structured incident JSON dumps to disk for staff demo review.")]
	public bool DumpIncidents { get; set; } = true;

	[Description("Directory path where incident JSON dumps are written.")]
	public string IncidentsDirectory { get; set; } = "logs/aegisac/incidents";
}

/// <summary>
/// Configuration for the server-side Anti-WallHack (Fog of War / Entity Transmission Occlusion) module.
/// </summary>
public sealed class AntiWallHackConfig
{
	[Description("Enable or disable server-side Anti-WallHack entity transmission occlusion.")]
	public bool Enabled { get; set; } = true;

	[Description("Enable corner-peeking velocity and input lead prediction to prevent pop-in when peeking around corners.")]
	public bool CornerPrediction { get; set; } = true;

	[Description("Prediction horizon in seconds for corner peeking (0.05 to 0.25).")]
	public float CornerPredictionSeconds { get; set; } = 0.12f;

	[Description("Number of ticks an enemy remains visible to a player after breaking line of sight to prevent flickering/pop-in.")]
	public int VisibleGraceTicks { get; set; } = 12;

	[Description("Maximum number of ray traces permitted per server tick to guarantee zero server frame lag.")]
	public int MaxTracesPerTick { get; set; } = 2048;
}

/// <summary>
/// Root configuration for Aegis Anti-Cheat.
/// </summary>
public sealed class AegisConfig
{
	[Description("Unique identifier for this server instance in Discord alerts and logs.")]
	public string ServerId { get; set; } = "cs2-server-1";

	[Description("Global debug mode for Aegis Anti-Cheat. Enables diagnostic console messages and in-game status commands.")]
	public bool Debug { get; set; } = false;

	[Description("Admin ban system preset: 'Default' (sw_ban #{userid}), 'T3Admin' (sw_addban {steamid}), or 'Custom' (uses BanCommand / KickCommand).")]
	public string AdminSystem { get; set; } = "Default";

	[Description("Server console command template executed for kicking. Available placeholders: {userid}, {slot}, {steamid}, {name}, {reason}.")]
	public string KickCommand { get; set; } = "sw_kick #{userid} \"[AegisAC] {reason}\"";

	[Description("Server console command template executed for banning. Available placeholders: {userid}, {slot}, {steamid}, {name}, {reason}.")]
	public string BanCommand { get; set; } = "sw_ban #{userid} 0 \"[AegisAC] {reason}\"; kickid {userid} \"[AegisAC] {reason}\"";

	[Description("Aimbot, RCS, Doubletap, Inhuman Accuracy, and Irregular Behavior detector configuration.")]
	public AimbotConfig Aimbot { get; set; } = new();

	[Description("Silent Aim and Snap-Return detector configuration.")]
	public SilentAimConfig SilentAim { get; set; } = new();

	[Description("Triggerbot and Synthetic Input detector configuration.")]
	public TriggerbotConfig Triggerbot { get; set; } = new();

	[Description("Aimlock wall-tracking detector configuration.")]
	public AimlockConfig Aimlock { get; set; } = new();

	[Description("Anti-Aim and Spinbot detector configuration.")]
	public AntiAimConfig AntiAim { get; set; } = new();

	[Description("Movement Automation (Bhop, Nulls / Snap-Tap, AutoStrafe) detector configuration.")]
	public MovementConfig Movement { get; set; } = new();

	[Description("Exploits and Input Integrity (Subtick Tamper, Desubtick, Invalid Input, NameChanger) detector configuration.")]
	public ExploitsConfig Exploits { get; set; } = new();

	[Description("Client ConVar verification (CVarScanner) detector configuration.")]
	public CVarScannerConfig CVarScanner { get; set; } = new();

	[Description("Client Event Subscription (DLL Injection) detector configuration.")]
	public DllInjectionConfig DllInjection { get; set; } = new();

	[Description("Server-side Anti-WallHack and network entity transmission occlusion configuration.")]
	public AntiWallHackConfig AntiWallHack { get; set; } = new();

	[Description("Incident telemetry and demo proof dump configuration.")]
	public TelemetryConfig Telemetry { get; set; } = new();

	[Description("Discord webhook notifications configuration.")]
	public DiscordConfig Discord { get; set; } = new();
}
