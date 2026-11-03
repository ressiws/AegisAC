# Troubleshooting Guide

This guide provides solutions to common issues encountered when deploying, configuring, and operating AegisAC.

---

## 1. Plugin Fails to Load

### Symptoms
- Running `sw plugins list` does not show `AegisAC`.
- Server console reports an assembly load or runtime initialization error.

### Checklist & Fixes
1. **Verify Plugin Directory Structure**:
   Ensure `AegisAC.dll` is located at:
   ```text
   <server_root>/game/csgo/addons/swiftlys2/plugins/AegisAC/AegisAC.dll
   ```
2. **Verify SwiftlyS2 Version**:
   AegisAC requires SwiftlyS2 **>= 1.1.6**. Check your framework version with:
   ```text
   sw version
   ```
3. **Verify .NET 10.0 Runtime**:
   The host machine must have the .NET 10.0 runtime installed. Verify with:
   ```bash
   dotnet --list-runtimes
   ```
   Ensure `Microsoft.NETCore.App 10.0.x` is present.

---

## 2. Anti-WallHack Visual Pop-in or Flickering

### Symptoms
- Players briefly pop into view slightly after rounding a corner instead of before.
- Rapidly peeking an angle causes visible enemy models to flicker.

### Recommended Adjustments (`config.jsonc`)
1. **Increase Corner Lookahead**:
   Increase `CornerPredictionSeconds` from `0.12` to `0.15` or `0.18`:
   ```jsonc
   "AntiWallHack": {
     "CornerPrediction": true,
     "CornerPredictionSeconds": 0.16,
     "PingAdaptivePrediction": true
   }
   ```
2. **Increase Visible Grace Ticks**:
   `VisibleGraceTicks` determines how long an entity remains transmitted after breaking line-of-sight. Increase from `12` to `16` or `20`:
   ```jsonc
   "AntiWallHack": {
     "VisibleGraceTicks": 16
   }
   ```
3. **Check Transmission Mode**:
   Ensure `TransmitCVarMode` is set appropriately:
   - `"1"`: Explicit client-side entity deletion (strictest wallhack neutralization).
   - `"0"`: Standard client entity dormancy (may feel smoother on high-latency connections).

---

## 3. High Server Tick Times / Raycast Load

### Symptoms
- Server frame times spike during crowded 10v10 or 32-player matches.
- Console logs report high trace consumption.

### Recommended Adjustments
1. **Lower Max Traces Per Tick**:
   Cap the total raycasts executed per tick across all players:
   ```jsonc
   "AntiWallHack": {
     "MaxTracesPerTick": 1024
   }
   ```
2. **Enable Angle and Distance Culling**:
   Ensure back-angle FOV and distance culling are active:
   ```jsonc
   "AntiWallHack": {
     "EnableCulling": true
   }
   ```
3. **Use Diagnostic Command**:
   Run `awh_status` in server console to view current tick trace usage and occluded pair counts (`"Debug": true` must be enabled).

---

## 4. In-Game Staff Alerts Not Received

### Symptoms
- Cheater detections appear in server console or Discord, but online staff members receive no in-game chat alerts.

### Checklist & Fixes
1. **Verify Permission Flag**:
   In `config.jsonc`, check `StaffAlerts.PermissionFlag`:
   ```jsonc
   "StaffAlerts": {
     "Enabled": true,
     "PermissionFlag": "@css/generic"
   }
   ```
   Verify that your admin groups or players have this permission assigned in SwiftlyS2's permissions system.
2. **Alert Rate-Limiting**:
   `StaffAlerts.CooldownSeconds` (default: 10.0s) limits notifications for the same suspect to prevent chat spam. Multiple rapid detections on the same player within the cooldown window will not trigger repeated chat messages.
3. **Self-Alert Suppression**:
   Administrators will not receive staff alerts for detections triggered by their own client.

---

## 5. Command Permission Denied

### Symptoms
- Executing `aegis_status` or `aegis_reload` returns `Access Denied` or does not respond.

### Checklist & Fixes
- All administrative commands in AegisAC are secured with the `@css/root` permission flag.
- Ensure the executing player or console user has the `@css/root` role assigned in SwiftlyS2 administrator configurations.
- Server console (RCON or host terminal) always possesses full execution authority.

---

## 6. Discord Webhooks Not Firing

### Symptoms
- Detections occur, but no message is posted to your Discord channel.

### Checklist & Fixes
1. **Verify Discord Config**:
   Confirm `Discord.Enabled` is `true` and the URL is populated:
   ```jsonc
   "Discord": {
     "Enabled": true,
     "WebhookUrl": "https://discord.com/api/webhooks/..."
   }
   ```
2. **Test Outbound Connectivity**:
   Ensure your server host has outbound HTTPS access to `discord.com`. Test from the server shell:
   ```bash
   curl -I https://discord.com
   ```
3. **Check Server Logs**:
   Look for HTTP error codes (e.g. 404 for invalid webhook URL, 429 for Discord rate limiting).

---

## 7. Managing Potential False Positives

### Recommended Calibration Workflow
1. **Initial Deployment**:
   Set `Punishment: "None"` across all modules during initial deployment.
2. **Review Incident Telemetry**:
   When `Telemetry.DumpIncidents: true` is enabled, incident details are saved to `logs/aegisac/incidents/*.json`. Inspect these files to review evidence metrics before enabling automated kicks or bans.
3. **Leverage the Conviction Engine**:
   Rather than setting individual sensitive modules to `Ban`, leave them on `None` and configure `Conviction.Punishment: "Ban"` with a balanced `ConvictionThreshold` (default `10.0`). This ensures players are only punished after accumulating sustained, multi-module evidence.
4. **Monitor Live Suspects**:
   Use `aegis_suspects` in the server console to inspect players currently accumulating suspicion scores and review which modules contributed to their score.
