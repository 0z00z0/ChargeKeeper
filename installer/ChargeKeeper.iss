; Inno Setup script for ChargeKeeper.
;
; Per-user install (no admin required). The app itself is requireAdministrator and
; elevates at runtime; the installer does not. The optional "Run at startup" task is
; the ONLY thing that elevates, and only if the user ticks it (see RegisterStartupTask).
;
; Build via installer\build-installer.ps1, which publishes the app and passes
; /DPublishDir and /DAppVersion to ISCC.

#define AppName       "ChargeKeeper"
#define AppExe        "ChargeKeeper.exe"
#define AppPublisher  "ZeroZero Software"
#define AppUrl        "https://github.com/0z00z0/ChargeKeeper"
#define TaskName      "ChargeKeeper AutoStart"

; Legacy names from this app's previous identity ("Lenovo Power Tray", v1.1.x and older).
; Kept ONLY so an in-place upgrade can kill the old process and clean up its leftovers —
; see [InstallDelete] and the legacy cleanup in [Code].
#define LegacyExe          "LenovoTray.exe"
#define LegacyTaskName     "LenovoTray AutoStart"
#define LegacyUpdateTask   "LenovoTray AutoUpdate"
#define LegacyWatchdogTask "LenovoTray Watchdog"

; The install folder an upgrade across the rename is still sitting in. The FINAL COMPONENT of a
; path, never a whole path. It must stay equal to InstallLocations.LegacyFolderName in Helpers\,
; as AppName above must stay equal to InstallLocations.ProductFolderName; the test
; EveryFolderLiteral_AgreesWithTheApplication reads this script and fails if either drifts.
#define LegacyDirName      "Lenovo Power Tray"

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

[Setup]
; AppId uniquely identifies this app for upgrades/uninstall — do not change it.
; Deliberately UNCHANGED across the Lenovo Power Tray -> ChargeKeeper rename so existing
; 1.1.x installs upgrade in place. A new value would orphan every existing install: the old one
; would never uninstall and both would sit in Apps & features.
AppId={{B1F8E4B2-3D7A-4C56-9E2F-7A1C9D5E6F40}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
; UsePreviousAppDir=no is what lets an install move OUT of the retired "{#LegacyDirName}" folder.
; With it left at the default, Inno reads {app} straight from the AppId-recorded uninstall key and
; a changed DefaultDirName is ignored on every upgrade — which is why such installs never moved.
; Turning it off hands the choice to ResolveInstallDir in [Code], which returns the recorded
; directory UNCHANGED unless its final component is the retired name. So a directory the user chose
; for themselves is still honoured; only the retired one is replaced.
; Consequence handled in [Code]: DisableDirPage's automatic hiding of the directory page on an
; upgrade keys off UsePreviousAppDir, so ShouldSkipPage restores it.
UsePreviousAppDir=no
DefaultDirName={code:ResolveInstallDir}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Inno Setup 6 defaults DisableWelcomePage=yes, which hides the Welcome page entirely — so the
; redesigned studio banner (WizardImageFile) and the studio-voice WelcomeLabel copy below would
; only ever appear on the Finished page. Show the Welcome page so the #60 installer redesign is
; actually seen (one extra "Next" click on the way in).
DisableWelcomePage=no
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
; Per-user: installs under %LocalAppData%\Programs, no UAC for the install itself.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=ChargeKeeper-Setup-{#AppVersion}
; #60: high-contrast setup icon, rendered PER FRAME SIZE. SetupIconFile is not merely the wizard's
; title-bar icon — it is Setup.exe's OWN file icon, so it lands on two opposite surfaces: Inno's
; LIGHT wizard title bar (16 px, #F3F3F3) and DARK Explorer / desktop / taskbar (32 px+, #202020 on
; Win11 dark). No single palette serves both: the dense "ink" tones score 11.87:1 on light but
; 1.24:1 on dark (invisible), while a dark-plated glyph scores 6.36:1 on dark but reads as an ugly
; box on light chrome. So the frames split by the size each surface asks for — 16 px stays ink on
; transparent for the wizard bar; 32 px and up are plated (dark #0e1620 square, light product
; glyph) for Explorer. Accepted cost: Explorer's "Small icons" view can request 16 px, where the
; ink glyph is weak on dark — the wizard's 16 px on light is guaranteed on every run, that view
; mode is optional, so we serve the certain case. Built by scripts\make-appicon.ps1 -HighContrast.
; The app's own icon (dark chrome only) is the plain product-palette Assets\AppIcon.ico.
SetupIconFile=..\Assets\SetupIcon.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; ZeroZero Software studio-look wizard graphics (issue #23). Built by
; installer\make-wizard-images.ps1 (native GDI+, no SVG rasteriser needed); design source
; is installer\wizard\*.svg. SetupIconFile above stays the product battery icon.
;
; SINGLE high-res (300 %) hero bitmap rather than a per-DPI comma list. On a mixed-DPI setup
; (100 % external primary + 175 % laptop panel) Inno picks the bitmap for the monitor Setup
; STARTS on, then UPSCALES it when shown on a higher-DPI monitor — that upscale is what made
; the banner text blurry. One 300 % bitmap means Inno can only ever DOWNSCALE (crisp at every
; scaling factor 100–300 %). Aspect matches Inno's image area (164:314 and 55:58) so the
; downscale is uniform. See make-wizard-images.ps1 for the full rationale.
WizardImageFile=wizard\wizimg-492x942.bmp
WizardSmallImageFile=wizard\wizsmall-165x174.bmp
; Set EXPLICITLY, not left to the default: with the old 5-variant lists Inno picked a bitmap that
; already matched the image area, so stretching was immaterial. With one 300 % hero the banner
; depends on it entirely — WizardImageStretch=no would centre the 492x942 bitmap at natural size in
; the 164x314 area and show roughly its middle ninth, cropping the [Ø] mark, "ZeroZero Software"
; and the "ChargeKeeper" wordmark straight off. Nothing renders these BMPs in CI, so only a manual
; look at a signed installer would ever catch that.
WizardImageStretch=yes
; Restart Manager is NOT used to close the running app (issue #119). Setup runs unelevated
; (PrivilegesRequired=lowest) while ChargeKeeper.exe is requireAdministrator, so Restart Manager
; cannot terminate it: it logs "Can use RestartManager to avoid reboot? No (1: Permission Denied)"
; and Setup gives up BEFORE the install phase — no program files and no uninstall key are written,
; so an upgrade attempted while the app is running silently does nothing at all.
; PrepareToInstall in [Code] stops the app itself, through an elevated taskkill, at the step that
; runs just before Setup's own in-use check would have.
CloseApplications=no
; Immaterial while CloseApplications=no (Setup only restarts what it closed), but kept explicit:
; the app is requireAdministrator, so Setup must never relaunch it — LaunchApp in [Code] owns the
; relaunch and does it through the elevated logon task where one exists.
RestartApplications=no

[Messages]
; ── ZeroZero Software studio voice (issue #66) ───────────────────────────────
; British English, plain language (per 0z0-design/design-language.md: no jargon;
; the "no telemetry, no accounts, no subscriptions" statement made comfortably and
; plainly), brand name exactly "ZeroZero Software". Only the strings below are
; overridden — every other wizard string keeps Inno's default English. The wizard
; font is deliberately NOT changed here (see InitializeWizard's note): the brand
; typeface lives only in the pre-rendered bitmap surfaces, so the copy stays in the
; default dialog font the target machine is guaranteed to have.
WelcomeLabel2=This will install {#AppName} on your computer.%n%n{#AppName} installs just for your user account, so no administrator rights are needed to set it up.%n%nNo telemetry, no accounts, no subscriptions.
; The app has no window — it runs from the notification area (system tray). Both
; finished-page strings are set so the first-time user knows where to find it,
; whichever variant Inno shows (with or without a post-install run option).
; ASCII-only on purpose: this .iss has no UTF-8 BOM, so Inno Setup 6 reads it as ANSI — a
; U+2014 em dash would ship as mojibake ("a-tilde ..."). Use plain ASCII punctuation here.
; Says "installed", not "installed and running": the post-install launch is an elevated
; ShellExec that the user can cancel at the UAC prompt, so "running" isn't guaranteed.
FinishedLabelNoIcons={#AppName} is installed. Look for its icon in the notification area (the system tray, next to the clock); that's where you open it, check the battery, and change its settings.
FinishedLabel={#AppName} is installed. Look for its icon in the notification area (the system tray, next to the clock); that's where you open it, check the battery, and change its settings.
; Quiet studio sign-off, bottom-left of every wizard page.
BeveledLabel=ZeroZero Software - Small tools. Zero bloat.

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[InstallDelete]
; Upgrades from Lenovo Power Tray (<= 1.1.x): the assembly was renamed LenovoTray ->
; ChargeKeeper, so the old binaries would otherwise linger next to the new ones inside
; the old install folder (same AppId -> same {app}). Also drop the old cached tray icon.
Type: files; Name: "{app}\{#LegacyExe}"
Type: files; Name: "{app}\LenovoTray.dll"
Type: files; Name: "{app}\LenovoTray.pri"
Type: files; Name: "{app}\LenovoTray.deps.json"
Type: files; Name: "{app}\LenovoTray.runtimeconfig.json"
Type: files; Name: "{app}\LenovoTray.pdb"
; The old tray icon was generated into {app} under two names across its life: plain
; "LenovoRed.ico" (1.0.0) and version-suffixed "LenovoRed-v2/-v4.ico" (1.0.10 - 1.1.x). The
; suffixed pattern alone left the plain file behind on the earliest installs, so match both.
Type: files; Name: "{app}\LenovoRed*.ico"
; Start-menu leftovers from the old name, both pointing at the deleted LenovoTray.exe: the
; loose shortcut an "All apps" install left, and the one inside the program group older
; versions created. The group folder goes only if nothing else is left in it.
Type: files; Name: "{autoprograms}\Lenovo Power Tray.lnk"
Type: files; Name: "{autoprograms}\Lenovo Power Tray\Lenovo Power Tray.lnk"
Type: dirifempty; Name: "{autoprograms}\Lenovo Power Tray"

[Icons]
; Per-user "All apps" Start-menu entry. IconFilename points at the exe itself (which embeds
; the icon via <ApplicationIcon> in the csproj) — same pattern as the desktop shortcut below
; and UninstallDisplayIcon above. A prior version pointed this at "{app}\AppIcon.ico"; that path
; has never existed on any install, so the shortcut silently showed a blank/generic icon once
; Explorer's icon cache stopped masking it.
; The reason is the PATH, not the file: the csproj ships Assets\AppIcon.ico with
; CopyToOutputDirectory=PreserveNewest, so it DOES publish — but to "Assets\AppIcon.ico", and
; [Files] copies {#PublishDir}\* with recursesubdirs, preserving that folder. The installed icon
; is therefore "{app}\Assets\AppIcon.ico", never "{app}\AppIcon.ico". (An earlier version of this
; comment claimed the file never publishes at all. It was wrong then and is wrong twice over now —
; even without CopyToOutputDirectory the WinUI targets copy globbed Content to the output anyway.)
; So: don't "fix" this by pointing IconFilename at {app}\AppIcon.ico after checking the csproj and
; seeing that the icon does ship — the loose root-level path is still what doesn't exist. Pointing
; at the exe stays correct and needs no [Files] entry, so leave it alone.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"; Comment: "{#AppName}"
; Optional desktop shortcut (off by default; ticked via the task below).
Name: "{userdesktop}\{#AppName}";  Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"; Tasks: desktopicon

[Tasks]
Name: "runstartup"; Description: "Run {#AppName} automatically at sign-in (starts elevated without a UAC prompt at boot)"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

; NOTE: launching the app is handled in [Code] (LaunchApp), not [Run]. A [Run] entry uses
; CreateProcess, which CANNOT start a requireAdministrator exe (fails with "elevation
; required"). LaunchApp starts it correctly — via the elevated logon task if one exists
; (no extra prompt), otherwise via ShellExec (the single UAC prompt the app needs).

[Code]
const
  TaskName         = '{#TaskName}';
  UpdateTaskName   = 'ChargeKeeper AutoUpdate';
  WatchdogTaskName = 'ChargeKeeper Watchdog';

  // Where Inno records this install's own directory. The GUID is AppId's, repeated because AppId
  // is the one line in this file that must never be restructured; the test
  // TheUninstallKey_NamesTheSameGuidAsAppId reads both and fails if they disagree.
  UninstallKey =
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{B1F8E4B2-3D7A-4C56-9E2F-7A1C9D5E6F40}_is1';

var
  // True when PrepareToInstall found (and killed) a running instance. Lets a SILENT upgrade
  // (winget / the AutoUpdate task) restart the app it killed: without this, a background
  // upgrade leaves the tray app dead until the next sign-in.
  WasRunning: Boolean;

  // The directory the previous install recorded, read before Setup overwrites the uninstall key.
  // Empty on a fresh install.
  PreviousAppDir: string;

  // True when this run moves an installation out of the retired product folder.
  MigratingFromLegacy: Boolean;

  // Both task definitions as they stood before this run, exported in PrepareToInstall. Empty when
  // the task did not exist or could not be read.
  AutoStartXmlFile, WatchdogXmlFile: string;

// True when the FINAL COMPONENT of Dir is the retired product folder. Matches
// InstallLocations.IsLegacyInstallDir in the app: the name, never a whole path, so an install
// under a non-default parent is still recognised.
function IsLegacyDir(const Dir: string): Boolean;
begin
  Result := (Dir <> '')
        and (CompareText(ExtractFileName(RemoveBackslashUnlessRoot(Dir)), '{#LegacyDirName}') = 0);
end;

function InitializeSetup(): Boolean;
begin
  PreviousAppDir := '';
  RegQueryStringValue(HKCU, UninstallKey, 'Inno Setup: App Path', PreviousAppDir);

  // Interactive runs only. Re-pointing an RL HIGHEST task needs elevation, and a silent run has
  // nobody to answer the consent prompt — the same stance this file already takes for the
  // Lenovo-era task cleanup. A silent upgrade installs where it already is and leaves the move to
  // the next interactive run; the app's own update route passes no silent switch, so that route
  // migrates (and, being started from the elevated app, raises no prompt at all).
  MigratingFromLegacy := IsLegacyDir(PreviousAppDir) and not WizardSilent();
  Result := True;
end;

// Where this run installs. Called by DefaultDirName, so it runs after InitializeSetup.
function ResolveInstallDir(Param: string): string;
begin
  if (PreviousAppDir = '') or MigratingFromLegacy then
    Result := ExpandConstant('{autopf}\{#AppName}')
  else
    // A directory the user chose for themselves. UsePreviousAppDir=no would otherwise discard it.
    Result := PreviousAppDir;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  // Restores what DisableDirPage=auto did before UsePreviousAppDir=no switched it off: an upgrade
  // does not ask again for a directory it already has.
  Result := (PageID = wpSelectDir) and (PreviousAppDir <> '');
end;

procedure InitializeWizard();
begin
  // Dense-steel page headings (issue #66) — the same on-white SteelBlue the small wizard
  // header image uses ($cSteelDense in installer\make-wizard-images.ps1 = #3F6374). This
  // recolours only the heading labels; body text and everything else stays default, and
  // WizardStyle / the light modern inner-page theme are untouched.
  //
  // ⚠ Pascal TColor is BGR, not RGB: #3F6374 (RGB) → $74633F. Do NOT "fix" this to $3F6374.
  //
  // PageNameLabel sits on the white header strip; WelcomeLabel1/FinishedHeadingLabel sit on
  // the white main page area. #3F6374 on white measures ~6.5:1 contrast, comfortably above
  // the 4.5:1 threshold, so all three carry the steel colour.
  WizardForm.PageNameLabel.Font.Color        := $74633F;  // inner-page title (white header strip)
  WizardForm.WelcomeLabel1.Font.Color        := $74633F;  // "Welcome" heading (white main area)
  WizardForm.FinishedHeadingLabel.Font.Color := $74633F;  // "Completing" heading (white main area)
end;

function ScheduledTaskExists(): Boolean;
var
  ResultCode: Integer;
begin
  // Querying does not require elevation; exit code 0 = the task exists.
  Result := Exec('schtasks.exe', '/Query /TN "' + TaskName + '"', '',
                 SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function WatchdogTaskExists(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('schtasks.exe', '/Query /TN "' + WatchdogTaskName + '"', '',
                 SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

procedure RegisterStartupTask();
var
  ResultCode: Integer;
  Params: string;
begin
  // The app rewrites this task at startup with power-safe settings from full XML
  // (StopIfGoingOnBatteries=false etc. — the schtasks CLI defaults below made Task Scheduler
  // hard-kill the instance the moment AC dropped at undock; root cause of the 2026-07
  // "vanished tray icon" incidents, see Helpers/WatchdogTask.cs). If the task already exists,
  // leave the app-maintained definition alone — recreating it here would regress those flags
  // until the app's next startup repair.
  if ScheduledTaskExists() then exit;

  // A logon task with RL HIGHEST lets the elevated app auto-start with no boot-time UAC
  // prompt. Creating a HIGHEST task needs admin, so this one step elevates via 'runas'
  // (exactly one UAC prompt — and only because the user ticked "Run at startup").
  Params := '/Create /TN "' + TaskName + '" /TR "\"' + ExpandConstant('{app}\{#AppExe}') +
            '\"" /SC ONLOGON /RL HIGHEST /F';
  if not ShellExec('runas', 'schtasks.exe', Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    MsgBox('Could not create the startup task. You can still enable "Launch at startup" '
           + 'from the app''s tray menu later.', mbInformation, MB_OK);
end;

function ProcessIsRunning(const ExeName: string): Boolean;
var
  ResultCode: Integer;
begin
  // tasklist|find: exit 0 only when the named process is present. Works without
  // elevation (the image name is visible even for an elevated process).
  Result := Exec(ExpandConstant('{cmd}'),
                 '/C tasklist /FI "IMAGENAME eq ' + ExeName + '" /NH | find /I "' + ExeName + '"',
                 '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function AppIsRunning(): Boolean;
begin
  Result := ProcessIsRunning('{#AppExe}');
end;

function LegacyTaskExists(): Boolean;
var
  ResultCode: Integer;
begin
  // The old "Lenovo Power Tray" install registered an elevated logon task pointing at the
  // now-renamed exe; querying it needs no elevation.
  Result := Exec('schtasks.exe', '/Query /TN "{#LegacyTaskName}"', '',
                 SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function LegacyWatchdogExists(): Boolean;
var
  ResultCode: Integer;
begin
  // Old-name watchdog task (<= 1.1.x). Left behind, it would probe for the deleted
  // LenovoTray.exe every 5 minutes forever; querying it needs no elevation.
  Result := Exec('schtasks.exe', '/Query /TN "{#LegacyWatchdogTask}"', '',
                 SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

procedure StopAppAndRemoveStartupTask();
var
  ResultCode: Integer;
begin
  // Stopping the running (elevated) app and deleting its RL HIGHEST logon + watchdog tasks all
  // need admin, so do them together in one elevated cmd -> at most ONE UAC prompt on uninstall.
  // Watchdog tasks go FIRST: they relaunch a missing app exe, so they must be gone before the
  // taskkill or they could resurrect the app mid-uninstall. The legacy Lenovo Power Tray
  // exe/tasks are included as free extra cleanup for installs that were upgraded across the
  // rename; all are no-ops on fresh ChargeKeeper installs.
  ShellExec('runas', ExpandConstant('{cmd}'),
            '/C schtasks /Delete /TN "' + WatchdogTaskName + '" /F'
            + ' & schtasks /Delete /TN "{#LegacyWatchdogTask}" /F'
            + ' & taskkill /IM "{#AppExe}" /F & taskkill /IM "{#LegacyExe}" /F'
            + ' & schtasks /Delete /TN "' + TaskName + '" /F'
            + ' & schtasks /Delete /TN "{#LegacyTaskName}" /F',
            '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure RemoveAutoUpdateTask();
var
  ResultCode: Integer;
begin
  // Non-elevated; harmless if the task doesn't exist. Kept for installs carrying one.
  Exec('schtasks.exe', '/Delete /TN "' + UpdateTaskName + '" /F', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure LaunchApp();
var
  ResultCode, i: Integer;
begin
  if ScheduledTaskExists() then
  begin
    // The elevated logon task exists -> run it on demand to start the app elevated with NO extra
    // UAC prompt (scheduled tasks bypass the consent prompt).
    Exec('schtasks.exe', '/Run /TN "' + TaskName + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    // BUT: a task created by an older installer (or the legacy-migration branch in ssInstall) via
    // plain `schtasks /Create` carries the schtasks default DisallowStartIfOnBatteries=true until
    // the app rewrites it power-safe on first run. On battery the scheduler ACCEPTS the /Run but
    // silently declines to launch the action — the exact "app didn't start after install" report.
    // /Run's own exit code is 0 either way, so verify the app actually came up instead: poll
    // briefly (the process is visible immediately, independent of the app's own startup-delay
    // setting) and only fall through to a direct launch if it did not.
    for i := 1 to 6 do
    begin
      if AppIsRunning() then exit;
      Sleep(500);
    end;
  end;
  // No task, or the task-run didn't bring the app up (battery-blocked) -> launch directly. 'runas'
  // raises the UAC consent dialog to the foreground (the app is requireAdministrator); 'open' also
  // works but the dialog can appear behind the installer window and be missed.
  ShellExec('runas', ExpandConstant('{app}\{#AppExe}'), '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
end;

// Exports one task's whole definition to a file under {tmp}, and returns that path whether or not
// anything was written: a task that does not exist leaves the file EMPTY (schtasks reports the
// miss on stderr), and the re-point script reads empty as "no such task". Redirected through cmd
// rather than captured, because schtasks writes /XML output as UTF-16 and only a file keeps it
// that way — which is also the only form schtasks /Create /XML reads back. Querying needs no
// elevation.
function ExportTaskXml(const TaskTitle, FileTitle: string): string;
var
  ResultCode: Integer;
begin
  Result := ExpandConstant('{tmp}\') + FileTitle + '.xml';
  Exec(ExpandConstant('{cmd}'),
       '/C schtasks /Query /TN "' + TaskTitle + '" /XML ONE > "' + Result + '"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Captures BOTH definitions as the app left them, before this run touches either. ssPostInstall
// puts the same definitions back with only their path changed, so everything the app configured —
// the power-safe settings above all — survives the move.
procedure ExportTaskDefinitions();
begin
  AutoStartXmlFile := ExportTaskXml(TaskName, 'autostart-task');
  WatchdogXmlFile  := ExportTaskXml(WatchdogTaskName, 'watchdog-task');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode, i: Integer;
  LegacyWasRunning, LegacyAutoStart: Boolean;
  Cmd: string;
begin
  // Kill any running instance BEFORE files are replaced so nothing is locked.
  // ChargeKeeper.exe is requireAdministrator (elevated), so a non-elevated taskkill is
  // refused with "Access is denied". Elevate via runas — one UAC prompt, then the kill
  // succeeds and the install continues without locked-file errors.
  //
  // This runs from PrepareToInstall, not ssInstall. Order measured from a Setup log:
  // PrepareToInstall -> Restart Manager in-use check -> ssInstall -> file copy. Both code steps
  // precede the copy, but only PrepareToInstall precedes the in-use check that is refused on this
  // elevated app and takes Setup down with it before anything installs (issue #119); it is also
  // the only step that can stop Setup with a readable message, as the return below does.
  //
  // Upgrades from Lenovo Power Tray (<= 1.1.x): the old LenovoTray.exe would also hold
  // file locks in the shared {app} folder, so it is killed in the SAME elevated cmd, and
  // — since we are elevated anyway — the old elevated tasks are cleaned up for free.
  // The legacy Watchdog goes FIRST (it would otherwise try to resurrect the old exe), and
  // if the user had opted into autostart (legacy AutoStart task exists), that choice is
  // MIGRATED: a "{#TaskName}" task pointing at the new exe is created in the same cmd
  // (the app re-registers it with power-safe XML at first startup). An interactive install
  // also elevates when only stale legacy tasks exist; a silent one never adds a prompt.
  // {app} is already resolved here — the directory page runs well before this step.
  Result           := '';
  WasRunning       := AppIsRunning();
  LegacyWasRunning := ProcessIsRunning('{#LegacyExe}');
  LegacyAutoStart  := LegacyTaskExists();
  if MigratingFromLegacy then ExportTaskDefinitions();
  if WasRunning or LegacyWasRunning or MigratingFromLegacy
     or ((LegacyAutoStart or LegacyWatchdogExists()) and not WizardSilent()) then
  begin
    Cmd := '/C schtasks /Delete /TN "{#LegacyWatchdogTask}" /F';
    if MigratingFromLegacy then
      // The Watchdog must be gone for the whole of this run. A probe firing between the taskkill
      // above and the re-point in ssPostInstall would start the OLD exe, which then holds the old
      // folder open and re-points both tasks back at itself. Deleting is recoverable — the app
      // re-registers a missing Watchdog on its next start — whereas DISABLING would not be: a
      // disabled task still matches the app's own definition, so the app would leave it disabled
      // for good. ssPostInstall puts the exported definition back, re-pointed.
      Cmd := Cmd + ' & schtasks /Delete /TN "' + WatchdogTaskName + '" /F';
    Cmd := Cmd
         + ' & taskkill /F /IM "{#AppExe}" & taskkill /F /IM "{#LegacyExe}"'
         + ' & schtasks /Delete /TN "{#LegacyTaskName}" /F';
    if LegacyAutoStart then
      Cmd := Cmd + ' & schtasks /Create /TN "' + TaskName + '" /TR "\"'
           + ExpandConstant('{app}\{#AppExe}') + '\"" /SC ONLOGON /RL HIGHEST /F';
    ShellExec('runas', ExpandConstant('{cmd}'), Cmd, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
  // Either exe having been running qualifies the silent-upgrade restart in ssPostInstall.
  WasRunning := WasRunning or LegacyWasRunning;

  // The legacy "LenovoTray AutoUpdate" logon task is non-elevated, so it can always be
  // removed without a prompt; harmless when it doesn't exist.
  Exec('schtasks.exe', '/Delete /TN "{#LegacyUpdateTask}" /F', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // taskkill returns once termination is requested, and the UAC prompt above can be declined
  // outright. Confirm the process is actually gone: with nothing left to unlock the install can
  // proceed, otherwise stop here with an instruction rather than failing mid-copy on a locked
  // exe. ASCII only — see the note in [Messages].
  for i := 1 to 10 do
  begin
    if not (AppIsRunning() or ProcessIsRunning('{#LegacyExe}')) then exit;
    Sleep(200);
  end;
  Result := '{#AppName} is still running, so its files cannot be replaced. Exit it from its icon '
          + 'in the notification area (the system tray, next to the clock), then run this '
          + 'installer again.';
end;

// The re-point script. It rewrites each exported definition so that ONLY the directory changes —
// every setting the app configured, above all the power-safe ones, is carried across untouched —
// and then VERIFIES the outcome by reading the live tasks back, because the folder deletion that
// follows depends on it. Exit code 0 means neither task starts from the old directory any more.
//
// Written to a file and run through PowerShell because rewriting one element of a task definition
// is beyond schtasks alone. If a machine policy refuses to run the script, the non-zero exit keeps
// the old directory, which is the safe outcome.
//
// ASCII only, and every path arrives as an ARGUMENT rather than embedded text: the file is written
// in the system's ANSI code page, so a path outside it would be mangled in the script but survives
// intact on the command line.
function BuildRepointScript(): string;
begin
  Result :=
    'param([string]$OldDir,[string]$NewDir,[string]$AutoXml,[string]$WatchXml)' + #13#10 +
    '$ErrorActionPreference = ''Stop''' + #13#10 +
    '$auto = ''' + TaskName + '''' + #13#10 +
    '$wdog = ''' + WatchdogTaskName + '''' + #13#10 +
    '$newExe = Join-Path $NewDir ''{#AppExe}''' + #13#10 +
    '' + #13#10 +
    '# Only the leading directory changes, and it appears once. No regular expression, so a path' + #13#10 +
    '# holding characters that are special to one cannot corrupt the result.' + #13#10 +
    'function Swap([string]$s) {' + #13#10 +
    '  if (-not $s) { return $s }' + #13#10 +
    '  $i = $s.IndexOf($OldDir, [StringComparison]::OrdinalIgnoreCase)' + #13#10 +
    '  if ($i -lt 0) { return $s }' + #13#10 +
    '  return $s.Substring(0, $i) + $NewDir + $s.Substring($i + $OldDir.Length)' + #13#10 +
    '}' + #13#10 +
    '' + #13#10 +
    '# A task that did not exist left an EMPTY export, which must not be mistaken for one that did.' + #13#10 +
    'function HasContent([string]$f) { return ($f -and (Test-Path $f) -and ((Get-Item $f).Length -gt 0)) }' + #13#10 +
    '' + #13#10 +
    'function Repoint([string]$name, [string]$file) {' + #13#10 +
    '  if (-not (HasContent $file)) { return $false }' + #13#10 +
    '  $doc = New-Object System.Xml.XmlDocument' + #13#10 +
    '  $doc.PreserveWhitespace = $true' + #13#10 +
    '  $doc.LoadXml((Get-Content -Raw -Path $file))' + #13#10 +
    '  $ns = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)' + #13#10 +
    '  $ns.AddNamespace(''t'', ''http://schemas.microsoft.com/windows/2004/02/mit/task'')' + #13#10 +
    '  foreach ($n in $doc.SelectNodes(''//t:Exec/t:Command'', $ns)) { $n.InnerText = Swap $n.InnerText }' + #13#10 +
    '  foreach ($n in $doc.SelectNodes(''//t:Exec/t:WorkingDirectory'', $ns)) { $n.InnerText = Swap $n.InnerText }' + #13#10 +
    '  $out = Join-Path $env:TEMP (''ck-'' + [guid]::NewGuid().ToString(''N'') + ''.xml'')' + #13#10 +
    '  $doc.Save($out)' + #13#10 +
    '  & schtasks.exe /Create /TN $name /XML $out /F | Out-Null' + #13#10 +
    '  $ok = ($LASTEXITCODE -eq 0)' + #13#10 +
    '  Remove-Item $out -Force -ErrorAction SilentlyContinue' + #13#10 +
    '  return $ok' + #13#10 +
    '}' + #13#10 +
    '' + #13#10 +
    '$ok = $false' + #13#10 +
    'try { $ok = Repoint $auto $AutoXml } catch { $ok = $false }' + #13#10 +
    '# Fallback, and only where a startup task really existed: a plain task at the correct path.' + #13#10 +
    '# It loses the power-safe settings until the app repairs them, which happens seconds later —' + #13#10 +
    '# the installer launches the app at the end of this same run.' + #13#10 +
    'if (-not $ok -and (HasContent $AutoXml)) {' + #13#10 +
    '  $tr = [char]34 + $newExe + [char]34' + #13#10 +
    '  & schtasks.exe /Create /TN $auto /TR $tr /SC ONLOGON /RL HIGHEST /F | Out-Null' + #13#10 +
    '}' + #13#10 +
    '# Best effort: a Watchdog that cannot be restored is re-registered by the app on its next' + #13#10 +
    '# start, and its absence resurrects nothing in the meantime.' + #13#10 +
    'try { [void](Repoint $wdog $WatchXml) } catch { }' + #13#10 +
    '' + #13#10 +
    '# The post-condition, read back off the live tasks rather than inferred from exit codes.' + #13#10 +
    '# The separator matters: without it a NEIGHBOURING folder whose name merely starts the same' + #13#10 +
    '# way would read as the old one.' + #13#10 +
    '$bad = 0' + #13#10 +
    '$oldPrefix = $OldDir.ToLowerInvariant() + [char]92' + #13#10 +
    'foreach ($n in @($auto, $wdog)) {' + #13#10 +
    '  $t = Get-ScheduledTask -TaskName $n -ErrorAction SilentlyContinue' + #13#10 +
    '  if ($t) { foreach ($a in @($t.Actions)) {' + #13#10 +
    '    $p = "$($a.Execute)".Trim([char]34)' + #13#10 +
    '    if ($p -and $p.ToLowerInvariant().StartsWith($oldPrefix)) { $bad = 1 }' + #13#10 +
    '  } }' + #13#10 +
    '}' + #13#10 +
    'exit $bad' + #13#10;
end;

// Points both tasks at the new directory. Elevated, because the startup task runs RL HIGHEST — one
// UAC prompt, or none at all when Setup was started by the already-elevated app.
function RepointTasks(): Boolean;
var
  ScriptFile, Params: string;
  ResultCode: Integer;
begin
  Result := False;
  ScriptFile := ExpandConstant('{tmp}\repoint-tasks.ps1');
  if not SaveStringToFile(ScriptFile, BuildRepointScript(), False) then exit;

  Params := '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptFile + '"'
          + ' "' + RemoveBackslashUnlessRoot(PreviousAppDir) + '"'
          + ' "' + RemoveBackslashUnlessRoot(ExpandConstant('{app}')) + '"'
          + ' "' + AutoStartXmlFile + '" "' + WatchdogXmlFile + '"';
  Result := ShellExec('runas', 'powershell.exe', Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
        and (ResultCode = 0);
end;

procedure RemoveLegacyInstallDir();
var
  Dir, Exe: string;
begin
  Dir := RemoveBackslashUnlessRoot(PreviousAppDir);
  if DelTree(Dir, True, True, True) then
  begin
    Log('Migration: old install directory removed.');
    exit;
  end;

  // Something in there is still open. The binary must not survive in a startable state: a running
  // image cannot be deleted, but it CAN be renamed, and a renamed path starts nothing.
  Exe := Dir + '\{#AppExe}';
  if FileExists(Exe) and not DeleteFile(Exe) then
    RenameFile(Exe, Exe + '.migrated');

  if DelTree(Dir, True, True, True) then
    Log('Migration: old install directory removed on the second pass.')
  else
    Log('Migration: old install directory could not be fully removed; its executable is gone or renamed.');
end;

// Every one of these must hold before anything is removed: this run is a migration, the recorded
// directory really carries the retired name, it is NOT the directory just installed into, and the
// new executable is on disk. The third guard is the one that matters most — without it a
// mis-resolved directory would delete the installation that was just written.
function LegacyMigrationCanProceed(): Boolean;
begin
  Result := MigratingFromLegacy
        and IsLegacyDir(PreviousAppDir)
        and (CompareText(RemoveBackslashUnlessRoot(PreviousAppDir),
                         RemoveBackslashUnlessRoot(ExpandConstant('{app}'))) <> 0)
        and FileExists(ExpandConstant('{app}\{#AppExe}'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    // Before everything else in this step: RegisterStartupTask and LaunchApp below both act on the
    // startup task, and must see it already naming the new directory.
    if LegacyMigrationCanProceed() then
    begin
      if RepointTasks() then
        RemoveLegacyInstallDir()
      else
        // Neither the definition-preserving route nor the plain fallback left the startup task
        // pointing at the new directory. The old one stays: an installation whose startup task
        // names a binary that still exists keeps working, one that names a deleted binary does not.
        Log('Migration: the scheduled tasks could not be re-pointed — old install directory kept.');
    end;

    if WizardIsTaskSelected('runstartup') then RegisterStartupTask();
    // Clears the winget logon task earlier versions created. The app checks GitHub itself.
    RemoveAutoUpdateTask();
    if not WizardSilent() then
      // Interactive install: launch after task creation so a freshly-created startup task
      // is used for a prompt-free launch.
      LaunchApp()
    else if WasRunning and ScheduledTaskExists() then
      // Silent upgrade (winget / AutoUpdate task) that killed a running instance: restart it
      // via the elevated logon task — no UI, no UAC. Without this the background upgrade
      // leaves the tray app dead until the next sign-in. When no task exists we stay silent
      // (a UAC prompt from an unattended install would be wrong) and accept the gap.
      Exec('schtasks.exe', '/Run /TN "' + TaskName + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // usUninstall fires just BEFORE files are removed — stop the app first so its files
  // aren't locked, otherwise the uninstall leaves the exe behind and the app keeps running.
  if CurUninstallStep = usUninstall then
  begin
    // Elevate once only if there's something elevated to do (app running or a HIGHEST task).
    if AppIsRunning() or ScheduledTaskExists() or WatchdogTaskExists() then
      StopAppAndRemoveStartupTask();

    RemoveAutoUpdateTask();   // non-elevated, no prompt
  end;
end;
