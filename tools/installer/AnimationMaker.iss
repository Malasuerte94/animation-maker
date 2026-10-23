#ifndef AppVersion
  #define AppVersion "0.1.0-beta.1"
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
VersionInfoVersion=0.1.0.1
AppVerName=Animation Maker {#AppVersion}
AppPublisher=Animation Maker contributors
AppPublisherURL=https://github.com/Malasuerte94/animation-maker
AppSupportURL=https://github.com/Malasuerte94/animation-maker/issues
AppUpdatesURL=https://github.com/Malasuerte94/animation-maker/releases
DefaultDirName={localappdata}\Programs\AnimationMaker
DefaultGroupName=Animation Maker
DisableProgramGroupPage=yes
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
Source: "{#AppSource}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Animation Maker"; Filename: "{app}\AnimationMaker.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Animation Maker"; Filename: "{app}\AnimationMaker.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\AnimationMaker.exe"; Description: "Launch Animation Maker"; Flags: nowait postinstall skipifsilent
