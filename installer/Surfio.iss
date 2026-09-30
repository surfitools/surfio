; Surfio installer — build with Inno Setup 6 (https://jrsoftware.org/isinfo.php)
; Run ..\publish.ps1; it publishes the app and compiles this script.

#define AppName "Surfio"
#define AppVersion "1.0.0"
#define AppPublisher "Surfi"
#define AppExe "Surfio.exe"

[Setup]
AppId={{6B7E3C2A-5F1D-4E8B-9A3C-2D4F6E8A1B90}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=Surfio-Setup-{#AppVersion}
SetupIconFile=..\Assets\surfio.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequiredOverridesAllowed=dialog
ChangesAssociations=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "assocvideo"; Description: "Offer Surfio for video files"; GroupDescription: "File types:"
Name: "assocaudio"; Description: "Offer Surfio for music files"; GroupDescription: "File types:"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; File type used by all associations
Root: HKA; Subkey: "Software\Classes\Surfio.Media"; ValueType: string; ValueData: "Media file"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Surfio.Media\DefaultIcon"; ValueType: string; ValueData: "{app}\{#AppExe},0"
Root: HKA; Subkey: "Software\Classes\Surfio.Media\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""
; "Open with" entry and Settings > Default apps registration
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Surfio\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Surfio\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Video and audio player"
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "Surfio"; ValueData: "Software\Surfio\Capabilities"; Flags: uninsdeletevalue
#include "associations.iss"

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch Surfio"; Flags: nowait postinstall skipifsilent
