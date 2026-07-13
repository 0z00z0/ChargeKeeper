# ChargeKeeper

**Battery care from the system tray** — charge limits, a live battery gauge, and smart standby
control. ChargeKeeper runs on Lenovo ThinkPads today (it requires the Lenovo Power Management
Driver) and is built to support more hardware over time.

> Formerly published as **Lenovo Power Tray**. See
> [Upgrading from Lenovo Power Tray](#upgrading-from-lenovo-power-tray).

Two power features, without opening the slow Lenovo Vantage app:

- **Smart Charge** — battery charge threshold, via the Lenovo Power Manager local-RPC interface
  (the same one Lenovo Vantage uses), through a small native bridge (`LenPower.dll`)
- **Smart Standby** — Modern Standby scheduling, via the `LenovoSmartStandby` Windows service

Left-click the tray icon for a battery dashboard (arc gauge with live % and charge-rate, threshold
tick markers, adjustable start/stop sliders); right-click for quick toggles plus a launch-at-startup
option. The tray icon itself shows a live battery-level arc, colour-coded green/orange/red — or,
optionally, the battery percentage as a number.

### Features

- **Threshold presets** — named charging profiles (**Daily** 60–80%, **Travel** 80–100%) from the
  tray right-click **Presets** submenu.
- **Charge to 100% once (travel override)** — a tray menu item that temporarily lifts the charge
  threshold for one cycle, then auto-restores it once the battery reaches full. Survives an app
  restart mid-charge.
- **Custom low-battery warning** — a toast at a user-set battery % while discharging (Settings).
- **Time remaining / time to full** — the dashboard shows estimated time-to-full (charging) or
  time-remaining (discharging).
- **Battery % history graph** — a persistent graph of recent battery level in the dashboard,
  with selectable time scales.
- **Configurable startup delay** — wait N seconds before the app initialises at sign-in (Settings).
- **Numeric % tray icon** — show the battery percentage as a number instead of the arc gauge
  (toggle in the tray menu and dashboard).
- **Settings persistence + Export/Import** — settings are stored as human-readable JSON at
  `%AppData%\ChargeKeeper\settings.json`. The dashboard's **Settings** section has **Export…** /
  **Import…** buttons (Win32 file dialogs, which work in this elevated app) and an **Open file**
  link. Settings are portable across machines by copying this file; automatic cloud sync is not yet
  implemented.

> ### ⚠️ 100% vibe coded
> This project was written **entirely by an AI assistant ("vibe coded")** through natural-language
> prompting — no line was hand-authored by a human. Treat it accordingly: it works on the author's
> machine, but it drives Lenovo's power-management RPC interface and a Windows service and comes
> with **no warranty**. Read the code before running it elevated on your own hardware.

![WinUI 3](https://img.shields.io/badge/UI-WinUI%203-blue) ![.NET 10](https://img.shields.io/badge/.NET-10-512BD4) ![License: MIT](https://img.shields.io/badge/License-MIT-green)

## Install

**winget** (recommended):

```powershell
winget install 0z00z0.ChargeKeeper
winget upgrade 0z00z0.ChargeKeeper   # update later
```

Or grab `ChargeKeeper-Setup.exe` from the [latest release](https://github.com/0z00z0/ChargeKeeper/releases)
and run it. The installer is **per-user — no admin needed to install** — and offers two options:

- **Run at startup** — auto-starts the app at sign-in. Ticking it asks for elevation once, to
  register a prompt-free elevated logon task.
- **Auto update in background** — a logon task that silently runs `winget upgrade` after each
  sign-in, so the app keeps itself current. Needs no elevation, and only works once the package is
  available from a winget source (see [installer/README.md](installer/README.md)).

The app itself shows a UAC prompt when it launches, since changing the charge threshold / standby
service requires administrator rights.

## Upgrading from Lenovo Power Tray

ChargeKeeper is the same app under a new name. Upgrading is safe and mostly automatic:

- **Settings and battery history migrate automatically.** On first launch, ChargeKeeper moves your
  old `%AppData%\LenovoPowerTray` folder to `%AppData%\ChargeKeeper` — settings, presets, and the
  battery history graph all carry over.
- **The installer cleans up after the old version.** Running the ChargeKeeper installer over an
  existing Lenovo Power Tray install closes the old app, removes its old binaries and scheduled
  tasks, and upgrades in place.
- **winget users: install the new ID once.** The package identity changed, so the old package will
  not auto-upgrade across the rename. Run:
  ```powershell
  winget install 0z00z0.ChargeKeeper
  ```
  and Windows' Apps list will show ChargeKeeper replacing the old entry.

## Requirements

- Windows 10 (1809+) / Windows 11
- Currently a **Lenovo ThinkPad** (built and tested on an X1 Yoga Gen 7) — the charge-limit
  feature talks to Lenovo's power-management driver; support for other vendors is planned
- **Administrator rights** — both features require elevation (the app manifest declares
  `requireAdministrator`)

### Smart Charge prerequisite — Lenovo Power Management Driver

Smart Charge talks directly to the **Lenovo Power Management Driver** (Windows service `PWMGR`,
"Lenovo Power and Battery") — the same service Lenovo Vantage uses under the hood.

**You do not need Lenovo Vantage.** You do need the driver.

It ships as part of the ThinkPad hardware driver package. If your laptop originally shipped with
Windows (or has had a full driver installation) it is almost certainly already present.

To check — run in an elevated PowerShell:

```powershell
Get-Service -Name PWMGR -ErrorAction SilentlyContinue
```

If the service appears (`Running` or `Stopped`), Smart Charge will work. If nothing is returned,
install the driver:

1. Go to your model's driver page on [Lenovo Support](https://support.lenovo.com/) (search your
   model name / serial number, or use
   [PC Support Auto-Detect](https://support.lenovo.com/us/en/solutions/ht003029)).
2. Select **Drivers & Software → category Power Management**.
3. Download and run **"Power Management Driver for Windows 10 and 11 (64-bit)"**.

If the driver is absent, Smart Charge shows as **Unavailable** in the tray and dashboard — the
rest of the app (Smart Standby, auto-start, battery gauge) works fine without it.

### Build prerequisites

- .NET 10 SDK
- A C++ toolset (Visual Studio / Build Tools with **"Desktop development with C++"**) to build the
  native Smart Charge bridge (`native/`) — only needed once
- A sibling clone of [ZeroZeroBrand](https://github.com/0z00z0/ZeroZeroBrand) (shared ZeroZero
  Software branding components — see [Shared components](#shared-components) below)

## Build from source

```powershell
# 0. Clone this repo AND the shared branding library as siblings (the csproj references
#    ..\ZeroZeroBrand\src\ZeroZero.Brand.WinUI by relative path).
git clone https://github.com/0z00z0/ChargeKeeper.git
git clone https://github.com/0z00z0/ZeroZeroBrand.git
cd ChargeKeeper

# 1. Build the native Smart Charge bridge (LenPower.dll). One-time, needs the C++ toolset.
#    build.cmd locates MSVC + MIDL automatically (incl. VS 2026 Insiders).
cd native
.\build.cmd
cd ..

# 2. Build the app (Release output is Authenticode-signed if a cert is set up — see USAGE.md).
#    The csproj copies native\LenPower.dll next to the executable.
dotnet build -c Release

# 3. Run elevated
dotnet run
# or right-click the compiled ChargeKeeper.exe → "Run as administrator"
```

> Smart Standby, the dashboard, and auto-start work without the native bridge. If `LenPower.dll`
> is missing, Smart Charge simply shows as **Unavailable** rather than breaking the app.

A UAC prompt appears on first launch. Enable **Launch at startup** from the right-click menu to
auto-start prompt-free on subsequent boots (via a Task Scheduler logon task).

See **[USAGE.md](USAGE.md)** for full usage, code-signing, troubleshooting, and architecture notes.

## Building the installer

The per-user installer is built with [Inno Setup](https://jrsoftware.org/isinfo.php) and distributed
via winget. See **[installer/README.md](installer/README.md)** for the full release workflow:

```powershell
winget install JRSoftware.InnoSetup     # one-time
cd installer
.\build-installer.ps1              # auto-bumps patch (or pass -Version 1.2.0 explicitly)
```

## External libraries

The app targets the Microsoft stack (.NET, Windows App SDK / WinUI 3, `System.*` runtime
packages). The only **non-Microsoft** dependencies are:

| Library | Author | Purpose | License |
|---------|--------|---------|---------|
| [H.NotifyIcon.WinUI](https://github.com/HavenDV/H.NotifyIcon) | HavenDV | System-tray icon + native context menu for WinUI 3 | MIT |
| [TaskScheduler](https://github.com/dahall/TaskScheduler) | David Hall | Managed wrapper over the Windows Task Scheduler API (auto-start) | MIT |
| [CommunityToolkit.WinUI.Controls.RangeSelector](https://github.com/CommunityToolkit/Windows) | .NET Foundation | Dual-handle range slider (Smart Charge start/stop threshold) | MIT |
| [MQTTnet](https://github.com/dotnet/MQTTnet) | The MQTTnet Project | MQTT client for the Home Assistant integration | MIT |

## Home Assistant (MQTT)

ChargeKeeper can publish to [Home Assistant](https://www.home-assistant.io/) over MQTT using HA's
**auto-discovery** — no YAML on the HA side. When enabled it connects to your broker and creates a
single **ChargeKeeper** device with entities for battery %, charge power (W), on-AC, the Smart Charge
start/stop thresholds, and the adapter rating; an availability topic (with a Last-Will) marks it
offline if the app stops.

It is **off by default** and never touches the network until you both enable it and set a broker
host. Configure it in `%AppData%\ChargeKeeper\settings.json` (create the keys if absent, then restart
ChargeKeeper):

```jsonc
{
  "HomeAssistantEnabled": true,
  "MqttBrokerHost": "homeassistant.local",   // or the broker IP
  "MqttBrokerPort": 1883,
  "MqttUsername": "your-mqtt-user",
  "MqttPassword": "your-mqtt-password",       // stored locally, same as any MQTT client
  "MqttUseTls": false,
  "MqttDiscoveryPrefix": "homeassistant"      // must match HA's MQTT discovery prefix
}
```

The password lives only in your own local settings file. A tray toggle and an in-app config screen
are planned; today configuration is via this file.

## Shared components

The **About** window comes from [ZeroZeroBrand](https://github.com/0z00z0/ZeroZeroBrand)
(`ZeroZero.Brand.WinUI.BrandAboutWindow`, MIT) — the shared branding library used across ZeroZero
Software apps, referenced as a sibling-folder `ProjectReference` (no NuGet package yet). How the
integration works:

- The app supplies data only (`BrandAboutOptions` → `AboutInfo`: name, version, description, repo
  URL, external-library credits); the library owns all chrome, layout, and the brand typeface.
- `OnCheckForUpdates` (`Func<Task<bool>>`) plugs the app's own update flow into the window's
  *Check for Updates* button. ChargeKeeper always returns `false` — it downloads the installer on a
  background task and exits itself when ready, so the window never needs to drive the exit
  (`OnBeforeExit` is therefore left unset).
- CI clones the library **pinned to an exact commit** (see the `ZEROZEROBRAND_REF` step in
  [`.github/workflows/ci.yml`](.github/workflows/ci.yml)); bump the pin deliberately after
  validating a new library version locally.

## Credits & acknowledgements

Smart Charge is the hard part: ThinkPad firmware does **not** expose the battery charge threshold
through WMI, so it has to be driven over the Lenovo Power Manager's local-RPC interface — the same
one Lenovo Vantage uses.

- **[LenPwrCtl](https://github.com/alandau/LenPwrCtl)** by **alandau** (MIT) — the Power Manager RPC
  interface in [`native/pwrmgr.idl`](native/pwrmgr.idl) (endpoint, context handles, and the
  `LpcGetChargeThreshold` / `LpcSetChargeThreshold` procedure layout) was **reverse-engineered by
  this project** and is reused here under its MIT license. This app would not be possible without it.
  Huge thanks. 🙏

The `native/` bridge (`lenpower.c`) is a thin wrapper that exposes two flat exports over that
interface for the managed app to P/Invoke; the interface definition itself is alandau's work.

**Tooling:** the installer is built with **[Inno Setup](https://jrsoftware.org/isinfo.php)** by
Jordan Russell & Martijn Laan (free, with attribution under its license), and distributed via
**[winget](https://github.com/microsoft/winget-cli)** (Microsoft, MIT).

## License

[MIT](LICENSE) © ZeroZero Software ([0z0.xyz](https://0z0.xyz)) — you are free to use, modify,
fork, and redistribute, including commercially, **provided you keep the copyright and license
notice**. See [LICENSE](LICENSE) for the full text.
