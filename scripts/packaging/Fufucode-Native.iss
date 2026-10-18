; PKG-1 原生安装包（零 Node / 零 Pi 运行时）。
; 与全量包 Fufucode.iss 并行、互不影响：独立 AppId、独立输出名、独立载荷目录 out\build-native。
; 载荷只含 Fufucode.App + Fufucode.Cli 的 win-x64 self-contained 发布。
#define AppName "Fufucode"
#ifndef AppVersion
#define AppVersion "0.1.0"
#endif
#ifndef OutName
#define OutName "Fufucode-Native-Setup"
#endif
; 载荷目录可切换：self-contained（build-native）或框架依赖瘦身版（build-native-slim）。
#ifndef PayloadRoot
#define PayloadRoot "build-native"
#endif
[Setup]
AppId={{7C4D2E31-5B60-4F27-9F0E-2A6C1D8B3E54}
AppName={#AppName}
AppVersion={#AppVersion}
; 独立的默认安装目录：原生包与全量包可并存，互不覆盖、互不卸载对方。
DefaultDirName={localappdata}\Programs\Fufucode Native
DefaultGroupName=Fufucode Native
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\..\out
OutputBaseFilename={#OutName}
Compression=lzma2
; 小载荷（两份 self-contained 发布、无 15k 文件的 Pi node_modules）允许 solid 压缩；
; 全量包因 truncated-installer 历史问题保持 SolidCompression=no，两者互不牵连。
SolidCompression=yes
SetupLogging=yes
ChangesEnvironment=yes
UninstallDisplayIcon={app}\Fufucode.exe
DisableProgramGroupPage=yes
[Files]
Source: "..\..\out\{#PayloadRoot}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "..\..\out\{#PayloadRoot}\integrity-native.sha256.json"; DestDir: "{app}"
[Icons]
; 与全量包同约定：快捷方式起始目录是用户文档目录，不是 {app}。
; 目标改 Fufucode.exe（GUI 启动器）：有图标、无 cmd 黑框、Windows 当正经应用对待。
Name: "{autoprograms}\Fufucode Native"; Filename: "{app}\Fufucode.exe"; WorkingDir: "{userdocs}"
Name: "{autodesktop}\Fufucode Native"; Filename: "{app}\Fufucode.exe"; WorkingDir: "{userdocs}"; Tasks: desktopicon
[Tasks]
Name: "desktopicon"; Description: "Create desktop shortcut"; GroupDescription: "Shortcuts:"
[Registry]
Root: HKCU; Subkey: "Software\Classes\fufucode"; ValueType: string; ValueData: "URL:Fufucode Session"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\fufucode"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\fufucode\shell\open\command"; ValueType: string; ValueData: """{app}\app\Fufucode.App.exe"" ""%1"""
[Run]
Filename: "{app}\Fufucode.exe"; Description: "Launch Fufucode"; Flags: postinstall nowait skipifsilent
Filename: "{app}\Fufucode.exe"; Parameters: "--selftest"; Description: "Run self-test"; Flags: postinstall unchecked

[Code]
const
  EnvironmentKey = 'Environment';
  InstallerKey = 'Software\Fufucode\Installer\Native';

function CanonicalPathEntry(Value: String): String;
begin
  Result := Trim(Value);
  if (Length(Result) >= 2) and (Result[1] = '"') and
     (Result[Length(Result)] = '"') then
    Result := Copy(Result, 2, Length(Result) - 2);
  while (Length(Result) > 0) and (Result[Length(Result)] = '\') do
    Delete(Result, Length(Result), 1);
  Result := Lowercase(Result);
end;

{ 保留其他所有 PATH 项，包括空项与 %变量%。 }
function RemovePathEntry(Value, Target: String; var Found: Boolean): String;
var
  Separator: Integer;
  Entry: String;
  HasEntry, More: Boolean;
begin
  Result := '';
  Found := False;
  HasEntry := False;
  repeat
    Separator := Pos(';', Value);
    More := Separator > 0;
    if More then begin
      Entry := Copy(Value, 1, Separator - 1);
      Delete(Value, 1, Separator);
    end else
      Entry := Value;
    if CanonicalPathEntry(Entry) = CanonicalPathEntry(Target) then
      Found := True
    else begin
      if HasEntry then Result := Result + ';';
      Result := Result + Entry;
      HasEntry := True;
    end;
  until not More;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  UserPath, CliPath, Ignored: String;
  Exists: Boolean;
begin
  if CurStep <> ssPostInstall then Exit;
  CliPath := ExpandConstant('{app}\cli');
  RegQueryStringValue(HKCU, EnvironmentKey, 'Path', UserPath);
  Ignored := RemovePathEntry(UserPath, CliPath, Exists);
  if Exists then Exit; { 不接管已存在的 PATH 项，避免与全量包互相删除。 }
  if UserPath <> '' then UserPath := UserPath + ';';
  if RegWriteExpandStringValue(HKCU, EnvironmentKey, 'Path', UserPath + CliPath) then
    RegWriteStringValue(HKCU, InstallerKey, 'CliPathAdded', CliPath)
  else
    Log('Could not add Fufucode CLI to the user PATH. Use the installed cli\fufucode.cmd directly.');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  UserPath, OwnedPath, UpdatedPath: String;
  Found: Boolean;
begin
  if CurUninstallStep <> usUninstall then Exit;
  if not RegQueryStringValue(HKCU, InstallerKey, 'CliPathAdded', OwnedPath) then Exit;
  if CanonicalPathEntry(OwnedPath) <> CanonicalPathEntry(ExpandConstant('{app}\cli')) then Exit;
  RegQueryStringValue(HKCU, EnvironmentKey, 'Path', UserPath);
  UpdatedPath := RemovePathEntry(UserPath, OwnedPath, Found);
  if (not Found) or RegWriteExpandStringValue(HKCU, EnvironmentKey, 'Path', UpdatedPath) then begin
    RegDeleteValue(HKCU, InstallerKey, 'CliPathAdded');
    RegDeleteKeyIfEmpty(HKCU, InstallerKey);
  end else
    Log('Could not remove the installed Fufucode CLI entry from the user PATH.');
end;
