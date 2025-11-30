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
Source: "BackerAgent\bin\Release\net9.0\win-x64\publish\BackerAgent.exe"; DestDir: "{app}\service"; Flags: ignoreversion
; Control app binary (publish output from BackerControl)
Source: "BackerControl\bin\Release\net9.0-windows\win-x64\publish\BackerControl.exe"; DestDir: "{app}\control"; Flags: ignoreversion
; Rclone tool
Source: "contrib\rclone.exe"; DestDir: "{app}\contrib"; Flags: ignoreversion
Source: "BackerAgent\bin\Release\net9.0\win-x64\publish\appsettings.json"; DestDir: "{app}\service"; Flags: ignoreversion

[Dirs]
Name: "{app}\service"
Name: "{app}\control"
Name: "{app}\contrib"

[Icons]
Name: "{group}\Backer Control"; Filename: "{app}\control\BackerControl.exe"
Name: "{group}\Uninstall Backer"; Filename: "{uninstallexe}"

[Run]
Filename: "sc.exe"; Parameters: "create BackerAgent binPath= ""{app}\service\BackerAgent.exe"" start= auto"; StatusMsg: "Registering Windows Service..."
Filename: "sc.exe"; Parameters: "start BackerAgent"; StatusMsg: "Starting Windows Service..."

[UninstallRun]
Filename: "sc.exe"; Parameters: "stop BackerAgent"; StatusMsg: "Stopping Windows Service..."; RunOnceId: "StopService"
Filename: "sc.exe"; Parameters: "delete BackerAgent"; StatusMsg: "Removing Windows Service..."; RunOnceId: "DeleteService"

[Code]
procedure UpdateAppSettings;
var
  AppSettingsFile: string;
  RclonePath: string;
  Json: TStringList;
  i: Integer;
  Found: Boolean;
begin
  AppSettingsFile := ExpandConstant('{app}\service\appsettings.json');
  RclonePath := ExpandConstant('{app}\contrib\rclone.exe');
  if FileExists(AppSettingsFile) then
  begin
    Json := TStringList.Create;
    try
      Json.LoadFromFile(AppSettingsFile);
      Found := False;
      for i := 0 to Json.Count - 1 do
      begin
        if Pos('"RclonePath"', Json[i]) > 0 then
        begin
          Json[i] := '  "RclonePath": "' + RclonePath + '",';
          Found := True;
          Break;
        end;
      end;
      if not Found then
      begin
        // Insert before closing brace
        if (Json.Count > 0) and (Pos('}', Json[Json.Count-1]) > 0) then
        begin
          Json.Insert(Json.Count-1, '  "RclonePath": "' + RclonePath + '",');
        end;
      end;
      Json.SaveToFile(AppSettingsFile);
    finally
      Json.Free;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    UpdateAppSettings;
end;
