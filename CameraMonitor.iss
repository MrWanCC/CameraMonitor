#define MyAppName "CameraMonitor"
#define MyAppVersion "1.1.0"
#define MyAppExeName "CameraMonitor.exe"

[Setup]
AppId={{18C2C66B-5825-4D61-A83A-4F5CC0E25271}
AppName={#MyAppName}
AppVersion={#MyAppVersion}

DefaultDirName={localappdata}\CameraMonitor
DefaultGroupName=CameraMonitor

OutputDir=Output
OutputBaseFilename=CameraMonitor_Setup_V1.0.0

Compression=lzma2
SolidCompression=yes

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

PrivilegesRequired=lowest
WizardStyle=modern

UninstallDisplayIcon={app}\{#MyAppExeName}

CloseApplications=yes

[Files]

; 主程序
Source: "D:\Workspace\CameraMonitor\CameraMonitor\CameraMonitor\bin\x64\Release\CameraMonitor.exe"; DestDir: "{app}"; Flags: ignoreversion

; .NET配置
Source: "D:\Workspace\CameraMonitor\CameraMonitor\CameraMonitor\bin\x64\Release\CameraMonitor.exe.config"; DestDir: "{app}"; Flags: ignoreversion

; 摄像头/ZLM配置
Source: "D:\Workspace\CameraMonitor\CameraMonitor\CameraMonitor\bin\x64\Release\config.ini"; DestDir: "{app}"; Flags: ignoreversion

; LibVLCSharp
Source: "D:\Workspace\CameraMonitor\CameraMonitor\CameraMonitor\bin\x64\Release\LibVLCSharp.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "D:\Workspace\CameraMonitor\CameraMonitor\CameraMonitor\bin\x64\Release\LibVLCSharp.WinForms.dll"; DestDir: "{app}"; Flags: ignoreversion

; VLC整个目录
Source: "D:\Workspace\CameraMonitor\CameraMonitor\CameraMonitor\bin\x64\Release\vlc\*"; DestDir: "{app}\vlc"; Flags: ignoreversion recursesubdirs createallsubdirs

; ============================================================
; 海康SDK运行库（V1.1.0 新增，设备状态诊断必需）
; ============================================================
Source: "D:\Workspace\CameraMonitor\CameraMonitor\CameraMonitor\bin\x64\Release\HCNetSDK.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "D:\Workspace\CameraMonitor\CameraMonitor\CameraMonitor\bin\x64\Release\HCCore.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "D:\Workspace\CameraMonitor\CameraMonitor\CameraMonitor\bin\x64\Release\HCNetSDKCom\*"; DestDir: "{app}\HCNetSDKCom"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "D:\Workspace\CameraMonitor\CameraMonitor\CameraMonitor\bin\x64\Release\HXVA.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "D:\Workspace\CameraMonitor\CameraMonitor\CameraMonitor\bin\x64\Release\NPQos.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "D:\Workspace\CameraMonitor\CameraMonitor\CameraMonitor\bin\x64\Release\SuperRender.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "D:\Workspace\CameraMonitor\CameraMonitor\CameraMonitor\bin\x64\Release\AudioProcess.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "D:\Workspace\CameraMonitor\CameraMonitor\CameraMonitor\bin\x64\Release\AudioRender.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "D:\Workspace\CameraMonitor\CameraMonitor\CameraMonitor\bin\x64\Release\GdiPlus.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "D:\Workspace\CameraMonitor\CameraMonitor\CameraMonitor\bin\x64\Release\OpenAL32.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "D:\Workspace\CameraMonitor\CameraMonitor\CameraMonitor\bin\x64\Release\hlog.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "D:\Workspace\CameraMonitor\CameraMonitor\CameraMonitor\bin\x64\Release\hpr.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "D:\Workspace\CameraMonitor\CameraMonitor\CameraMonitor\bin\x64\Release\libcrypto-3-x64.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "D:\Workspace\CameraMonitor\CameraMonitor\CameraMonitor\bin\x64\Release\libssl-3-x64.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "D:\Workspace\CameraMonitor\CameraMonitor\CameraMonitor\bin\x64\Release\libmmd.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "D:\Workspace\CameraMonitor\CameraMonitor\CameraMonitor\bin\x64\Release\libsafefunc.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "D:\Workspace\CameraMonitor\CameraMonitor\CameraMonitor\bin\x64\Release\zlib1.dll"; DestDir: "{app}"; Flags: ignoreversion

[Icons]

Name: "{autodesktop}\CameraMonitor"; Filename: "{app}\CameraMonitor.exe"
Name: "{group}\CameraMonitor"; Filename: "{app}\CameraMonitor.exe"
Name: "{group}\卸载 CameraMonitor"; Filename: "{uninstallexe}"

[Run]

Filename: "{app}\CameraMonitor.exe"; Description: "启动 CameraMonitor"; Flags: nowait postinstall skipifsilent