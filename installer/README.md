# Installer & distribution

ChargeKeeper ships as a **per-user Inno Setup installer** (`%LocalAppData%`, no admin to
install), published as an asset on each **GitHub release**, with winget manifests attached to the
same release alongside it. It is not carried by any winget source — see the winget section below.
The app itself is elevated at runtime; the installer is not.

## Build the installer

Prerequisite (one-time): **Inno Setup**

```powershell
winget install JRSoftware.InnoSetup
```

Then:

```powershell
cd installer
.\build-installer.ps1              # auto-bumps patch (e.g. 1.2.0 → 1.2.1)
.\build-installer.ps1 -Version 1.3.0   # explicit override
```

This builds the native Smart Charge bridge, publishes the app self-contained (win-x64, no trimming),
signs both the published exe and the installer exe (if a code-signing cert is present), and
compiles `ChargeKeeper.iss` into:

```
installer\Output\ChargeKeeper-Setup-<version>.exe
```

The filename always carries the version: `ChargeKeeper.iss` sets
`OutputBaseFilename=ChargeKeeper-Setup-{#AppVersion}`, and `build-installer.ps1` passes the version
in as `/DAppVersion`. Version 1.28.0 therefore builds `ChargeKeeper-Setup-1.28.0.exe`, and that is
the name the release asset carries. There is no unversioned `ChargeKeeper-Setup.exe`. The build
script deletes any earlier `ChargeKeeper-Setup-*.exe` from `Output` before compiling, so the folder
holds exactly one installer.

The script then prints the installer's **SHA256**, computed after signing. A tagged release does not
need that value — the workflow computes the hash itself and patches the manifests with it — so it
serves a local manifest test.

### What the installer does
- Installs per-user to `%LocalAppData%\Programs\ChargeKeeper` — **no admin prompt**.
- Adds a Start-menu shortcut.
- Optional **"Run at startup"** checkbox: if ticked, creates a `RunLevel=Highest` logon task
  (`ChargeKeeper AutoStart`) so the elevated app auto-starts with no boot-time UAC. Creating that
  task is the *only* step that elevates, and only when the box is checked. (The same task is what
  the app's "Launch at startup" tray toggle manages.)
- Any `ChargeKeeper AutoUpdate` logon task from an earlier version is removed on install. It ran
  `winget upgrade`, and the package is not in a winget source, so it found nothing. Updates are the
  app's own job now: it queries GitHub Releases 30 s after start and every 24 hours thereafter.
  - When an update is found while the app is running, Inno closes it (`CloseApplications=yes`) and
    replaces the files but does **not** relaunch (`RestartApplications=no`) — relaunching an
    elevated app would pop an unexpected UAC prompt. The new version starts at the next sign-in
    (if "Run at startup" is on) or the next manual launch.

### Upgrading from Lenovo Power Tray (≤ 1.1.x)

The Inno `AppId` was deliberately **kept** across the rename, so running the ChargeKeeper
installer over an existing Lenovo Power Tray install upgrades it in place:

- The old `LenovoTray.exe` process is killed together with the new one in the same elevated step.
- The stale `LenovoTray.*` binaries and cached icon files are deleted from the install folder
  (`[InstallDelete]`).
- The old scheduled tasks (`LenovoTray AutoStart`, `LenovoTray AutoUpdate`) are removed; tick the
  corresponding checkboxes to get their ChargeKeeper replacements.
- Upgraded installs keep living in their old `%LocalAppData%\Programs\Lenovo Power Tray` folder
  (Inno reuses the recorded install path); fresh installs go to `...\ChargeKeeper`. Cosmetic only.
- The app migrates `%AppData%\LenovoPowerTray` → `%AppData%\ChargeKeeper` on first launch, so
  settings and battery history carry over.
- The **winget identity is new** (`0z00z0.ChargeKeeper`), and nothing upgrades through winget in
  either direction: neither identity is carried by a winget source, so `winget install` and
  `winget upgrade` find nothing for either one. The upgrade route is to run the ChargeKeeper
  installer over the existing install.

## Installer visual design (wizard art & setup icon)

The installer is a **"made by ZeroZero Software" surface**, so it carries the studio identity —
but only as a *shell*. The design rule settled by #60 keeps a clear split between what belongs to
the studio and what belongs to the product:

**Studio surface (constant).** The dark `#0a0f17` background and the canonical `[Ø]` studio mark
stay. The mark is the one element allowed to keep its studio bracket gradients (teal→blue,
purple→indigo) — it is the studio's signature, not the app's. This is why the `[Ø]` appears on the
wizard banner even though it never appears in the app's own icon.

**Product framing (flat, muted).** Everything *around* the mark that frames ChargeKeeper — the
accent bars, the battery glyph, and the inner-page headings — uses ChargeKeeper's flat muted
product palette as **flat fills** (no gradients on the product framing; the `[Ø]` mark is the only
element that keeps gradients). The one exception is the dark banner's own background, which keeps a
subtle radial *glow* vignette (a soft `#16232c`→`#0a0f17`) as part of the studio surface — a
background tone, not framing:

| Role                         | On dark banner            | Dense on-white (inner pages) |
|------------------------------|---------------------------|------------------------------|
| SteelBlue (body / structure) | `#7FA8B8`                 | `#3F6374`                    |
| Sage (charge fill)           | `#7AB88F`                 | `#4F8F67`                    |
| Terracotta (guard line)      | `#C9926B`                 | `#B57745`                    |

Both columns (plus the denser still "ink" tones the setup icon's 16 px frame uses) live in one
table in `scripts\BatteryGlyph.ps1` — `$BatteryGlyphPalettes.Product` / `.Dense` / `.Ink`. Retint
there, re-run both generators, and every surface follows.

**Inner pages stay light.** The wizard runs `WizardStyle=modern` (light modern inner pages) with
dense-steel headings. The brand typeface (Cascadia Mono) appears **only in the pre-rendered
bitmaps** — it is never set as the wizard dialog font, so the inner pages use the native system UI
font and stay legible at every DPI.

The intent is that this installer is a working reference: future ZeroZero Software installers
(HyperVManagerTray, M365Migrator) should follow the same studio-surface-vs-product-framing split,
swapping only the per-product palette.

### Which script generates what

There is **no SVG rasteriser on the build machine**, so every installer bitmap is drawn natively
with System.Drawing (GDI+) from the same geometry the reference SVGs describe:

| Artefact                                   | Generator                             | Notes |
|--------------------------------------------|---------------------------------------|-------|
| `installer\wizard\wizimg-492x942.bmp` (side banner) and `wizsmall-165x174.bmp` (header) | `installer\make-wizard-images.ps1` | 24-bit BMPs. **One bitmap each**, rendered at 300 % and referenced by `ChargeKeeper.iss` via `WizardImageFile` / `WizardSmallImageFile`, so Inno only ever **downscales** it (crisp at every 100–300 % display scaling) — see the "blurry banner" note below. |
| The battery glyph inside all of the above | `scripts\BatteryGlyph.ps1` | Dot-sourced by **both** `make-wizard-images.ps1` and `scripts\make-appicon.ps1`: one copy of the geometry, and one palette table (Product / Dense / Ink) so a brand tint change is a single edit. Callers own their own surface (plates, banners, text); this file owns the glyph. |
| `Assets\SetupIcon.ico` (`SetupIconFile`)   | `scripts\make-appicon.ps1 -HighContrast` | The steel battery glyph, rendered **per frame size** because this file is Setup.exe's own icon and lands on two opposite surfaces: the **16 px** frame is dense "ink" (`#1C333F`/`#366B4A`/`#99592C`) on transparent, for Inno's light wizard title bar; the **32/48/64/128/256 px** frames are **plated** (dark `#0e1620` square, light product glyph) for dark Explorer. See "one glyph, two treatments" below. The app's own icon is the plain product-palette `Assets\AppIcon.ico`. |

`installer\wizard\*.svg` (`wizard-image.svg`, `wizard-small.svg`) are **design references only** —
they are not consumed by the build. They must be kept in sync with the GDI+ geometry in
`make-wizard-images.ps1`: if you change one, change the other and re-run the script so the shipped
BMPs match the reference.

**One glyph, two treatments.** The same battery geometry ships as two icons, because no single
icon reads on both dark and light chrome:

| File | Frames | Treatment | Reads against |
|------|--------|-----------|---------------|
| `Assets\AppIcon.ico` | all | product / GaugePalette — SteelBlue `#7FA8B8`, Sage `#7AB88F`, Terracotta `#C9926B`, transparent, no plate | **Dark** chrome: the app's own `#0a0f17` title bar, taskbar, Alt-Tab |
| `Assets\SetupIcon.ico` | 16 px | dense "ink" — `#1C333F`, `#366B4A`, `#99592C`, transparent, no plate | **Light** chrome: Inno's wizard title bar (`#F3F3F3`) |
| `Assets\SetupIcon.ico` | 32/48/64/128/256 px | **plated** — dark `#0e1620` rounded square, `#1a2840` edge, product-palette glyph on top | **Dark** chrome: Explorer, desktop, taskbar (`#202020` on Win11 dark) |

The app icon is simple: the app only ever shows it on dark chrome, so one transparent product
palette covers every frame. `SetupIcon.ico` is the awkward one — `SetupIconFile` is **Setup.exe's
own file icon**, not just the wizard's title-bar icon, so it is drawn on both a light title bar and
(usually) dark Explorer. Measured, no palette wins both: ink scores **11.87:1** on `#F3F3F3` but
**1.24:1** on `#202020`; a plated glyph scores **6.36:1** on `#202020` but reads as a dark box on
the light title bar. An earlier revision tried each in turn and neither held.

Splitting by frame size resolves it, because the two surfaces ask for different sizes — the wizard
bar takes 16 px, Explorer takes 32 px and up. **The accepted cost:** Explorer's "Small icons" list
view can request 16 px, where the ink glyph is weak on dark (**2.96:1** at best). That is a real
regression in one optional view mode, traded for the wizard's 16 px on light being correct on every
single run. Serving the guaranteed case beats hedging both badly.

`AppIcon.ico` needs `CopyToOutputDirectory` in the csproj — `TitleBarTheme.ApplyDark` resolves it
by path at runtime and silently does nothing if it isn't beside the exe.

**Why a single hero bitmap instead of a per-DPI variant list.** A comma-separated
`WizardImageFile` list lets Inno pick a per-DPI bitmap, but on a **mixed-DPI setup** (e.g. a
100 % external monitor as primary + a 175 % laptop panel) Inno selects the bitmap for the monitor
Setup *starts* on and then **upscales** it when the wizard is shown on / dragged to the higher-DPI
monitor — and upscaling a bitmap is what made the banner text look soft. Shipping one bitmap
rendered at the top of the range (300 %) means Inno can only ever **downscale**, which stays crisp
at every scaling factor.

The script used to also emit 100/125/150/175/200 % variants of each, and this section used to say
they were kept because they "keep the reference SVGs honest". They didn't — rendering a bitmap that
nothing opens, references, or compares validates nothing, and the ten files were ~1.9 MB tracked in
git and rewritten on every run. They are gone; `make-wizard-images.ps1` now emits exactly the two
bitmaps `ChargeKeeper.iss` consumes. Keeping the SVGs honest is still a manual review job (below).

## Releasing

### The only release route — GitHub Actions

`.github/workflows/release.yml` builds, signs and publishes everything on a `v*.*.*` tag push. It is
the only way a release is created.

**One-time setup: configure signing secrets**

The workflow signs `ChargeKeeper.exe` and `ChargeKeeper-Setup-<version>.exe` with an Authenticode PFX.
Add these two repository secrets (Settings → Secrets and variables → Actions → New repository secret):

| Secret name          | Value                                                                 |
|----------------------|-----------------------------------------------------------------------|
| `CODE_SIGN_PFX`      | Base64-encoded PFX file (see below)                                   |
| `CODE_SIGN_PASSWORD` | Password used when the PFX was exported                               |

With `CODE_SIGN_PFX` absent the signing step is skipped, and what happens next depends on the ref.
A tag push fails the run outright rather than publishing an unsigned installer to users. Any other
ref only warns, so a dry run stays useful.

**How to export a PFX for CI**

```powershell
# From a machine where the cert is already installed in the personal store:
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -like '*ZeroZero*' }
$pfxPassword = ConvertTo-SecureString 'your-password' -AsPlainText -Force
Export-PfxCertificate -Cert $cert -FilePath codesign.pfx -Password $pfxPassword

# Encode it for the GitHub secret:
[Convert]::ToBase64String([IO.File]::ReadAllBytes('codesign.pfx')) | Set-Clipboard
# Paste the clipboard value into the CODE_SIGN_PFX secret on GitHub.

# Delete the local copy when done:
Remove-Item codesign.pfx
```

**How to cut a release**

1. Bump `<Version>` in `ChargeKeeper.csproj` — that is the single source, and the tag must match it.
   `ChargeKeeper.iss` takes the version from the build (`/DAppVersion`) and holds none of its own.
2. Push a tag:
   ```powershell
   git tag v1.2.3
   git push origin v1.2.3
   ```
3. GitHub Actions will:
   - Build the native bridge and publish the app.
   - Compile the installer with Inno Setup 6.
   - Authenticode-sign the `.exe` files (if secrets are set).
   - Compute the SHA256 and patch the winget manifests in-place.
   - Create a GitHub Release named **"ChargeKeeper v1.2.3"** with the installer
     and winget manifest files attached.
   - Run `winget validate` against the patched manifests.

The workflow can also be triggered manually (Actions → Release → Run workflow) with a version
string, to build and validate without pushing a tag. Such a run **publishes nothing**: the release
step and the manifest-validation job are both gated on the ref being a tag.

### Building locally (not a release route)

A release is published **only** by pushing a `v*.*.*` tag; the workflow above builds, signs,
publishes and attaches everything. Creating a release by hand — `gh release create` or the web UI —
is not part of the routine, and a hand-made release would carry neither the patched manifests nor
the assertion that the manifests describe the build.

A local build is for testing the installer itself:

1. `build-installer.ps1 -Version X.Y.Z`.
2. The installer lands at `installer\Output\ChargeKeeper-Setup-X.Y.Z.exe`, and the script prints its
   SHA256.
3. The `winget\` manifests need no editing for a release — the workflow patches `PackageVersion`,
   `InstallerUrl` and `InstallerSha256` in its own working copy on every tag. Edit them only to test
   a manifest against a locally built installer.

## winget

**The package is in no winget source.** It is not in `microsoft/winget-pkgs` and not in any other
repository winget searches, so `winget install 0z00z0.ChargeKeeper` and
`winget upgrade 0z00z0.ChargeKeeper` fail with *No package found matching input criteria*. Getting
the package accepted upstream is tracked as issue #15; until that lands, no winget command resolves
the identifier by name.

### Installing from the release manifests

Three manifest files are attached as assets to every release:

```
0z00z0.ChargeKeeper.yaml
0z00z0.ChargeKeeper.installer.yaml
0z00z0.ChargeKeeper.locale.en-GB.yaml
```

Downloading all three into one folder and installing from that folder works today:

```powershell
winget settings --enable LocalManifestFiles   # one-time, needs admin
winget install --manifest <folder>
```

The workflow patches the manifests for the release it is publishing, so they describe that build
exactly: matching package identifier and version, an `InstallerUrl` pointing at that release's
`ChargeKeeper-Setup-<version>.exe`, an `InstallerSha256` equal to that asset's hash, locale
`en-GB`, installer type `inno`, scope `user`, and the silent-install switches. A later step in the
workflow asserts those fields against the build rather than trusting the patch.

Downloading the installer asset and running it is the simpler route and needs no winget at all.

### Manifest source (`winget\`)

`installer\winget\` holds the three manifests the release workflow patches before attaching them.
It is the **maintainer's source** for those files, not an install route: the version, URL and hash
committed there describe whichever build they were last edited for. Validate a change before
committing it:

```powershell
winget validate --manifest installer\winget
```

Regenerating them from a published release with **wingetcreate** is convenient:

```powershell
winget install Microsoft.WingetCreate
wingetcreate update 0z00z0.ChargeKeeper --version X.Y.Z `
  --urls <URL of the release's ChargeKeeper-Setup-X.Y.Z.exe> --out installer\winget
```
