# Installation Guide

This guide walks you through installing, configuring, and verifying **Aegis Anti-Cheat (AegisAC)** on a Counter-Strike 2 dedicated server.

---

## Prerequisites

Before installing AegisAC, ensure your dedicated server meets the following requirements:

- **Game**: Counter-Strike 2 Dedicated Server (Linux x86_64 or Windows Server x86_64).
- **Server Framework**: [SwiftlyS2](https://swiftlys2.net) version **1.1.6** or higher installed in `<server_root>/game/csgo/addons/swiftlys2`.
- **Runtime**: **.NET 10.0 Runtime** installed on the host system (required by SwiftlyS2).
- **Administrative Privileges**: Access to your server filesystem and server console.

---

## Binary Installation

### 1. Download or Build AegisAC

Obtain `AegisAC.dll` from the official repository releases, or compile it from source as described in [Building from Source](#building-from-source).

### 2. Place Plugin Files

1. Navigate to your CS2 server directory:
   ```text
   <server_root>/game/csgo/addons/swiftlys2/plugins/
   ```
2. Create an `AegisAC` folder if it does not already exist:
   ```text
   <server_root>/game/csgo/addons/swiftlys2/plugins/AegisAC/
   ```
3. Copy `AegisAC.dll` into that directory:
   ```text
   <server_root>/game/csgo/addons/swiftlys2/plugins/AegisAC/AegisAC.dll
   ```

### 3. Start the Server

Start or restart your CS2 dedicated server. 

Upon first load, SwiftlyS2 automatically initializes the plugin and creates the default configuration file:
```text
<server_root>/game/csgo/addons/swiftlys2/configs/plugins/AegisAC/config.jsonc
```

---

## Verifying the Installation

Open your server console (or RCON / game console with `@css/root` permission) and run:

```text
sw plugins list
```

AegisAC should appear in the loaded plugins list with status active:
```text
[AegisAC] initialized. Commands: aegis_status aegis_suspects aimbot_status silentaim_status triggerbot_status aimlock_status antiaim_status dllinjection_status cvar_status awh_status aegis_reload
```

You can also run the reload command to verify responsive communication:
```text
aegis_reload
```
Expected output:
```text
[AegisAC] Configuration reloaded successfully.
```

---

## Building from Source

If you want to build the plugin yourself from source:

### 1. Prerequisites for Building
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Git

### 2. Clone the Repository
```bash
git clone https://github.com/ressiws/AegisAC.git
cd AegisAC
```

### 3. Run Automated Tests
Before compiling for deployment, verify that all unit tests pass:
```bash
dotnet test
```

### 4. Publish the Release Build
Execute the publish command targeting .NET 10:
```bash
dotnet publish -c Release
```

The compiled binary and dependencies will be generated in:
```text
src/bin/Release/net10.0/publish/AegisAC.dll
```

Copy `AegisAC.dll` to your server's plugin directory as described above.

---

## Next Steps

- Proceed to the [Configuration Guide](CONFIGURATION.md) to customize detection thresholds, punishment actions, Discord webhooks, and in-game staff alerts.
- Read the [Detection Modules Overview](DETECTION_MODULES.md) for details on what each module observes.
- Refer to the [Troubleshooting Guide](TROUBLESHOOTING.md) if you encounter any setup or runtime issues.
