; ForgeFlow Lite Installer Script

#define MyAppName "ForgeFlow Lite"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "A.Hatem T-Zone SYR"
#define MyAppURL "https://4tiko.net"
#define MyAppExeName "ForgeFlow.exe"

[Setup]
AppId={{73FBD696-FF50-4A43-A1F1-BB5E12E1CF8F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL=https://github.com/Abo7atem/ForgeFlow/releases/latest
DefaultDirName={commonpf}\ForgeFlow
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=Output
OutputBaseFilename=ForgeFlow_Lite_Setup_{#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=ForgeFlow\Assets\ForgeFlow.ico

[Languages]
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "ForgeFlow\bin\Release\net8.0-windows\Protected\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "ForgeFlow\bin\Release\net8.0-windows\Protected\*.dll"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "ForgeFlow\bin\Release\net8.0-windows\Protected\*.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "ForgeFlow\bin\Release\net8.0-windows\Protected\version.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "ForgeFlow\bin\Release\net8.0-windows\Protected\Tools\*"; DestDir: "{app}\Tools"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "ForgeFlow\bin\Release\net8.0-windows\Protected\runtimes\*"; DestDir: "{app}\runtimes"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\Tools"
Type: filesandordirs; Name: "{app}\runtimes"
