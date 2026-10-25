; Fufucode 实验性测试渠道安装包（TEST-1）。
; 与正式安装包 Fufucode-Native.iss 的载体相同，但安装身份完全独立，可与正式版并存：
;   独立 AppId / 安装目录 / 开始菜单项 / 数据根 / 协议名。
; 载荷目录 out\build-test（自包含）或 out\build-test-slim（框架依赖）。
#define AppName "Fufucode Test"
#ifndef AppVersion
#define AppVersion "0.1.0-test.1"
#endif
#ifndef OutName
#define OutName "Fufucode-Test-Setup"
#endif
#ifndef PayloadRoot
#define PayloadRoot "build-test"
#endif
#ifndef IntegrityManifest
#define IntegrityManifest "integrity-test.sha256.json"
#endif
; 程序启动器据此注入 FUFUCODE_DATA_ROOT（相对 %LOCALAPPDATA%）。
#ifndef DataRootName
#define DataRootName "FufucodeTest"
#endif
[Setup]
AppId={{2F8A5C14-7D3E-4B92-A1F6-0C47E9B3D805}
AppName={#AppName}
AppVersion={#AppVersion}
; 独立安装目录：与正式版互不覆盖、互不卸载对方。
DefaultDirName={localappdata}\Programs\Fufucode Test
DefaultGroupName=Fufucode Test
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\..\out
OutputBaseFilename={#OutName}
Compression=lzma2
SolidCompression=yes
SetupLogging=yes
ChangesEnvironment=yes
UninstallDisplayIcon={app}\Fufucode.exe
DisableProgramGroupPage=yes
; 显式静默安装参数与更新器一致（/SILENT /NORESTART /CLOSEAPPLICATIONS）。
[Files]
Source: "..\..\out\{#PayloadRoot}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "..\..\out\{#PayloadRoot}\{#IntegrityManifest}"; DestDir: "{app}"
[Icons]
Name: "{autoprograms}\Fufucode Test"; Filename: "{app}\Fufucode.exe"; WorkingDir: "{userdocs}"
Name: "{autodesktop}\Fufucode Test"; Filename: "{app}\Fufucode.exe"; WorkingDir: "{userdocs}"; Tasks: desktopicon
[Tasks]
Name: "desktopicon"; Description: "Create desktop shortcut"; GroupDescription: "Shortcuts:"
[Registry]
; 测试渠道自己的协议名：后装的测试包不会抢走正式版的会话/审批链接。
Root: HKCU; Subkey: "Software\Classes\fufucode-test"; ValueType: string; ValueData: "URL:Fufucode Test Session"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\fufucode-test"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\fufucode-test\shell\open\command"; ValueType: string; ValueData: """{app}\app\Fufucode.App.exe"" ""%1"""
[Run]
Filename: "{app}\Fufucode.exe"; Description: "Launch Fufucode Test"; Flags: postinstall nowait skipifsilent
Filename: "{app}\Fufucode.exe"; Parameters: "--selftest"; Description: "Run self-test"; Flags: postinstall unchecked

[Code]
const
  EnvironmentKey = 'Environment';
  InstallerKey = 'Software\Fufucode\Installer\Test';

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
  if Exists then Exit; { 已有同名项时不重复添加。 }
  if UserPath <> '' then UserPath := UserPath + ';';
  if RegWriteExpandStringValue(HKCU, EnvironmentKey, 'Path', UserPath + CliPath) then
    RegWriteStringValue(HKCU, InstallerKey, 'CliPathAdded', CliPath)
  else
    Log('Could not add the Fufucode Test CLI to the user PATH. Use cli\fufucode.cmd directly.');
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
    Log('Could not remove the Fufucode Test CLI entry from the user PATH.');
end;
