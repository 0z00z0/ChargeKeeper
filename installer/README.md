# Installer & distribution

ChargeKeeper ships as a **per-user Inno Setup installer** (`%LocalAppData%`, no admin to
install) and is distributed through **winget**. The app itself is elevated at runtime; the installer
is not.

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
installer\Output\ChargeKeeper-Setup.exe
```

The script prints the installer's **SHA256** — paste it into the winget manifest (below).

### What the installer does
- Installs per-user to `%LocalAppData%\Programs\ChargeKeeper` — **no admin prompt**.
- Adds a Start-menu shortcut.
- Optional **"Run at startup"** checkbox: if ticked, creates a `RunLevel=Highest` logon task
  (`ChargeKeeper AutoStart`) so the elevated app auto-starts with no boot-time UAC. Creating that
  task is the *only* step that elevates, and only when the box is checked. (The same task is what
  the app's "Launch at startup" tray toggle manages.)
- Optional **"Auto update in background"** checkbox: creates a **non-elevated** logon task
  (`ChargeKeeper AutoUpdate`, runs 5 min after sign-in) that runs
  `winget upgrade --id 0z00z0.ChargeKeeper --silent`. Creating it needs **no admin** (no UAC).
  - This only finds updates once the package is reachable from a **winget source** — i.e. submitted
    to the public `microsoft/winget-pkgs`, or a [local source](#winget) the machine has configured.
    Until then the task runs harmlessly and finds nothing.
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
- The **winget identity is new** (`0z00z0.ChargeKeeper`) — the old package ID will not auto-upgrade
  across the rename; users install the new ID once.

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
| `installer\wizard\wizimg-*.bmp` (side banner) and `wizsmall-*.bmp` (header) | `installer\make-wizard-images.ps1` | 24-bit BMPs. `ChargeKeeper.iss` references a **single 300 %-resolution hero** of each (`wizimg-492x942.bmp`, `wizsmall-165x174.bmp`) via `WizardImageFile` / `WizardSmallImageFile`, so Inno only ever **downscales** it (crisp at every 100–300 % display scaling). The intermediate per-DPI variants are still emitted but unused — see the "blurry banner" note below. |
| `Assets\SetupIcon.ico` (`SetupIconFile`)   | `scripts\make-appicon.ps1 -HighContrast` | The steel battery glyph in dense "ink" tones (`#1C333F`/`#366B4A`/`#99592C`) on a **transparent** background, so it blends into Inno's light title bar while staying legible at 16 px. The app's own dark title bar uses the product-palette `Assets\AppIcon.ico` instead — see "one glyph, two palettes" below. |

`installer\wizard\*.svg` (`wizard-image.svg`, `wizard-small.svg`) are **design references only** —
they are not consumed by the build. They must be kept in sync with the GDI+ geometry in
`make-wizard-images.ps1`: if you change one, change the other and re-run the script so the shipped
BMPs match the reference.

**One glyph, two palettes — and no plate.** The same battery geometry ships as two icons, because
no single icon reads on both a dark and a light title bar:

| File | Palette | Reads against |
|------|---------|---------------|
| `Assets\AppIcon.ico` | product / GaugePalette — SteelBlue `#7FA8B8`, Sage `#7AB88F`, Terracotta `#C9926B` | **Dark** chrome: the app's own `#0a0f17` title bar, taskbar, Alt-Tab |
| `Assets\SetupIcon.ico` | dense "ink" — `#1C333F`, `#366B4A`, `#99592C` | **Light** chrome: Inno's wizard title bar |

Both are **fully transparent**. An earlier revision drew the glyph on a dark `#0e1620` rounded
plate so one file could serve both, but on Inno's light title bar that plate reads as a dark box
that refuses to blend. Re-tinting the glyph per background and dropping the plate is what actually
works. `AppIcon.ico` needs `CopyToOutputDirectory` in the csproj — `TitleBarTheme.ApplyDark`
resolves it by path at runtime and silently does nothing if it isn't beside the exe.

**Why a single hero bitmap instead of a per-DPI variant list.** A comma-separated
`WizardImageFile` list lets Inno pick a per-DPI bitmap, but on a **mixed-DPI setup** (e.g. a
100 % external monitor as primary + a 175 % laptop panel) Inno selects the bitmap for the monitor
Setup *starts* on and then **upscales** it when the wizard is shown on / dragged to the higher-DPI
monitor — and upscaling a bitmap is what made the banner text look soft. Shipping one bitmap
rendered at the top of the range (300 %) means Inno can only ever **downscale**, which stays crisp
at every scaling factor. The intermediate variants are still generated (they keep the reference
SVGs honest and are handy for inspection) but are not referenced by `ChargeKeeper.iss`.

## Releasing

### Automated release (recommended) — GitHub Actions

`.github/workflows/release.yml` builds, signs, and publishes everything automatically.

**One-time setup: configure signing secrets**

The workflow signs `ChargeKeeper.exe` and `ChargeKeeper-Setup.exe` with an Authenticode PFX.
Add these two repository secrets (Settings → Secrets and variables → Actions → New repository secret):

| Secret name          | Value                                                                 |
|----------------------|-----------------------------------------------------------------------|
| `CODE_SIGN_PFX`      | Base64-encoded PFX file (see below)                                   |
| `CODE_SIGN_PASSWORD` | Password used when the PFX was exported                               |

If either secret is absent the signing step is skipped and the rest of the workflow continues.

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

1. Bump the version in your code / `.iss` file as needed.
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

You can also trigger the workflow manually (Actions → Release → Run workflow) and supply a
version string if you want to build without pushing a tag.

### Manual release

1. `build-installer.ps1 -Version X.Y.Z`.
2. Create a GitHub Release tagged `vX.Y.Z` on `0z00z0/ChargeKeeper` and attach
   `ChargeKeeper-Setup.exe`.
3. Update `winget/` manifests: bump `PackageVersion`, set the `InstallerUrl` to the new asset, and
   set `InstallerSha256` to the value the build script printed.

## winget

End users:

```powershell
winget install 0z00z0.ChargeKeeper     # first install (per-user, silent, no admin)
winget upgrade 0z00z0.ChargeKeeper      # update to a newer published version
```

> winget has **no background auto-updater** — updates happen when the user runs `winget upgrade`.
> This is the intended trade-off for keeping the app and installer simple.

### Manifests (`winget/`)
Three files target `0z00z0.ChargeKeeper`: `*.installer.yaml`, `*.locale.en-US.yaml`, and the version
manifest. Validate / test locally before publishing:

```powershell
winget validate --manifest installer\winget
winget install --manifest installer\winget    # local install test (enable local manifests once:
                                               #   winget settings --enable LocalManifestFiles)
```

Regenerating from a release with **wingetcreate** is convenient:

```powershell
winget install Microsoft.WingetCreate
wingetcreate update 0z00z0.ChargeKeeper --version X.Y.Z --urls <Setup.exe URL> --out installer\winget
```

Submitting to the public `microsoft/winget-pkgs` repo is **optional** — the manifests work with a
local source (above) without any submission.
