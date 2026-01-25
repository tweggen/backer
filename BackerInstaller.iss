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

[Dirs]
Name: "{app}\service"
Name: "{app}\control"
Name: "{app}\contrib"

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

[Code]
procedure UpdateAppSettings(
  AppSettingsFile: string;
  RclonePath: string);
var
  EscapedPath: string;
  Json: TStringList;
  i: Integer;
begin
  if FileExists(AppSettingsFile) then
  begin
    Json := TStringList.Create;
    try
      Json.LoadFromFile(AppSettingsFile);

      // Escape backslashes for valid JSON
      EscapedPath := RclonePath;
      StringChangeEx(EscapedPath, '\', '\\', True);

      for i := 0 to Json.Count - 1 do
      begin
        if Pos('"RClonePath"', Json[i]) > 0 then
        begin
          Json[i] := '    "RClonePath": "' + EscapedPath + '",';
          Break;
        end;
      end;

      Json.SaveToFile(AppSettingsFile);
    finally
      Json.Free;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var 
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    UpdateAppSettings(
      ExpandConstant('{commonappdata}\Backer\appsettings.json'),
      ExpandConstant('{app}\contrib\rclone.exe'));
    UpdateAppSettings(
      ExpandConstant('{app}\service\appsettings.json'),
      ExpandConstant('{app}\contrib\rclone.exe'));

    Exec(ExpandConstant('sc.exe'),
           'create BackerAgent binPath= "' + ExpandConstant('{app}\service\BackerAgent.exe') + '" start= auto',
           '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('sc.exe'), 'start BackerAgent', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;    
end;
