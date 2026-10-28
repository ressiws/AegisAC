using System;
using System.Text;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;
using AegisAC.Configuration;

namespace AegisAC.Modules;

/// <summary>
/// Dedicated Server-Side Client Event Subscription &amp; DLL Injection Detector.
/// Reimplemented for SwiftlyS2 from CS2AC Legacy's dll_injection_module.cpp:
/// - Inspects visible client-side Source 2 game event subscriptions via IGameEventService.IsListeningToEvent.
/// - Scans against 117 blacklisted game events that legitimate CS2 clients and game-instructor never subscribe to,
///   but injected internal cheats register via IGameEventManager2::AddListener.
/// - Runs on a staggered schedule (10s initial delay, 2-minute interval, at most 1 player scanned per server tick)
///   to guarantee zero frame-time overhead and zero hot-path allocations.
/// </summary>
internal sealed class DllInjectionDetector
{
    public const int MaxPlayers = 64;

    /// <summary>
    /// Exact 117 blacklisted client-side Source 2 game event subscriptions from CS2AC Legacy's dll_injection_module.cpp.
    /// </summary>
    internal static readonly string[] BlacklistedClientEvents =
    [
        "gameui_hidden",
        "player_chat",
        "player_score",
        "player_shoot",
        "game_init",
        "game_start",
        "game_end",
        "ugc_map_info_received",
        "ugc_map_unsubscribed",
        "ugc_map_download_error",
        "ugc_file_download_finished",
        "ugc_file_download_start",
        "dm_bonus_weapon_start",
        "survival_announce_phase",
        "reset_game_titledata",
        "vote_ended",
        "vote_started",
        "vote_options",
        "endmatch_mapvote_selecting_map",
        "endmatch_cmm_start_reveal_items",
        "inventory_updated",
        "client_loadout_changed",
        "add_player_sonar_icon",
        "door_open",
        "door_closed",
        "door_break",
        "bullet_damage",
        "hostage_call_for_help",
        "vip_escaped",
        "vip_killed",
        "silencer_detach",
        "inspect_weapon",
        "player_spawned",
        "item_pickup_slerp",
        "item_pickup_failed",
        "enter_rescue_zone",
        "exit_rescue_zone",
        "silencer_off",
        "silencer_on",
        "round_poststart",
        "tagrenade_detonate",
        "inferno_extinguish",
        "mb_input_lock_success",
        "mb_input_lock_cancel",
        "nav_generate",
        "achievement_info_loaded",
        "hltv_changed_mode",
        "show_deathpanel",
        "hide_deathpanel",
        "player_avenged_teammate",
        "achievement_earned_local",
        "repost_xbox_achievements",
        "match_end_conditions",
        "write_profile_data",
        "trial_time_expired",
        "update_matchmaking_stats",
        "enable_restart_voting",
        "sfuievent",
        "start_vote",
        "teamchange_pending",
        "material_default_complete",
        "cs_prev_next_spectator",
        "tournament_reward",
        "start_halftime",
        "ammo_refill",
        "parachute_pickup",
        "parachute_deploy",
        "dronegun_attack",
        "drone_dispatched",
        "loot_crate_visible",
        "loot_crate_opened",
        "open_crate_instr",
        "smoke_beacon_paradrop",
        "drone_cargo_detached",
        "drone_above_roof",
        "dz_item_interaction",
        "survival_teammate_respawn",
        "guardian_wave_restart",
        "bullet_flight_resolution",
        "server_message",
        "player_full_update",
        "local_player_controller_team",
        "local_player_pawn_changed",
        "ragdoll_dissolved",
        "team_info",
        "team_score",
        "hltv_rank_camera",
        "hltv_rank_entity",
        "demo_stop",
        "map_shutdown",
        "hostname_changed",
        "game_message",
        "round_start_pre_entity",
        "round_start_post_nav",
        "player_hintmessage",
        "broken_breakable",
        "door_close",
        "vote_failed",
        "vote_passed",
        "vote_cast_yes",
        "vote_cast_no",
        "achievement_event",
        "achievement_write_failed",
        "bonus_updated",
        "flare_ignite_npc",
        "helicopter_grenade_punt_miss",
        "physgun_pickup",
        "cart_updated",
        "store_pricesheet_updated",
        "item_schema_initialized",
        "drop_rate_modified",
        "event_ticket_modified",
        "gc_connected",
        "instructor_start_lesson",
        "instructor_close_lesson",
        "clientside_lesson_closed",
        "dynamic_shadow_light_changed"
    ];

    private readonly ISwiftlyCore? _core;
    private readonly ILogger? _logger;
    private DllInjectionConfig _config;
    private readonly Action<IPlayer?, string, string, PunishmentType>? _onViolation;
    private readonly DateTime[] _nextScanTimes = new DateTime[MaxPlayers + 1];

    public long StatScansCompleted { get; private set; }
    public long StatDetections { get; private set; }

    public DllInjectionDetector(
        ISwiftlyCore? core,
        ILogger? logger,
        DllInjectionConfig config,
        Action<IPlayer?, string, string, PunishmentType>? onViolation = null)
    {
        _core = core;
        _logger = logger;
        _config = config;
        _onViolation = onViolation;
    }

    public DllInjectionDetector(
        ISwiftlyCore? core,
        ILogger? logger,
        AimbotConfig legacyConfig,
        Action<IPlayer?, string, string, PunishmentType>? onViolation = null)
        : this(
            core,
            logger,
            new DllInjectionConfig
            {
                Enabled = legacyConfig.Enabled,
                Punishment = legacyConfig.Punishment
            },
            onViolation)
    {
    }

    public void UpdateConfig(DllInjectionConfig config) => _config = config;

    public void ResetAll()
    {
        Array.Clear(_nextScanTimes);
        StatScansCompleted = 0;
        StatDetections = 0;
    }

    public void ResetPlayer(int slot)
    {
        if (ValidSlot(slot))
        {
            _nextScanTimes[slot] = DateTime.MinValue;
        }
    }

    public string Stats() =>
        $"enabled={_config.Enabled} blacklist={BlacklistedClientEvents.Length} scans={StatScansCompleted} detections={StatDetections} interval={_config.ScanIntervalSeconds:F0}s";

    /// <summary>
    /// Staggered background scan (at most 1 player per server frame, configurable initial delay and interval)
    /// for blacklisted client-side game event subscriptions via SwiftlyS2's IGameEventService.IsListeningToEvent.
    /// </summary>
    public void OnGameFrame()
    {
        if (!_config.Enabled || _core == null)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        double initialDelay = Math.Max(1.0, _config.InitialScanDelaySeconds);
        double interval = Math.Max(10.0, _config.ScanIntervalSeconds);

        for (int slot = 0; slot < MaxPlayers; slot++)
        {
            var player = _core.PlayerManager.GetPlayer(slot);
            if (player == null || !AimbotDetector.IsEligibleHuman(player))
            {
                continue;
            }

            if (_nextScanTimes[slot] == DateTime.MinValue)
            {
                _nextScanTimes[slot] = now.AddSeconds(initialDelay);
                continue;
            }

            if (now < _nextScanTimes[slot])
            {
                continue;
            }

            _nextScanTimes[slot] = now.AddSeconds(interval);
            string playerName = player.Controller?.PlayerName ?? $"Player#{slot}";
            ScanClientEventSubscriptions(slot, player, playerName, _core.GameEvent.IsListeningToEvent);
            break; // Scan at most 1 player per frame (zero tick-time spike)
        }
    }

    internal bool ScanClientEventSubscriptions(
        int slot,
        IPlayer? player,
        string playerName,
        Func<int, string, bool> isListeningToEvent)
    {
        if (!_config.Enabled || !ValidSlot(slot))
        {
            return false;
        }

        StatScansCompleted++;
        int matchCount = 0;
        StringBuilder? matches = null;

        for (int i = 0; i < BlacklistedClientEvents.Length; i++)
        {
            string ev = BlacklistedClientEvents[i];
            if (!isListeningToEvent(slot, ev))
            {
                continue;
            }

            matchCount++;
            matches ??= new StringBuilder(128);
            if (matches.Length > 0 && matches.Length <= 700)
            {
                matches.Append(", ");
            }
            if (matches.Length <= 700)
            {
                matches.Append(ev);
                if (matches.Length > 700)
                {
                    matches.Append(", ...");
                }
            }
        }

        if (matchCount == 0 || matches == null)
        {
            return false;
        }

        StatDetections++;
        string details = $"DLL Injection: client listening for {matchCount} blacklisted game event(s) associated with injected cheats ({matches}).";
        _logger?.LogWarning("[AegisAC] [DllInjection] {Name} {Details}", playerName, details);
        _onViolation?.Invoke(player, "DLL Injection", details, _config.Punishment);
        return true;
    }

    private static bool ValidSlot(int slot) => slot >= 0 && slot <= MaxPlayers;
}
