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

; AppName is also the install folder's name and must stay equal to
; InstallLocations.ProductFolderName in Helpers\.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

[Setup]
; AppId uniquely identifies this app for upgrades/uninstall — do not change it. A new value would
; orphan every existing install: the old one would never uninstall and both would sit in Apps &
; features.
AppId={{B1F8E4B2-3D7A-4C56-9E2F-7A1C9D5E6F40}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
; UsePreviousAppDir=no hands the choice of {app} to ResolveInstallDir in [Code], which returns the
; directory the previous install recorded, or the default on a fresh install.
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
; #60/#(icon legibility): high-contrast setup icon. SetupIconFile is not merely the wizard's
; title-bar icon — it is Setup.exe's OWN file icon, so it lands on Inno's LIGHT wizard title bar
; (#F3F3F3) and on DARK Explorer / desktop / taskbar (#202020 on Win11 dark), at whatever frame
; size each surface's own DPI scaling requests — an earlier revision split the two treatments by
; frame size (ink at 16 px, a dark plate at 32 px+) on the assumption that only Explorer asks for
; the larger frames; a DPI-scaled title bar asks for one of those too, so the dark plate showed up
; as a dark square in the light wizard window. Every frame is now the dense "ink" glyph on a
; transparent background (11.87:1 on light) with a near-white halo outline underneath the strokes
; (scripts\BatteryGlyph.ps1's Draw-BatteryGlyph): the halo all but disappears on light chrome and
; reads as a defining ring on dark chrome, so no frame's background is decided by its size any
; more. Built by scripts\make-appicon.ps1 -HighContrast.
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
  // is the one line in this file that must never be restructured; the two must agree.
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

// True when the application started this run for its own update. That run is silent like a winget
// or scheduled one and cannot be told from them by WizardSilent, yet it differs in every way that
// matters here: a user asked for it, the application is elevated and exiting for it, and it expects
// to be started again afterwards. The switch is passed by UnattendedUpdate in the application; the
// pair is pinned by the test UnattendedUpdateTests.
function StartedByTheApplication(): Boolean;
begin
  Result := ExpandConstant('{param:UPDATEFROMAPP|0}') = '1';
end;

function InitializeSetup(): Boolean;
begin
  PreviousAppDir := '';
  RegQueryStringValue(HKCU, UninstallKey, 'Inno Setup: App Path', PreviousAppDir);
  Result := True;
end;

// Where this run installs. Called by DefaultDirName, so it runs after InitializeSetup.
function ResolveInstallDir(Param: string): string;
begin
  if PreviousAppDir = '' then
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
  // Dense-steel page headings (issue #66) — the on-white SteelBlue tier
  // ($BatteryGlyphPalettes.Dense.Body in scripts\BatteryGlyph.ps1 = #3F6374). The small wizard
  // header image itself now draws in the denser Ink tier instead (see make-wizard-images.ps1's
  // Render-Small), so the two no longer share one colour — this recolours only the heading
  // labels; body text and everything else stays default, and WizardStyle / the light modern
  // inner-page theme are untouched.
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

// ---------------------------------------------------------------------------
// Retry block. Self-contained on purpose: a presence poll, an elevated termination attempt and the
// loop around them, every one of them named by executable, so another requireAdministrator
// single-instance installer can lift the three routines whole. Nothing beyond {#AppName} and
// {#AppExe} is baked in.
// ---------------------------------------------------------------------------

// True once ExeName is gone. taskkill returns as soon as termination is REQUESTED, and the consent
// prompt behind it can be declined outright, so presence is polled rather than assumed.
function WaitForProcessToExit(const ExeName: string): Boolean;
var
  i: Integer;
begin
  for i := 1 to 10 do
  begin
    if not ProcessIsRunning(ExeName) then
    begin
      Result := True;
      exit;
    end;
    Sleep(200);
  end;
  Result := False;
end;

// One elevated attempt at ending ExeName. Setup runs PrivilegesRequired=lowest while the
// application is requireAdministrator, so an unelevated taskkill is refused with "Access is denied".
procedure StopProcessElevated(const ExeName: string);
var
  ResultCode: Integer;
begin
  ShellExec('runas', ExpandConstant('{cmd}'), '/C taskkill /F /IM "' + ExeName + '"',
            '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Empty once ExeName is gone, otherwise the message Setup stops on. PrepareToInstall's return value
// is terminal — Setup does not re-enter the callback, and the Preparing to Install page carries no
// Retry control of its own — so the retry happens here, inside the callback, before it returns.
// Retry re-tests presence and only then re-attempts the kill, so an application exited from its own
// icon raises no second consent prompt.
//
// A silent run has nobody to answer, so it takes the terminal message immediately: WizardSilent
// covers /SILENT, where Inno Setup still displays error boxes and a modal prompt would orphan
// itself behind no wizard, and the IDCANCEL default covers /SUPPRESSMSGBOXES. Silent behaviour is
// therefore exactly what it was. The two conditions are tested separately rather than in one
// expression, because Pascal Script does not guarantee short-circuit evaluation.
//
// ASCII only in both strings — see the note in [Messages].
function ConfirmProcessHasExited(const ExeName: string): String;
var
  Answer: Integer;
begin
  Result := '';
  while not WaitForProcessToExit(ExeName) do
  begin
    Answer := IDCANCEL;
    if not WizardSilent() then
      Answer := SuppressibleMsgBox(
        '{#AppName} is still running, so its files cannot be replaced.' + #13#10#13#10
        + 'Exit it from its icon in the notification area (the system tray, next to the clock), '
        + 'then choose Retry.',
        mbError, MB_RETRYCANCEL, IDCANCEL);
    if Answer <> IDRETRY then
    begin
      Result := '{#AppName} is still running, so its files cannot be replaced. Exit it from its '
              + 'icon in the notification area (the system tray, next to the clock), then run '
              + 'this installer again.';
      exit;
    end;
    if ProcessIsRunning(ExeName) then StopProcessElevated(ExeName);
  end;
end;

// ---------------------------------------------------------------------------
// The application's own update. Unattended by request: it was agreed to in the application's update
// dialog, so no wizard is shown and no message box can be answered.
// ---------------------------------------------------------------------------

// The application queues its own exit as it starts this run, so that exit is still in flight when
// Setup gets here. Waiting for it is what keeps the update unattended: the termination below is
// elevated, and neither its consent prompt nor the refusal further down has anybody to answer it.
// Roughly sixteen seconds, then the ordinary path takes over — a process still present after that
// is stuck rather than closing.
procedure WaitForTheStartingApplicationToExit();
var
  i: Integer;
begin
  if not StartedByTheApplication() then exit;
  for i := 1 to 8 do
    if WaitForProcessToExit('{#AppExe}') then exit;
end;

// Where a refusal is stated, since an unattended run states it nowhere else: the message box is
// suppressed and the application that asked for the update has exited. The next start reads this
// beside its own record of the attempt and reports both. The file name must stay equal to
// UnattendedUpdate.RefusalFileName in the application; the test UnattendedUpdateTests pins the pair.
// ASCII only — see the note in [Messages].
procedure RecordTheRefusal();
var
  Dir: string;
begin
  Dir := ExpandConstant('{userappdata}\{#AppName}');
  if ForceDirectories(Dir) then
    SaveStringToFile(Dir + '\update-refused.txt',
                     'Setup installed nothing: {#AppName} was still running when it started.',
                     False);
end;

procedure StopAppAndRemoveStartupTask();
var
  ResultCode: Integer;
begin
  // Stopping the running (elevated) app and deleting its RL HIGHEST logon + watchdog tasks all
  // need admin, so do them together in one elevated cmd -> at most ONE UAC prompt on uninstall.
  // The watchdog task goes FIRST: it relaunches a missing app exe, so it must be gone before the
  // taskkill or it could resurrect the app mid-uninstall.
  ShellExec('runas', ExpandConstant('{cmd}'),
            '/C schtasks /Delete /TN "' + WatchdogTaskName + '" /F'
            + ' & taskkill /IM "{#AppExe}" /F'
            + ' & schtasks /Delete /TN "' + TaskName + '" /F',
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
    // BUT: a task created by RegisterStartupTask via plain `schtasks /Create` carries the schtasks default DisallowStartIfOnBatteries=true until
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

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  // Kill any running instance BEFORE files are replaced so nothing is locked.
  // ChargeKeeper.exe is requireAdministrator (elevated), so a non-elevated taskkill is
  // refused with "Access is denied". Elevate via runas -- one UAC prompt, then the kill
  // succeeds and the install continues without locked-file errors.
  //
  // This runs from PrepareToInstall, not ssInstall. Order measured from a Setup log:
  // PrepareToInstall -> Restart Manager in-use check -> ssInstall -> file copy. Both code steps
  // precede the copy, but only PrepareToInstall precedes the in-use check that is refused on this
  // elevated app and takes Setup down with it before anything installs (issue #119); it is also
  // the only step that can stop Setup with a readable message, as the return below does.
  //
  // The application's own update comes through here with its exit already requested, so that exit
  // is waited for FIRST -- before anything reads the process or elevates to end it. Everything below
  // then sees the ordinary case of an application that is simply not running.
  WaitForTheStartingApplicationToExit();
  WasRunning := AppIsRunning();
  if WasRunning then StopProcessElevated('{#AppExe}');

  // taskkill returns once termination is requested, and the UAC prompt above can be declined
  // outright. Confirm the process is actually gone: with nothing left to unlock the install can
  // proceed, otherwise offer a retry, so the application can be exited from its own icon and the
  // installation carried on in the same run rather than started over. Only when that is declined
  // does the terminal message stop Setup, rather than failing mid-copy on a locked exe.
  Result := ConfirmProcessHasExited('{#AppExe}');

  // Setup stops here on a non-empty result, and in an unattended run it stops showing nothing at
  // all. Leave the reason on disk where the next start reads it, so the one failure this flow can
  // have is stated to somebody rather than only counted in an exit code nothing is left to read.
  if (Result <> '') and StartedByTheApplication() then RecordTheRefusal();
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('runstartup') then RegisterStartupTask();
    // Clears the winget logon task earlier versions created. The app checks GitHub itself.
    RemoveAutoUpdateTask();
    if not WizardSilent() then
      // Interactive install: launch after task creation so a freshly-created startup task
      // is used for a prompt-free launch.
      LaunchApp()
    else if StartedByTheApplication() then
      // The application asked for this update and closed itself for it, so it is put back — and
      // unconditionally, unlike the branch below. WasRunning is False here by design (the exit is
      // waited for in PrepareToInstall rather than forced), and gating on the AutoStart task would
      // answer a user's own Update with the application simply gone. LaunchApp prefers that task
      // where it exists and otherwise starts the application directly, which costs at most the one
      // consent prompt the application always needs — and none at all when Setup inherited the
      // elevated token of the application that started it.
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
