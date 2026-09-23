#ifndef AppVersion
  #define AppVersion "0.3.10"
#endif
#ifndef PayloadDir
  #error PayloadDir must point to the self-contained publish directory.
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

#define AppName "屏幕翻译器"

[Setup]
AppId={{9B7A3C32-8408-47D0-87CB-451606B4EA72}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=ScreenTranslator
DefaultDirName={localappdata}\Programs\ScreenTranslator
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir={#OutputDir}
OutputBaseFilename=ScreenTranslator-Setup-{#AppVersion}-win-x64
UninstallDisplayIcon={app}\ScreenTranslator.exe
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
Uninstallable=yes
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Windows installer

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\ScreenTranslator.exe"; IconFilename: "{app}\ScreenTranslator.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\ScreenTranslator.exe"; IconFilename: "{app}\ScreenTranslator.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\ScreenTranslator.exe"; Description: "启动{#AppName}"; Flags: nowait postinstall skipifsilent
