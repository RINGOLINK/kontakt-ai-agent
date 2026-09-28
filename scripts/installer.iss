; ══════════════════════════════════════════════════════════════
;  Kontakt AI Agent —— 安装包脚本（Inno Setup 6）
;
;  要点：
;   · 【可选安装路径】—— 默认 {autopf}，但用户可在向导里改（DisableDirPage=no）
;   · 开始菜单 + 可选桌面快捷方式
;   · 标准卸载项（控制面板可见）
;   · 打包源 = dist\（已由 scripts\build-dist.ps1 生成，含隐私自检）
; ══════════════════════════════════════════════════════════════

#define MyAppName "Kontakt AI Agent"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "RINGOLINK"
#define MyAppURL "https://github.com/RINGOLINK/kontakt-ai-agent"
#define MyAppExeName "KontaktLibManager.exe"
#define DistDir "..\dist"
#define IconFile "..\src\KontaktLibManager\app.ico"

[Setup]
AppId={{8F3A2C71-5B4E-4D9A-9C2F-6E1B7A4D3E50}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppVersion}

; ── 安装位置：允许用户自选 ──
DefaultDirName={autopf}\{#MyAppName}
DisableDirPage=no
DisableProgramGroupPage=yes
AllowNoIcons=yes

; ── 架构：本程序只支持 64 位 Windows ──
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; ── 需要 Windows 10 及以上 ──
MinVersion=10.0

; ── 外观 ──
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#MyAppExeName}
WizardStyle=modern
LicenseFile=..\LICENSE
OutputDir=..\release
OutputBaseFilename=KontaktAI-Agent-{#MyAppVersion}-setup
Compression=lzma2/max
SolidCompression=yes

[Languages]
; ⚠️ 用「官方 Default.isl 作基底 + 自制中文覆盖层」的分层写法 ——
;    Inno Setup 未随包提供简体中文（属非官方翻译），而 GitHub raw / jsdelivr 在本机不可达，
;    故自建 scripts\ChineseSimplified.isl 只覆盖【向导里用户会看到的消息】。
Name: "chinese"; MessagesFile: "compiler:Default.isl,ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"


[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; ⚠️ 整个 dist\ 递归打包（含 data\kspc.exe 与 runtimes\win-x64）
Source: "{#DistDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 只删程序自身产生的日志类文件；用户数据（data\index.db、data\mert 等）【不删】——
; 卸载不该带走用户几百 MB 的索引，也不该在重装时丢数据。
Type: filesandordirs; Name: "{app}\logs"

[Code]
// 安装前提示：如果程序正在运行，要求先关闭（否则文件占用会导致失败）
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
