# Configuration Guide

AegisAC uses a structured JSON with Comments (`config.jsonc`) file managed by the SwiftlyS2 configuration service.

---

## File Location & Reloading

- **Location**: `<server_root>/game/csgo/addons/swiftlys2/configs/plugins/AegisAC/config.jsonc`
- **Dynamic Reload**: Configuration changes can be reloaded at runtime without restarting the server:
  - Automatically via SwiftlyS2's file change watcher (`reloadOnChange: true`).
  - Manually via the server console or admin command: `aegis_reload` (requires `@css/root`).

---

## Punishment Modes

Each detection module features an independent `Punishment` setting that defines the action taken when a suspect triggers a detection.

| Mode | String Representation | Numeric Value | Description |
| :--- | :--- | :--- | :--- |
| **Log Only** | `"None"` | `0` | Records detections to server console, dumps JSON telemetry (if enabled), sends Discord alerts (if enabled), and sends in-game staff alerts without kicking or banning the player. **Recommended during initial deployment and calibration.** |
| **Notify** | `"None"` | `1` | In AegisAC, notification to logs, Discord, and staff chat is automatically active for all detections regardless of punishment setting. When configured without automatic ejection, detections are tracked for staff observation. |
| **Kick** | `"Kick"` | `1` (code enum) / `2` | Immediately disconnects the suspect from the server executing `KickCommand`. |
| **Ban** | `"Ban"` | `2` (code enum) / `3` | Immediately bans the suspect executing `BanCommand` or the configured `AdminSystem` preset. |

> **Note**: Both string identifiers (e.g. `"None"`, `"Kick"`, `"Ban"`) and integer values are accepted by the configuration parser. Do not treat `0` as disabled—to disable a module entirely, set its `"Enabled": false` property.

---

## Root Configuration

| Key | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `Prefix` | string | `"AegisAC"` | Text tag prefixed in chat announcements, kick/ban reasons, and server console logs. |
| `ServerId` | string | `"cs2-server-1"` | Unique identifier for this server instance in Discord alerts and telemetry dumps. |
| `Debug` | bool | `false` | Enables diagnostic console output and activates in-game status commands (`aegis_status`, `awh_status`, etc.). |
| `AdminSystem` | string | `"Default"` | Admin preset for bans: `"Default"` (`sw_ban #{userid}`), `"T3Admin"` (`sw_addban {steamid}`), or `"Custom"`. |
| `KickCommand` | string | `"sw_kick #{userid} \"[{prefix}] {reason}\""` | Console command executed when a kick punishment is triggered. Placeholders: `{prefix}`, `{userid}`, `{slot}`, `{steamid}`, `{name}`, `{reason}`. |
| `BanCommand` | string | `"sw_ban #{userid} 0 \"[{prefix}] {reason}\"; kickid {userid} \"[{prefix}] {reason}\""` | Console command executed when a ban punishment is triggered. Placeholders: `{prefix}`, `{userid}`, `{slot}`, `{steamid}`, `{name}`, `{reason}`. |

---

## Detection Modules Configuration

### 1. Aimbot (`Aimbot`)

Configures snap angle detection, mechanical smoothing, recoil compensation analysis, inhuman accuracy, and doubletap exploits.

| Key | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `Enabled` | bool | `true` | Enables or disables the aimbot detection module. |
| `Punishment` | string / int | `"None"` | Punishment applied on detection (`"None"`, `"Kick"`, `"Ban"`). |
| `TeammatesAreEnemies` | bool | `false` | Treat teammates as enemies (useful for deathmatch or FFA servers). |
| `SnapAngleThreshold` | float | `26.0` | Minimum single-tick snap angle in degrees required for convergence detection. |
| `SmoothMinimumMovement` | float | `12.0` | Minimum angular movement in degrees required for standard smooth aimbot trajectory detection. |
| `LowFovSmoothMinimumMovement` | float | `3.5` | Minimum angular movement in degrees required for low-FOV smooth aimbot trajectory detection. |
| `SmoothMinimumPathEfficiency` | float | `0.96` | Minimum path efficiency ratio (0.0 to 1.0) required for smooth trajectory detection. |
| `EnableRcsCheck` | bool | `true` | Enables recoil compensation / RCS macro spray pattern verification. |
| `EnableInhumanAccuracyCheck` | bool | `true` | Enables sustained inhuman accuracy detection across aimed shots. |
| `EnableIrregularBehaviorCheck` | bool | `true` | Enables irregular combat behavior detection (repeated airborne and far-distance no-scope shots). |
| `EnableDoubletap` | bool | `true` | Enables doubletap / rapid-fire exploit detection comparing weapon cycle intervals. |
| `EnableSilentAim` | bool | `true` | Enables sub-tick silent-aim evaluation within the aimbot pipeline. |
| `EnableAntiAim` | bool | `true` | Enables anti-aim evaluation. |

### 2. Silent Aim & Snap-Return (`SilentAim`)

Dedicated analysis of sub-tick viewangle desynchronization and post-shot 1-tick snaps.

| Key | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `Enabled` | bool | `true` | Enables or disables the silent aim module. |
| `Punishment` | string / int | `"None"` | Punishment applied on detection (`"None"`, `"Kick"`, `"Ban"`). |
| `ScoreThreshold` | float | `10.0` | Accumulated weighted point threshold required to trigger a sub-tick silent aim detection. |
| `EnableSubtickSilentAim` | bool | `true` | Compares base viewangles against historical attack angles, weapon spread, and mouse travel. |
| `EnableSnapReturn` | bool | `true` | Enables post-shot snap-return detection for 1-tick snaps returning to prior angles. |
| `SnapReturnMinimumAngle` | float | `6.0` | Minimum 1-tick snap angle in degrees required for snap-return detection. |
| `SnapReturnMaximumSurroundingAngle` | float | `3.5` | Maximum angular difference in degrees between pre-shot and post-shot camera angles. |

### 3. Triggerbot & Synthetic Input (`Triggerbot`)

Monitors reaction timing on crosshair intersection, synthetic click cadences, and line-of-sight conditions.

| Key | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `Enabled` | bool | `true` | Enables or disables the triggerbot detection module. |
| `Punishment` | string / int | `"None"` | Punishment applied on detection (`"None"`, `"Kick"`, `"Ban"`). |
| `EvidenceThreshold` | int | `4` | Number of unnatural triggerbot reaction incidents within 5 minutes required to punish. |
| `MaxReactionTimeMs` | float | `90.0` | Maximum reaction time in milliseconds considered an unnatural reaction. |
| `EnableNativeTrace` | bool | `true` | Verifies line-of-sight and hitboxes to crosshair targets using server physics raycasts. |
| `EnableSyntheticInputCheck` | bool | `true` | Enables detection of isolated sub-2-tick synthetic click-release injection patterns. |
| `EnableSmokeCheck` | bool | `true` | Excludes shots fired through active smoke volumes from reaction scoring. |

### 4. Aimlock (`Aimlock`)

Detects continuous target locking and crosshair adherence through occluding world geometry.

| Key | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `Enabled` | bool | `true` | Enables or disables the aimlock detection module. |
| `Punishment` | string / int | `"None"` | Punishment applied on detection (`"None"`, `"Kick"`, `"Ban"`). |
| `EvidenceThreshold` | int | `3` | Number of sustained aimlock tracking incidents within 5 minutes required to punish. |
| `TrackingDurationSeconds` | float | `1.0` | Duration in seconds of uninterrupted mechanical tracking required for an incident. |
| `MinimumDistance` | float | `150.0` | Minimum distance in units between player and target to evaluate tracking. |
| `MinimumTargetTravel` | float | `128.0` | Minimum traversal in units the target must move to eliminate stationary false positives. |
| `RequireWallOcclusion` | bool | `true` | Requires world geometry occlusion so visible combat tracking is not flagged. |

### 5. Anti-Aim & Spinbot (`AntiAim`)

Checks for untrusted viewangles, high-speed spinbots, and pitch/yaw jitter patterns.

| Key | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `Enabled` | bool | `true` | Enables or disables the anti-aim detection module. |
| `Punishment` | string / int | `"None"` | Punishment applied on detection (`"None"`, `"Kick"`, `"Ban"`). |
| `EnableUntrustedCheck` | bool | `true` | Validates pitch [-89.5, +89.5], roll [-50, +50], and rejects non-finite NaN/Inf angles. |
| `EnableSpinbotCheck` | bool | `true` | Enables continuous spinbot rotational velocity and alternating yaw jitter detection. |

### 6. Movement Automation (`Movement`)

Detects frame-perfect bunnyhopping, opposite-direction nulls / snap-tap (SOCD), and auto-strafe symmetry.

| Key | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `Enabled` | bool | `true` | Enables or disables movement automation detection. |
| `Punishment` | string / int | `"None"` | Punishment applied on detection (`"None"`, `"Kick"`, `"Ban"`). |
| `EnableBhopCheck` | bool | `true` | Enables frame-perfect bunnyhop landing chain detection. |
| `AutoDisableOnAutoBunnyhoppingConvar` | bool | `true` | Automatically disables bhop detection when `sv_autobunnyhopping 1` is active on server. |
| `BhopStreakThreshold` | int | `10` | Consecutive frame-perfect bunnyhop landings required to flag bhop automation. |
| `EnableNullsCheck` | bool | `true` | Enables airborne Nulls / Snap-Tap (SOCD) opposite-direction switch detection. |
| `NullsStreakThreshold` | int | `64` | Consecutive airborne 0.00ms opposite-direction strafe switches (A <-> D) to flag. |
| `EnableAutoStrafeCheck` | bool | `true` | Enables airborne mechanical strafe symmetry detection. |

### 7. Exploits & Input Integrity (`Exploits`)

Verifies sub-tick integrity, usercmd timestamps, input consistency, and rapid name spamming.

| Key | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `Enabled` | bool | `true` | Enables or disables exploit and input integrity detection. |
| `Punishment` | string / int | `"None"` | Punishment applied on detection (`"None"`, `"Kick"`, `"Ban"`). |
| `EnableSubtickTamperCheck` | bool | `true` | Detects extreme `CSubtickMoveStep` fraction and timestamp tampering. |
| `EnableDesubtickCheck` | bool | `true` | Detects desubtick modifications when fractional move timing is flattened to 0.0. |
| `EnableInvalidInputCheck` | bool | `true` | Verifies movement buttons against recorded sub-tick movement records. |
| `EnableNameChangerCheck` | bool | `true` | Detects rapid name changing spam (>= 5 name changes within 60 seconds). |
| `EnableMouselessAimCheck` | bool | `true` | Detects viewangle rotation (>= 1.0 deg) with zero mouse delta movement before firing. |
| `EnableForgedCommandCheck` | bool | `true` | Detects identical repeated sub-tick fractional timings and zero-tick attack history. |

### 8. Client ConVar Scanner (`CVarScanner`)

Periodically queries client console variables to prevent forbidden local visual overrides.

| Key | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `Enabled` | bool | `true` | Enables or disables client ConVar verification scanning. |
| `Punishment` | string / int | `"None"` | Punishment applied on detection (`"None"`, `"Kick"`, `"Ban"`). |
| `InitialScanDelaySeconds` | float | `15.0` | Delay in seconds after a player connects before running their first CVar query scan. |
| `ScanIntervalSeconds` | float | `90.0` | Interval in seconds between recurring CVar query scans per player. |
| `CheckDrawOtherModels` | bool | `true` | Flags wireframe model override values (`r_drawothermodels 2`). |
| `CheckMatWireframe` | bool | `true` | Flags wireframe material values (`mat_wireframe != 0`). |
| `CheckSvCheats` | bool | `true` | Flags client reporting local cheats enabled (`sv_cheats 1`). |
| `CheckHostTimescale` | bool | `true` | Flags local timescale speedhack values (`host_timescale != 1.0`). |
| `CheckFogEnable` | bool | `true` | Flags disabling environmental fog (`fog_enable 0`). |
| `CheckPredictionAndLagComp` | bool | `true` | Verifies client prediction settings (`cl_lagcompensation`, `cl_predict`). |

### 9. DLL Injection & Event Scanner (`DllInjection`)

Scans connected clients for illicit event subscriptions commonly registered by internal cheats.

| Key | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `Enabled` | bool | `true` | Enables or disables client event subscription scanning. |
| `Punishment` | string / int | `"None"` | Punishment applied on detection (`"None"`, `"Kick"`, `"Ban"`). |
| `InitialScanDelaySeconds` | float | `10.0` | Delay in seconds after player connects before initial event scan. |
| `ScanIntervalSeconds` | float | `120.0` | Interval in seconds between recurring scans per player. |

### 10. Server-Side Anti-WallHack (`AntiWallHack`)

Controls server-side network entity transmission (`CheckTransmit`), withholding occluded enemy pawns.

| Key | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `Enabled` | bool | `true` | Enables or disables server-side network entity occlusion. |
| `ShadowMode` | bool | `false` | When true, executes all raycasts and logging without suppressing network packets. |
| `CornerPrediction` | bool | `true` | Enables velocity and input lead prediction to prevent pop-in around corners. |
| `CornerPredictionSeconds` | float | `0.12` | Base prediction lookahead horizon in seconds for corner peeking. |
| `PingAdaptivePrediction` | bool | `true` | Dynamically scales corner prediction with player network ping. |
| `TargetPrediction` | bool | `true` | Extrapolates enemy velocity to compensate for peeker's advantage. |
| `SmokeOcclusion` | bool | `true` | Occludes enemy pawns completely blocked by dense smoke grenade volumes. |
| `SmokeRadius` | float | `145.0` | Radius in units of smoke grenade occlusion spheres. |
| `CombatReveal` | bool | `true` | Immediately reveals shooting enemies within audible distance. |
| `CombatRevealDistance` | float | `1000.0` | Maximum audible reveal distance in units for weapon gunfire. |
| `EnableCulling` | bool | `true` | Enables back-angle FOV and distance culling to conserve raycast budgets. |
| `TransmitCVarMode` | string | `"1"` | `sv_enable_donttransmit` mode: `"1"` (explicit deletion), `"0"` (dormancy), or `"preserve"`. |
| `VisibleGraceTicks` | int | `12` | Number of ticks an enemy remains visible after breaking line of sight to prevent flickering. |
| `MaxTracesPerTick` | int | `2048` | Maximum number of physics ray traces permitted per server tick. |

---

## Integrations & Moderation Systems

### Cumulative Conviction Engine (`Conviction`)

Aggregates evidence across multiple detection modules over time using exponential decay. When sustained suspicion exceeds the threshold, automated conviction is triggered.

| Key | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `Enabled` | bool | `true` | Enables or disables the cumulative conviction engine. |
| `Punishment` | string / int | `"Ban"` | Punishment executed when conviction threshold is reached (`"None"`, `"Kick"`, `"Ban"`). |
| `ConvictionThreshold` | float | `10.0` | Cumulative score required to trigger conviction action. |
| `HalfLifeMinutes` | float | `10.0` | Evidence decay half-life in minutes (suspicion decays over time). |
| `MinimumDistinctModules` | int | `2` | Minimum distinct modules required to apply cross-module synergy multiplier. |

### Staff In-Game Alerts (`StaffAlerts`)

Sends private in-game chat messages directly to online administrators when a player is detected or convicted.

| Key | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `Enabled` | bool | `true` | Enables or disables private in-game staff alerts. |
| `PermissionFlag` | string | `"@css/generic"` | Permission required to receive staff alerts (e.g. `"@css/generic"`, `"@css/ban"`, `"@css/root"`). |
| `CooldownSeconds` | float | `10.0` | Minimum cooldown in seconds between alerts for the same suspect to prevent chat spam. |

### Incident Telemetry (`Telemetry`)

| Key | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `DumpIncidents` | bool | `true` | Writes structured incident JSON dumps to disk for staff review and demo playback. |
| `IncidentsDirectory` | string | `"logs/aegisac/incidents"` | Directory where incident JSON files are stored. |

### Discord Webhook Alerts (`Discord`)

| Key | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `Enabled` | bool | `false` | Enables or disables Discord webhook embeds. |
| `WebhookUrl` | string | `""` | Discord webhook URL destination. |

---

## Example `config.jsonc`

```jsonc
{
  "AegisAC": {
    "Prefix": "AegisAC",
    "ServerId": "cs2-server-1",
    "Debug": false,
    "AdminSystem": "Default",
    "KickCommand": "sw_kick #{userid} \"[{prefix}] {reason}\"",
    "BanCommand": "sw_ban #{userid} 0 \"[{prefix}] {reason}\"; kickid {userid} \"[{prefix}] {reason}\"",

    "Aimbot": {
      "Enabled": true,
      "Punishment": "None",
      "TeammatesAreEnemies": false,
      "SnapAngleThreshold": 26.0,
      "SmoothMinimumMovement": 12.0,
      "LowFovSmoothMinimumMovement": 3.5,
      "SmoothMinimumPathEfficiency": 0.96,
      "EnableRcsCheck": true,
      "EnableInhumanAccuracyCheck": true,
      "EnableIrregularBehaviorCheck": true,
      "EnableDoubletap": true,
      "EnableSilentAim": true,
      "EnableAntiAim": true
    },

    "SilentAim": {
      "Enabled": true,
      "Punishment": "None",
      "ScoreThreshold": 10.0,
      "EnableSubtickSilentAim": true,
      "EnableSnapReturn": true,
      "SnapReturnMinimumAngle": 6.0,
      "SnapReturnMaximumSurroundingAngle": 3.5
    },

    "Triggerbot": {
      "Enabled": true,
      "Punishment": "None",
      "EvidenceThreshold": 4,
      "MaxReactionTimeMs": 90.0,
      "EnableNativeTrace": true,
      "EnableSyntheticInputCheck": true,
      "EnableSmokeCheck": true
    },

    "Aimlock": {
      "Enabled": true,
      "Punishment": "None",
      "EvidenceThreshold": 3,
      "TrackingDurationSeconds": 1.0,
      "MinimumDistance": 150.0,
      "MinimumTargetTravel": 128.0,
      "RequireWallOcclusion": true
    },

    "AntiAim": {
      "Enabled": true,
      "Punishment": "None",
      "EnableUntrustedCheck": true,
      "EnableSpinbotCheck": true
    },

    "Movement": {
      "Enabled": true,
      "Punishment": "None",
      "EnableBhopCheck": true,
      "AutoDisableOnAutoBunnyhoppingConvar": true,
      "BhopStreakThreshold": 10,
      "EnableNullsCheck": true,
      "NullsStreakThreshold": 64,
      "EnableAutoStrafeCheck": true
    },

    "Exploits": {
      "Enabled": true,
      "Punishment": "None",
      "EnableSubtickTamperCheck": true,
      "EnableDesubtickCheck": true,
      "EnableInvalidInputCheck": true,
      "EnableNameChangerCheck": true,
      "EnableMouselessAimCheck": true,
      "EnableForgedCommandCheck": true
    },

    "CVarScanner": {
      "Enabled": true,
      "Punishment": "None",
      "InitialScanDelaySeconds": 15.0,
      "ScanIntervalSeconds": 90.0,
      "CheckDrawOtherModels": true,
      "CheckMatWireframe": true,
      "CheckSvCheats": true,
      "CheckHostTimescale": true,
      "CheckFogEnable": true,
      "CheckPredictionAndLagComp": true
    },

    "DllInjection": {
      "Enabled": true,
      "Punishment": "None",
      "InitialScanDelaySeconds": 10.0,
      "ScanIntervalSeconds": 120.0
    },

    "AntiWallHack": {
      "Enabled": true,
      "ShadowMode": false,
      "CornerPrediction": true,
      "CornerPredictionSeconds": 0.12,
      "PingAdaptivePrediction": true,
      "TargetPrediction": true,
      "SmokeOcclusion": true,
      "SmokeRadius": 145.0,
      "CombatReveal": true,
      "CombatRevealDistance": 1000.0,
      "EnableCulling": true,
      "TransmitCVarMode": "1",
      "VisibleGraceTicks": 12,
      "MaxTracesPerTick": 2048
    },

    "Conviction": {
      "Enabled": true,
      "Punishment": "Ban",
      "ConvictionThreshold": 10.0,
      "HalfLifeMinutes": 10.0,
      "MinimumDistinctModules": 2
    },

    "StaffAlerts": {
      "Enabled": true,
      "PermissionFlag": "@css/generic",
      "CooldownSeconds": 10.0
    },

    "Telemetry": {
      "DumpIncidents": true,
      "IncidentsDirectory": "logs/aegisac/incidents"
    },

    "Discord": {
      "Enabled": false,
      "WebhookUrl": ""
    }
  }
}
```
