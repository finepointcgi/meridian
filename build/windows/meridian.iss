; Meridian for Windows: a per-user installer (no administrator rights) built by build/package.sh with Inno Setup 6.
;   iscc /DAppVersion=0.10.1 /DSourceDir=<published folder> /DOutputDir=<artifacts> /DOutputName=Meridian-0.10.1-windows-x64-setup meridian.iss
; Installs to %LOCALAPPDATA%\Programs\Meridian, adds Start menu and desktop shortcuts and an uninstaller, and registers
; .meridian (and .sysml) so double-clicking a model opens it in Meridian's local workspace.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef NumericVersion
  #define NumericVersion AppVersion
#endif
#ifndef SourceDir
  #define SourceDir "..\..\artifacts\work\win-x64\Meridian"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts"
#endif
#ifndef OutputName
  #define OutputName "Meridian-" + AppVersion + "-windows-x64-setup"
#endif

[Setup]
AppId={{6E0F3C52-8B1D-4C7A-9E54-2D7B1A9C4F31}
AppName=Meridian
AppVersion={#AppVersion}
AppVerName=Meridian {#AppVersion}
AppPublisher=Meridian
AppPublisherURL=https://github.com/finepointcgi/meridian
AppSupportURL=https://github.com/finepointcgi/meridian/issues
AppUpdatesURL=https://github.com/finepointcgi/meridian/releases
VersionInfoVersion={#NumericVersion}
; Per-user: {autopf} resolves to %LOCALAPPDATA%\Programs when running without elevation.
PrivilegesRequired=lowest
DefaultDirName={autopf}\Meridian
DisableProgramGroupPage=yes
DisableDirPage=auto
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
SetupIconFile=..\assets\meridian.ico
UninstallDisplayIcon={app}\Meridian.exe
UninstallDisplayName=Meridian
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=yes
; Updating from inside the app: close the running copy, then reopen it afterwards.
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "associate"; Description: "Open .meridian and .sysml files with Meridian"; GroupDescription: "File types:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Files from an older version that the new one no longer ships.
Type: filesandordirs; Name: "{app}\wwwroot"

[Icons]
Name: "{autoprograms}\Meridian"; Filename: "{app}\Meridian.exe"
Name: "{autodesktop}\Meridian"; Filename: "{app}\Meridian.exe"; Tasks: desktopicon

[Registry]
; HKA = HKEY_CURRENT_USER for a per-user install.
Root: HKA; Subkey: "Software\Classes\.meridian"; ValueType: string; ValueName: ""; ValueData: "Meridian.Project"; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.meridian"; ValueType: string; ValueName: "Content Type"; ValueData: "application/x-meridian"; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.meridian\OpenWithProgids"; ValueType: string; ValueName: "Meridian.Project"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Meridian.Project"; ValueType: string; ValueName: ""; ValueData: "Meridian Project"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Meridian.Project\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\Meridian.exe,0"
Root: HKA; Subkey: "Software\Classes\Meridian.Project\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Meridian.exe"" ""%1"""
Root: HKA; Subkey: "Software\Classes\.sysml"; ValueType: string; ValueName: ""; ValueData: "Meridian.SysML"; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.sysml\OpenWithProgids"; ValueType: string; ValueName: "Meridian.SysML"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Meridian.SysML"; ValueType: string; ValueName: ""; ValueData: "SysML v2 Textual Model"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Meridian.SysML\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\Meridian.exe,0"
Root: HKA; Subkey: "Software\Classes\Meridian.SysML\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Meridian.exe"" ""%1"""
Root: HKA; Subkey: "Software\Classes\Applications\Meridian.exe\SupportedTypes"; ValueType: string; ValueName: ".meridian"; ValueData: ""; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\Meridian.exe\SupportedTypes"; ValueType: string; ValueName: ".sysml"; ValueData: ""
; MagicDraw / Cameo projects: offered under "Open with" only (Cameo keeps the default association).
Root: HKA; Subkey: "Software\Classes\.mdzip\OpenWithProgids"; ValueType: string; ValueName: "Meridian.MagicDraw"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.mdxml\OpenWithProgids"; ValueType: string; ValueName: "Meridian.MagicDraw"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Meridian.MagicDraw"; ValueType: string; ValueName: ""; ValueData: "MagicDraw / Cameo Project"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Meridian.MagicDraw\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\Meridian.exe,0"
Root: HKA; Subkey: "Software\Classes\Meridian.MagicDraw\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Meridian.exe"" ""%1"""
Root: HKA; Subkey: "Software\Classes\Applications\Meridian.exe\SupportedTypes"; ValueType: string; ValueName: ".mdzip"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\Meridian.exe\SupportedTypes"; ValueType: string; ValueName: ".mdxml"; ValueData: ""

[Run]
Filename: "{app}\Meridian.exe"; Description: "{cm:LaunchProgram,Meridian}"; Flags: nowait postinstall skipifsilent
