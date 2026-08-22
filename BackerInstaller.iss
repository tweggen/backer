; Backer Installer Script

[Setup]
AppName=Backer
AppVersion=1.0
DefaultDirName={commonpf}\Backer
DefaultGroupName=Backer
OutputDir=output
OutputBaseFilename=BackerInstaller
Compression=lzma
SolidCompression=yes
PrivilegesRequired=admin

[Files]
; Service binary (publish output from BackerAgent)
Source: "BackerAgent\bin\Release\net9.0\win-x64\publish\*"; \
    DestDir: "{app}\service"; \
    Flags: ignoreversion recursesubdirs
; Deploy a default appsettings.json into ProgramData\Backer
Source: "BackerAgent\bin\Release\net9.0\win-x64\publish\appsettings.json"; \
    DestDir: "{commonappdata}\Backer"; \
    Flags: ignoreversion
; Control app binary (publish output from YourBacker - cross-platform Avalonia app)
Source: "YourBacker\bin\Release\net9.0\win-x64\publish\*"; \
    DestDir: "{app}\control"; \
    Flags: ignoreversion recursesubdirs

; Rclone tool
Source: "contrib\rclone.exe"; DestDir: "{app}\contrib"; Flags: ignoreversion

; Git client (MinGit) for the git transfer engine. worker/WorkerGit shells out
; to a real git CLI, and a Windows service does not inherit the installing
; user's PATH - so we ship our own instead of hoping one is installed. Fetch it
; with contrib\fetch-mingit.ps1 before compiling; without it this line fails the
; compile with "no files found matching", which is the point.
; MinGit is GPLv2 - its LICENSE.txt sits at the root of the tree and is copied
; along with the binaries. See docs/THIRD-PARTY.md for the source offer.
Source: "contrib\git\*"; DestDir: "{app}\contrib\git"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
Name: "{app}\service"
Name: "{app}\control"
Name: "{app}\contrib"
Name: "{app}\contrib\git"

[Icons]
Name: "{group}\YourBacker"; Filename: "{app}\control\YourBacker.exe"
Name: "{group}\Uninstall Backer"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\control\YourBacker.exe"; Description: "Launch YourBacker"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "YourBacker"; \
    ValueData: """{app}\control\YourBacker.exe"""; Flags: uninsdeletevalue


[UninstallRun]
Filename: "sc.exe"; Parameters: "stop BackerAgent"; StatusMsg: "Stopping Windows Service..."; RunOnceId: "StopService"
Filename: "sc.exe"; Parameters: "delete BackerAgent"; StatusMsg: "Removing Windows Service..."; RunOnceId: "DeleteService"

[UninstallDelete]
; The git engine's bare mirror cache (GitWorkerOptions.CacheRoot, defaulted in
; WorkerGit/DependencyInjection.cs to EnvironmentDetector.GetConfigDir). It is
; disposable by design and can reach gigabytes, so it goes with the uninstall.
Type: filesandordirs; Name: "{commonappdata}\Backer\Config\git-cache"

[Code]
{ True when the last non-whitespace character of S is a comma. Written out
  rather than using TrimRight so this works on every Inno version. }
function EndsWithComma(S: string): Boolean;
var
  i: Integer;
begin
  Result := False;
  for i := Length(S) downto 1 do
  begin
    if (S[i] = ' ') or (S[i] = #9) or (S[i] = #13) or (S[i] = #10) then
      Continue;
    Result := (S[i] = ',');
    exit;
  end;
end;

{ Rewrites one "Key": "value" line of a JSON file in place, preserving the
  original indentation and trailing comma. Deliberately line-based rather than
  a real JSON edit - Inno Setup has no JSON support, and both keys we patch
  (RClonePath, GitPath) ship in appsettings.json as a single-line string. }
procedure UpdateJsonStringSetting(
  AppSettingsFile: string;
  Key: string;
  Value: string);
var
  EscapedValue: string;
  Indent: string;
  Suffix: string;
  Json: TStringList;
  i: Integer;
  KeyPos: Integer;
begin
  if not FileExists(AppSettingsFile) then
    exit;

  Json := TStringList.Create;
  try
    Json.LoadFromFile(AppSettingsFile);

    // Escape backslashes for valid JSON
    EscapedValue := Value;
    StringChangeEx(EscapedValue, '\', '\\', True);

    for i := 0 to Json.Count - 1 do
    begin
      KeyPos := Pos('"' + Key + '"', Json[i]);
      if KeyPos > 0 then
      begin
        Indent := Copy(Json[i], 1, KeyPos - 1);
        if EndsWithComma(Json[i]) then
          Suffix := ','
        else
          Suffix := '';
        Json[i] := Indent + '"' + Key + '": "' + EscapedValue + '"' + Suffix;
        Break;
      end;
    end;

    Json.SaveToFile(AppSettingsFile);
  finally
    Json.Free;
  end;
end;

{ Points the agent at the two tools this installer bundles. }
procedure UpdateAppSettings(AppSettingsFile: string);
begin
  UpdateJsonStringSetting(AppSettingsFile, 'RClonePath',
    ExpandConstant('{app}\contrib\rclone.exe'));
  UpdateJsonStringSetting(AppSettingsFile, 'GitPath',
    ExpandConstant('{app}\contrib\git\cmd\git.exe'));
end;

{ The agent probes `git --version` at startup and, on failure, simply never
  advertises the "git" capability - git rules then queue forever with nothing
  visible to the user. Check here instead, where we can still say so. }
procedure VerifyBundledGit();
var
  GitExe: string;
  ResultCode: Integer;
begin
  GitExe := ExpandConstant('{app}\contrib\git\cmd\git.exe');

  if not FileExists(GitExe) then
  begin
    if not WizardSilent then
      MsgBox('The bundled git client is missing from ' + GitExe + '.' #13#10
             + 'Backup rules using git repositories will stay queued and never run.',
             mbError, MB_OK);
    exit;
  end;

  if not Exec(GitExe, '--version', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
  begin
    if not WizardSilent then
      MsgBox('The bundled git client did not run (' + GitExe + ').' #13#10
             + 'Backup rules using git repositories will stay queued and never run.',
             mbError, MB_OK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    UpdateAppSettings(ExpandConstant('{commonappdata}\Backer\appsettings.json'));
    UpdateAppSettings(ExpandConstant('{app}\service\appsettings.json'));

    VerifyBundledGit();

    Exec(ExpandConstant('sc.exe'),
           'create BackerAgent binPath= "' + ExpandConstant('{app}\service\BackerAgent.exe') + '" start= auto',
           '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('sc.exe'), 'start BackerAgent', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
