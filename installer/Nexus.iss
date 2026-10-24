; Nexus installer (Inno Setup 6)
; Builds Nexus-Setup.exe from the AOT publish output.
;
;   Compile: ISCC.exe Nexus.iss
;   Output:  installer\output\Nexus-Setup.exe
;
; Behaviour:
;   - Single UAC prompt (PrivilegesRequired=admin)
;   - If no Microsoft Edge WebView2 Runtime is registered (debloated images,
;     a manual uninstall), shows a page saying so, then downloads Microsoft's
;     Evergreen Standalone x64 installer and runs it silently before the
;     payload lands (PrepareToInstall in [Code]). The dashboard, widgets and
;     panel kiosk are WebView2 hosts, so without it they come up black.
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
; Inno's file-copy and [Run] failures. By the time these show, PrepareToInstall
; and UnlockTarget have cleared every lock of ours, so what remains is almost
; always antivirus quarantining a payload file or blocking it from running.
#define AntivirusCause "Antivirus software blocking Nexus by mistake (a false positive) is the usual cause."
#define AntivirusDocsUrl "https://hellonexus.com/docs/troubleshooting/antivirus"
#define AntivirusFix "Open your antivirus, restore the file above from its quarantine, add an exception for the Nexus folder"
ErrorCreatingTemp=Setup could not save this file. {#AntivirusCause}%n%n{#AntivirusFix}, then click Try again.%n%nMore information: {#AntivirusDocsUrl}%n%nDetails:
ErrorRenamingTemp=Setup could not save this file. {#AntivirusCause}%n%n{#AntivirusFix}, then click Try again.%n%nMore information: {#AntivirusDocsUrl}%n%nDetails:
ErrorReplacingExistingFile=Setup could not replace this file. {#AntivirusCause}%n%n{#AntivirusFix}, then click Try again.%n%nMore information: {#AntivirusDocsUrl}%n%nDetails:
ErrorExecutingProgram=Setup could not run this file:%n%1%n%n{#AntivirusCause}%n%n{#AntivirusFix}, then run Setup again.%n%nMore information: {#AntivirusDocsUrl}

[InstallDelete]
Type: filesandordirs; Name: "{app}\tools\gamesync"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion; BeforeInstall: UnlockTarget

[UninstallDelete]
; GPU shader-program and thumbnail caches the service writes beside the exe.
Type: filesandordirs; Name: "{app}\cache"

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
; Antivirus can quarantine Nexus.exe during the copy or when --install runs it,
; so it is checked on both sides (PayloadPresent, RerunInstallIfBlocked).
Filename: "{app}\{#MyAppExeName}"; Parameters: "--install"; Flags: runhidden waituntilterminated; StatusMsg: "Installing Nexus service..."; Check: PayloadPresent; AfterInstall: RerunInstallIfBlocked
; Open the dashboard as the chromeless --app window (overlay WebView2, Edge --app
; fallback), the same as the tray's "Open dashboard". runasoriginaluser drops the
; installer's elevation so it launches in the user session, like the tray's
; schtasks path; without it the window would spawn elevated/in the wrong session.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--open-app"; Flags: nowait skipifsilent runasoriginaluser; StatusMsg: "Opening dashboard..."; Check: PayloadInstalled

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

// Process lookups for StopServiceIfRunning and the Authenticode check for the
// WebView2 download, native so the installer spawns no scripting host. Every
// declaration assumes Setup is a 32-bit process; InitializeSetup enforces it.
type
  TTrustGuid = record
    D1, D2, D3, D4: Cardinal;
  end;
  TTrustFileInfo = record
    Size, FilePath, FileHandle, KnownSubject: Cardinal;
  end;
  TTrustData = record
    Size, PolicyData, SipData, UIChoice, Revocation, UnionChoice, FileInfo,
    StateAction, StateData, UrlRef, ProvFlags, UIContext, SigSettings: Cardinal;
  end;

function NxSnapshot(Flags, Pid: Cardinal): Cardinal;
  external 'CreateToolhelp32Snapshot@kernel32.dll stdcall';
function NxProcessFirst(Snap: Cardinal; Entry: Cardinal): Boolean;
  external 'Process32FirstW@kernel32.dll stdcall';
function NxProcessNext(Snap: Cardinal; Entry: Cardinal): Boolean;
  external 'Process32NextW@kernel32.dll stdcall';
function NxOpenProcess(Access: Cardinal; Inherit: Integer; Pid: Cardinal): Cardinal;
  external 'OpenProcess@kernel32.dll stdcall';
function NxCloseHandle(Handle: Cardinal): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function NxLastError(): Cardinal;
  external 'GetLastError@kernel32.dll stdcall';
function NxCurrentProcess(): Cardinal;
  external 'GetCurrentProcess@kernel32.dll stdcall';
function NxIsWow64(Process: Cardinal; var Wow64: Cardinal): Boolean;
  external 'IsWow64Process@kernel32.dll stdcall';
function NxImageName(Process, Flags: Cardinal; Buf: Cardinal; var Size: Cardinal): Boolean;
  external 'QueryFullProcessImageNameW@kernel32.dll stdcall';
function NxQueryProcess(Process, InfoClass: Cardinal; Buf, Len: Cardinal; var Returned: Cardinal): Cardinal;
  external 'NtQueryInformationProcess@ntdll.dll stdcall delayload';
function NxWaitObject(Handle, Milliseconds: Cardinal): Cardinal;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function NxTickCount(): Cardinal;
  external 'GetTickCount@kernel32.dll stdcall';
procedure NxCopyMemory(var Dest: Cardinal; Src, Len: Cardinal);
  external 'RtlMoveMemory@kernel32.dll stdcall';
procedure NxPutDword(Dest: Cardinal; var Src: Cardinal; Len: Cardinal);
  external 'RtlMoveMemory@kernel32.dll stdcall';
function NxNextChar(const S: String): Cardinal;
  external 'CharNextW@user32.dll stdcall';
function NxRecordNext(var R: TTrustFileInfo): Cardinal;
  external 'CharNextW@user32.dll stdcall';
function NxVerifyTrust(Wnd: Cardinal; var Action: TTrustGuid; var Data: TTrustData): Integer;
  external 'WinVerifyTrust@wintrust.dll stdcall delayload';
function NxProvData(State: Cardinal): Cardinal;
  external 'WTHelperProvDataFromStateData@wintrust.dll stdcall delayload';
function NxProvSigner(Prov, Index, Counter, CounterIndex: Cardinal): Cardinal;
  external 'WTHelperGetProvSignerFromChain@wintrust.dll stdcall delayload';
function NxProvCert(Signer, Index: Cardinal): Cardinal;
  external 'WTHelperGetProvCertFromChain@wintrust.dll stdcall delayload';
function NxCertName(Cert, NameType, Flags: Cardinal; var TypePara: Cardinal; Name, Chars: Cardinal): Cardinal;
  external 'CertGetNameStringW@crypt32.dll stdcall delayload';

const
  TH32CS_SNAPPROCESS = 2;
  PROCESS_QUERY_LIMITED_INFORMATION = $1000;
  SYNCHRONIZE_ACCESS = $100000;
  ProcessCommandLineInformation = 60;
  ERROR_ACCESS_DENIED = 5;
  STATUS_INFO_LENGTH_MISMATCH = $C0000004;
  WTD_UI_NONE = 2;
  WTD_CHOICE_FILE = 1;
  WTD_STATEACTION_VERIFY = 1;
  WTD_STATEACTION_CLOSE = 2;
  CERT_NAME_RDN_TYPE = 2;
  CERT_X500_NAME_STR = 3;

// A writable buffer for a DLL to fill. Pascal Script passes a var String as a
// pointer to the interpreter's string variable, not to its characters, so the
// buffer is handed over as an address: CharNextW returns its input advanced by
// one character, which needs a non-null first character.
function NewBuffer(Chars: Integer): String;
var
  I: Integer;
begin
  SetLength(Result, Chars);
  for I := 1 to Chars do Result[I] := ' ';
end;

function BufferAddress(const Buf: String): Cardinal;
begin
  Result := NxNextChar(Buf) - 2;
end;

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
  // Edge Update's client id for the WebView2 Runtime; pv under it is the
  // installed version (Microsoft's documented detection).
  WebView2ClientKey = 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2UserClientKey = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  // Microsoft's permalink for the Evergreen Standalone x64 installer. The
  // bootstrapper would fetch the same payload itself, invisibly; the
  // standalone one downloads through Inno's page, so the long part has a
  // real progress bar.
  WebView2InstallerUrl = 'https://go.microsoft.com/fwlink/?linkid=2124701';
  WebView2InstallerName = 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe';

var
  DesktopShortcutCheck: TNewCheckBox;
  FrontAsserted: Boolean;
  WebView2Missing: Boolean;
  WebView2DownloadPage: TDownloadWizardPage;

// A per-machine registration whose version folder is gone (a failed Edge
// update) fails the WebView2 loader the same way as no runtime, and the
// runtime installer repairs both, so the file is checked too.
function WebView2Installed(): Boolean;
var
  Version: String;
begin
  Result := False;
  if RegQueryStringValue(HKLM, WebView2ClientKey, 'pv', Version)
     and (Version <> '') and (Version <> '0.0.0.0') then
    Result := FileExists(ExpandConstant('{commonpf32}\Microsoft\EdgeWebView\Application\' + Version + '\msedgewebview2.exe'));
  if not Result and RegQueryStringValue(HKCU, WebView2UserClientKey, 'pv', Version) then
    Result := (Version <> '') and (Version <> '0.0.0.0');
  Log('[nexus] WebView2 runtime installed=' + IntToStr(Integer(Result)) + ' pv=' + Version);
end;

// The download is trusted on TLS alone, so before it runs elevated require a
// valid Authenticode chain with Microsoft as the signer.
function WebView2InstallerTrusted(const Path: String): Boolean;
var
  Action: TTrustGuid;
  Data: TTrustData;
  FileInfo: TTrustFileInfo;
  Prov, Signer, CertCtx, CertPtr, TypePara: Cardinal;
  Subject: String;
  Chars: Cardinal;
begin
  Result := False;
  Action.D1 := $00AAC56B;
  Action.D2 := $11D0CD44;
  Action.D3 := $C000C28C;
  Action.D4 := $EE95C24F;
  FileInfo.Size := 16;
  FileInfo.FilePath := BufferAddress(Path);
  FileInfo.FileHandle := 0;
  FileInfo.KnownSubject := 0;
  Data.Size := 52;
  Data.PolicyData := 0;
  Data.SipData := 0;
  Data.UIChoice := WTD_UI_NONE;
  Data.Revocation := 0;
  Data.UnionChoice := WTD_CHOICE_FILE;
  Data.StateAction := WTD_STATEACTION_VERIFY;
  Data.StateData := 0;
  Data.UrlRef := 0;
  Data.ProvFlags := 0;
  Data.UIContext := 0;
  Data.SigSettings := 0;
  // The union member is a pointer to the file info; Pascal Script cannot take
  // an address, so both pointers come from CharNextW, which returns the input
  // pointer advanced by one character.
  Data.FileInfo := 0;
  try
    Data.FileInfo := NxRecordNext(FileInfo) - 2;
    if NxVerifyTrust($FFFFFFFF, Action, Data) = 0 then
    begin
      Prov := NxProvData(Data.StateData);
      Signer := 0;
      if Prov <> 0 then Signer := NxProvSigner(Prov, 0, 0, 0);
      if Signer <> 0 then CertPtr := NxProvCert(Signer, 0) else CertPtr := 0;
      if CertPtr <> 0 then
      begin
        NxCopyMemory(CertCtx, CertPtr + 4, 4);
        TypePara := CERT_X500_NAME_STR;
        Subject := NewBuffer(512);
        Chars := NxCertName(CertCtx, CERT_NAME_RDN_TYPE, 0, TypePara, BufferAddress(Subject), 512);
        if Chars > 1 then
          Result := Pos(AnsiLowercase('O=Microsoft Corporation'), AnsiLowercase(Copy(Subject, 1, Chars - 1))) > 0;
      end;
    end;
  finally
    Data.StateAction := WTD_STATEACTION_CLOSE;
    NxVerifyTrust($FFFFFFFF, Action, Data);
  end;
  Log('[nexus] WebView2 installer signature trusted=' + IntToStr(Integer(Result)));
end;

// Download + silent install of the runtime, ahead of the payload so the
// dashboard --open-app launches into a working WebView2. A failure never
// blocks the Nexus install: the overlay asks again on first launch.
procedure InstallWebView2Runtime();
var
  SetupPath: String;
  ResultCode: Integer;
begin
  WebView2DownloadPage.Clear;
  WebView2DownloadPage.Add(WebView2InstallerUrl, WebView2InstallerName, '');
  WebView2DownloadPage.Show;
  try
    try
      WebView2DownloadPage.Download;
    except
      Log('[nexus] WebView2 installer download failed: ' + GetExceptionMessage);
      SuppressibleMsgBox('The Microsoft Edge WebView2 Runtime could not be downloaded (' + GetExceptionMessage + ').'#13#10#13#10
        + 'Nexus will be installed anyway and will ask to install the runtime when it first opens.',
        mbInformation, MB_OK, IDOK);
      exit;
    end;
    SetupPath := ExpandConstant('{tmp}\' + WebView2InstallerName);
    WebView2DownloadPage.SetText('Installing the Microsoft Edge WebView2 Runtime...', '');
    WebView2DownloadPage.ProgressBar.Style := npbstMarquee;
    WebView2DownloadPage.AbortButton.Visible := False;
    if not WebView2InstallerTrusted(SetupPath) then
      ResultCode := -2
    else if not Exec(SetupPath, '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      ResultCode := -1;
    Log('[nexus] WebView2 installer exit=' + IntToStr(ResultCode));
    if not WebView2Installed() then
      SuppressibleMsgBox('The Microsoft Edge WebView2 Runtime did not install (code ' + IntToStr(ResultCode) + ').'#13#10#13#10
        + 'Nexus will be installed anyway and will ask to install the runtime when it first opens.',
        mbInformation, MB_OK, IDOK);
  finally
    WebView2DownloadPage.Hide;
  end;
end;

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

  WebView2Missing := not WebView2Installed();
  if WebView2Missing then
  begin
    CreateOutputMsgPage(wpSelectDir,
      'Microsoft Edge WebView2 Runtime',
      'A Windows component Nexus needs is missing on this PC',
      'Nexus displays its windows through the native Microsoft Edge WebView2 Runtime. '
      + 'Windows normally includes it, but it was not found on this PC.'#13#10#13#10
      + 'Setup will download it from Microsoft (about 200 MB) and install it before installing Nexus. '
      + 'This needs an internet connection.');
    WebView2DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
  end;

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

// Our Nexus.exe paths: {app} plus the exe NexusService is registered to run,
// which differs when an upgrade picks a new folder.
function OwnNexusExes(): TArrayOfString;
var
  ImagePath: String;
  Q: Integer;
begin
  SetArrayLength(Result, 1);
  Result[0] := ExpandConstant('{app}\{#MyAppExeName}');
  if RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Services\NexusService', 'ImagePath', ImagePath) then
  begin
    ImagePath := Trim(ImagePath);
    if Copy(ImagePath, 1, 1) = '"' then
    begin
      Delete(ImagePath, 1, 1);
      Q := Pos('"', ImagePath);
      if Q > 0 then ImagePath := Copy(ImagePath, 1, Q - 1);
    end
    else
    begin
      Q := Pos('.exe', Lowercase(ImagePath));
      if Q > 0 then ImagePath := Copy(ImagePath, 1, Q + 3);
    end;
    if ImagePath <> '' then
    begin
      SetArrayLength(Result, 2);
      Result[1] := ImagePath;
    end;
  end;
end;

function SameName(const A, B: String): Boolean;
begin
  Result := AnsiLowercase(A) = AnsiLowercase(B);
end;

// Process ids whose image name is Name, from a ToolHelp snapshot. The entry is
// a PROCESSENTRY32W held in a string buffer (Pascal Script records cannot hold
// the inline name array); the size, pid and name are read at their fixed
// offsets in that layout.
function ProcessIdsByName(const Name: String): array of Cardinal;
var
  Snap, Size: Cardinal;
  Entry, Exe: String;
  I, N: Integer;
  More: Boolean;
begin
  SetArrayLength(Result, 0);
  Snap := NxSnapshot(TH32CS_SNAPPROCESS, 0);
  if Snap = $FFFFFFFF then exit;
  try
    Entry := NewBuffer(278);
    Size := 556;
    NxPutDword(BufferAddress(Entry), Size, 4);
    More := NxProcessFirst(Snap, BufferAddress(Entry));
    while More do
    begin
      Exe := '';
      for I := 19 to 278 do
      begin
        if Entry[I] = #0 then break;
        Exe := Exe + Entry[I];
      end;
      if SameName(Exe, Name) then
      begin
        N := GetArrayLength(Result);
        SetArrayLength(Result, N + 1);
        Result[N] := Ord(Entry[5]) + Ord(Entry[6]) * 65536;
      end;
      More := NxProcessNext(Snap, BufferAddress(Entry));
    end;
  finally
    NxCloseHandle(Snap);
  end;
end;

// A query handle keeps the pid from being recycled while a kill is pending, so
// callers hold it until taskkill returns. Err is the open failure, 0 on success.
function OpenQuery(Pid: Cardinal; var Err: Cardinal): Cardinal;
begin
  Result := NxOpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, Pid);
  Err := 0;
  if Result = 0 then Err := NxLastError();
end;

// Empty when the path cannot be read.
function HandleImagePath(H: Cardinal): String;
var
  Len: Cardinal;
  Buf: String;
begin
  Result := '';
  Buf := NewBuffer(1024);
  Len := 1024;
  if NxImageName(H, 0, BufferAddress(Buf), Len) then Result := Copy(Buf, 1, Len);
end;

// The buffer holds a UNICODE_STRING header (two lengths and a pointer, sized for
// this 32-bit process) followed by the text. A command line too long for the
// first buffer reports the size it needs, and is retried once at that size.
function HandleCommandLine(H: Cardinal): String;
var
  Returned, Status: Cardinal;
  Buf: String;
  Chars, Bytes: Integer;
begin
  Result := '';
  Chars := 4096;
  Buf := NewBuffer(Chars);
  Status := NxQueryProcess(H, ProcessCommandLineInformation, BufferAddress(Buf), Chars * 2, Returned);
  if Status = STATUS_INFO_LENGTH_MISMATCH then
  begin
    Chars := Returned div 2 + 8;
    Buf := NewBuffer(Chars);
    Status := NxQueryProcess(H, ProcessCommandLineInformation, BufferAddress(Buf), Chars * 2, Returned);
  end;
  if Status = 0 then
  begin
    Bytes := Ord(Buf[1]);
    if Bytes > 0 then Result := Copy(Buf, 5, Bytes div 2);
  end;
end;

// Selected by path (our exes, or a folder holding our overlay, which covers a
// junction/subst/8.3 spelling) because other products ship a Nexus.exe too. A
// process that cannot be opened for access reasons counts as ours; one that
// already exited does not. H stays open for the caller to release after the kill.
function IsOwnNexus(Pid: Cardinal; const Own: TArrayOfString; var H: Cardinal): Boolean;
var
  Path: String;
  Err: Cardinal;
  I: Integer;
begin
  Result := False;
  H := OpenQuery(Pid, Err);
  if H = 0 then
  begin
    Result := Err = ERROR_ACCESS_DENIED;
    exit;
  end;
  Path := HandleImagePath(H);
  if Path = '' then exit;
  for I := 0 to GetArrayLength(Own) - 1 do
    if SameName(Own[I], Path) then
    begin
      Result := True;
      exit;
    end;
  Result := FileExists(ExtractFilePath(Path) + 'overlay\nexus-overlay.exe');
end;

// Image is the name the pid is expected to still have, so a recycled pid that
// now belongs to another program is not killed.
procedure KillPid(Pid: Cardinal; Tree: Boolean; const Image: String);
var
  ResultCode: Integer;
  Args: String;
begin
  Args := '/F ';
  if Tree then Args := Args + '/T ';
  if Image <> '' then Args := Args + '/FI "IMAGENAME eq ' + Image + '" ';
  Exec(ExpandConstant('{sys}\taskkill.exe'), Args + '/PID ' + IntToStr(Pid), '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Kills our own Nexus.exe processes, no /T (it descends into the matched tree,
// which could catch Inno's own helper), and returns the pids still to wait on.
function KillOwnNexus(): array of Cardinal;
var
  All: array of Cardinal;
  Own: TArrayOfString;
  H: Cardinal;
  I, N: Integer;
begin
  SetArrayLength(Result, 0);
  Own := OwnNexusExes();
  All := ProcessIdsByName('{#MyAppExeName}');
  for I := 0 to GetArrayLength(All) - 1 do
  begin
    if IsOwnNexus(All[I], Own, H) then
    begin
      KillPid(All[I], False, '{#MyAppExeName}');
      N := GetArrayLength(Result);
      SetArrayLength(Result, N + 1);
      Result[N] := All[I];
    end;
    if H <> 0 then NxCloseHandle(H);
  end;
end;

// Kills processes named Name selected by image path when Path is set, else by a
// command line fragment. A process that cannot be opened is skipped.
procedure KillMatching(const Name, Path, CmdPart: String; Tree: Boolean);
var
  Pids: array of Cardinal;
  H, Err: Cardinal;
  I: Integer;
  Hit: Boolean;
begin
  Pids := ProcessIdsByName(Name);
  for I := 0 to GetArrayLength(Pids) - 1 do
  begin
    H := OpenQuery(Pids[I], Err);
    if H = 0 then continue;
    if Path <> '' then
      Hit := SameName(HandleImagePath(H), Path)
    else
      Hit := Pos(AnsiLowercase(CmdPart), AnsiLowercase(HandleCommandLine(H))) > 0;
    if Hit then KillPid(Pids[I], Tree, '');
    NxCloseHandle(H);
  end;
end;

// Waits for each pid to exit, sharing one budget across all of them.
procedure WaitForPids(const Pids: array of Cardinal; BudgetMs: Integer);
var
  Start: Cardinal;
  Left: Int64;
  H: Cardinal;
  I: Integer;
begin
  Start := NxTickCount();
  for I := 0 to GetArrayLength(Pids) - 1 do
  begin
    Left := Int64(NxTickCount()) - Int64(Start);
    if Left < 0 then Left := Left + 4294967296;
    Left := BudgetMs - Left;
    if Left <= 0 then exit;
    H := NxOpenProcess(SYNCHRONIZE_ACCESS, 0, Pids[I]);
    if H <> 0 then
    begin
      NxWaitObject(H, Cardinal(Left));
      NxCloseHandle(H);
    end;
  end;
end;

procedure StopServiceIfRunning();
var
  ResultCode: Integer;
  AdbExe: String;
  Pids, Waits, Extra: array of Cardinal;
  I, N: Integer;
begin
  // Every process holding a payload file open must be gone before [Files], or
  // Inno reboot-renames the locked file and that pending entry then blocks every
  // later install. Order matters:
  //   1. net stop (not sc stop) blocks until the service reports STOPPED. The
  //      service shuts down in ~1s and reports a clean stop, so no failure-action
  //      restart races us, and its job-owned OpenRGB / overlay are already gone.
  //   2. Nexus.exe: the user-session helper shares this image and keeps
  //      Nexus.exe locked even after the service stops. Selected by path
  //      (IsOwnNexus), then killed by pid. No /T (it descends into the matched
  //      tree, which could catch Inno's own helper).
  //   3. Sidecar taskkills are a belt in case a kill-job hadn't reaped them yet.
  //   4. adb.exe: the phone/Q-series watchers spawn `adb start-server`, which
  //      DETACHES its own daemon - net stop does not reap it. That daemon (and
  //      any transient adb client) keeps the whole {app}\tools\adb folder locked
  //      (its own image, the sibling DLLs it LoadLibrary'd - AdbWinApi/
  //      AdbWinUsbApi - and a CWD lock, since the service pins
  //      WorkingDirectory there), so [Files] fails to replace adb.exe with
  //      "DeleteFile ... Access is denied". Kill ONLY the adb whose image is our
  //      bundled copy (matched by exact image path, case-insensitively since
  //      Windows paths are case-folded), so a user's own Android SDK adb is left
  //      alone. The kill goes through taskkill /F: the daemon inherited the
  //      service's LocalSystem token, and taskkill self-enables SeDebugPrivilege
  //      to terminate a SYSTEM process (the same reach that reaps the processes
  //      above); a plain TerminateProcess from a normal-admin elevation can be
  //      denied.
  Exec(ExpandConstant('{sys}\net.exe'), 'stop NexusService', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Pids := KillOwnNexus();
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM OpenRGB-headless.exe /F /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM nexus-overlay.exe /F /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  AdbExe := ExpandConstant('{app}\tools\adb\adb.exe');
  KillMatching('adb.exe', AdbExe, '', True);
  //   5. msedgewebview2.exe: the dashboard/overlay/kiosk WebView2 children
  //      outlive their host and keep {app}\wwwroot files open, which used to
  //      be Restart Manager's job to clear (CloseApplications=no now). Match
  //      only our own by the --user-data-dir the overlay passes
  //      (%LocalAppData%\Nexus\DesktopWebView2), so another app's WebView2 is
  //      left alone; the runtime image lives outside {app}, so a path match
  //      cannot distinguish them.
  KillMatching('msedgewebview2.exe', '', '\Nexus\DesktopWebView2', False);
  //   6. Then wait for the images to unmap. taskkill and Kill both return once
  //      termination is REQUESTED, not once the process is gone, so [Files]
  //      could otherwise start while a dying process still held a payload file.
  //      One budget shared across the names, not one per process. Only the
  //      three killed above, Nexus.exe by the same path selection: steps 4 and
  //      5 select adb and msedgewebview2 by path/command line and deliberately
  //      spare foreign ones, which never exit, so waiting on those by name
  //      would hand the whole budget to a process nobody killed. Purely an
  //      optimisation - a clean delete beats a rename-aside - since
  //      UnlockTarget handles whatever is still locked; correctness never rests
  //      on the wait being long enough, which is why it can be this short.
  Waits := ProcessIdsByName('OpenRGB-headless.exe');
  Extra := ProcessIdsByName('nexus-overlay.exe');
  for I := 0 to GetArrayLength(Extra) - 1 do
  begin
    N := GetArrayLength(Waits);
    SetArrayLength(Waits, N + 1);
    Waits[N] := Extra[I];
  end;
  for I := 0 to GetArrayLength(Pids) - 1 do
  begin
    N := GetArrayLength(Waits);
    SetArrayLength(Waits, N + 1);
    Waits[N] := Pids[I];
  end;
  WaitForPids(Waits, 2000);
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

// Shown when Nexus.exe is gone after the copy step or after --install ran it.
function AntivirusBlockedPrompt(): Integer;
var
  Btns: TArrayOfString;
  ErrCode: Integer;
begin
  SetArrayLength(Btns, 3);
  Btns[0] := 'Try again';
  Btns[1] := 'Learn more';
  Btns[2] := 'Close';
  repeat
    Result := SuppressibleTaskDialogMsgBox('Your antivirus blocked Nexus',
      'Nexus.exe was blocked while Setup was installing it. This is almost always '
      + 'antivirus software quarantining it by mistake (a false positive). Nexus is digitally signed and safe.'#13#10#13#10
      + 'To finish installing:'#13#10
      + '  1. Open your antivirus and find Nexus.exe in its quarantine or protection history.'#13#10
      + '  2. Restore it, and add an exception for ' + ExpandConstant('{app}') + '.'#13#10
      + '  3. Click Try again.',
      mbError, MB_YESNOCANCEL, Btns, 0, IDCANCEL);
    if Result = IDNO then
      ShellExecAsOriginalUser('open', '{#AntivirusDocsUrl}', '', '', SW_SHOWNORMAL, ewNoWait, ErrCode);
  until Result <> IDNO;
  if Result = IDYES then
    Result := IDRETRY;
end;

function PayloadInstalled(): Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\{#MyAppExeName}'));
end;

// Check for the --install [Run] entry: holds the install on the prompt until
// the user restores Nexus.exe or gives up. Silent installs (OTA) get Close.
function PayloadPresent(): Boolean;
begin
  Result := PayloadInstalled();
  while not Result do
  begin
    Log('[nexus] Nexus.exe missing, likely quarantined');
    if AntivirusBlockedPrompt() <> IDRETRY then exit;
    Result := PayloadInstalled();
  end;
end;

// Behavioural antivirus engines act when --install runs the exe, so a removal
// can also happen during that run; repeat it once the user has restored the file.
procedure RerunInstallIfBlocked();
var
  ResultCode: Integer;
begin
  while not PayloadInstalled() do
  begin
    if not PayloadPresent() then exit;
    Exec(ExpandConstant('{app}\{#MyAppExeName}'), '--install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if WebView2Missing then InstallWebView2Runtime();
  StopServiceIfRunning();
  // After the kills, so anything a previous install had to rename aside is now
  // unheld and deletes cleanly.
  SweepStaleFiles(ExpandConstant('{app}'));
  Result := '';
end;

function HasSwitch(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if SameName(ParamStr(I), Name) then Result := True;
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
    // A silent uninstall has nobody to answer the dialog, and an unanswered
    // dialog hangs it: keep the data unless /DELETEDATA asks for the purge.
    if UninstallSilent() then
    begin
      if HasSwitch('/DELETEDATA') then UninstArgs := UninstArgs + ' --purge';
    end
    else
    begin
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
    end;
    TmpExe := ExpandConstant('{tmp}\nexus-uninst.exe');
    if FileCopy(ExpandConstant('{app}\{#MyAppExeName}'), TmpExe, False) then
      Exec(TmpExe, UninstArgs, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode)
    else
      Exec(ExpandConstant('{app}\{#MyAppExeName}'), UninstArgs, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

// The process-level imports above are laid out for a 32-bit Setup. Inno's
// compiler emits only 32-bit Setup programs; this stops a future 64-bit Setup
// from running them with the wrong structure sizes.
function InitializeSetup(): Boolean;
var
  Wow64: Cardinal;
begin
  Result := True;
  if IsWin64 and NxIsWow64(NxCurrentProcess(), Wow64) and (Wow64 = 0) then
  begin
    Log('[nexus] Setup is a 64-bit process; the native process helpers need a 32-bit one');
    SuppressibleMsgBox('This installer was built as a 64-bit Setup, which its process helpers do not support.', mbError, MB_OK, IDOK);
    Result := False;
  end;
end;
