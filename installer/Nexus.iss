; Nexus installer (Inno Setup 6)
; Builds Nexus-Setup.exe from the AOT publish output.
;
;   Compile: ISCC.exe Nexus.iss
;   Output:  installer\output\Nexus-Setup.exe
;
; Behaviour:
;   - Single UAC prompt (PrivilegesRequired=admin)
;   - Extracts the AOT payload to %ProgramFiles%\Nexus\
;   - Calls Nexus.exe --install as one elevated step. That primitive
;     handles all the real work: stop+delete existing service, sc create
;     NexusService (LocalSystem, Automatic, depend=PawnIO), grant
;     SERVICE_START to Authenticated Users via DACL, install PawnIO,
;     write Add/Remove Programs reg, open the firewall, start the service.
;   - Drops a Start Menu .lnk to Nexus.exe so Windows search finds it. The
;     .lnk is created natively by [Icons] (no PowerShell dependency); --install
;     re-asserts it and removes the legacy http:// .url shortcuts.
;   - Optionally drops a desktop icon (a checkbox on the directory page, checked
;     by default; see DesktopIconChecked in [Code]).
;
; Uninstall calls Nexus.exe --uninstall which mirrors the install: stop
; service, sc delete, remove firewall rule + edge-swipe policy + Add/Remove
; reg + shortcut.
; Inno then removes the install dir on top of that.

#define MyAppName "Nexus"
; Versions come from build-installer.ps1 (read from the VERSION file). The
; fallbacks only apply to a bare ISCC run with no /D overrides.
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef MyAppVersionInfo
  #define MyAppVersionInfo "0.0.0.0"
#endif
#define MyAppPublisher "Nexus"
#define MyAppURL "https://hellonexus.com"
#define MyAppExeName "Nexus.exe"
#ifndef PublishDir
  #define PublishDir "..\..\aot"
#endif

[Setup]
AppId={{8F2E3A4D-9C5B-4E7A-B1F8-3C2A5E9D0F12}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersionInfo}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
; Machine-scope only: Nexus runs as a LocalSystem Windows Service, which is
; inherently shared by every account on the PC. The per-user install
; option from the previous schtask era is gone.
DefaultDirName={commonpf64}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableWelcomePage=yes
DisableDirPage=no
DisableReadyPage=yes
DisableFinishedPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=Nexus-Setup
OutputDir=output
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
WizardSmallImageFile=logo-small.bmp
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\icon.ico
ShowLanguageDialog=no
; CloseApplications=no keeps Inno from loading RstrtMgr.dll (Restart Manager).
; PrepareToInstall already terminates every process that holds a payload file
; open, which is more precise than what Restart Manager would close for us.
CloseApplications=no
RestartApplications=no
; OTA passes its own /LOG (UpdateInstaller.cs), but a hand-run installer
; carries no switches. Log unconditionally so a user-reported failure has
; a %TEMP%\Setup Log*.txt to read - Log() calls are dropped without this.
SetupLogging=yes
; build-installer.ps1 -Sign defines EnableSigning and the nexussign tool via
; /S. SignedUninstaller matters for Smart App Control: the extracted
; unins000.exe is a PE on the installed image and must be signed like the rest.
#ifdef EnableSigning
SignTool=nexussign
SignedUninstaller=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
; Soften Inno's default "Some elements could not be removed" warning.
; The process-stop logic in [Code] should ensure we always reach
; UninstalledAll, but if a stray file is held open we'd rather not
; alarm the user.
UninstalledMost=%1 uninstall complete.%n%nA few files were still in use and will be cleaned up on next sign-in.

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion; BeforeInstall: UnlockTarget

[Icons]
; Launch shortcut -> Nexus.exe (no args; routes through WindowsLauncher.Run to
; start/recover the service and open the dashboard). A .lnk to the exe is what
; Windows Start search indexes - the previous http:// .url never surfaced.
; Inno writes the Start-menu copy natively (no PowerShell dependency); --install
; re-asserts the same {group}\Nexus.lnk and clears the legacy .url shortcuts.
; AppUserModelID must match Platform.Windows.ToastNotifications.AppUserModelId.
; An unpackaged app gets no interactive toast without a Start-menu shortcut
; carrying this id, and the failure is silent - Windows accepts the toast and
; never draws it. This shortcut is also where the toast's attribution icon
; comes from (measured): the registry IconUri under
; HKCU\Software\Classes\AppUserModelId is NOT read for it, so an unstamped
; .lnk shows the generic placeholder no matter what that value says.
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Comment: "Open the Nexus dashboard"; AppUserModelID: "HelloNexus.Nexus"
; Desktop icon gated on the "Create a desktop shortcut" checkbox rendered on the
; directory page (DesktopIconChecked in [Code]). A [Tasks] entry would instead
; add a separate "Select Additional Tasks" wizard page. The check is also false
; for a silent install (WizardSilent), so an OTA never recreates a deleted icon.
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Comment: "Open the Nexus dashboard"; Check: DesktopIconChecked
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"

[Run]
; Single canonical install call. The --install primitive registers the
; Windows Service, installs PawnIO, opens the firewall, writes Add/Remove
; Programs, and starts the service. It is idempotent so re-running this
; installer is safe.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--install"; Flags: runhidden waituntilterminated; StatusMsg: "Installing Nexus service..."
; Open the dashboard as the chromeless --app window (overlay WebView2, Edge --app
; fallback), the same as the tray's "Open dashboard". runasoriginaluser drops the
; installer's elevation so it launches in the user session, like the tray's
; schtasks path; without it the window would spawn elevated/in the wrong session.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--open-app"; Flags: nowait skipifsilent runasoriginaluser; StatusMsg: "Opening dashboard..."

; NOTE: --uninstall is intentionally NOT run from [UninstallRun]. Running the
; payload {app}\Nexus.exe leaves its file handle held a moment past the
; uninstaller's delete attempt, so Nexus.exe is queued for delete-on-reboot,
; which makes Windows report "previous program not completed" and blocks every
; reinstall. CurUninstallStepChanged runs it from a {tmp} copy instead.

[Code]
// Win32 imports used to lift the wizard (and the uninstaller) above other
// windows after an elevated relaunch. Without this, Windows' foreground-lock
// can leave the window behind the user's existing windows (Explorer, browser,
// etc.) which makes the install look like it stalled.
function SetForegroundWindow(hWnd: Integer): Boolean;
  external 'SetForegroundWindow@user32.dll stdcall';
function ShowWindow(hWnd: Integer; nCmdShow: Integer): Boolean;
  external 'ShowWindow@user32.dll stdcall';
function AllowSetForegroundWindow(dwProcessId: DWORD): Boolean;
  external 'AllowSetForegroundWindow@user32.dll stdcall';
function SetWindowPos(hWnd, hWndInsertAfter, X, Y, cx, cy: Integer; uFlags: Cardinal): Boolean;
  external 'SetWindowPos@user32.dll stdcall';

procedure ForceWindowToFront(Wnd: Integer);
begin
  if Wnd = 0 then exit;
  ShowWindow(Wnd, 5); // SW_SHOW
  // A freshly-elevated process loses SetForegroundWindow to the foreground-lock,
  // so first lift the Z-order with a TOPMOST/NOTOPMOST toggle (Z-order isn't
  // gated by the lock), then claim focus. The flag bits hold position+size and
  // just restack and show the window.
  SetWindowPos(Wnd, -1, 0, 0, 0, 0, $43); // HWND_TOPMOST
  SetWindowPos(Wnd, -2, 0, 0, 0, 0, $43); // HWND_NOTOPMOST
  AllowSetForegroundWindow($FFFFFFFF);     // ASFW_ANY
  SetForegroundWindow(Wnd);
end;

procedure BringWizardToFront();
begin
  if WizardForm <> nil then ForceWindowToFront(WizardForm.Handle);
end;

const
  // Suffix for a destination UnlockTarget could not delete and had to move
  // out of the way. Swept on the next install and on uninstall.
  StaleSuffix = '.nexus-stale';

var
  DesktopShortcutCheck: TNewCheckBox;
  FrontAsserted: Boolean;

function DesktopIconChecked(): Boolean;
begin
  // False for a silent install (and so for OTA) - a background update must never
  // recreate a desktop icon the user deleted - unless the caller asks with
  // /DESKTOPICON=1 (the web installer, which drives this wizard silently on a
  // first install); otherwise follow the dir-page box.
  if WizardSilent() then
    Result := ExpandConstant('{param:DESKTOPICON|0}') = '1'
  else
    Result := DesktopShortcutCheck.Checked;
end;

procedure InitializeWizard();
begin
  BringWizardToFront();

  // Render the "Create a desktop shortcut" option on the directory page itself
  // (a [Tasks] entry would instead add a separate Select Additional Tasks page).
  DesktopShortcutCheck := TNewCheckBox.Create(WizardForm);
  DesktopShortcutCheck.Parent := WizardForm.SelectDirPage;
  DesktopShortcutCheck.Left := WizardForm.DirEdit.Left;
  DesktopShortcutCheck.Width := WizardForm.SelectDirPage.Width - DesktopShortcutCheck.Left;
  DesktopShortcutCheck.Height := ScaleY(17);
  // Sit just above the disk-space label (the page's bottom control), in the empty
  // gap below the path edit. Anchoring *below* DiskSpaceLabel pushed the box off
  // the visible page; going up from it keeps it on-screen and clear of the label.
  DesktopShortcutCheck.Top := WizardForm.DiskSpaceLabel.Top - DesktopShortcutCheck.Height - ScaleY(12);
  DesktopShortcutCheck.Caption := ExpandConstant('{cm:CreateDesktopIcon}');
  DesktopShortcutCheck.Checked := True;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  // InitializeWizard runs before the wizard is visible, so SetForegroundWindow
  // there can no-op; re-assert front once the first page is actually on screen.
  if not FrontAsserted then
  begin
    BringWizardToFront();
    FrontAsserted := True;
  end;
end;

procedure StopServiceIfRunning();
var
  ResultCode: Integer;
  AdbExe: String;
  TaskKill: String;
begin
  // Every process holding a payload file open must be gone before [Files], or
  // Inno reboot-renames the locked file and that pending entry then blocks every
  // later install. Order matters:
  //   1. net stop (not sc stop) blocks until the service reports STOPPED. The
  //      service shuts down in ~1s and reports a clean stop, so no failure-action
  //      restart races us, and its job-owned OpenRGB / overlay are already gone.
  //   2. Nexus.exe: the user-session helper shares this image and keeps
  //      Nexus.exe locked even after the service stops. No /T (it descends into
  //      the matched tree, which could catch Inno's own helper).
  //   3. Sidecar taskkills are a belt in case a kill-job hadn't reaped them yet.
  //   4. adb.exe: the phone/Q-series watchers spawn `adb start-server`, which
  //      DETACHES its own daemon - net stop does not reap it. That daemon (and
  //      any transient adb client) keeps the whole {app}\tools\adb folder locked
  //      (its own image, the sibling DLLs it LoadLibrary'd - AdbWinApi/
  //      AdbWinUsbApi/libwinpthread - and a CWD lock, since the service pins
  //      WorkingDirectory there), so [Files] fails to replace adb.exe with
  //      "DeleteFile ... Access is denied". Kill ONLY the adb whose image is our
  //      bundled copy (matched by exact ExecutablePath, -ieq since Windows paths
  //      are case-folded), so a user's own Android SDK adb is left alone.
  //      taskkill can't filter by path, so PowerShell resolves the PIDs - but the
  //      kill goes through taskkill /F, NOT Stop-Process: the daemon inherited the
  //      service's LocalSystem token, and taskkill self-enables SeDebugPrivilege
  //      to terminate a SYSTEM process (the same reach that reaps the processes
  //      above); a plain Stop-Process from a normal-admin elevation can be denied.
  //      {app} is user-chosen, so any apostrophe in it is doubled first, else it
  //      would close the single-quoted PowerShell literal and skip the kill.
  Exec(ExpandConstant('{sys}\net.exe'), 'stop NexusService', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Nexus.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM OpenRGB-headless.exe /F /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM nexus-overlay.exe /F /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  AdbExe := ExpandConstant('{app}\tools\adb\adb.exe');
  TaskKill := ExpandConstant('{sys}\taskkill.exe');
  StringChangeEx(AdbExe, '''', '''''', True);
  StringChangeEx(TaskKill, '''', '''''', True);
  //   5. msedgewebview2.exe: the dashboard/overlay/kiosk WebView2 children
  //      outlive their host and keep {app}\wwwroot files open, which used to
  //      be Restart Manager's job to clear (CloseApplications=no now). Match
  //      only our own by the --user-data-dir the overlay passes
  //      (%ProgramData%\Nexus\DesktopWebView2), so another app's WebView2 is
  //      left alone; the runtime image lives outside {app}, so a path match
  //      cannot distinguish them. Folded into the same powershell hop as the
  //      adb sweep to keep this to one process spawn.
  //   6. Then wait for the images to unmap. taskkill and Kill both return once
  //      termination is REQUESTED, not once the process is gone, so [Files]
  //      could otherwise start while a dying process still held a payload file.
  //      One budget shared across the names, not one per process. Only the
  //      three killed by image name above: steps 4 and 5 select adb and
  //      msedgewebview2 by path/command line and deliberately spare foreign
  //      ones, which never exit, so waiting on those by name would hand the
  //      whole budget to a process nobody killed. Purely an optimisation - a
  //      clean delete beats a rename-aside - since UnlockTarget handles whatever
  //      is still locked; correctness never rests on the wait being long enough,
  //      which is why it can be this short.
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "Get-Process -Name adb -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Path -ieq ''' + AdbExe + ''' } | ' +
    'ForEach-Object { & ''' + TaskKill + ''' /F /T /PID $_.Id }; ' +
    'Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Name -ieq ''msedgewebview2.exe'' -and ' +
    '$_.CommandLine -like ''*\Nexus\DesktopWebView2*'' } | ' +
    'ForEach-Object { & ''' + TaskKill + ''' /F /PID $_.ProcessId }; ' +
    '$d = [DateTime]::UtcNow.AddMilliseconds(2000); ' +
    'foreach ($p in Get-Process -Name Nexus,OpenRGB-headless,nexus-overlay -ErrorAction SilentlyContinue) { ' +
    '$ms = [int]($d - [DateTime]::UtcNow).TotalMilliseconds; ' +
    'if ($ms -gt 0) { try { [void]$p.WaitForExit($ms) } catch { } } }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Reaps the aside-renames UnlockTarget left behind on a previous install. By the
// time this runs the processes that held them are gone, so a plain delete works.
// Recursive because the locked binaries are spread across {app}, {app}\overlay
// and {app}\tools\adb.
procedure SweepStaleFiles(const Dir: String);
var
  FindRec: TFindRec;
  Full: String;
begin
  if not FindFirst(AddBackslash(Dir) + '*', FindRec) then exit;
  try
    repeat
      if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
      begin
        Full := AddBackslash(Dir) + FindRec.Name;
        // FILE_ATTRIBUTE_REPARSE_POINT: this runs as SYSTEM, so following a
        // junction planted under {app} would redirect the deletes below out of
        // {app} entirely.
        if (FindRec.Attributes and $400) = 0 then
        begin
          // FILE_ATTRIBUTE_DIRECTORY
          if (FindRec.Attributes and $10) <> 0 then
            SweepStaleFiles(Full)
          // Contains, not ends-with: the numbered fallbacks append a digit.
          else if Pos(StaleSuffix, FindRec.Name) > 0 then
            DeleteFile(Full);
        end;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

// Runs once per file of the [Files] wildcard entry, immediately before Inno
// replaces that file. BeforeInstall on a wildcard entry fires per matched file;
// CurrentFileName is that file's destination.
//
// Inno retries a locked destination 4 times and then shows an Abort/Retry/Ignore
// box. OTA passes /SUPPRESSMSGBOXES, which answers it with Abort - so one stuck
// file rolls back the WHOLE update. Clearing the destination here keeps that
// from ever being reachable:
//   - delete it, which is what Inno would do anyway;
//   - a mapped PE refuses DeleteFile with code 5 but permits a rename, so move
//     it aside and let Inno write into the freed name. SweepStaleFiles reaps it.
//   - if neither works the file cannot be replaced at all: log it and let Inno
//     take its normal course, rather than silently shipping a half-old install.
procedure UnlockTarget();
var
  Target, Stale: String;
  I: Integer;
begin
  // Raw first: this arrives already expanded, and re-expanding can only corrupt
  // it - a '{{' in the user-chosen install dir collapses to '{' silently, naming
  // a different file. Expand only when the raw value names nothing, and swallow
  // the "Unknown constant" a literal '{' raises, which would otherwise escape
  // BeforeInstall and abort the install this procedure exists to protect.
  Target := CurrentFileName;
  if not FileExists(Target) then
  begin
    try
      Target := ExpandConstant(Target);
    except
    end;
  end;
  if not FileExists(Target) then exit;
  if DeleteFile(Target) then exit;

  // Numbered fallbacks: a leftover aside-rename from an earlier attempt can
  // still be held by a process that outlived it, in which case neither the
  // delete nor a rename onto that name would succeed. Don't let that one stuck
  // name be what fails the update.
  for I := 0 to 9 do
  begin
    if I = 0 then
      Stale := Target + StaleSuffix
    else
      Stale := Target + StaleSuffix + IntToStr(I);
    DeleteFile(Stale);
    if RenameFile(Target, Stale) then
    begin
      Log('[nexus] locked, renamed aside so the update can continue: ' + Target);
      exit;
    end;
  end;

  Log('[nexus] LOCKED AND UNMOVABLE, install will fail on this file: ' + Target);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopServiceIfRunning();
  // After the kills, so anything a previous install had to rename aside is now
  // unheld and deletes cleanly.
  SweepStaleFiles(ExpandConstant('{app}'));
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  TmpExe: String;
  UninstArgs: String;
  Btns: TArrayOfString;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopServiceIfRunning();
    // Aside-renames are not in the uninstall log, so without this they survive
    // and leave {app} behind as a non-empty directory.
    SweepStaleFiles(ExpandConstant('{app}'));
    // Lift the uninstall progress form (and so the data-wipe dialog parented to
    // it) above the user's other windows, same foreground-lock fix as the wizard.
    ForceWindowToFront(UninstallProgressForm.Handle);
    // Ask here, not in InitializeUninstall: by usUninstall the uninstall
    // progress form exists, so the dialog parents to it and shows on top. A
    // dialog in InitializeUninstall has no parent window and can hide behind
    // other windows (looked like "no prompt"). TaskDialogMsgBox lets the buttons
    // say what they do. The instruction is a "Keep?" question, not "Remove?", on
    // purpose: if the TaskDialog API is ever unavailable, TaskDialogMsgBox falls
    // back to a plain Yes/No MsgBox that ignores the custom labels, and only the
    // "Keep?" wording keeps Yes=keep / No=remove reading correctly there too.
    // "Keep my data" is the first/default button so Enter never wipes data;
    // "Remove all data" (the No button) adds --purge, clearing %ProgramData%\Nexus\.
    UninstArgs := '--uninstall';
    SetArrayLength(Btns, 2);
    Btns[0] := 'Keep my data';     // Yes -> keep (default)
    Btns[1] := 'Remove all data';  // No  -> purge
    if TaskDialogMsgBox('Keep your Nexus data?',
        'Removing it permanently erases:' + #13#10 +
        '     - All settings and profiles' + #13#10 +
        '     - Installed apps and widget layouts' + #13#10 +
        '     - Paired devices and remote sessions' + #13#10 +
        '     - Screen-time history and imported media' + #13#10 +
        '     - Logs, downloads and caches' + #13#10 + #13#10 +
        'This can''t be undone.',
        mbConfirmation, MB_YESNO, Btns, 0) = IDNO then
      UninstArgs := UninstArgs + ' --purge';
    TmpExe := ExpandConstant('{tmp}\nexus-uninst.exe');
    if FileCopy(ExpandConstant('{app}\{#MyAppExeName}'), TmpExe, False) then
      Exec(TmpExe, UninstArgs, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode)
    else
      Exec(ExpandConstant('{app}\{#MyAppExeName}'), UninstArgs, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
