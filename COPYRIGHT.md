# Copyright & Authors

**Aegis Anti-Cheat (AegisAC) for Counter-Strike 2**  
Copyright (C) 2026 **swisser** & **sevon**

## Authors
- **swisser**: [https://github.com/ressiws](https://github.com/ressiws)
- **sevon**: [https://github.com/victor201202](https://github.com/victor201202)

## Module Registry & Descriptions

### 1. Aimbot Detection (`src/Modules/Aimbot.cs`)
- **Authors**: swisser & sevon (2026)
- **Description**: Inspects user command viewangles, single-tick angular convergence jumps, snap-returns, and smooth trajectory envelopes, correlating damage events (`player_hurt`) and weapon firing (`weapon_fire`) for zero false-positive detection.

### 2. AimMath Utilities (`src/Modules/AimMath.cs`)
- **Authors**: swisser & sevon (2026)
- **Description**: Provides high-performance 3D vector forward projections, angular distance deltas, and finite mathematical validation utilities.

### 3. Shot Correlator (`src/Modules/ShotCorrelator.cs`)
- **Authors**: swisser & sevon (2026)
- **Description**: Reconciles CS2 user commands, server simulation ticks, and weapon firing histories for sub-tick accuracy.

### 4. Triggerbot Detection (`src/Modules/Triggerbot.cs`)
- **Authors**: swisser & sevon (2026)
- **Description**: Performs crosshair raycast targeting and sub-tick reaction time analysis. Flags unnatural automated firing upon target acquisition while compensating for smoke occlusion and holding angles.

---

Licensed under the **Apache License, Version 2.0**.
See [LICENSE](LICENSE) and [NOTICE](NOTICE) for terms.
