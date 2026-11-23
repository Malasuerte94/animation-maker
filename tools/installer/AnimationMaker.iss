#ifndef AppVersion
  #define AppVersion "0.1.0-beta.5"
#endif
#ifndef AppSource
  #error AppSource must point to the published portable folder
#endif
#ifndef ReleaseOutput
  #error ReleaseOutput must point to the release assets folder
#endif

[Setup]
AppId={{58B579D7-92D1-4EF2-8266-868070DE4E09}
AppName=Animation Maker
AppVersion={#AppVersion}
VersionInfoVersion={#GetVersionNumbersString(AppSource + "\AnimationMaker.dll")}
AppVerName=Animation Maker {#AppVersion}
AppPublisher=Animation Maker contributors
AppPublisherURL=https://github.com/Malasuerte94/animation-maker
AppSupportURL=https://github.com/Malasuerte94/animation-maker/issues
AppUpdatesURL=https://github.com/Malasuerte94/animation-maker/releases
DefaultDirName={localappdata}\Programs\AnimationMaker
DefaultGroupName=Animation Maker
DisableProgramGroupPage=yes
Uninstallable=not PortableUpdate
CreateUninstallRegKey=not PortableUpdate
UsePreviousAppDir=not PortableUpdate
SetupMutex=AnimationMakerSetup-58B579D7
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
UninstallDisplayIcon={app}\AnimationMaker.exe
LicenseFile={#AppSource}\LICENSE
InfoAfterFile={#AppSource}\THIRD-PARTY-NOTICES.md
OutputDir={#ReleaseOutput}
OutputBaseFilename=AnimationMaker-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#AppSource}\*"; Excludes: "\update-channel"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#AppSource}\update-channel"; DestDir: "{app}"; Check: PortableUpdate; Flags: ignoreversion
Source: "installed.channel"; DestName: "update-channel"; DestDir: "{app}"; Check: not PortableUpdate; Flags: ignoreversion

[Icons]
Name: "{userprograms}\Animation Maker"; Filename: "{app}\AnimationMaker.exe"; WorkingDir: "{app}"; Check: not PortableUpdate
Name: "{userdesktop}\Animation Maker"; Filename: "{app}\AnimationMaker.exe"; WorkingDir: "{app}"; Tasks: desktopicon; Check: not PortableUpdate

[Run]
Filename: "{app}\AnimationMaker.exe"; Description: "Launch Animation Maker"; Flags: nowait postinstall skipifsilent

[Code]
function PortableUpdate: Boolean;
begin
  Result := ExpandConstant('{param:AMPORTABLE|0}') = '1';
end;

function OpenProcess(Access: Cardinal; Inherit: Boolean; Pid: Cardinal): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: Cardinal): Cardinal;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Pid: Integer;
  Parent: THandle;
  SessionName, Mode: String;
  Marker: AnsiString;
  InstalledVersion, NewVersion: Int64;
begin
  Result := '';
  Pid := StrToIntDef(ExpandConstant('{param:AMWAITPID|0}'), 0);
  if Pid = 0 then Exit;
  if PortableUpdate then Mode := 'portable' else Mode := 'installed';
  if not LoadStringFromFile(ExpandConstant('{app}\update-channel'), Marker) or
     (Trim(String(Marker)) <> Mode) then begin
    Result := 'The update target does not match this copy of Animation Maker.';
    Exit;
  end;
  SessionName := ExpandConstant('{param:AMSESSION|}');
  if Pos('Local\AnimationMaker.', SessionName) <> 1 then begin
    Result := 'The update session is missing.';
    Exit;
  end;
  Parent := OpenProcess($00100000, False, Pid);
  if Parent <> 0 then begin
    if WaitForSingleObject(Parent, 30000) <> 0 then Result := 'Animation Maker is still closing.';
    CloseHandle(Parent);
    if Result <> '' then Exit;
  end;
  CreateMutex(SessionName + '.Installing');
  if CheckForMutexes(SessionName) then begin
    Result := 'Another editor window is open.';
    Exit;
  end;
  if not GetPackedVersion(ExpandConstant('{app}\AnimationMaker.dll'), InstalledVersion) or
     not StrToVersion('{#GetVersionNumbersString(AppSource + "\AnimationMaker.dll")}', NewVersion) or
     (ComparePackedVersion(InstalledVersion, NewVersion) >= 0) then
    Result := 'This copy already has an equivalent or newer version.';
end;
