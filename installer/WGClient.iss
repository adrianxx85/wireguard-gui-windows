; Installer für WG Client (Inno Setup 6)
; Bauen: ..\build.ps1  (oder ISCC.exe WGClient.iss nach dotnet publish)
; Standalone-Edition (ohne WireGuard-App, mit tunnel.dll/wireguard.dll): ..\build.ps1 -Standalone
;   bzw. ISCC.exe /DStandalone WGClient.iss nach native\build-native.ps1

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
; muss zum Mutex-Namen in App.xaml.cs passen – Setup erkennt so eine laufende App
AppMutex=WGClientAppMutex
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

function ShouldInstallDotNet: Boolean;
begin
  Result := NeedDotNet;
end;

function ShouldInstallWireGuard: Boolean;
begin
  Result := NeedWireGuard;
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

function ScQuery(const Name, State: String): Boolean;
var
  ResultCode: Integer;
begin
  { sc.exe gibt die Zustände unabhängig von der Systemsprache englisch aus (z. B. "4  RUNNING") }
  Result := Exec(ExpandConstant('{cmd}'), '/c ""' + ExpandConstant('{sys}\sc.exe') + '" query "WireGuardTunnel$' + Name +
    '" | "' + ExpandConstant('{sys}\find.exe') + '" "' + State + '" >nul"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
    and (ResultCode = 0);
end;

procedure Sc(const Args: String);
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

{ Stoppt einen eigenen Tunnel-Dienst und wartet (max. 30 s), bis er die Programmdateien freigibt. }
procedure StopEmbeddedTunnel(const Name: String);
var
  I: Integer;
begin
  Sc('stop "WireGuardTunnel$' + Name + '"');
  for I := 1 to 120 do
  begin
    if not RegKeyExists(HKLM, ServiceKey(Name)) or ScQuery(Name, 'STOPPED') then Exit;
    Sleep(250);
  end;
end;

procedure StopAppTunnels;
var
  FindRec: TFindRec;
  Name: String;
  ResultCode: Integer;
begin
  if FindFirst(ActiveDir + '*.conf', FindRec) then
  try
    repeat
      Name := ChangeFileExt(FindRec.Name, '');
      if FileExists(WireGuardExe) and IsWireGuardAppTunnel(Name, ActiveDir + FindRec.Name) then
        Exec(WireGuardExe, '/uninstalltunnelservice "' + Name + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
      else if IsEmbeddedTunnel(Name, ActiveDir + FindRec.Name) then
      begin
        StopEmbeddedTunnel(Name);
        Sc('delete "WireGuardTunnel$' + Name + '"');
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

{ ---------- Update: eigene Tunnel-Dienste sperren WGClient.exe und tunnel.dll ---------- }

var
  RestartTunnels, EmbeddedTunnels: TStringList;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  FindRec: TFindRec;
  Name: String;
begin
  Result := '';
  RestartTunnels := TStringList.Create;
  EmbeddedTunnels := TStringList.Create;
  if FindFirst(ActiveDir + '*.conf', FindRec) then
  try
    repeat
      Name := ChangeFileExt(FindRec.Name, '');
      if IsEmbeddedTunnel(Name, ActiveDir + FindRec.Name) then
      begin
        EmbeddedTunnels.Add(Name);
        if ScQuery(Name, 'RUNNING') then RestartTunnels.Add(Name);
        StopEmbeddedTunnel(Name);
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  I: Integer;
begin
  if (CurStep <> ssPostInstall) or (RestartTunnels = nil) then Exit;
#ifdef Standalone
  for I := 0 to RestartTunnels.Count - 1 do
    Sc('start "WireGuardTunnel$' + RestartTunnels[I] + '"');
#else
  { Die Standard-Edition kann eigene Tunnel-Dienste nicht ausführen: entfernen, neu verbinden über WireGuard }
  for I := 0 to EmbeddedTunnels.Count - 1 do
    Sc('delete "WireGuardTunnel$' + EmbeddedTunnels[I] + '"');
#endif
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  DataDir := ExpandConstant('{commonappdata}\WGClient');
  if CurUninstallStep = usUninstall then
    StopAppTunnels; { von WG Client gestartete Tunnel trennen }

  if (CurUninstallStep = usPostUninstall) and DirExists(DataDir) and not UninstallSilent then
    if MsgBox('Gespeicherte Tunnel-Konfigurationen (inkl. privater Schlüssel) ebenfalls löschen?' + #13#10#13#10 +
              'Wähle "Nein", wenn du WG Client später wieder installieren und die Tunnel behalten möchtest.',
              mbConfirmation, MB_YESNO) = IDYES then
      DelTree(DataDir, True, True, True);
end;
