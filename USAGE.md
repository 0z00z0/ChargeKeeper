# ChargeKeeper

Battery care from the system tray — charge limits, a live battery gauge, and smart standby
control. Runs on ThinkPad laptops today (formerly published as **Lenovo Power Tray**); built to
support more hardware over time.

## Features

| Feature | Mechanism | Admin required |
|---------|-----------|----------------|
| **Smart Charge** | Lenovo Power Manager RPC via `LenPower.dll` bridge | ✓ |
| **Smart Standby** | `LenovoSmartStandby` Windows service | ✓ |

## Running

The app manifest declares `requireAdministrator`.  A UAC prompt appears on the
first launch; auto-start via Task Scheduler is prompt-free on subsequent boots.

```powershell
# From an elevated terminal
dotnet run
# or right-click the compiled .exe → "Run as administrator"
```

### Command-line switches

| Switch | Purpose |
|--------|---------|
| `/debug [on\|off]` | **Command, not a launch mode.** Turns **crash-dump capture** on or off (WER LocalDumps → `%AppData%\ChargeKeeper\dumps`), then exits — it never starts the tray. Off by default on release builds so a shipped app never quietly writes minidumps of itself into your profile; debug builds arm it regardless. |
| `--watchdog-relaunch` | Internal. Used by the `ChargeKeeper Watchdog` scheduled task's 5-minute probe — not meant to be typed by hand. |
| `--auto-relaunch` | Internal. Set when the app restarts itself after a GPU-reset teardown. |

`/debug` **persists** your choice as a marker file — `%AppData%\ChargeKeeper\crash-dumps-armed.marker`,
whose mere presence means "capture is on" — and applies it to the registry immediately, then the
process exits. It does not matter whether the tray app is already running: dump capture is governed
by a machine-wide key, so the change takes effect at once and nothing needs restarting.

The choice is a marker file rather than a setting in `settings.json` for a specific reason. The
running tray app keeps its settings in memory and rewrites the **whole** file whenever it saves, so a
flag stored there would be clobbered by a stale in-memory copy: arm capture with `/debug`, toggle any
preset in the tray, and the flag would be silently reset — leaving the next boot disarmed, which is
exactly the reboot-to-reproduce case the switch exists for. A marker file has no other writers, so
nothing the tray does can reach it. (It is the same pattern as the watchdog's own
`watchdog-hold.marker` next to it.)

```powershell
ChargeKeeper.exe /debug       # arm capture — survives reboots, sign-in autostart, and app restarts
ChargeKeeper.exe /debug off   # disarm again
```

Each invocation shows a UAC prompt (the app is `requireAdministrator`, and the registration lives in
**HKLM**) and reports what it did to `%AppData%\ChargeKeeper\app.log` — there is no console output,
because the exe is a windowed app.

Because the choice is stored rather than read from the command line, **every** way the app can start
— your own launch, the `ChargeKeeper AutoStart` logon task (which passes no arguments), a watchdog
probe, a self-heal relaunch — reads the same answer and re-asserts it. So dumps armed with `/debug`
stay armed across the reboot you need to reproduce the crash, and are not disarmed behind your back
by a probe five minutes later.

The HKLM key outlives the process, so "off" is not merely "don't arm": a run with capture disabled
actively **removes** the registration. One `/debug` session therefore cannot leave a machine dumping
forever.

> Disarming also removes the shared `...\Windows Error Reporting\LocalDumps` key when nothing else
> is registered under it. That key's mere presence turns dump collection on **machine-wide**, for
> every application — arming ours creates it as a side effect, so leaving it behind would have
> every other crashing app on the machine writing minidumps into your profile. It is left alone
> whenever another app is still registered there.

## Tray interactions

| Input | Action |
|-------|--------|
| Left-click | Toggle the battery dashboard popup |
| Right-click | Context menu with live-state toggles |
| Click away from popup | Auto-dismisses the dashboard |

### Context menu items
- **Smart Charge** — enable/disable the charge threshold  
- **Presets** — submenu of named threshold profiles: **Daily** (60–80 %) and **Travel** (80–100 %).
  Selecting one enables Smart Charge and applies those thresholds; the active preset is checkmarked  
- **⚡ Charge to 100 % once** — travel override: saves the current threshold, disables it so the
  battery charges fully, then auto-restores the saved threshold on the next Charging → full
  transition. While active the item reads **✕ Cancel charge override**; the override state persists
  across an app restart  
- **Smart Standby** — start/stop the `LenovoSmartStandby` service  
- **Numeric % icon** — toggle the tray icon between the arc gauge and a numeric percentage  
- **Launch at startup** — add/remove the Task Scheduler auto-start entry  
- **Exit**

## Dashboard popup
Appears bottom-right above the taskbar.  Closes on focus loss.  Refreshes every 5 s.

- Circular arc gauge: charge %, colour-coded green > 50 %, orange ≤ 50 %, red ≤ 20 %
- Amber tick marks on the arc at the Smart Charge start% and stop% positions (visible when Smart
  Charge is enabled with valid thresholds)
- Power source (AC / Battery) and charge/drain rate in watts
- **TIME stat** — time-to-full when charging, time-remaining when discharging; shows **—** when the
  charge rate is negligible
- **Battery % history graph** — a graph of battery level over a selectable time span,
  colour-coded like the gauge, persisted across restarts
- **Smart Charge badge** — shows current thresholds; expands to reveal Start/Stop sliders when
  Smart Charge is enabled. Sliders are constrained (≥ 5% gap); **Apply** writes the new thresholds
  immediately via `LenSetChargeThreshold`
- **Smart Standby badge** — shows service running state
- **Settings expander** — a collapsible section for app options (see [Settings](#settings))

The tray icon is a live battery-level arc (same colour scheme as the gauge), updated on every
`Battery.ReportUpdated` event. It can be switched to a **numeric percentage** instead of the arc
via the **Numeric % icon** tray toggle or the dashboard Settings.

A **custom low-battery warning** raises a toast when the battery drops to a configurable % while
discharging. It has 5 % hysteresis so it won't repeat-fire as the level hovers around the threshold.
Configured in Settings (on/off toggle + threshold).

## Settings

Settings persist to `%AppData%\ChargeKeeper\settings.json` — a roaming, human-readable JSON file.

> **Upgrading from Lenovo Power Tray?** On first launch the app automatically moves the old
> `%AppData%\LenovoPowerTray` folder to `%AppData%\ChargeKeeper`, so settings and battery history
> carry over.

The dashboard's collapsible **Settings** expander exposes:

| Setting | Effect |
|---------|--------|
| **Low-battery warning** | On/off toggle + threshold % for the discharge toast (5 % hysteresis) |
| **Startup delay** | Seconds to wait before the app initialises at sign-in — keeps it off the critical path when many elevated apps start at once |
| **Numeric % icon** | Render the tray icon as a number instead of the arc gauge |

It also offers:

- **Export… / Import…** — back up or load the settings file. These use classic Win32 file dialogs
  because the app runs elevated, where the WinRT pickers are unreliable
- **Open file** — reveals `settings.json` in Explorer

The file is portable by copy across machines. Automatic cloud sync is not yet implemented (a planned
future option).

## Building

```powershell
dotnet build -c Release
```

Output: `bin\Release\net10.0-windows10.0.26100.0\win-x64\`

## Installing & updating

End users install via **winget** (`winget install 0z00z0.ChargeKeeper`) or by running
`ChargeKeeper-Setup.exe` from the GitHub releases. The installer is a **per-user Inno Setup**
package — it installs to `%LocalAppData%` with **no admin prompt**, adds a Start-menu shortcut, and
offers two checkboxes: **"Run at startup"** and **"Auto update in background"**. Updates otherwise
come from `winget upgrade`.

The **"Auto update in background"** option creates a non-elevated logon task (`ChargeKeeper AutoUpdate`)
that runs `winget upgrade` silently after each sign-in — so the app self-updates without the user
running anything, *provided the package is reachable from a winget source* (public `winget-pkgs`
submission or a local source). It needs no elevation to set up.

The app stays `requireAdministrator`, so it elevates only at runtime. The single place the installer
elevates is when "Run at startup" is ticked, to register a `RunLevel=Highest` logon task
(`ChargeKeeper AutoStart`) — the same task the in-app "Launch at startup" toggle manages.

Upgrading over an existing **Lenovo Power Tray** install works in place: the installer closes the
old `LenovoTray.exe`, deletes its stale binaries and scheduled tasks, and keeps the recorded
install folder. Note that the winget package identity changed with the rename, so winget users
run `winget install 0z00z0.ChargeKeeper` once; the old package ID will not upgrade across the rename.

Building and releasing the installer (needs `winget install JRSoftware.InnoSetup`) is documented in
**[installer/README.md](installer/README.md)**:

```powershell
cd installer
.\build-installer.ps1              # auto-bumps patch (e.g. 1.2.0 → 1.2.1)
.\build-installer.ps1 -Version 1.3.0   # explicit override
```

## Code signing

The app is Authenticode-signed so the UAC elevation prompt shows a verified
publisher (`ZeroZero Software`) instead of *"Unknown Publisher"*.

**One-time setup** — create and trust a self-signed code-signing certificate:

```powershell
.\scripts\sign.ps1 -Setup
```

This creates a 5-year cert in `Cert:\CurrentUser\My` and registers it as a trusted
root + trusted publisher for the current user (no admin required).

**Signing happens automatically** on every `Release` build via the `SignOutput`
MSBuild target, which calls `scripts\sign.ps1`. To sign manually:

```powershell
.\scripts\sign.ps1                                   # signs the latest Release exe
.\scripts\sign.ps1 -Path path\to\ChargeKeeper.exe    # sign a specific file
```

Verify a signature:

```powershell
Get-AuthenticodeSignature .\bin\Release\net10.0-windows10.0.26100.0\win-x64\ChargeKeeper.exe
```

**Notes**
- The certificate's private key lives only in the Windows cert store — no `.pfx`
  is written to the project folder, so nothing secret is committed.
- To use a real CA-issued certificate, import it into `Cert:\CurrentUser\My` with
  subject `CN=ZeroZero Software` (or pass `-Subject` to `scripts\sign.ps1`); signing picks it up
  by subject automatically — no other change needed.
- A self-signed cert is trusted only on machines where `-Setup` has been run.
  Other machines will still show "Unknown Publisher" unless the public cert is
  imported into their trusted stores.

## Auto-start

`HKCU\Run` entries for elevated apps trigger a UAC prompt on every boot.
`TaskSchedulerHelper` instead creates a Task Scheduler logon-trigger task with
`RunLevel = Highest` — elevated, prompt-free.

## Smart Charge (battery charge threshold)

### Prerequisite: Lenovo Power Management Driver

Smart Charge requires the **Lenovo Power Management Driver** (Windows service `PWMGR`,
"Lenovo Power and Battery"). It ships with the ThinkPad hardware driver package and is
almost certainly already present if the laptop has ever had a full driver installation.

To verify (elevated PowerShell): `Get-Service -Name PWMGR -ErrorAction SilentlyContinue`

If the service is missing, download **"Power Management Driver for Windows 10 and 11
(64-bit)"** from [Lenovo Support](https://support.lenovo.com/) for your model. Without
it, Smart Charge shows as **Unavailable** — the rest of the app works fine.

### How Smart Charge works

ThinkPad firmware does **not** expose the battery charge threshold through the
Lenovo BIOS WMI provider (`Lenovo_BiosSetting`) — that class has no charge-threshold
key on these machines. The threshold is owned by the **Lenovo Power Manager**, which
Lenovo Vantage drives over a local-RPC (`ncalrpc`) interface.

`ChargeThresholdService` reaches it through a small native bridge, **`LenPower.dll`**
(sources in `native/`), which marshals the Power Manager's RPC calls via a
MIDL-generated client stub. The managed side P/Invokes two flat exports:

- `LenGetChargeThreshold(battery, out capable, out enabled, out start, out stop)`
- `LenSetChargeThreshold(battery, start, stop)`  (start = stop = 0 disables, i.e. charges to 100 %)

### Building the native bridge

`LenPower.dll` is **not** built by `dotnet build`; build it once with the VC++
toolset (any edition with "Desktop development with C++"):

```powershell
cd native
.\build.cmd        # runs MIDL + cl, emits LenPower.dll
```

The csproj copies `native\LenPower.dll` next to the app on build. To verify the
RPC path against your hardware, run (from an **elevated** shell):

```powershell
cd native
.\test-read.ps1    # prints the live capable/enabled/start/stop values
```

If the dashboard shows *"Unavailable"*, the bridge couldn't reach the Power Manager
(DLL missing, driver not installed, or not running elevated); *"Not supported"* means
the firmware reported the battery as not threshold-capable.

## Project structure

```
ChargeKeeper/
├── App.xaml / .cs                   — Tray icon lifetime, coordinates dashboard
├── MainWindow.xaml / .cs            — Invisible 1×1 host window (keeps WinUI 3 alive)
│
├── Services/                        — App services + static facades over the active vendor module
│   ├── VendorCatalog.cs             — Selects the active vendor power module (Lenovo today)
│   ├── ChargeThresholdService.cs    — Facade → IChargeThresholdProvider of the active vendor
│   ├── StandbyService.cs            — Facade → IStandbyProvider of the active vendor
│   ├── ToastService.cs              — Windows toast notifications (charge complete, AC connected)
│   └── UpdateCheckService.cs        — GitHub releases API check; notifies if a newer version exists
│
├── Vendors/                         — Vendor-specific power management, one project per vendor
│   ├── Abstractions/                — ChargeKeeper.Vendors.Abstractions: the vendor-neutral contract
│   │                                  (IVendorPowerModule, IChargeThresholdProvider, IStandbyProvider)
│   └── Lenovo/                      — ChargeKeeper.Vendors.Lenovo: LenPower.dll P/Invoke (Power
│                                      Manager RPC) + LenovoSmartStandby service control
│
├── native/                          — Native bridge (built separately via build.cmd)
│   ├── pwrmgr.idl                   — RPC interface definition (MIDL input)
│   ├── lenpower.c                   — Flat C exports wrapping the RPC client stub
│   ├── build.cmd                    — MIDL + cl build → LenPower.dll
│   └── test-read.ps1                — Elevated manual read check
│
├── installer/                       — Per-user Inno Setup installer + winget manifests
│   ├── ChargeKeeper.iss             — Inno script (per-user, optional Run-at-startup task)
│   ├── build-installer.ps1          — publish + compile → Output\ChargeKeeper-Setup.exe
│   └── winget/                      — winget manifests (0z00z0.ChargeKeeper)
│
├── brand/                           — Brand assets
│   └── chargekeeper-icon.svg        — Authoritative vector of the "Guarded Battery" app icon
│
├── Features/                        — Toggleable capabilities behind one interface
│   ├── IToggleFeature.cs            — Name / IsEnabled / SetEnabled abstraction
│   └── PowerFeatures.cs             — SmartCharge / SmartStandby / AutoStart implementations
│
├── UI/                              — Visual layer
│   ├── DashboardWindow.xaml / .cs   — Battery popup, arc gauge, status badges
│   └── TrayMenu.cs                  — Builds the right-click menu from the feature list
│
├── Helpers/                         — Infrastructure utilities
│   ├── AppColors.cs                 — Shared colour constants and pre-allocated brushes
│   ├── IconGenerator.cs             — Static brand-mark tray icon (file-based) + live battery arc icon
│   ├── NativeMethods.cs             — Win32 P/Invoke: per-monitor work area + DPI, monitor clamping, native dialogs
│   ├── RelayCommand.cs              — Minimal ICommand for tray click binding
│   └── TaskSchedulerHelper.cs       — Auto-start management via Task Scheduler
│
└── scripts/
    ├── make-appicon.ps1             — Regenerates Assets\AppIcon.ico from the brand geometry
    │                                  (-HighContrast → Assets\SetupIcon.ico, dense tones for the
    │                                   installer's light title bar)
    └── sign.ps1                     — Authenticode signing (Release builds + one-time cert setup)
```

## Design notes

- **No public API surface** — in the app project all service/feature types are
  `internal`; the only `public` class is `App`, required by the WinUI framework.
  The `Vendors/*` projects expose only their contract types (interfaces + the
  module class) as `public` — implementations stay `internal`.
- **Feature abstraction** — the three menu toggles implement `IToggleFeature`
  (`Name` / `IsAvailable` / `IsEnabled` / `SetEnabled`). `IsAvailable` distinguishes
  "hardware not capable" from "feature off", so `TrayMenu` can grey out incapable items
  rather than showing them as unchecked. `SetEnabled` returns `bool` to propagate write
  failures. `TrayMenu` builds and refreshes all items in a single loop.
- **UI thread safety** — toggle writes and dashboard badge reads all run on background
  threads via `Task.Run` (RPC and service calls can block for seconds); results are
  marshalled back to the UI thread with `DispatcherQueue.TryEnqueue`.
- **Vendor split** — everything Lenovo-specific lives in its own project
  (`Vendors/Lenovo`) behind the vendor-neutral interfaces in `Vendors/Abstractions`,
  so another vendor (e.g. HP) is a new project plus one line in `VendorCatalog`.
  The app keeps thin static facades (`ChargeThresholdService`, `StandbyService`)
  so call sites are unchanged from before the split.
- **Native interop** — Smart Charge is the one feature that can't be done from
  managed code or WMI; it goes through `LenPower.dll` (see `native/`). The managed
  provider fails soft (`Read()` → `null`) if the bridge or driver is absent, so
  the rest of the app works on non-Lenovo hardware.
- **DPI** — the app is declared `PerMonitorV2`-aware. `AppWindow.Resize/Move` work
  in physical pixels while XAML lays out in DIPs, so the popup is sized and placed
  using the work area **and** DPI scale of the monitor under the cursor
  (`NativeMethods.GetCursorMonitorMetrics`) — correct on mixed-DPI multi-monitor setups.
