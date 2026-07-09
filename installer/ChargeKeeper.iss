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
#define WingetId      "0z00z0.ChargeKeeper"

; Legacy names from this app's previous identity ("Lenovo Power Tray", v1.1.x and older).
; Kept ONLY so an in-place upgrade can kill the old process and clean up its leftovers —
; see [InstallDelete] and the legacy cleanup in [Code].
#define LegacyExe          "LenovoTray.exe"
#define LegacyTaskName     "LenovoTray AutoStart"
#define LegacyUpdateTask   "LenovoTray AutoUpdate"
#define LegacyWatchdogTask "LenovoTray Watchdog"

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

[Setup]
; AppId uniquely identifies this app for upgrades/uninstall — do not change it.
; Deliberately UNCHANGED across the Lenovo Power Tray -> ChargeKeeper rename so existing
; 1.1.x installs upgrade in place. Consequence: upgraded installs keep living in their old
; "%LocalAppData%\Programs\Lenovo Power Tray" folder (Inno reuses the recorded {app}),
; while fresh installs get "...\ChargeKeeper". Cosmetic only — accepted trade-off.
AppId={{B1F8E4B2-3D7A-4C56-9E2F-7A1C9D5E6F40}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
; Per-user: installs under %LocalAppData%\Programs, no UAC for the install itself.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=ChargeKeeper-Setup-{#AppVersion}
SetupIconFile=..\Assets\AppIcon.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Let a silent (background) update close the running tray app and replace its files.
; Do NOT auto-restart it afterwards — the app is requireAdministrator, so relaunching
; would pop a UAC prompt out of nowhere. It returns at the next sign-in / manual launch.
CloseApplications=yes
RestartApplications=no

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
Type: files; Name: "{app}\LenovoRed-*.ico"

[Icons]
; Per-user "All apps" Start-menu entry. IconFilename is set explicitly so the shortcut
; always shows the embedded app icon (some shells don't pick it up from the target alone).
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\AppIcon.ico"; Comment: "{#AppName}"
; Optional desktop shortcut (off by default; ticked via the task below).
Name: "{userdesktop}\{#AppName}";  Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"; Tasks: desktopicon

[Tasks]
Name: "runstartup"; Description: "Run {#AppName} automatically at sign-in (starts elevated without a UAC prompt at boot)"; Flags: unchecked
Name: "autoupdate"; Description: "Auto update in background (checks for updates via winget after each sign-in)"
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

var
  // True when ssInstall found (and killed) a running instance. Lets a SILENT upgrade
  // (winget / the AutoUpdate task) restart the app it killed: without this, a background
  // upgrade leaves the tray app dead until the next sign-in.
  WasRunning: Boolean;

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

procedure RegisterAutoUpdateTask();
var
  ResultCode: Integer;
  Params: string;
begin
  // Per-user, NON-elevated logon task (runs 5 min after sign-in) that lets winget pull
  // any newer published version silently. No /RL HIGHEST -> creating it needs no admin,
  // so the "Auto update in background" option never triggers a UAC prompt.
  Params := '/Create /TN "' + UpdateTaskName + '" /TR "winget upgrade --id {#WingetId} '
          + '--silent --accept-package-agreements --accept-source-agreements" /SC ONLOGON '
          + '/DELAY 0005:00 /F';
  Exec('schtasks.exe', Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure RemoveAutoUpdateTask();
var
  ResultCode: Integer;
begin
  // Non-elevated; harmless if the task doesn't exist.
  Exec('schtasks.exe', '/Delete /TN "' + UpdateTaskName + '" /F', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure LaunchApp();
var
  ResultCode: Integer;
begin
  if ScheduledTaskExists() then
    // The elevated logon task exists -> run it on demand to start the app elevated
    // with NO extra UAC prompt (scheduled tasks bypass the consent prompt).
    Exec('schtasks.exe', '/Run /TN "' + TaskName + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
  else
    // No task -> use 'runas' so the UAC consent dialog is raised to the foreground.
    // 'open' also works but the dialog can appear behind the installer window and be missed.
    ShellExec('runas', ExpandConstant('{app}\{#AppExe}'), '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  LegacyWasRunning, LegacyAutoStart: Boolean;
  Cmd: string;
begin
  if CurStep = ssInstall then
  begin
    // Kill any running instance BEFORE files are replaced so nothing is locked.
    // ChargeKeeper.exe is requireAdministrator (elevated), so a non-elevated taskkill is
    // refused with "Access is denied". Elevate via runas — one UAC prompt, then the kill
    // succeeds and the install continues without locked-file errors.
    //
    // Upgrades from Lenovo Power Tray (<= 1.1.x): the old LenovoTray.exe would also hold
    // file locks in the shared {app} folder, so it is killed in the SAME elevated cmd, and
    // — since we are elevated anyway — the old elevated tasks are cleaned up for free.
    // The legacy Watchdog goes FIRST (it would otherwise try to resurrect the old exe), and
    // if the user had opted into autostart (legacy AutoStart task exists), that choice is
    // MIGRATED: a "{#TaskName}" task pointing at the new exe is created in the same cmd
    // (the app re-registers it with power-safe XML at first startup). An interactive install
    // also elevates when only stale legacy tasks exist; a silent one never adds a prompt.
    WasRunning       := AppIsRunning();
    LegacyWasRunning := ProcessIsRunning('{#LegacyExe}');
    LegacyAutoStart  := LegacyTaskExists();
    if WasRunning or LegacyWasRunning or ((LegacyAutoStart or LegacyWatchdogExists()) and not WizardSilent()) then
    begin
      Cmd := '/C schtasks /Delete /TN "{#LegacyWatchdogTask}" /F'
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
    // removed without a prompt; harmless when it doesn't exist. Its ChargeKeeper
    // replacement is created in ssPostInstall when the autoupdate task is ticked.
    Exec('schtasks.exe', '/Delete /TN "{#LegacyUpdateTask}" /F', '',
         SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('runstartup') then RegisterStartupTask();
    if WizardIsTaskSelected('autoupdate') then RegisterAutoUpdateTask();
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
