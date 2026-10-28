using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Natives;
using AegisAC.Configuration;
using AegisAC.Modules;
using AegisAC.Notifications;
using AegisAC.Punishment;

namespace AegisAC;

[PluginMetadata(
	Id = "AegisAC",
	Version = "1.0.0",
	Name = "Aegis Anti-Cheat",
	Author = "swisser & sevon",
	Description = "Server-side anti-cheat plugin for CS2.",
	MinimumAPIVersion = "1.1.6")]
public sealed class AegisAC : BasePlugin
{
	private AegisConfig _config = new();
	private PunishmentManager _punishmentManager = null!;
	private AimbotDetector _detector = null!;
	private TriggerbotDetector _triggerbot = null!;
	private AimlockDetector _aimlock = null!;
	private IDisposable? _configChangeToken;

	public AegisAC(ISwiftlyCore core) : base(core) { }

	public override void Load(bool hotReload)
	{
		Utils.Initialize(Core);

		// Initialize plugin configuration using SwiftlyS2 configuration service
		Core.Configuration
			.InitializeJsonWithModel<AegisConfig>("config.jsonc", "AegisAC", addMissingKeys: true)
			.Configure(builder =>
			{
				builder.AddJsonFile(Core.Configuration.GetConfigPath("config.jsonc"), optional: false, reloadOnChange: true);
			});

		LoadConfig();
		_configChangeToken = ChangeToken.OnChange(() => Core.Configuration.Manager.GetReloadToken(), LoadConfig);

		_punishmentManager = new PunishmentManager(Core, _config);

		_detector = new AimbotDetector(
			_config.Aimbot,
			id => Core.PlayerManager.GetPlayer(id),
			AnnounceAimbot,
			msg => { if (Utils.DebugMode) Utils.ConsoleLog(msg); });

		_triggerbot = new TriggerbotDetector(
			_config.Triggerbot,
			Core.Trace,
			id => Core.PlayerManager.GetPlayer(id),
			(a, b) => _detector.AreEnemies(a, b),
			AnnounceTriggerbot,
			msg => { if (Utils.DebugMode) Utils.ConsoleLog(msg); });

		_aimlock = new AimlockDetector(
			_config.Aimlock,
			tick => _detector.FindFrame(tick),
			id => Core.PlayerManager.GetPlayer(id),
			(a, b) => _detector.AreEnemies(a, b),
			AnnounceAimlock,
			msg => { if (Utils.DebugMode) Utils.ConsoleLog(msg); });

		Core.GameHooks.Controller.ProcessUsercmds.Pre += OnProcessUsercmds;
		Core.GameHooks.Movement.SetupMove.Pre += OnSetupMove;
		Core.Event.OnTick += OnTick;
		Core.Event.OnClientDisconnected += OnClientDisconnected;
		Core.Event.OnMapLoad += OnMapLoad;

		Utils.Log("initialized. Commands: aimbot_status triggerbot_status aimlock_status aegis_reload");
	}

	public override void Unload()
	{
		_configChangeToken?.Dispose();
		Core.GameHooks.Controller.ProcessUsercmds.Pre -= OnProcessUsercmds;
		Core.GameHooks.Movement.SetupMove.Pre -= OnSetupMove;
		Core.Event.OnTick -= OnTick;
		Core.Event.OnClientDisconnected -= OnClientDisconnected;
		Core.Event.OnMapLoad -= OnMapLoad;
	}

	private void LoadConfig()
	{
		var section = Core.Configuration.Manager.GetSection("AegisAC");
		var loaded = section.Get<AegisConfig>();
		if (loaded != null)
		{
			_config = loaded;
			Utils.DebugMode = _config.Debug;
			_punishmentManager?.UpdateConfig(_config);
			_detector?.UpdateConfig(_config.Aimbot);
			_triggerbot?.UpdateConfig(_config.Triggerbot);
			_aimlock?.UpdateConfig(_config.Aimlock);
		}
	}

	private int CurrentTick => Core.Engine.GlobalVars.TickCount;

	private void OnProcessUsercmds(ref ProcessUsercmdsPreContext ctx)
	{
		_detector.OnProcessUsercmds(ctx.Params.Player, ctx.Params.Usercmds);
		_triggerbot.OnProcessUsercmds(ctx.Params.Player, ctx.Params.Usercmds);
		_aimlock.OnProcessUsercmds(ctx.Params.Player, ctx.Params.Usercmds);
	}

	private void OnSetupMove(ref SetupMoveMovementPreContext ctx)
	{
		int cmdNum = (int)ctx.Params.UserCmd.CommandNumber;
		_detector.OnSetupMove(ctx.Params.Player, cmdNum, CurrentTick);
		_triggerbot.OnSetupMove(ctx.Params.Player, cmdNum, CurrentTick);
		_aimlock.OnSetupMove(ctx.Params.Player, cmdNum, CurrentTick);
	}

	private void OnTick()
	{
		_detector.OnGameFrame(CurrentTick);
		_aimlock.OnGameFrame(CurrentTick);
	}

	private void OnClientDisconnected(SwiftlyS2.Shared.Events.IOnClientDisconnectedEvent e)
	{
		_detector.OnClientDisconnect(e.PlayerId);
		_triggerbot.OnClientDisconnect(e.PlayerId);
		_aimlock.OnClientDisconnect(e.PlayerId);
	}

	private void OnMapLoad(SwiftlyS2.Shared.Events.IOnMapLoadEvent e)
	{
		_detector.Reset();
		_triggerbot.Reset();
		_aimlock.Reset();
	}

	[GameEventHandler(HookMode.Post)]
	public HookResult OnWeaponFire(EventWeaponFire e)
	{
		var player = e.UserIdPlayer;

		if (player is { IsValid: true })
		{
			_detector.OnWeaponFire(
				player,
				e.Weapon ?? "",
				CurrentTick);
			_triggerbot.OnWeaponFire(
				player,
				e.Weapon ?? "",
				CurrentTick);
		}

		return HookResult.Continue;
	}

	[GameEventHandler(HookMode.Post)]
	public HookResult OnPlayerHurt(EventPlayerHurt e)
	{
		var attacker = e.AttackerPlayer;
		var victim = e.UserIdPlayer;
		if (attacker is { IsValid: true } && victim is { IsValid: true })
		{
			try
			{
				if (Core.ConVar.Find<bool>("mp_teammates_are_enemies")?.Value is bool cvarVal)
					_config.Aimbot.TeammatesAreEnemies = cvarVal;
			}
			catch { }

			int? matchedCmd = _detector.OnPlayerHurt(attacker, victim, e.Weapon ?? "", CurrentTick);
			_triggerbot.OnPlayerHurt(attacker, victim, e.Weapon ?? "", e.ActualDmgHealth, matchedCmd);
		}
		return HookResult.Continue;
	}

	[GameEventHandler(HookMode.Post)]
	public HookResult OnSmokeDetonate(EventSmokegrenadeDetonate e)
	{
		Vector center = new(e.X, e.Y, e.Z);
		_triggerbot.OnSmokeDetonate(center);
		return HookResult.Continue;
	}

	[Command("aimbot_status", registerRaw: true, permission: "@css/root")]
	public void AimbotStatusCommand(ICommandContext context)
	{
		if (!_config.Debug)
		{
			context.Reply("[AegisAC] Debug mode is disabled.");
			return;
		}

		context.Reply($"[AegisAC Aimbot] tick={CurrentTick} {_detector.Stats()}");
	}

	[Command("triggerbot_status", registerRaw: true, permission: "@css/root")]
	public void TriggerbotStatusCommand(ICommandContext context)
	{
		if (!_config.Debug)
		{
			context.Reply("[AegisAC] Debug mode is disabled.");
			return;
		}

		context.Reply($"[AegisAC Triggerbot] tick={CurrentTick} {_triggerbot.Stats()}");
	}

	[Command("aimlock_status", registerRaw: true, permission: "@css/root")]
	public void AimlockStatusCommand(ICommandContext context)
	{
		if (!_config.Debug)
		{
			context.Reply("[AegisAC] Debug mode is disabled.");
			return;
		}

		context.Reply($"[AegisAC Aimlock] tick={CurrentTick} {_aimlock.Stats()}");
	}

	[Command("aegis_reload", registerRaw: true, permission: "@css/root")]
	[CommandAlias("aegisreload", registerRaw: true)]
	public void ReloadCommand(ICommandContext context)
	{
		LoadConfig();
		context.Reply("[AegisAC] Configuration reloaded successfully.");
	}

	private string CurrentMapName
	{
		get
		{
			try
			{
				string? name = Core.Engine.GlobalVars.MapName;
				return string.IsNullOrEmpty(name) ? "unknown" : name;
			}
			catch
			{
				return "unknown";
			}
		}
	}

	private void AnnounceAimbot(IPlayer player, string detection, string evidence)
	{
		string name = player.Controller?.PlayerName ?? $"#{player.PlayerID}";
		Utils.Log($" \x04[AegisAC]\x01 \x07{name}\x01 detected: \x0F{detection}\x01");

		_punishmentManager.Punish(player, _config.Aimbot.Punishment, "Aimbot", detection);

		if (_config.Discord.Enabled && !string.IsNullOrWhiteSpace(_config.Discord.WebhookUrl))
		{
			DiscordWebhookNotifier.SendAlert(
				_config.Discord.WebhookUrl,
				_config.ServerId,
				CurrentMapName,
				CurrentTick,
				name,
				player.SteamID,
				"Aimbot",
				detection,
				_config.Aimbot.Punishment,
				evidence);
		}
	}

	private void AnnounceTriggerbot(IPlayer player, string detection, string evidence)
	{
		string name = player.Controller?.PlayerName ?? $"#{player.PlayerID}";
		Utils.Log($" \x04[AegisAC]\x01 \x07{name}\x01 detected: \x0F{detection}\x01");

		_punishmentManager.Punish(player, _config.Triggerbot.Punishment, "Triggerbot", detection);

		if (_config.Discord.Enabled && !string.IsNullOrWhiteSpace(_config.Discord.WebhookUrl))
		{
			DiscordWebhookNotifier.SendAlert(
				_config.Discord.WebhookUrl,
				_config.ServerId,
				CurrentMapName,
				CurrentTick,
				name,
				player.SteamID,
				"Triggerbot",
				detection,
				_config.Triggerbot.Punishment,
				evidence);
		}
	}

	private void AnnounceAimlock(IPlayer player, string detection, string evidence)
	{
		string name = player.Controller?.PlayerName ?? $"#{player.PlayerID}";
		Utils.Log($" \x04[AegisAC]\x01 \x07{name}\x01 detected: \x0F{detection}\x01");

		_punishmentManager.Punish(player, _config.Aimlock.Punishment, "Aimlock", detection);

		if (_config.Discord.Enabled && !string.IsNullOrWhiteSpace(_config.Discord.WebhookUrl))
		{
			DiscordWebhookNotifier.SendAlert(
				_config.Discord.WebhookUrl,
				_config.ServerId,
				CurrentMapName,
				CurrentTick,
				name,
				player.SteamID,
				"Aimlock",
				detection,
				_config.Aimlock.Punishment,
				evidence);
		}
	}
}
