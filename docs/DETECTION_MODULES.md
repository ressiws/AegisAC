# Detection Modules Overview

AegisAC provides server-side detection modules that analyze player input commands (`CUserCmd`), viewangle dynamics, weapon firing patterns, movement states, client configuration, and physics raycasts.

This document describes what each module detects, what it reports to server logs, and how it is configured from a server administrator's perspective.

---

## Implemented Modules

### 1. Aimbot Detection (`Aimbot`)
- **What it detects**:
  - Blatant angular snaps where player crosshair snaps onto a target immediately before firing.
  - Mechanical trajectory smoothing that exhibits artificial path efficiency across consecutive ticks.
  - Recoil Compensation (RCS) macros and automated spray control scripts that replicate weapon recoil tables with robotic precision.
  - Sustained inhuman accuracy and irregular kill behavior (e.g. repeated mid-air sniper shots at extended ranges).
  - Rapid-fire and doubletap exploits that attempt to fire weapons faster than permitted by the weapon's base cycle time.
- **What it reports**: Log entries prefixed with `[Aimbot]`, including suspect name, SteamID, detection category (`SNAP_AIM`, `SMOOTH_AIM`, `RCS_MACRO`, `INHUMAN_ACCURACY`, `DOUBLETAP`), and angular or timing evidence.
- **Admin Guidance**: Keep `Punishment` set to `None` (Log Only) during initial setup to observe normal player variance on your server.

---

### 2. Silent Aim & Snap-Return (`SilentAim`)
- **What it detects**:
  - Sub-tick camera and shot vector discrepancies, identifying situations where weapon damage vectors or historical attack viewangles diverge from the client's rendered base camera orientation.
  - Post-shot 1-tick snap returns, where an aimbot snaps to an enemy on the exact tick of bullet release and instantly returns to the previous viewing angle on the following user command.
- **What it reports**: Log entries tagged as `SILENT_AIM` or `SNAP_RETURN` indicating angular divergence and sub-tick packet verification.
- **Admin Guidance**: Highly reliable against un-smoothed silent aim routines. Configurable point accumulation allows tuning sensitivity before taking punitive action.

---

### 3. Triggerbot & Synthetic Input (`Triggerbot`)
- **What it detects**:
  - Inhuman reaction times where weapon discharge occurs within an impossibly brief interval (e.g. ≤90ms) after an enemy enters the crosshair hitbox.
  - Synthetic input patterns, catching automated mouse clicks injected directly into usercmds without human press-and-hold durations.
  - Verifies target visibility with server-side physics raycasts and excludes shots through active smoke grenade volumes.
- **What it reports**: Incident logs recording measured reaction latency in milliseconds, target bone hit, and verification evidence.
- **Admin Guidance**: Uses a sliding evidence count window (`EvidenceThreshold`) to require multiple unnatural reactions before punishing, protecting against lucky pre-fires.

---

### 4. Aimlock (`Aimlock`)
- **What it detects**:
  - Mechanical crosshair tracking of moving enemy targets behind occluding world geometry (walls, boxes, doors).
  - Requires the target to be actively moving across the screen while behind solid walls, distinguishing intentional wall-tracking from coincidentally aiming near a doorway.
- **What it reports**: Log entries recording tracking duration (seconds), target distance, and occlusion status.
- **Admin Guidance**: Requires `RequireWallOcclusion: true` to prevent false positives during regular visible gunfights.

---

### 5. Anti-Aim & Spinbot (`AntiAim`)
- **What it detects**:
  - Untrusted viewangles violating server bounds (e.g. pitch exceeding [-89.5, +89.5], roll exceeding [-50, +50], or invalid NaN/infinite floats).
  - High-velocity spinbots exhibiting rotational speeds beyond human capabilities across multiple consecutive ticks.
  - Alternating pitch/yaw jitter patterns commonly used by rage cheats to desynchronize hitboxes.
- **What it reports**: Flags categorized as `ANTI_UNTRUSTED`, `SPINBOT`, or `YAW_JITTER`.
- **Admin Guidance**: Untrusted angle checks can safely be set to `Kick` or `Ban` immediately, as standard game clients cannot generate out-of-bounds angles under normal gameplay conditions.

---

### 6. Movement Automation (`Movement`)
- **What it detects**:
  - Frame-perfect bunnyhopping chains (landing and jumping on the identical tick continuously). Automatically pauses detection when `sv_autobunnyhopping 1` is active on the server.
  - Directional Nulls / Snap-Tap (SOCD) macros, catching airborne 0.00ms opposite-direction strafe reversals (e.g., instant A-to-D key transitions without neutral release).
  - Auto-strafe scripts exhibiting mechanical yaw and strafe velocity symmetry in mid-air.
- **What it reports**: Incident entries recording streak counts, ground ticks, and strafe timing.
- **Admin Guidance**: Set `BhopStreakThreshold` (default 10) according to your server type; competitive servers may use lower thresholds while surf/kz servers should configure or disable accordingly.

---

### 7. Exploits & Input Integrity (`Exploits`)
- **What it detects**:
  - Subtick manipulation and extreme fraction tampering in `CSubtickMoveStep` records.
  - Desubtick modifications where client input scripts strip fractional sub-tick timings to 0.0.
  - Input consistency violations between movement button masks and sub-tick packet records.
  - Rapid name changer spam (frequently changing display name to evade manual moderation).
  - Mouseless aim (significant viewangle changes without recorded physical mouse movement deltas before firing).
  - Forged attack commands with identical fractional timings.
- **What it reports**: Flags identifying specific protocol anomalies (`SUBTICK_TAMPER`, `DESUBTICK`, `NAME_CHANGER`, `MOUSELESS_AIM`, `FORGED_CMD`).
- **Admin Guidance**: Detects protocol modifications and cheat-specific packet tampering directly at the network/usercmd interface.

---

### 8. Client ConVar Scanner (`CVarScanner`)
- **What it detects**:
  - Periodic client console variable query scans verifying clients are not using client-side rendering exploits.
  - Inspects variables including `r_drawothermodels 2` (wireframe wallhacks), `mat_wireframe`, `sv_cheats 1`, `host_timescale != 1.0` (speedhack), `fog_enable 0`, and prediction settings (`cl_lagcompensation`, `cl_predict`).
- **What it reports**: Query response mismatches showing the flagged ConVar, expected value, and reported client value.
- **Admin Guidance**: Executes on a configurable timer (`InitialScanDelaySeconds` and `ScanIntervalSeconds`) to continuously monitor connected players.

---

### 9. DLL Injection & Event Scanner (`DllInjection`)
- **What it detects**:
  - Periodically audits connected clients for event listener subscriptions associated with internal cheat hooks and injected libraries.
- **What it reports**: Flagged event identifiers and client response status.
- **Admin Guidance**: Operates transparently in the background on a repeating interval (`ScanIntervalSeconds`).

---

### 10. Server-Side Anti-WallHack (`AntiWallHack`)
- **What it detects & prevents**:
  - Prevents wallhacks and radar ESP by controlling network entity transmission (`CheckTransmit`).
  - Enemy players behind solid walls and occluding geometry are omitted from network packets sent to clients who have no legitimate line of sight.
  - Features corner peeking prediction, ping-adaptive lead compensation, target velocity extrapolation, volumetric smoke grenade occlusion, and gunshot combat reveal.
  - Includes a `ShadowMode` setting allowing server owners to evaluate occlusion decisions and performance before actively withholding packets.
- **What it reports**: Occlusion statistics, visible transitions, and trace execution counts via `awh_status`.
- **Admin Guidance**: Detailed tuning options for trace budgets (`MaxTracesPerTick`) and peek tolerances (`CornerPredictionSeconds`, `VisibleGraceTicks`) allow optimizing for competitive integrity and server performance.

---

### 11. Cumulative Conviction Engine (`Conviction`)
- **What it detects & prevents**:
  - Aggregates suspicion points across multiple detection modules over time.
  - Applies time-decay (configurable half-life) so older, isolated incidents fade away.
  - Employs cross-module synergy multipliers when a suspect is flagged across different module categories (e.g. triggering both Triggerbot and Aimbot).
  - Triggers automated punishments when a player's cumulative score crosses `ConvictionThreshold`.
- **What it reports**: Real-time suspect scores and conviction summaries inspectable via `aegis_suspects`.
- **Admin Guidance**: Allows running individual detection modules in Log Only mode while still automatically punishing players who repeatedly trigger multiple modules over a match.

---

### 12. Staff In-Game Alerts (`StaffAlerts`)
- **What it does**:
  - Delivers stealth in-game chat messages directly to connected staff members who have the configured permission flag (e.g. `@css/generic`, `@css/root`).
  - Regular players cannot see these notifications.
  - Includes suspect name, detection type, evidence summary, and rate-limiting cooldown per suspect to avoid chat spam.
- **Admin Guidance**: Configure `PermissionFlag` to match your administrative permission group in SwiftlyS2.

---

## Planned Modules (Future Roadmap)

The following capabilities are in active development or planned for future releases, and are **not yet implemented** in the current production release:

1. **Machine Learning Behavioral Clustering**: Unsupervised offline clustering of player mouse trajectories against competitive baseline profiles.
2. **Server-Side Grenade Trajectory Verification**: Validation of grenade throw vectors and jump-throw consistency against physics limits.
3. **Automated Demo Marker Integration**: Automatic recording of tick markers within server GOTV / CSTV demos when high-confidence detections occur.
