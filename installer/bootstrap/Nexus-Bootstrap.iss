; Nexus web installer (Inno Setup 6) - the small executable hellonexus.com
; hands out. It carries no payload: on Install it downloads the current
; Nexus-Setup.exe (the full offline installer every release publishes) plus
; that release's SHA256SUMS, verifies the hash, and runs it.
;
;   Compile: ISCC.exe /DChannel=stable Nexus-Bootstrap.iss
;            ISCC.exe /DChannel=beta   Nexus-Bootstrap.iss
;   Output:  installer\output\Nexus-Installer.exe / Nexus-Installer-Beta.exe
;   Usually via ..\build-installer.ps1 -Bootstrap, which builds both.
;
; Built and signed ONCE, reused unchanged across releases: SmartScreen keys
; reputation on the downloaded file's hash (see ..\README.md, "Web
; installers"). Rebuild ONLY when this script changes, and bump StubVersion -
; a rebuild is a new hash starting from zero.
;
; Channel selects which release the site's redirects resolve: stable prefers
; the newest non-prerelease, beta takes the newest release of either kind.
; The installed service then derives its own update channel from the
; version it received (UpdateService.ResolveChannelForBuild).

#ifndef Channel
  #define Channel "stable"
#endif
#ifndef BaseUrl
  #define BaseUrl "https://hellonexus.com"
#endif
#if Channel == "beta"
  #define StubName "Nexus-Installer-Beta"
  #define StubTitle "Nexus Beta"
#elif Channel == "stable"
  #define StubName "Nexus-Installer"
  #define StubTitle "Nexus"
#else
  #error Channel must be "stable" or "beta"
#endif
; Bump on any change to this script; it is the only version this file has.
#define StubVersion "1.0.0"
#define PayloadName "Nexus-Setup.exe"
; AppId of ..\Nexus.iss: its uninstall key holds the install location.
#define PayloadAppId "{8F2E3A4D-9C5B-4E7A-B1F8-3C2A5E9D0F12}"
#define SumsName "SHA256SUMS"
; Overridable so a trial build can pin one release's GitHub asset URLs
; directly, bypassing the site's resolver.
#ifndef SumsUrl
  #define SumsUrl BaseUrl + "/download/offline/sha256sums?channel=" + Channel
#endif
#ifndef PayloadUrl
  #define PayloadUrl BaseUrl + "/download/offline/windows?channel=" + Channel
#endif

[Setup]
AppName={#StubTitle}
AppVersion={#StubVersion}
VersionInfoVersion={#StubVersion}.0
AppPublisher=Nexus
AppPublisherURL=https://hellonexus.com
AppSupportURL=https://hellonexus.com
; Nothing is installed by this wizard itself: no app dir, no uninstaller,
; no registry. The downloaded Nexus-Setup.exe owns all of that.
CreateAppDir=no
Uninstallable=no
DisableWelcomePage=no
DisableReadyPage=no
DisableProgramGroupPage=yes
DisableFinishedPage=yes
; The stub runs unelevated; Nexus-Setup.exe carries requireAdministrator, so
; the ShellExec in CurStepChanged raises the single UAC prompt with its publisher.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
OutputBaseFilename={#StubName}
OutputDir=..\output
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
WizardSmallImageFile=..\logo-small.bmp
SetupIconFile=..\..\icon.ico
ShowLanguageDialog=no
SetupLogging=yes
; build-installer.ps1 -Sign defines EnableSigning and the nexussign tool via /S.
#ifdef EnableSigning
SignTool=nexussign
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This will download the latest [name] release from hellonexus.com and start its installer.%n%nAn internet connection is required. Click Next to continue.
WizardReady=Ready to Download
ReadyLabel1=Setup will download the latest [name] release and start its installer.
ReadyLabel2a=Click Install to continue, or click Back to review the settings.
ReadyLabel2b=Click Install to continue.

[Code]
var
  DownloadPage: TDownloadWizardPage;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  if Progress = ProgressMax then
    Log(Format('Downloaded %s (%d bytes)', [FileName, ProgressMax]));
  Result := True;
end;

procedure InitializeWizard();
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), @OnDownloadProgress);
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := 'Release channel:' + NewLine + Space + '{#Channel}' + NewLine + NewLine +
            'Download source:' + NewLine + Space + '{#BaseUrl}';
end;

// The SHA256SUMS line for the payload: "<64 hex>  Nexus-Setup.exe". Tolerates
// the "*name" and "./name" spellings other tools emit. '' when absent.
function ExpectedPayloadHash(const SumsFile: String): String;
var
  Lines: TArrayOfString;
  I: Integer;
  Line, Name: String;
begin
  Result := '';
  if not LoadStringsFromFile(SumsFile, Lines) then
    Exit;
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Trim(Lines[I]);
    if (Length(Line) > 65) and (Line[65] = ' ') then
    begin
      Name := Trim(Copy(Line, 66, Length(Line)));
      if Copy(Name, 1, 1) = '*' then
        Delete(Name, 1, 1);
      if Copy(Name, 1, 2) = './' then
        Delete(Name, 1, 2);
      if CompareText(Name, '{#PayloadName}') = 0 then
      begin
        Result := Lowercase(Copy(Line, 1, 64));
        Exit;
      end;
    end;
  end;
end;

// Install click: fetch the checksum list first so the payload download can
// be pinned to its published hash (the download page rejects a mismatch).
function NextButtonClick(CurPageID: Integer): Boolean;
var
  Hash: String;
begin
  Result := True;
  if CurPageID <> wpReady then
    Exit;
  DownloadPage.Clear;
  DownloadPage.Add('{#SumsUrl}', '{#SumsName}', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      Hash := ExpectedPayloadHash(ExpandConstant('{tmp}\{#SumsName}'));
      if Hash = '' then
        RaiseException('The release checksum list has no entry for {#PayloadName}.');
      DownloadPage.Clear;
      DownloadPage.Add('{#PayloadUrl}', '{#PayloadName}', Hash);
      DownloadPage.Download;
    except
      if DownloadPage.AbortedByUser then
        Log('Download aborted by user.')
      else
        SuppressibleMsgBox(AddPeriod(GetExceptionMessage), mbCriticalError, MB_OK, IDOK);
      Result := False;
    end;
  finally
    DownloadPage.Hide;
  end;
end;

// The payload's own "open dashboard" step is skipped under /SILENT, so do it
// here, from the user's session, at the location it installed to.
procedure OpenDashboard();
var
  Dir: String;
  ResultCode: Integer;
begin
  if not RegQueryStringValue(HKLM64, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#PayloadAppId}_is1', 'InstallLocation', Dir) then
    Dir := ExpandConstant('{commonpf64}\Nexus');
  Exec(AddBackslash(Dir) + 'Nexus.exe', '--open-app', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
end;

// Runs the verified payload. ShellExec (not CreateProcess) so its
// requireAdministrator manifest elevates it; waiting keeps {tmp} alive until
// it finishes. Done from [Code] rather than [Run] so a payload that fails, or
// a declined UAC prompt, fails this Setup too: exit code 3, versus 1 for a
// download or hash failure (a scripted "Nexus-Installer.exe /VERYSILENT" must
// not report success with nothing installed). This wizard is the only one
// the user sees: an interactive run drives the payload with /SILENT (a
// progress window, its default directory) plus /DESKTOPICON=1, since a silent
// payload creates no desktop icon unless asked (Nexus.iss DesktopIconChecked;
// payloads older than that switch ignore it), a silent stub run (/SILENT or /VERYSILENT; the script cannot tell them
// apart) drives it very silently. The stub's own window stays up, disabled,
// behind the payload's progress: hiding it first would forfeit foreground
// activation for the process it launches.
procedure CurStepChanged(CurStep: TSetupStep);
var
  Params: String;
  ResultCode: Integer;
begin
  if CurStep <> ssInstall then
    Exit;
  if WizardSilent then
    Params := '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
  else
    Params := '/SILENT /DESKTOPICON=1';
  if not ShellExec('', ExpandConstant('{tmp}\{#PayloadName}'), Params, '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
  begin
    // 1223 = the user declined the UAC prompt: as deliberate as a cancel.
    if ResultCode = 1223 then
      Abort;
    RaiseException(Format('Could not start {#PayloadName} (error %d).', [ResultCode]));
  end;
  Log(Format('{#PayloadName} exited with code %d', [ResultCode]));
  // Inno exit codes 2 and 5 are the user cancelling the payload's wizard: end
  // quietly (still a non-zero exit), no error box for a deliberate cancel.
  if (ResultCode = 2) or (ResultCode = 5) then
    Abort;
  if ResultCode <> 0 then
    RaiseException(Format('{#PayloadName} exited with code %d.', [ResultCode]));
  if not WizardSilent then
    OpenDashboard;
end;
