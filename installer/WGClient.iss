; Installer für WG Client (Inno Setup 6)
; Bauen: ..\build.ps1  (oder ISCC.exe WGClient.iss nach dotnet publish)
; Standalone-Edition (ohne WireGuard-App, mit tunnel.dll/wireguard.dll): ..\build.ps1 -Standalone

#define MyAppName "WG Client"
#define MyAppExe "WGClient.exe"
#define MyAppVersion GetStringFileInfo("..\publish\WGClient.exe", "ProductVersion")
#define MyPublisher "WG Client"
#define CoffeeUrl "https://paypal.me/AdrianZblewski"
#define DotNetUrl "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe"
#define WireGuardUrl "https://download.wireguard.com/windows-client/wireguard-installer.exe"

[Setup]
AppId={{8F3C2A51-6B7E-4D2A-9C1F-3E5B7A9D4C21}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
#ifdef Standalone
AppVerName={#MyAppName} {#MyAppVersion} (Standalone)
#else
AppVerName={#MyAppName} {#MyAppVersion}
#endif
AppPublisher={#MyPublisher}
AppPublisherURL={#CoffeeUrl}
DefaultDirName={autopf}\WG Client
DefaultGroupName=WG Client
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
#ifdef Standalone
OutputBaseFilename=WGClient-Standalone-Setup-{#MyAppVersion}
#else
OutputBaseFilename=WGClient-Setup-{#MyAppVersion}
#endif
SetupIconFile=..\WGClient\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; muss zu App.xaml.cs passen: WGClientAppMutex = Instanz in dieser Sitzung, Global\WGClientAppRunning = Instanz
; in irgendeiner Sitzung (ab 1.4.3), Global\WGClientAppMutex = laufende 1.4.2
AppMutex=WGClientAppMutex,Global\WGClientAppRunning,Global\WGClientAppMutex
ChangesAssociations=yes

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: "desktopicon"; Description: "Desktop-Verknüpfung erstellen"; GroupDescription: "Verknüpfungen:"
Name: "openwith"; Description: ".conf-Dateien im Menü ""Öffnen mit"" anbieten"; GroupDescription: "Integration:"

[Files]
Source: "..\publish\{#MyAppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\ANLEITUNG.md"; DestDir: "{app}"; DestName: "Anleitung.txt"; Flags: ignoreversion
#ifdef Standalone
; WireGuard-Bausteine: tunnel.dll (MIT, aus wireguard-windows gebaut) und wireguard.dll (signiert, wireguard-nt)
Source: "..\native\bin\amd64\tunnel.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\native\bin\amd64\wireguard.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\native\bin\amd64\LICENSE-wireguard-windows.txt"; DestDir: "{app}\Lizenzen"; Flags: ignoreversion
Source: "..\native\bin\amd64\LICENSE-wireguard-nt.txt"; DestDir: "{app}\Lizenzen"; Flags: ignoreversion
#else

[InstallDelete]
; Wechsel von der Standalone- zur Standard-Edition: eingebettete Bausteine entfernen
Type: files; Name: "{app}\tunnel.dll"
Type: files; Name: "{app}\wireguard.dll"
Type: filesandordirs; Name: "{app}\Lizenzen"
#endif

[Icons]
Name: "{autoprograms}\WG Client"; Filename: "{app}\{#MyAppExe}"
Name: "{autoprograms}\WG Client Anleitung"; Filename: "{app}\Anleitung.txt"
Name: "{autodesktop}\WG Client"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Registry]
; "Öffnen mit" für .conf – macht WG Client NICHT zum Standardprogramm
Root: HKA; Subkey: "Software\Classes\WGClient.conf"; ValueType: string; ValueData: "WireGuard-Konfiguration"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\WGClient.conf\DefaultIcon"; ValueType: string; ValueData: "{app}\{#MyAppExe},0"; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\WGClient.conf\shell\open\command"; ValueType: string; ValueData: """{app}\{#MyAppExe}"" ""%1"""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.conf\OpenWithProgids"; ValueType: string; ValueName: "WGClient.conf"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#MyAppExe}\SupportedTypes"; ValueType: string; ValueName: ".conf"; ValueData: ""; Flags: uninsdeletekey; Tasks: openwith

[Run]
Filename: "{tmp}\dotnet-desktop-runtime.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installiere .NET 10 Desktop Runtime ..."; Check: ShouldInstallDotNet; Flags: waituntilterminated
#ifndef Standalone
Filename: "{tmp}\wireguard-installer.exe"; StatusMsg: "Installiere WireGuard ..."; Check: ShouldInstallWireGuard; Flags: waituntilterminated
#endif
Filename: "{app}\{#MyAppExe}"; Description: "WG Client jetzt starten"; Flags: postinstall nowait skipifsilent runascurrentuser
Filename: "{#CoffeeUrl}"; Description: "Gefällt dir WG Client? Spendier mir einen Kaffee (PayPal)"; Flags: postinstall shellexec nowait skipifsilent unchecked

[Code]
var
  DownloadPage: TDownloadWizardPage;
  NeedDotNet, NeedWireGuard: Boolean;

function IsDotNetInstalled: Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec);
  if Result then FindClose(FindRec);
end;

function WireGuardExe: String;
begin
  Result := ExpandConstant('{commonpf64}\WireGuard\wireguard.exe');
end;

function SetEnvironmentVariable(Name, Value: String): BOOL;
  external 'SetEnvironmentVariableW@kernel32.dll stdcall';

function VerifyAuthenticode(const FilePath, ExpectedSigner: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  if not FileExists(FilePath) then Exit;
  SetEnvironmentVariable('WG_VERIFY_FILE', FilePath);
  SetEnvironmentVariable('WG_VERIFY_SIGNER', ExpectedSigner);
  try
    Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "$s = Get-AuthenticodeSignature -LiteralPath $env:WG_VERIFY_FILE; if ($s.Status -eq ''Valid'' -and $s.SignerCertificate -and (($s.SignerCertificate.Subject -split '',\s*'') -ccontains (''O='' + $env:WG_VERIFY_SIGNER))) { exit 0 } else { exit 1 }"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  finally
    SetEnvironmentVariable('WG_VERIFY_FILE', '');
    SetEnvironmentVariable('WG_VERIFY_SIGNER', '');
  end;
  if not Result then Log('Signaturprüfung fehlgeschlagen für ' + FilePath + ' (erwartet: ' + ExpectedSigner + ')');
end;

function ShouldInstallDotNet: Boolean;
begin
  Result := NeedDotNet and VerifyAuthenticode(ExpandConstant('{tmp}\dotnet-desktop-runtime.exe'), 'Microsoft Corporation');
end;

function ShouldInstallWireGuard: Boolean;
begin
  Result := NeedWireGuard and VerifyAuthenticode(ExpandConstant('{tmp}\wireguard-installer.exe'), 'WireGuard LLC');
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

function InitializeSetup: Boolean;
begin
  NeedDotNet := not IsDotNetInstalled;
#ifdef Standalone
  NeedWireGuard := False; { Standalone bringt die WireGuard-Bausteine selbst mit }
#else
  NeedWireGuard := not FileExists(WireGuardExe);
#endif
  Result := True;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), 'Fehlende Komponenten werden heruntergeladen ...', @OnDownloadProgress);
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo;
  if MemoTasksInfo <> '' then Result := Result + NewLine + NewLine + MemoTasksInfo;
  if NeedDotNet or NeedWireGuard then
  begin
    Result := Result + NewLine + NewLine + 'Wird zusätzlich heruntergeladen und installiert:';
    if NeedDotNet then Result := Result + NewLine + Space + '.NET 10 Desktop Runtime (Microsoft)';
    if NeedWireGuard then Result := Result + NewLine + Space + 'WireGuard für Windows (wireguard.com)';
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID <> wpReady) or not (NeedDotNet or NeedWireGuard) then Exit;

  DownloadPage.Clear;
  if NeedDotNet then DownloadPage.Add('{#DotNetUrl}', 'dotnet-desktop-runtime.exe', '');
  if NeedWireGuard then DownloadPage.Add('{#WireGuardUrl}', 'wireguard-installer.exe', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      if NeedDotNet and not VerifyAuthenticode(ExpandConstant('{tmp}\dotnet-desktop-runtime.exe'), 'Microsoft Corporation') then
      begin
        DeleteFile(ExpandConstant('{tmp}\dotnet-desktop-runtime.exe'));
        RaiseException('Die digitale Signatur der .NET 10 Desktop Runtime ist ungültig.');
      end;
      if NeedWireGuard and not VerifyAuthenticode(ExpandConstant('{tmp}\wireguard-installer.exe'), 'WireGuard LLC') then
      begin
        DeleteFile(ExpandConstant('{tmp}\wireguard-installer.exe'));
        RaiseException('Die digitale Signatur des WireGuard-Installers ist ungültig.');
      end;
    except
      if DownloadPage.AbortedByUser then
        Result := False
      else if SuppressibleMsgBox('Download fehlgeschlagen: ' + GetExceptionMessage + #13#10#13#10 +
          'Ohne Internet ohne diese Komponenten fortfahren? Du kannst sie später selbst installieren.',
          mbError, MB_YESNO, IDNO) = IDYES then
      begin
        NeedDotNet := False;
        NeedWireGuard := False;
      end
      else
        Result := False;
    end;
  finally
    DownloadPage.Hide;
  end;
end;

{ ---------- Deinstallation ---------- }

function QuoteServicePath(const Path: String): String;
begin
  if (Pos(' ', Path) > 0) or (Pos(#9, Path) > 0) then
    Result := '"' + Path + '"'
  else
    Result := Path;
end;

function ServiceKey(const Name: String): String;
begin
  Result := 'SYSTEM\CurrentControlSet\Services\WireGuardTunnel$' + Name;
end;

function ActiveDir: String;
begin
  Result := ExpandConstant('{commonappdata}\WGClient\active\');
end;

{ Dienst der offiziellen WireGuard-App (Standard-Edition) }
function IsWireGuardAppTunnel(const Name, ConfigPath: String): Boolean;
var
  ImagePath: String;
begin
  Result := RegQueryStringValue(HKLM, ServiceKey(Name), 'ImagePath', ImagePath)
    and (CompareText(ImagePath, QuoteServicePath(WireGuardExe) + ' /tunnelservice ' + QuoteServicePath(ConfigPath)) = 0);
end;

// Eigener Tunnel-Dienst (Standalone-Edition): "<app>\WGClient.exe" /service "<conf>" – siehe EmbeddedTunnel.cs
function IsEmbeddedTunnel(const Name, ConfigPath: String): Boolean;
var
  ImagePath: String;
begin
  Result := RegQueryStringValue(HKLM, ServiceKey(Name), 'ImagePath', ImagePath)
    and (CompareText(ImagePath, '"' + ExpandConstant('{app}\{#MyAppExe}') + '" /service "' + ConfigPath + '"') = 0);
end;

const
  Stopped = 1;
  StartPending = 2;
  StopPending = 3;
  Running = 4;

type
  TServiceStatus = record
    ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
      ServiceSpecificExitCode, CheckPoint, WaitHint: LongWord;
  end;

function OpenSCManager(Machine, Database: LongWord; Access: LongWord): THandle;
  external 'OpenSCManagerW@advapi32.dll stdcall';
function OpenService(Manager: THandle; Name: String; Access: LongWord): THandle;
  external 'OpenServiceW@advapi32.dll stdcall';
function QueryServiceStatus(Service: THandle; var Status: TServiceStatus): BOOL;
  external 'QueryServiceStatus@advapi32.dll stdcall';
function CloseServiceHandle(Service: THandle): BOOL;
  external 'CloseServiceHandle@advapi32.dll stdcall';
function GetTickCount64: Int64;
  external 'GetTickCount64@kernel32.dll stdcall';

function TunnelState(const Name: String): Integer;
var
  Manager, Service: THandle;
  Status: TServiceStatus;
begin
  Result := 0;
  Manager := OpenSCManager(0, 0, 1); { SC_MANAGER_CONNECT }
  if Manager = 0 then Exit;
  try
    Service := OpenService(Manager, 'WireGuardTunnel$' + Name, 4); { SERVICE_QUERY_STATUS }
    if Service = 0 then Exit;
    try
      if QueryServiceStatus(Service, Status) then Result := Status.CurrentState;
    finally
      CloseServiceHandle(Service);
    end;
  finally
    CloseServiceHandle(Manager);
  end;
end;

function Sc(const Action, Name: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'), Action + ' "WireGuardTunnel$' + Name + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  if not Result then Log('sc.exe ' + Action + ' fehlgeschlagen für ' + Name + ': ' + IntToStr(ResultCode));
end;

function StopTunnel(const Name: String): Boolean;
var
  Deadline: Int64;
  State: Integer;
  StopSent: Boolean;
begin
  StopSent := False;
  Deadline := GetTickCount64 + 30000;
  repeat
    State := TunnelState(Name);
    Result := (State = Stopped) or not RegKeyExists(HKLM, ServiceKey(Name));
    if Result then Exit;
    { Auch im Startvorgang wiederholt versuchen: sc stop gelingt, sobald der Dienst Steuerbefehle annimmt }
    if not StopSent and ((State = StartPending) or (State >= Running)) then StopSent := Sc('stop', Name);
    Sleep(250);
  until GetTickCount64 >= Deadline;
end;

function StartTunnel(const Name: String): Boolean;
var
  Deadline: Int64;
  State: Integer;
begin
  Result := False;
  Deadline := GetTickCount64 + 60000;
  State := TunnelState(Name);
  while (State = StopPending) and (GetTickCount64 < Deadline) do
  begin
    Sleep(250);
    State := TunnelState(Name);
  end;
  if (State = Stopped) and not Sc('start', Name) then Exit;
  repeat
    State := TunnelState(Name);
    Result := State = Running;
    if Result or (State = Stopped) or (State = 0) then Exit;
    Sleep(250);
  until GetTickCount64 >= Deadline;
end;

function VerifyEmbeddedInstallation: Boolean;
var
  Exe, Target: String;
  ResultCode: Integer;
begin
  Exe := ExpandConstant('{tmp}\{#MyAppExe}');
  Target := ExpandConstant('{app}\{#MyAppExe}');
  Result := False;
  try
    if not FileExists(Exe) then ExtractTemporaryFile('{#MyAppExe}');
    Result := Exec(Exe, '/verify-service-files "' + Target + '"', ExpandConstant('{tmp}'), SW_HIDE,
      ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  except
    Log(GetExceptionMessage);
  end;
end;

function DeleteTunnelService(const Name: String): Boolean;
var
  ResultCode: Integer;
begin
  if not RegKeyExists(HKLM, ServiceKey(Name)) then
  begin
    Result := True;
    Exit;
  end;
  { 1060 = Dienst existiert nicht mehr, 1072 = bereits zum Löschen markiert }
  Result := Exec(ExpandConstant('{sys}\sc.exe'), 'delete "WireGuardTunnel$' + Name + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and ((ResultCode = 0) or (ResultCode = 1060) or (ResultCode = 1072));
  if not Result then Log('sc.exe delete fehlgeschlagen für ' + Name + ': ' + IntToStr(ResultCode));
end;

function StopAppTunnels: Boolean;
var
  FindRec: TFindRec;
  Name: String;
begin
  Result := True;
  if FindFirst(ActiveDir + '*.conf', FindRec) then
  try
    repeat
      Name := ChangeFileExt(FindRec.Name, '');
      if IsWireGuardAppTunnel(Name, ActiveDir + FindRec.Name) or IsEmbeddedTunnel(Name, ActiveDir + FindRec.Name) then
        if not (StopTunnel(Name) and DeleteTunnelService(Name)) then
          Result := False;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

{ ---------- Update: eigene Tunnel-Dienste sperren WGClient.exe und tunnel.dll ---------- }

var
  RestartTunnels, EmbeddedTunnels: TStringList;
  PostInstallReached, TunnelUpdateFailed, AllTunnelsStopped: Boolean;

procedure ReportTunnelUpdateFailure(const Message: String);
begin
  TunnelUpdateFailed := True;
  Log(Message);
  if not WizardSilent then MsgBox(Message, mbError, MB_OK);
end;

procedure RestartSavedTunnels;
var
  I: Integer;
  Name: String;
begin
  if RestartTunnels = nil then Exit;
  if RestartTunnels.Count = 0 then Exit;
  if not VerifyEmbeddedInstallation then
  begin
    RestartTunnels.Clear;
    ReportTunnelUpdateFailure('Die Tunnel-Dienste konnten nicht neu gestartet werden. Der Programmordner ist unsicher oder unvollständig.');
    Exit;
  end;
  for I := RestartTunnels.Count - 1 downto 0 do
  begin
    Name := RestartTunnels[I];
    RestartTunnels.Delete(I);
    if not StartTunnel(Name) then
      ReportTunnelUpdateFailure('Tunnel "' + Name + '" konnte nicht neu gestartet werden.');
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  FindRec: TFindRec;
  Name: String;
  Verified: Boolean;
  State: Integer;
begin
  Result := '';
  if RestartTunnels = nil then RestartTunnels := TStringList.Create;
  if EmbeddedTunnels = nil then EmbeddedTunnels := TStringList.Create;
  Verified := False;
  if FindFirst(ActiveDir + '*.conf', FindRec) then
  try
    repeat
      Name := ChangeFileExt(FindRec.Name, '');
      if IsEmbeddedTunnel(Name, ActiveDir + FindRec.Name) then
      begin
#ifndef Standalone
        { Die Standard-Edition stoppt und entfernt diese Dienste nur und startet nichts aus dem Programmordner.
          Fehlen tunnel.dll/wireguard.dll schon (Reste eines früheren Wechsels), darf das Update nicht scheitern. }
        Verified := True;
#endif
        if not Verified then
        begin
          if not VerifyEmbeddedInstallation then
          begin
            Result := 'Der Programmordner der Standalone-Edition ist unsicher oder unvollständig. WG Client unter Programme neu installieren.';
            Exit;
          end;
          Verified := True;
        end;
        if EmbeddedTunnels.IndexOf(Name) < 0 then EmbeddedTunnels.Add(Name);
        State := TunnelState(Name);
        { Vor dem Stoppen merken: Läuft das Stoppen in den Timeout, muss der Tunnel beim Abbruch trotzdem
          wieder starten (StartTunnel wartet ein noch laufendes StopPending ab). }
        if (State >= StartPending) and (State <> StopPending) and (RestartTunnels.IndexOf(Name) < 0) then
          RestartTunnels.Add(Name);
        if not StopTunnel(Name) then
        begin
          RestartSavedTunnels;
          Result := 'Tunnel "' + Name + '" konnte nicht gestoppt werden. Das Update wurde angehalten.';
          Exit;
        end;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
#ifndef Standalone
var
  I: Integer;
#endif
begin
  if CurStep <> ssPostInstall then Exit;
  PostInstallReached := True;
  if EmbeddedTunnels = nil then Exit;
#ifdef Standalone
  RestartSavedTunnels;
#else
  { Die Standard-Edition kann eigene Tunnel-Dienste nicht ausführen: entfernen, neu verbinden über WireGuard }
  for I := 0 to EmbeddedTunnels.Count - 1 do
    if RegKeyExists(HKLM, ServiceKey(EmbeddedTunnels[I])) and
      ((TunnelState(EmbeddedTunnels[I]) <> Stopped) or not DeleteTunnelService(EmbeddedTunnels[I])) then
      ReportTunnelUpdateFailure('Der alte Dienst für Tunnel "' + EmbeddedTunnels[I] + '" konnte nicht entfernt werden.');
#endif
end;

procedure DeinitializeSetup;
begin
  if not PostInstallReached then RestartSavedTunnels;
  if RestartTunnels <> nil then RestartTunnels.Free;
  if EmbeddedTunnels <> nil then EmbeddedTunnels.Free;
end;

function GetCustomSetupExitCode: Integer;
begin
  if TunnelUpdateFailed then Result := 40 else Result := 0;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  DataDir := ExpandConstant('{commonappdata}\WGClient');
  if CurUninstallStep = usUninstall then
  begin
    AllTunnelsStopped := StopAppTunnels;
    if not AllTunnelsStopped then
      RaiseException('Mindestens ein Tunnel-Dienst konnte nicht gestoppt und entfernt werden.');
  end;

  if (CurUninstallStep = usPostUninstall) and AllTunnelsStopped and DirExists(DataDir) and not UninstallSilent then
    if MsgBox('Gespeicherte Tunnel-Konfigurationen (inkl. privater Schlüssel) ebenfalls löschen?' + #13#10#13#10 +
              'Wähle "Nein", wenn du WG Client später wieder installieren und die Tunnel behalten möchtest.',
              mbConfirmation, MB_YESNO) = IDYES then
      DelTree(DataDir, True, True, True);
end;
