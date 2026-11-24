; Inno Setup script for AudioSwitch. Built by build-release.ps1, which stages the files and passes AppVersion and Stage.
; Silent install: AudioSwitch-Setup-x.y.z.exe /VERYSILENT /SUPPRESSMSGBOXES /PeerHost=192.0.2.20 [/Hotkey=F13] [/NoFirewall]

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Stage
  #define Stage "..\obj\stage"
#endif

[Setup]
AppId={{7B6C3E2A-5F1D-4C8E-9A2B-3D4E5F6A7B8C}
AppName=AudioSwitch
AppVersion={#AppVersion}
AppVerName=AudioSwitch {#AppVersion}
AppPublisher=wyattgardner
AppPublisherURL=https://github.com/wyattgardner/audioswitch
AppSupportURL=https://github.com/wyattgardner/audioswitch/issues
VersionInfoVersion={#AppVersion}.0
; Per-user, no admin. Not under %LOCALAPPDATA%, which Claude desktop redirects when it runs installers.
DefaultDirName={%USERPROFILE}\Tools\AudioSwitch
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=AudioSwitch-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE
UninstallDisplayName=AudioSwitch {#AppVersion}
UninstallDisplayIcon={app}\AudioSwitch.exe
CloseApplications=no

[Files]
Source: "{#Stage}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
; Extracted to {tmp} first so an upgrade can stop the running copies before their files are replaced.
Source: "{#Stage}\configure.ps1"; Flags: dontcopy

[UninstallDelete]
Type: files; Name: "{app}\state.txt"
Type: files; Name: "{app}\events.log"
Type: files; Name: "{app}\prefs.txt"
Type: files; Name: "{app}\hotkey.txt"
Type: files; Name: "{app}\rx-muted.txt"
Type: files; Name: "{app}\endpoint-muted.txt"
Type: files; Name: "{app}\install.log"
Type: filesandordirs; Name: "{app}\local"

[Code]
var
  PeerPage: TInputQueryWizardPage;
  FirewallPage: TInputOptionWizardPage;

function HasParam(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then
      Result := True;
end;

function RunPowerShell(const Args: String): Integer;
var
  Code: Integer;
begin
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
              '-NoProfile -ExecutionPolicy Bypass ' + Args, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  Result := Code;
end;

function ParamOrPrevious(const Name, Default: String): String;
begin
  Result := ExpandConstant('{param:' + Name + '|}');
  if Result = '' then
    Result := GetPreviousData(Name, Default);
end;

procedure InitializeWizard;
begin
  PeerPage := CreateInputQueryPage(wpSelectDir,
    'Other machine', 'Which machine do you switch audio with?',
    'Enter the other machine''s IP address or name. A DHCP reservation keeps it from changing. ' +
    'Then pick this machine''s hotkey: it sends this machine''s audio to the other one, and Shift plus it pulls the other machine''s audio here.');
  PeerPage.Add('Other machine''s IP address or name:', False);
  PeerPage.Add('Hotkey on this machine (for example F13 or Ctrl+Alt+F9):', False);
  PeerPage.Values[0] := ParamOrPrevious('PeerHost', '');
  PeerPage.Values[1] := ParamOrPrevious('Hotkey', 'F13');

  FirewallPage := CreateInputOptionPage(PeerPage.ID,
    'Windows Firewall', 'Let the other machine connect?',
    'AudioSwitch listens on TCP 8080 (audio) and 8091 (switching). The rules allow only the other machine''s address. Adding them asks for admin rights once.',
    False, False);
  FirewallPage.Add('Allow the other machine through Windows Firewall');
  FirewallPage.Values[0] := not HasParam('/NoFirewall');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = PeerPage.ID) and (Trim(PeerPage.Values[0]) = '') then
  begin
    MsgBox('Enter the other machine''s IP address or name.', mbError, MB_OK);
    Result := False;
  end;
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'PeerHost', Trim(PeerPage.Values[0]));
  SetPreviousData(PreviousDataKey, 'Hotkey', Trim(PeerPage.Values[1]));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  ExtractTemporaryFile('configure.ps1');
  RunPowerShell('-File "' + ExpandConstant('{tmp}\configure.ps1') + '" -InstallDir "' + ExpandConstant('{app}') + '" -Stop');
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Args: String;
begin
  if CurStep <> ssPostInstall then
    exit;
  Args := '-File "' + ExpandConstant('{app}\configure.ps1') + '" -InstallDir "' + ExpandConstant('{app}') + '"';
  if Trim(PeerPage.Values[0]) <> '' then
    Args := Args + ' -PeerHost "' + Trim(PeerPage.Values[0]) + '"';
  if Trim(PeerPage.Values[1]) <> '' then
    Args := Args + ' -Hotkey "' + Trim(PeerPage.Values[1]) + '"';
  if FirewallPage.Values[0] then
    Args := Args + ' -Firewall';
  if RunPowerShell(Args) <> 0 then
    SuppressibleMsgBox('AudioSwitch was installed, but setting it up failed. Details are in ' +
      ExpandConstant('{app}\install.log') + '.', mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RunPowerShell('-File "' + ExpandConstant('{app}\configure.ps1') + '" -InstallDir "' + ExpandConstant('{app}') + '" -Uninstall');
end;
