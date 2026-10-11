#define AppName "Fufucode"
#ifndef AppVersion
#define AppVersion "0.1.0"
#endif
#ifndef OutName
#define OutName "Fufucode-Setup"
#endif
[Setup]
AppId={{640956A0-9ACB-4E56-BDBA-49608B84942E}
AppName={#AppName}
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\Fufucode
DefaultGroupName=Fufucode
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\..\out
OutputBaseFilename={#OutName}
Compression=lzma2
; SolidCompression=no: the earlier "setup files are corrupted" runtime failure was caused
; by a TRUNCATED installer (ISCC killed mid-write by a 180s foreground shell timeout), not by
; solid compression itself. solid=yes on the app-only subset installs fine; solid=yes on the
; FULL ~585MB/15k-file payload is not yet verified end-to-end, so the proven-safe default is
; no. A completed solid=yes build may still work — but it is not the recorded evidence.
; Recorded evidence keeps the conservative default.
SolidCompression=no
SetupLogging=yes
ChangesEnvironment=yes
UninstallDisplayIcon={app}\app\Fufucode.App.exe
DisableProgramGroupPage=yes
[Files]
Source: "..\..\out\build\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
; Integrity manifest — written per version by scripts/packaging/integrity.ps1.
; It ships with the code tree and is uninstalled with it (user data lives outside {app}).
Source: "..\..\out\build\integrity.sha256.json"; DestDir: "{app}"
[Icons]
; D Wave2 finding: shortcuts should start in the user's Documents dir, not {app}.
Name: "{autoprograms}\Fufucode"; Filename: "{app}\Fufucode.cmd"; WorkingDir: "{userdocs}"
Name: "{autodesktop}\Fufucode"; Filename: "{app}\Fufucode.cmd"; WorkingDir: "{userdocs}"; Tasks: desktopicon
[Tasks]
Name: "desktopicon"; Description: "Create desktop shortcut"; GroupDescription: "Shortcuts:"
[Registry]
Root: HKCU; Subkey: "Software\Classes\fufucode"; ValueType: string; ValueData: "URL:Fufucode Session"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\fufucode"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\fufucode\shell\open\command"; ValueType: string; ValueData: """{app}\app\Fufucode.App.exe"" ""%1"""
[Run]
Filename: "{app}\Fufucode.cmd"; Description: "Launch Fufucode"; Flags: postinstall nowait skipifsilent
Filename: "{app}\Fufucode.cmd"; Parameters: "--selftest"; Description: "Run self-test"; Flags: postinstall unchecked

[Code]
const
  EnvironmentKey = 'Environment';
  InstallerKey = 'Software\Fufucode\Installer';

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

{ Preserve all other PATH entries, including empty entries and %variables%. }
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
  if Exists then Exit; { Never take ownership of a pre-existing PATH entry. }
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
