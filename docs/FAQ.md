# Frequently Asked Questions (FAQ)

This document answers common operational and architectural questions about AegisAC.

---

### General & Architecture

#### Does AegisAC require players to install any client-side software?
No. AegisAC is completely server-side. It hooks into the CS2 dedicated server engine via the SwiftlyS2 framework and evaluates incoming user commands (`CUserCmd`), viewangle dynamics, physics raycasts, and network entity transmission. Players join and play normally.

#### What operating systems are supported?
AegisAC supports any platform supported by Counter-Strike 2 dedicated servers and the SwiftlyS2 framework, including:
- Linux x86_64 (Ubuntu, Debian, CentOS, AlmaLinux, Rocky Linux)
- Windows Server x86_64 / Windows 10/11 x86_64

#### Does AegisAC support CS2's sub-tick system?
Yes. AegisAC evaluates sub-tick usercmd records directly, including `CSubtickMoveStep` timestamps, fractional press/release timings, historical attack viewangles (`attack1_start_history_index`), and camera angles across sub-tick packets.

---

### Moderation & Configuration

#### What is the difference between punishment modes?
Each module configures an independent punishment action:
- `0` / `"None"` — **Log Only**: Records the detection to server logs, dumps incident JSON telemetry, posts Discord alerts (if enabled), and sends private in-game staff alerts without kicking or banning the player. This is the recommended mode during initial deployment.
- `1` / `"Kick"` (numeric enum `1` in code) / `2` — **Kick**: Immediately disconnects the suspect from the server.
- `2` / `"Ban"` (numeric enum `2` in code) / `3` — **Ban**: Enforces a ban through the configured admin system (`Default`, `T3Admin`, or `Custom`).

*Note: In AegisAC, notification to logs, Discord, and staff chat is automatically active for all detections regardless of punishment setting.*

#### What is the Cumulative Conviction Engine?
The Cumulative Conviction Engine is a cross-module suspicion matrix. Rather than punishing immediately on a single isolated detection from one module, it scores detections over time. Scores decay naturally using a configurable half-life (e.g. 10 minutes), and cross-module synergies are rewarded when a player triggers multiple distinct detection categories (e.g. Aimbot and Triggerbot). When the accumulated score exceeds `ConvictionThreshold`, automated punishment is executed.

#### How do I check active suspects in real time?
Authorized administrators (`@css/root`) can execute:
```text
aegis_suspects
```
This prints the top active suspects, their current decayed score, conviction threshold, and a breakdown of which modules have flagged them.

#### Can I customize the kick and ban commands?
Yes. In `config.jsonc`, you can set `AdminSystem` to `"Default"`, `"T3Admin"`, or `"Custom"`. You can customize `KickCommand` and `BanCommand` templates using the following placeholders:
- `{prefix}` — Configured plugin prefix.
- `{userid}` — CS2 user ID.
- `{slot}` — Player entity slot.
- `{steamid}` — Player 64-bit Steam ID.
- `{name}` — Player display name.
- `{reason}` — Module detection reason.

---

### Anti-WallHack (Fog of War)

#### How does server-side Anti-WallHack work?
Anti-WallHack intercepts the server's entity transmission hook (`CheckTransmit`). If an enemy player is completely occluded by solid world geometry or dense smoke volumes, their network entity is omitted from packet transmission to that client. Because the client never receives the enemy's coordinates, wallhacks and radar ESP cannot render or locate them.

#### What is Shadow Mode?
`ShadowMode` (under `AntiWallHack` in `config.jsonc`) allows server administrators to run the occlusion tracing pipeline and review diagnostic metrics (`awh_status`) without actually withholding entity packets from clients. This enables verifying performance and trace budgets on busy servers before turning on active packet suppression.

#### Does Anti-WallHack cause enemies to pop in unexpectedly?
To prevent pop-in, AegisAC includes corner peeking velocity prediction (`CornerPrediction`), ping-adaptive lookahead (`PingAdaptivePrediction`), target velocity extrapolation (`TargetPrediction`), and a visibility grace period (`VisibleGraceTicks`). If pop-in occurs on your server, increase `CornerPredictionSeconds` and `VisibleGraceTicks` in `config.jsonc`.

#### Will Anti-WallHack work on community or workshop maps?
Yes. Anti-WallHack utilizes native server physics raycasts against the loaded map's BSP/VPHYS collision hulls. It operates automatically across standard competitive maps and custom workshop maps without requiring pre-computed navigation meshes.

---

### Performance & Diagnostics

#### What is the performance impact of AegisAC on a server?
Anti-cheat overhead depends heavily on player count and configuration. To keep performance bounded:
- Anti-WallHack enforces a strict per-tick raycast ceiling (`MaxTracesPerTick`, default 2048) and applies back-angle FOV and distance culling to discard irrelevant traces early.
- High-frequency tracking loops use pre-allocated buffers to minimize runtime heap allocations.
- Actual server CPU load should be measured under full player load using server diagnostic tools.

#### How do I inspect live performance and module telemetry?
1. Enable debug mode in `config.jsonc`:
   ```jsonc
   "Debug": true
   ```
2. Reload configuration:
   ```text
   aegis_reload
   ```
3. Run diagnostic status commands in console:
   - `aegis_status` — Global overview of all modules.
   - `awh_status` — Anti-WallHack trace budget and occlusion pairs.
   - `aimbot_status`, `silentaim_status`, `triggerbot_status`, `aimlock_status`, `antiaim_status`, `movement_status`, `exploits_status`, `dllinjection_status`, `cvar_status`.
