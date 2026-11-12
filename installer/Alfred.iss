#ifndef AppVersion
#define AppVersion "2.0.1"
#endif
#ifndef PayloadDir
#error PayloadDir must point to the curated release payload
#endif
#ifndef OutputPath
#define OutputPath "..\releases"
#endif
#ifndef Edition
#define Edition "Windows-x64"
#endif
[Setup]
AppId={{8B821D27-BBD9-4E6A-A949-F96F06FA67B3}
AppName=Alfred
AppVersion={#AppVersion}
AppPublisher=Azfar99
AppPublisherURL=https://github.com/Azfar99/Alfred
AppSupportURL=https://github.com/Azfar99/Alfred/issues
AppUpdatesURL=https://github.com/Azfar99/Alfred/releases
DefaultDirName={code:DefaultInstallDir}
DefaultGroupName=Alfred
DisableProgramGroupPage=yes
DisableDirPage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputPath}
OutputBaseFilename=Alfred-{#AppVersion}-{#Edition}-Setup
SetupIconFile=Alfred.ico
UninstallDisplayIcon={app}\Alfred.exe
WizardStyle=modern dynamic
Compression=lzma2/fast
SolidCompression=yes
LZMADictionarySize=32768
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
VersionInfoVersion={#AppVersion}.0
[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
[Dirs]
Name: "{app}\data"; Flags: uninsneveruninstall
Name: "{app}\temp"; Flags: uninsneveruninstall
[Files]
Source: "{#PayloadDir}\prerequisites\vc_redist.x64.exe"; Flags: dontcopy
Source: "{#PayloadDir}\app\*"; DestDir: "{app}"; Excludes: "data\*,temp\*,build\*"; Flags: ignoreversion recursesubdirs
[Icons]
Name: "{autoprograms}\Alfred"; Filename: "{app}\Alfred.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Alfred"; Filename: "{app}\Alfred.exe"; WorkingDir: "{app}"; Tasks: desktopicon
[Run]
Filename: "{app}\Alfred.exe"; Description: "Launch Alfred"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
[Code]
function DefaultInstallDir(Param: String): String;
begin
  if DirExists('F:\') then Result := 'F:\Apps\Alfred'
  else Result := ExpandConstant('{localappdata}\Programs\Alfred');
end;
function InitializeSetup(): Boolean;
var Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 461808);
  if not Result then MsgBox('Alfred needs .NET Framework 4.7.2 or later. Install it from Microsoft, then run Setup again.', mbError, MB_OK);
end;
function HasVCRuntime(): Boolean;
var A, B: Int64;
begin
  Result := GetPackedVersion(ExpandConstant('{sys}\vcruntime140.dll'), A) and GetPackedVersion(ExpandConstant('{sys}\msvcp140.dll'), B);
  if Result then Result := (ComparePackedVersion(A, PackVersionComponents(14, 40, 0, 0)) >= 0) and (ComparePackedVersion(B, PackVersionComponents(14, 40, 0, 0)) >= 0);
end;
function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code: Integer;
begin
  Result := '';
  if not HasVCRuntime() then begin
    ExtractTemporaryFile('vc_redist.x64.exe');
    if not Exec(ExpandConstant('{tmp}\vc_redist.x64.exe'), '/install /passive /norestart', '', SW_SHOW, ewWaitUntilTerminated, Code) then begin
      Result := 'Could not start the Microsoft Visual C++ runtime installer. Please install it from Microsoft and retry.'; exit;
    end;
    if Code = 3010 then begin NeedsRestart := True; Result := 'Microsoft runtime installed. Restart Windows and run Alfred Setup again.'; exit; end;
    if (Code <> 0) and (Code <> 1638) then begin Result := 'Microsoft runtime setup returned code ' + IntToStr(Code) + '. Please install it from Microsoft and retry.'; exit; end;
    if not HasVCRuntime() then begin Result := 'The Microsoft Visual C++ runtime is still missing or too old.'; exit; end;
  end;
  if FileExists(ExpandConstant('{app}\Alfred.exe')) then Exec(ExpandConstant('{app}\Alfred.exe'), '--quit', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code);
end;
function InitializeUninstall(): Boolean;
var Code: Integer;
begin
  Result := True;
  if FileExists(ExpandConstant('{app}\Alfred.exe')) then Exec(ExpandConstant('{app}\Alfred.exe'), '--quit', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code);
end;
