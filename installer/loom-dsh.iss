; Loom for DSH 安装脚本（Inno Setup 6）
; 参考: DeskOrder tools\DeskOrder.iss（同一套免管理员 / 用户目录 / 固定安装包名 / 静默升级模式）
;
; 编译:   ISCC.exe /DAppVersion=0.1.1 installer\loom-dsh.iss
; 产物:   dist\loom-dsh-Setup.exe（文件名固定不带版本号 → releases/latest/download 与
;         未来应用内更新的直链永远拿到最新版）
; 交互安装: 免管理员，默认装到 {userpf}\Loom（%LOCALAPPDATA%\Programs\Loom），路径可改；
;           装完可选立即启动。
; 静默升级: /SILENT /DIR=<原安装目录>（应用内更新）；装完自动重启应用。
; 数据目录: <安装目录>\Data —— 不在 [Files] 清单内，卸载保留；安装版首次启动会自动把
;           %LOCALAPPDATA%\Loom 的旧数据迁移进来（DataMigration，只复制不删除）。

#define MyAppName "Loom for DSH"
#define MyAppShortName "Loom"
#define MyAppPublisher "Loom for DSH"
#ifndef AppVersion
  #define AppVersion "0.1.1"
#endif

; ── 打包/沙箱测试钩子（正常打包不传这些 /D 参数）──────────────────────────────
; MyOutputDir / MyOutputBaseFilename: 打包脚本可覆盖产物位置（默认 dist\loom-dsh-Setup.exe）。
; TestSandbox: DSH 沙箱只允许写工作区（注册表与 %APPDATA% 被拒绝），端到端安装测试用
;              /DTestSandbox=1 编译一个“不写卸载注册表项、图标落在安装目录内”的变体，
;              以便在工作区内验证安装/升级/卸载机制；正式产物不带此定义。
#ifndef MyOutputDir
  #define MyOutputDir "..\dist"
#endif
#ifndef MyOutputBaseFilename
  #define MyOutputBaseFilename "loom-dsh-Setup"
#endif
; 安装包内嵌的主程序路径：默认 dist\loom-dsh.exe；打包脚本传 /DMyExePath=<绝对路径>，
; 这样自定义输出目录（-OutputDir）时不会误打包旧 dist 里的 exe。
#ifndef MyExePath
  #define MyExePath "..\dist\loom-dsh.exe"
#endif
#ifdef TestSandbox
  #define MyGroupDir "{app}\sandbox-icons"
  #define MyDesktopDir "{app}\sandbox-desktop"
#else
  #define MyGroupDir "{group}"
  #define MyDesktopDir "{autodesktop}"
#endif

[Setup]
; 固定 AppId（升级识别 + 卸载注册表键）。每个产品一个，永不修改。
AppId={{1172EF08-41CF-4CC6-9EDD-4B3E51C20E14}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher={#MyAppPublisher}
WizardStyle=modern
; 免管理员：装到用户目录，安装路径在向导里可改（与 DeskOrder 一致）
PrivilegesRequired=lowest
DefaultDirName={userpf}\{#MyAppShortName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DirExistsWarning=no
; 固定安装包文件名：in-app update 直链依赖，勿加版本号
OutputDir={#MyOutputDir}
OutputBaseFilename={#MyOutputBaseFilename}
Compression=lzma2/max
SolidCompression=yes
; 仅 64 位 Windows（主程序 win-x64 self-contained）
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; 升级时等待旧实例退出（Main 进程的托盘单实例互斥体），再覆盖 exe
CloseApplications=yes
RestartApplications=no
AppMutex=Local\LoomForDsh-Instance-v1
SetupIconFile=..\src\Loom\Assets\Icons\loom-check-light.ico
UninstallDisplayIcon={app}\loom-dsh.exe
UninstallDisplayName={#MyAppName}
#ifdef TestSandbox
; 沙箱端到端测试：注册表写入被平台拒绝 → 不注册卸载项（unins000.exe 仍会生成，可手动运行）
CreateUninstallRegKey=no
#endif

[Languages]
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; \
    GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 安装目录只放主程序 + 卸载器；用户数据目录 Data 由 [Dirs] 创建、不在此清单 → 卸载时保留。
Source: "{#MyExePath}"; DestDir: "{app}"; Flags: ignoreversion

[Dirs]
; uninsneveruninstall: 卸载不删 Data（迁移/快照/报告/日志都在里面）
Name: "{app}\Data"; Flags: uninsneveruninstall

[Icons]
; MyGroupDir/MyDesktopDir 默认 {group}/{autodesktop}；沙箱测试变体改为安装目录内路径
;（%APPDATA%/%USERPROFILE% 在 DSH 沙箱中不可写，仅用于端到端安装验证）。
Name: "{#MyGroupDir}\{#MyAppName}"; Filename: "{app}\loom-dsh.exe"
Name: "{#MyGroupDir}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{#MyDesktopDir}\{#MyAppName}"; Filename: "{app}\loom-dsh.exe"; Tasks: desktopicon

[Run]
; 交互安装：向导最后的“立即运行”勾选项（静默安装时跳过）
Filename: "{app}\loom-dsh.exe"; Description: "{cm:LaunchProgram,{#MyAppName}}"; \
    Flags: nowait postinstall skipifsilent
; 应用内静默升级（已安装 + /SILENT）：装完自动重启应用（DeskOrder 同款）
Filename: "{app}\loom-dsh.exe"; Flags: nowait runasoriginaluser; Check: SilentUpgrade

[Code]
var
  IsUpgradeInstall: Boolean;
  PreviousInstallDir: String;

// [Run] 的 Check 只能是脚本里自定义的函数，这里包一层内置的 WizardSilent。
// 只有“旧版存在 + 静默安装(应用内升级)”才自动重启应用；首次静默安装不拉起托盘。
function SilentUpgrade: Boolean;
begin
  Result := IsUpgradeInstall and WizardSilent;
end;

// 同一 AppId 已安装过时，先静默卸载旧版，避免“已存在该软件”提示卡住 /SILENT 升级。
// Data 目录不在旧版卸载清单内 → 用户数据自动保留，新版本首启继续使用。
function InitializeSetup(): Boolean;
var
  Uninst: String;
  Res: Integer;
  Key: String;
  Value: String;
begin
  Result := True;
  IsUpgradeInstall := False;
  PreviousInstallDir := '';
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{{1172EF08-41CF-4CC6-9EDD-4B3E51C20E14}_is1';
  if RegQueryStringValue(HKCU, Key, 'UninstallString', Uninst) then
  begin
    IsUpgradeInstall := True;
    // 记住旧安装位置：交互升级时把目录页预填为原路径（静默升级由 /DIR 指定）。
    if RegQueryStringValue(HKCU, Key, 'Inno Setup: App Path', Value) then
      PreviousInstallDir := RemoveBackslashUnlessRoot(Value)
    else if RegQueryStringValue(HKCU, Key, 'InstallLocation', Value) then
      PreviousInstallDir := RemoveBackslashUnlessRoot(Value);
    Uninst := RemoveQuotes(Uninst);
    Exec(Uninst, '/SILENT /NORESTART', '', SW_SHOW, ewWaitUntilTerminated, Res);
  end;
end;

procedure InitializeWizard();
begin
  // 交互升级：沿用旧安装目录，避免用户升级后被挪到默认目录。
  if (not WizardSilent) and (PreviousInstallDir <> '') then
    WizardForm.DirEdit.Text := PreviousInstallDir;
end;
