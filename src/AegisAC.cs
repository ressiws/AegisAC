using System;
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
using SwiftlyS2.Shared.ProtobufDefinitions;
using AegisAC.Configuration;
using AegisAC.Modules;
using AegisAC.Notifications;
using AegisAC.Punishment;
using AegisAC.Telemetry;

namespace AegisAC;

[PluginMetadata(
	Id = "AegisAC",
	Version = "1.1.0",
	Name = "Aegis Anti-Cheat",
	Author = "swisser & sevon",
	Description = "Server-side anti-cheat plugin for CS2.",
	MinimumAPIVersion = "1.1.6")]
public sealed class AegisAC : BasePlugin
{
	private AegisConfig _config = new();
	private string Prefix => !string.IsNullOrWhiteSpace(_config.Prefix) ? _config.Prefix : "AegisAC";
	private PunishmentManager _punishmentManager = null!;
	private NetworkSafetyMonitor _networkSafety = null!;
	private AimbotDetector _detector = null!;
	private TriggerbotDetector _triggerbot = null!;
	private AimlockDetector _aimlock = null!;
	private AntiAimDetector _antiAim = null!;
	private MovementDetector _movement = null!;
	private ExploitsDetector _exploits = null!;
	private DllInjectionDetector _dllInjection = null!;
	private CVarScannerDetector _cvarScanner = null!;
	private AntiWallHackDetector _antiWallHack = null!;
	private ConvictionEngine _conviction = null!;
	private readonly Dictionary<(int Slot, string Module), DateTime> _lastStaffAlertTimes = new();
	private IDisposable? _configChangeToken;
	private Guid _fireBulletsHookGuid;
	private bool _hasFireBulletsHook;

	public AegisAC(ISwiftlyCore core) : base(core) { }

	public override void Load(bool hotReload)
	{
		Utils.Initialize(Core);
		Utils.Prefix = Prefix;

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
		_conviction = new ConvictionEngine(_config.Conviction);
		_networkSafety = new NetworkSafetyMonitor();

		_detector = new AimbotDetector(
			_config.Aimbot,
			id => Core.PlayerManager.GetPlayer(id),
			AnnounceAimbot,
			msg => { if (Utils.DebugMode) Utils.ConsoleLog(msg); },
			_networkSafety);
		_detector.UpdateConfig(_config.Aimbot, _config.SilentAim);

		_triggerbot = new TriggerbotDetector(
			_config.Triggerbot,
			Core.Trace,
			id => Core.PlayerManager.GetPlayer(id),
			(a, b) => _detector.AreEnemies(a, b),
			AnnounceTriggerbot,
			msg => { if (Utils.DebugMode) Utils.ConsoleLog(msg); },
			tick => _detector.FindFrame(tick),
			_networkSafety);

		_aimlock = new AimlockDetector(
			_config.Aimlock,
			tick => _detector.FindFrame(tick),
			id => Core.PlayerManager.GetPlayer(id),
			(a, b) => _detector.AreEnemies(a, b),
			AnnounceAimlock,
			msg => { if (Utils.DebugMode) Utils.ConsoleLog(msg); },
			Core.Trace,
			networkSafety: _networkSafety);

		_antiAim = new AntiAimDetector(
			Core,
			null,
			_config.AntiAim,
			_networkSafety,
			(player, detection, details, _) =>
			{
				if (player != null)
					AnnounceAntiAim(player, detection, details);
			});

		_movement = new MovementDetector(
			Core,
			null,
			_config.Movement,
			_networkSafety,
			(player, detection, details, _) =>
			{
				if (player != null)
					AnnounceMovement(player, detection, details);
			},
			() =>
			{
				try
				{
					return Core.ConVar.Find<bool>("sv_autobunnyhopping")?.Value == true;
				}
				catch
				{
					return false;
				}
			});

		_exploits = new ExploitsDetector(
			Core,
			null,
			_config.Exploits,
			_networkSafety,
			(player, detection, details, _) =>
			{
				if (player != null)
					AnnounceExploits(player, detection, details);
			});

		_dllInjection = new DllInjectionDetector(
			Core,
			null,
			_config.DllInjection,
			(player, detection, details, _) =>
			{
				if (player != null)
					AnnounceDllInjection(player, detection, details);
			});

		_cvarScanner = new CVarScannerDetector(
			Core,
			null,
			_config.CVarScanner,
			(player, detection, details, _) =>
			{
				if (player != null)
					AnnounceCVar(player, detection, details);
			},
			() =>
			{
				try
				{
					return Core.ConVar.Find<bool>("sv_cheats")?.Value == true;
				}
				catch
				{
					return false;
				}
			});

		_antiWallHack = new AntiWallHackDetector(Core, _config.AntiWallHack);

		Core.GameHooks.Controller.ProcessUsercmds.Pre += OnProcessUsercmds;
		Core.GameHooks.Movement.SetupMove.Pre += OnSetupMove;
		Core.Event.OnTick += OnTick;
		Core.Event.OnClientDisconnected += OnClientDisconnected;
		Core.Event.OnMapLoad += OnMapLoad;

		try
		{
			_fireBulletsHookGuid = Core.NetMessage.HookServerMessage<CMsgTEFireBullets>(OnFireBullets);
			_hasFireBulletsHook = true;
		}
		catch (Exception ex)
		{
			Utils.ConsoleLog($"CMsgTEFireBullets netmessage hook unavailable (using conservative spread fallback): {ex.Message}");
		}

		Utils.Log("initialized. Commands: aegis_status aegis_suspects aimbot_status silentaim_status triggerbot_status aimlock_status antiaim_status dllinjection_status cvar_status awh_status aegis_reload");
	}

	public override void Unload()
	{
		_antiWallHack?.Dispose();
		_configChangeToken?.Dispose();
		if (_hasFireBulletsHook)
		{
			try
			{
				Core.NetMessage.Unhook(_fireBulletsHookGuid);
			}
			catch { }
			_hasFireBulletsHook = false;
		}

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
			Utils.Prefix = Prefix;
			RefreshTeammatesAreEnemies();
			Utils.DebugMode = _config.Debug;
			_punishmentManager?.UpdateConfig(_config);
			_detector?.UpdateConfig(_config.Aimbot, _config.SilentAim);
			_triggerbot?.UpdateConfig(_config.Triggerbot);
			_aimlock?.UpdateConfig(_config.Aimlock);
			_antiAim?.UpdateConfig(_config.AntiAim);
			_movement?.UpdateConfig(_config.Movement);
			_exploits?.UpdateConfig(_config.Exploits);
			_dllInjection?.UpdateConfig(_config.DllInjection);
			_cvarScanner?.UpdateConfig(_config.CVarScanner);
			_antiWallHack?.UpdateConfig(_config.AntiWallHack);
			_conviction?.UpdateConfig(_config.Conviction);
		}
	}

	private void RefreshTeammatesAreEnemies()
	{
		try
		{
			if (Core.ConVar.Find<bool>("mp_teammates_are_enemies")?.Value is bool cvarVal)
			{
				_config.Aimbot.TeammatesAreEnemies = cvarVal;
				if (_antiWallHack != null)
				{
					_antiWallHack.TeammatesAreEnemies = cvarVal;
				}
			}
		}
		catch { }
	}

	private int CurrentTick => Core.Engine.GlobalVars.TickCount;

	private HookResult OnFireBullets(CMsgTEFireBullets msg)
	{
		try
		{
			int playerSlot = (int)(msg.Player & 0x3FFFu);
			if (playerSlot >= 0 && playerSlot <= AimbotDetector.MaxPlayers)
			{
				_detector.OnFireBullets(
					playerSlot,
					CurrentTick,
					msg.WeaponId,
					msg.Inaccuracy,
					msg.Spread,
					msg.Angles);
			}
		}
		catch { }
		return HookResult.Continue;
	}

	private void OnProcessUsercmds(ref ProcessUsercmdsPreContext ctx)
	{
		var player = ctx.Params.Player;
		if (player == null || !player.IsValid || ctx.Params.Usercmds.Count == 0)
		{
			return;
		}

		int tick = CurrentTick;

		if (!player.IsFakeClient)
		{
			int firstCmd = (int)ctx.Params.Usercmds[0].CommandNumber;
			int lastCmd = (int)ctx.Params.Usercmds[^1].CommandNumber;
			_networkSafety.OnProcessUsercmds(player.PlayerID, ctx.Params.Usercmds.Count, firstCmd, lastCmd);
		}

		_exploits.OnProcessUsercmds(player, ctx.Params.Usercmds, tick);

		var pawn = player.PlayerPawn;
		if (pawn is { IsValid: true } && pawn.LifeState == 0 && pawn.Health > 0)
		{
			_detector.OnProcessUsercmds(player, ctx.Params.Usercmds);
			_triggerbot.OnProcessUsercmds(player, ctx.Params.Usercmds);
			_aimlock.OnProcessUsercmds(player, ctx.Params.Usercmds);
			_antiAim.OnProcessUsercmds(player, ctx.Params.Usercmds, tick);
			_movement.OnProcessUsercmds(player, ctx.Params.Usercmds, tick);
		}
	}

	private void OnSetupMove(ref SetupMoveMovementPreContext ctx)
	{
		var player = ctx.Params.Player;
		if (player == null || !player.IsValid)
		{
			return;
		}

		var pawn = player.PlayerPawn;
		if (pawn is not { IsValid: true } || pawn.LifeState != 0 || pawn.Health <= 0)
		{
			return;
		}

		int cmdNum = (int)ctx.Params.UserCmd.CommandNumber;
		int tick = CurrentTick;
		_detector.OnSetupMove(player, cmdNum, tick);
		_triggerbot.OnSetupMove(player, cmdNum, tick);
		_aimlock.OnSetupMove(player, cmdNum, tick);
	}

	private void OnTick()
	{
		int tick = CurrentTick;

		// Sample player network channels every 16 ticks (~250ms) for lag/loss/choke gating
		if ((tick & 15) == 0)
		{
			for (int id = 0; id < AimbotDetector.MaxPlayers; id++)
			{
				var p = Core.PlayerManager.GetPlayer(id);
				if (p is { IsValid: true, IsFakeClient: false })
				{
					_networkSafety.SamplePlayer(p);
				}
			}
		}

		_detector.OnGameFrame(tick);
		_aimlock.OnGameFrame(tick);
		_dllInjection.OnGameFrame();
		_cvarScanner.OnGameFrame();
		_antiWallHack.OnGameFrame(tick);
	}

	private void OnClientDisconnected(SwiftlyS2.Shared.Events.IOnClientDisconnectedEvent e)
	{
		_networkSafety.ResetPlayer(e.PlayerId);
		_detector.OnClientDisconnect(e.PlayerId);
		_triggerbot.OnClientDisconnect(e.PlayerId);
		_aimlock.OnClientDisconnect(e.PlayerId);
		_antiAim.ResetPlayer(e.PlayerId);
		_movement.ResetPlayer(e.PlayerId);
		_exploits.ResetPlayer(e.PlayerId);
		_dllInjection.ResetPlayer(e.PlayerId);
		_cvarScanner.ResetPlayer(e.PlayerId);
		_antiWallHack.ResetPlayer(e.PlayerId);
		_conviction?.ResetPlayer(e.PlayerId);
		if (Core.PlayerManager.GetPlayer(e.PlayerId) is { } p && p.SteamID > 0)
		{
			_conviction?.ResetPlayer(p.SteamID);
		}
		var keysToRemove = _lastStaffAlertTimes.Keys.Where(k => k.Slot == e.PlayerId).ToList();
		foreach (var k in keysToRemove)
		{
			_lastStaffAlertTimes.Remove(k);
		}
	}

	private void OnMapLoad(SwiftlyS2.Shared.Events.IOnMapLoadEvent e)
	{
		RefreshTeammatesAreEnemies();
		_networkSafety.ResetAll();
		_detector.Reset();
		_triggerbot.Reset();
		_aimlock.Reset();
		_antiAim.ResetAll();
		_movement.ResetAll();
		_exploits.ResetAll();
		_dllInjection.ResetAll();
		_cvarScanner.ResetAll();
		_antiWallHack.OnMapLoad();
		_conviction?.ResetAll();
		_lastStaffAlertTimes.Clear();
	}

	[GameEventHandler(HookMode.Post)]
	public HookResult OnWeaponFire(EventWeaponFire e)
	{
		var player = e.UserIdPlayer;

		if (player is { IsValid: true })
		{
			_antiWallHack?.OnWeaponFired(player.PlayerID, CurrentTick);
			_detector.OnWeaponFire(
				player,
				e.Weapon ?? "",
				CurrentTick);
			_triggerbot.OnWeaponFire(
				player,
				e.Weapon ?? "",
				CurrentTick);
			_exploits.OnWeaponFire(player, CurrentTick);
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
			int? matchedCmd = _detector.OnPlayerHurt(attacker, victim, e.Weapon ?? "", CurrentTick);
			_triggerbot.OnPlayerHurt(attacker, victim, e.Weapon ?? "", e.ActualDmgHealth, matchedCmd);
		}
		return HookResult.Continue;
	}

	[GameEventHandler(HookMode.Post)]
	public HookResult OnPlayerDeath(EventPlayerDeath e)
	{
		var attacker = e.AttackerPlayer;
		var victim = e.UserIdPlayer;
		if (attacker is { IsValid: true } && victim is { IsValid: true })
		{
			var pawn = attacker.PlayerPawn;
			bool inAir = pawn is { IsValid: true } && (pawn.Flags & 1u) == 0;
			bool noScope = pawn is { IsValid: true } && !pawn.IsScoped;

			_detector.OnPlayerDeath(
				attacker,
				victim,
				e.Weapon ?? "",
				e.Headshot,
				e.Penetrated,
				noScope,
				thruSmoke: false,
				inAir,
				e.Distance,
				CurrentTick);
		}
		return HookResult.Continue;
	}

	[GameEventHandler(HookMode.Post)]
	public HookResult OnSmokeDetonate(EventSmokegrenadeDetonate e)
	{
		Vector center = new(e.X, e.Y, e.Z);
		_triggerbot.OnSmokeDetonate(center);
		_antiWallHack?.OnSmokeDetonate(center, (float)Core.Engine.GlobalVars.CurrentTime);
		return HookResult.Continue;
	}

	[GameEventHandler(HookMode.Post)]
	public HookResult OnHegrenadeDetonate(EventHegrenadeDetonate e)
	{
		Vector center = new(e.X, e.Y, e.Z);
		_antiWallHack?.OnHeDetonate(center, (float)Core.Engine.GlobalVars.CurrentTime);
		return HookResult.Continue;
	}

		[GameEventHandler(HookMode.Post)]
	public HookResult OnRoundStart(EventRoundStart e)
	{
		_antiWallHack?.OnRoundStart();
		return HookResult.Continue;
	}

	[GameEventHandler(HookMode.Post)]
	public HookResult OnRoundEnd(EventRoundEnd e)
	{
		_antiWallHack?.OnRoundEnd();
		return HookResult.Continue;
	}

	[GameEventHandler(HookMode.Post)]
	public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd e)
	{
		_antiWallHack?.OnRoundStart();
		return HookResult.Continue;
	}

	[Command("aegis_status", registerRaw: true, permission: "@css/root")]
	[CommandAlias("aegisstatus", registerRaw: true)]
	public void AegisStatusCommand(ICommandContext context)
	{
		if (!_config.Debug)
		{
			context.Reply($"[{Prefix}] Debug mode is disabled. Set \"Debug\": true in config.jsonc to inspect live module telemetry.");
			return;
		}

		context.Reply($"[{Prefix} Status] tick={CurrentTick} map={CurrentMapName}");
		context.Reply($"  Aimbot      : {_detector.Stats()}");
		context.Reply($"  SilentAim   : {_detector.SilentAim.Stats()}");
		context.Reply($"  Triggerbot  : {_triggerbot.Stats()}");
		context.Reply($"  Aimlock     : {_aimlock.Stats()}");
		context.Reply($"  AntiAim     : {_antiAim.Stats()}");
		context.Reply($"  Movement    : {_movement.Stats()}");
		context.Reply($"  Exploits    : {_exploits.Stats()}");
		context.Reply($"  DllInjection: {_dllInjection.Stats()}");
		context.Reply($"  CVarScanner : {_cvarScanner.Stats()}");
		context.Reply($"  AntiWallHack: {_antiWallHack.Stats()}");
	}

	[Command("aimbot_status", registerRaw: true, permission: "@css/root")]
	public void AimbotStatusCommand(ICommandContext context)
	{
		if (!_config.Debug)
		{
			context.Reply($"[{Prefix}] Debug mode is disabled.");
			return;
		}

		context.Reply($"[{Prefix} Aimbot] tick={CurrentTick} {_detector.Stats()}");
	}

	[Command("silentaim_status", registerRaw: true, permission: "@css/root")]
	public void SilentAimStatusCommand(ICommandContext context)
	{
		if (!_config.Debug)
		{
			context.Reply($"[{Prefix}] Debug mode is disabled.");
			return;
		}

		context.Reply($"[{Prefix} SilentAim] tick={CurrentTick} {_detector.SilentAim.Stats()}");
	}

	[Command("triggerbot_status", registerRaw: true, permission: "@css/root")]
	public void TriggerbotStatusCommand(ICommandContext context)
	{
		if (!_config.Debug)
		{
			context.Reply($"[{Prefix}] Debug mode is disabled.");
			return;
		}

		context.Reply($"[{Prefix} Triggerbot] tick={CurrentTick} {_triggerbot.Stats()}");
	}

	[Command("aimlock_status", registerRaw: true, permission: "@css/root")]
	public void AimlockStatusCommand(ICommandContext context)
	{
		if (!_config.Debug)
		{
			context.Reply($"[{Prefix}] Debug mode is disabled.");
			return;
		}

		context.Reply($"[{Prefix} Aimlock] tick={CurrentTick} {_aimlock.Stats()}");
	}

	[Command("antiaim_status", registerRaw: true, permission: "@css/root")]
	public void AntiAimStatusCommand(ICommandContext context)
	{
		if (!_config.Debug)
		{
			context.Reply($"[{Prefix}] Debug mode is disabled.");
			return;
		}

		context.Reply($"[{Prefix} AntiAim] tick={CurrentTick} {_antiAim.Stats()}");
	}

	[Command("dllinjection_status", registerRaw: true, permission: "@css/root")]
	public void DllInjectionStatusCommand(ICommandContext context)
	{
		if (!_config.Debug)
		{
			context.Reply($"[{Prefix}] Debug mode is disabled.");
			return;
		}

		context.Reply($"[{Prefix} DllInjection] tick={CurrentTick} {_dllInjection.Stats()}");
	}

	[Command("cvar_status", registerRaw: true, permission: "@css/root")]
	public void CVarStatusCommand(ICommandContext context)
	{
		if (!_config.Debug)
		{
			context.Reply($"[{Prefix}] Debug mode is disabled.");
			return;
		}

		context.Reply($"[{Prefix} CVarScanner] tick={CurrentTick} {_cvarScanner.Stats()}");
	}

	[Command("awh_status", registerRaw: true, permission: "@css/root")]
	[CommandAlias("antiwallhack_status", registerRaw: true)]
	public void AntiWallHackStatusCommand(ICommandContext context)
	{
		context.Reply($"[{Prefix} AntiWallHack] tick={CurrentTick} {_antiWallHack.Stats()}");
	}

	[Command("aegis_reload", registerRaw: true, permission: "@css/root")]
	[CommandAlias("aegisreload", registerRaw: true)]
	public void ReloadCommand(ICommandContext context)
	{
		LoadConfig();
		context.Reply($"[{Prefix}] Configuration reloaded successfully.");
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

	private void NotifyStaffInGame(IPlayer suspect, string moduleName, string detection, string evidence, bool isConviction = false)
	{
		if (!_config.StaffAlerts.Enabled || !suspect.IsValid)
			return;

		DateTime now = DateTime.UtcNow;
		int slot = suspect.PlayerID;
		var alertKey = (slot, moduleName);

		if (!isConviction)
		{
			if (_lastStaffAlertTimes.TryGetValue(alertKey, out var lastTime))
			{
				if ((now - lastTime).TotalSeconds < Math.Max(1.0f, _config.StaffAlerts.CooldownSeconds))
					return;
			}
			_lastStaffAlertTimes[alertKey] = now;
		}

		string suspectName = suspect.Controller?.PlayerName ?? $"#{slot}";
		string flag = !string.IsNullOrWhiteSpace(_config.StaffAlerts.PermissionFlag) ? _config.StaffAlerts.PermissionFlag : "@css/generic";

		string cleanEvidence = evidence.Replace("\r\n", " ").Replace("\n", " ").Trim();
		if (cleanEvidence.Length > 160)
		{
			cleanEvidence = cleanEvidence[..157] + "...";
		}

		string message = isConviction
			? $" \x04[{Prefix}]\x01 \x02[CONVICTION ALERT]\x01 Suspect \x07{suspectName}\x01 reached conviction threshold! (\x0F{detection}\x01 - {cleanEvidence})"
			: $" \x04[{Prefix}]\x01 \x09[STAFF ALERT]\x01 Suspect \x07{suspectName}\x01 flagged for \x0F{detection}\x01 (\x06{moduleName}\x01 - {cleanEvidence})";

		foreach (var admin in Core.PlayerManager.GetAllValidPlayers())
		{
			if (admin == null || !admin.IsValid || admin.IsFakeClient || admin.PlayerID == slot)
				continue;

			bool hasPerm = false;
			try
			{
				hasPerm = Core.Permission.PlayerHasPermission(admin.SteamID, flag);
			}
			catch (Exception ex)
			{
				Utils.ConsoleLog($"[StaffAlerts] Permission check error for admin {admin.PlayerID}: {ex.Message}");
			}

			if (hasPerm)
			{
				try
				{
					admin.SendChat(message);
				}
				catch (Exception ex)
				{
					Utils.ConsoleLog($"[StaffAlerts] SendChat failed for admin {admin.PlayerID}: {ex.Message}");
				}
			}
		}
	}

	private void AnnounceDetection(IPlayer player, string moduleName, string detection, string evidence, PunishmentType punishment)
	{
		string name = player.Controller?.PlayerName ?? $"#{player.PlayerID}";
		Utils.Log($" \x04[{Prefix}]\x01 \x07{name}\x01 detected: \x0F{detection}\x01");
		Utils.ConsoleLog($"[{moduleName}] {name} ({player.SteamID}) detected: {detection} — {evidence}");

		_punishmentManager.Punish(player, punishment, moduleName, detection);

		// Send private stealth in-game chat alert to online staff members
		NotifyStaffInGame(player, moduleName, detection, evidence, isConviction: false);

		// Feed detection telemetry into the Cumulative Conviction Engine
		if (_conviction != null && _conviction.NoteEvidence(player, moduleName, detection, evidence, out bool convicted, out string convictionSummary))
		{
			string convDetails = $"Cumulative Conviction triggered for {name}: {convictionSummary}";
			Utils.Log($" \x04[{Prefix}]\x01 \x07{name}\x01 \x0FCONVICTED\x01 by Cumulative Evidence Matrix: {convictionSummary}");
			Utils.ConsoleLog($"[CumulativeConviction] {name} ({player.SteamID}) {convDetails}");
			NotifyStaffInGame(player, "CumulativeConviction", "Convicted", convictionSummary, isConviction: true);
			_punishmentManager.Punish(player, _config.Conviction.Punishment, "CumulativeConviction", convictionSummary);

			if (_config.Telemetry.DumpIncidents)
			{
				IncidentDumper.DumpIncident(
					_config.Telemetry.IncidentsDirectory,
					_config.ServerId,
					CurrentMapName,
					CurrentTick,
					name,
					player.SteamID,
					player.UserID,
					player.PlayerID,
					"CumulativeConviction",
					"Convicted",
					_config.Conviction.Punishment.ToString(),
					convictionSummary);
			}

			if (_config.Discord.Enabled && !string.IsNullOrWhiteSpace(_config.Discord.WebhookUrl))
			{
				DiscordWebhookNotifier.SendAlert(
					_config.Discord.WebhookUrl,
					_config.ServerId,
					CurrentMapName,
					CurrentTick,
					name,
					player.SteamID,
					"CumulativeConviction",
					"Convicted",
					_config.Conviction.Punishment,
					convictionSummary,
					Prefix);
			}
		}

		if (_config.Telemetry.DumpIncidents)
		{
			IncidentDumper.DumpIncident(
				_config.Telemetry.IncidentsDirectory,
				_config.ServerId,
				CurrentMapName,
				CurrentTick,
				name,
				player.SteamID,
				player.UserID,
				player.PlayerID,
				moduleName,
				detection,
				punishment.ToString(),
				evidence);
		}

		if (_config.Discord.Enabled && !string.IsNullOrWhiteSpace(_config.Discord.WebhookUrl))
		{
			DiscordWebhookNotifier.SendAlert(
				_config.Discord.WebhookUrl,
				_config.ServerId,
				CurrentMapName,
				CurrentTick,
				name,
				player.SteamID,
				moduleName,
				detection,
				punishment,
				evidence,
				Prefix);
		}
	}

	private void AnnounceAimbot(IPlayer player, string detection, string evidence)
	{
		bool isSilentAim = detection.Contains("SILENT", StringComparison.OrdinalIgnoreCase)
			|| detection.Contains("SNAP_RETURN", StringComparison.OrdinalIgnoreCase);

		if (isSilentAim)
		{
			AnnounceDetection(player, "SilentAim", detection, evidence, _config.SilentAim.Punishment);
			return;
		}

		AnnounceDetection(player, "Aimbot", detection, evidence, _config.Aimbot.Punishment);
	}

	private void AnnounceTriggerbot(IPlayer player, string detection, string evidence) =>
		AnnounceDetection(player, "Triggerbot", detection, evidence, _config.Triggerbot.Punishment);

	private void AnnounceAimlock(IPlayer player, string detection, string evidence) =>
		AnnounceDetection(player, "Aimlock", detection, evidence, _config.Aimlock.Punishment);

	[Command("movement_status", registerRaw: true, permission: "@css/root")]
	public void MovementStatusCommand(ICommandContext context)
	{
		if (!_config.Debug)
		{
			context.Reply($"[{Prefix}] Debug mode is disabled.");
			return;
		}

		context.Reply($"[{Prefix} Movement] tick={CurrentTick} {_movement.Stats()}");
	}

	[Command("exploits_status", registerRaw: true, permission: "@css/root")]
	public void ExploitsStatusCommand(ICommandContext context)
	{
		if (!_config.Debug)
		{
			context.Reply($"[{Prefix}] Debug mode is disabled.");
			return;
		}

		context.Reply($"[{Prefix} Exploits] tick={CurrentTick} {_exploits.Stats()}");
	}

	[Command("aegis_suspects", registerRaw: true, permission: "@css/root")]
	public void SuspectsCommand(ICommandContext context)
	{
		var suspects = _conviction.GetActiveSuspects();
		if (suspects.Count == 0)
		{
			context.Reply($"[{Prefix}] No active suspects tracked with significant suspicion score.");
			return;
		}

		context.Reply($"[{Prefix}] === Active Suspects Matrix ({suspects.Count}) ===");
		for (int i = 0; i < Math.Min(suspects.Count, 10); i++)
		{
			var s = suspects[i];
			string status = s.HasBeenConvicted ? "[CONVICTED]" : "[SUSPECT]";
			string modules = string.Join(", ", s.ModuleCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}[{kv.Value}x]"));
			context.Reply($"#{i + 1} {s.PlayerName} (Steam: {s.SteamId}) Score: {s.DecayedScore:F1}/{_config.Conviction.ConvictionThreshold:F1} {status} | Modules: {modules}");
		}
	}

	private void AnnounceAntiAim(IPlayer player, string detection, string evidence) =>
		AnnounceDetection(player, "AntiAim", detection, evidence, _config.AntiAim.Punishment);

	private void AnnounceMovement(IPlayer player, string detection, string evidence) =>
		AnnounceDetection(player, "Movement", detection, evidence, _config.Movement.Punishment);

	private void AnnounceExploits(IPlayer player, string detection, string evidence) =>
		AnnounceDetection(player, "Exploits", detection, evidence, _config.Exploits.Punishment);

	private void AnnounceDllInjection(IPlayer player, string detection, string evidence) =>
		AnnounceDetection(player, "DllInjection", detection, evidence, _config.DllInjection.Punishment);

	private void AnnounceCVar(IPlayer player, string detection, string evidence) =>
		AnnounceDetection(player, "CVarScanner", detection, evidence, _config.CVarScanner.Punishment);
}
