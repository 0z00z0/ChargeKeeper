# Installer & distribution

Lenovo Power Tray ships as a **per-user Inno Setup installer** (`%LocalAppData%`, no admin to
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
.\build-installer.ps1              # auto-bumps patch (e.g. 1.0.2 → 1.0.3)
.\build-installer.ps1 -Version 1.1.0   # explicit override
```

This builds the native Smart Charge bridge, publishes the app self-contained (win-x64, no trimming),
signs both the published exe and the installer exe (if a code-signing cert is present), and
compiles `LenovoPowerTray.iss` into:

```
installer\Output\LenovoPowerTray-Setup.exe
```

The script prints the installer's **SHA256** — paste it into the winget manifest (below).

### What the installer does
- Installs per-user to `%LocalAppData%\Programs\Lenovo Power Tray` — **no admin prompt**.
- Adds a Start-menu shortcut.
- Optional **"Run at startup"** checkbox: if ticked, creates a `RunLevel=Highest` logon task
  (`LenovoTray AutoStart`) so the elevated app auto-starts with no boot-time UAC. Creating that task
  is the *only* step that elevates, and only when the box is checked. (The same task is what the
  app's "Launch at startup" tray toggle manages.)
- Optional **"Auto update in background"** checkbox: creates a **non-elevated** logon task
  (`LenovoTray AutoUpdate`, runs 5 min after sign-in) that runs
  `winget upgrade --id 0z00z0.LenovoPowerTray --silent`. Creating it needs **no admin** (no UAC).
  - This only finds updates once the package is reachable from a **winget source** — i.e. submitted
    to the public `microsoft/winget-pkgs`, or a [local source](#winget) the machine has configured.
    Until then the task runs harmlessly and finds nothing.
  - When an update is found while the app is running, Inno closes it (`CloseApplications=yes`) and
    replaces the files but does **not** relaunch (`RestartApplications=no`) — relaunching an
    elevated app would pop an unexpected UAC prompt. The new version starts at the next sign-in
    (if "Run at startup" is on) or the next manual launch.

## Releasing

### Automated release (recommended) — GitHub Actions

`.github/workflows/release.yml` builds, signs, and publishes everything automatically.

**One-time setup: configure signing secrets**

The workflow signs `LenovoTray.exe` and `LenovoPowerTray-Setup.exe` with an Authenticode PFX.
Add these two repository secrets (Settings → Secrets and variables → Actions → New repository secret):

| Secret name          | Value                                                                 |
|----------------------|-----------------------------------------------------------------------|
| `CODE_SIGN_PFX`      | Base64-encoded PFX file (see below)                                   |
| `CODE_SIGN_PASSWORD` | Password used when the PFX was exported                               |

If either secret is absent the signing step is skipped and the rest of the workflow continues.

**How to export a PFX for CI**

```powershell
# From a machine where the cert is already installed in the personal store:
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -like '*Zero Zero*' }
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
   - Create a GitHub Release named **"Lenovo Power Tray v1.2.3"** with the installer
     and winget manifest files attached.
   - Run `winget validate` against the patched manifests.

You can also trigger the workflow manually (Actions → Release → Run workflow) and supply a
version string if you want to build without pushing a tag.

### Manual release

1. `build-installer.ps1 -Version X.Y.Z`.
2. Create a GitHub Release tagged `vX.Y.Z` on `0z00z0/LenovoPowerTray` and attach
   `LenovoPowerTray-Setup.exe`.
3. Update `winget/` manifests: bump `PackageVersion`, set the `InstallerUrl` to the new asset, and
   set `InstallerSha256` to the value the build script printed.

## winget

End users:

```powershell
winget install 0z00z0.LenovoPowerTray     # first install (per-user, silent, no admin)
winget upgrade 0z00z0.LenovoPowerTray      # update to a newer published version
```

> winget has **no background auto-updater** — updates happen when the user runs `winget upgrade`.
> This is the intended trade-off for keeping the app and installer simple.

### Manifests (`winget/`)
Three files target `0z00z0.LenovoPowerTray`: `*.installer.yaml`, `*.locale.en-US.yaml`, and the version
manifest. Validate / test locally before publishing:

```powershell
winget validate --manifest installer\winget
winget install --manifest installer\winget    # local install test (enable local manifests once:
                                               #   winget settings --enable LocalManifestFiles)
```

Regenerating from a release with **wingetcreate** is convenient:

```powershell
winget install Microsoft.WingetCreate
wingetcreate update 0z00z0.LenovoPowerTray --version X.Y.Z --urls <Setup.exe URL> --out installer\winget
```

Submitting to the public `microsoft/winget-pkgs` repo is **optional** — the manifests work with a
local source (above) without any submission.
