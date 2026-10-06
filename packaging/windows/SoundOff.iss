; Windows installer. Built by the release workflow:
;   iscc /DAppVersion=1.0.0 /DSourceDir=<published app> /DOutputDir=<dist> packaging\windows\SoundOff.iss
; Installs for the current user only, so it needs no administrator rights. The transcription runtime, models, settings
; and projects live outside the install folder and are left alone by the uninstaller.

#ifndef AppVersion
  #error Pass /DAppVersion=x.y.z
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=<published app folder>
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

[Setup]
AppId={{6F1B7C2E-3D4A-4E8B-9C51-2A7D9E0F4B13}
AppName=SoundOff
AppVersion={#AppVersion}
AppVerName=SoundOff {#AppVersion}
AppPublisher=Vayne Altapascine
AppPublisherURL=https://github.com/vaynealtapascine/SoundOff
AppSupportURL=https://github.com/vaynealtapascine/SoundOff/issues
DefaultDirName={localappdata}\Programs\SoundOff
DefaultGroupName=SoundOff
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile={#SourceDir}\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=SoundOff-{#AppVersion}-win-x64-setup
UninstallDisplayIcon={app}\SoundOff.Desktop.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; An upgrade replaces the app wholesale, so files a newer version dropped do not linger.
Type: filesandordirs; Name: "{app}\worker"
Type: filesandordirs; Name: "{app}\setup"
Type: filesandordirs; Name: "{app}\ffmpeg"

[UninstallDelete]
; Anything running setup left in the install folder.
Type: filesandordirs; Name: "{app}\setup"

[Icons]
Name: "{group}\SoundOff"; Filename: "{app}\SoundOff.Desktop.exe"
Name: "{group}\Set up SoundOff transcription"; Filename: "{app}\setup\setup-transcription.cmd"
Name: "{autodesktop}\SoundOff"; Filename: "{app}\SoundOff.Desktop.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\setup\setup-transcription.cmd"; Description: "Set up transcription now (downloads about 2 GB)"; Flags: postinstall shellexec skipifsilent; Check: not RuntimeInstalled
Filename: "{app}\SoundOff.Desktop.exe"; Description: "Open SoundOff"; Flags: postinstall nowait skipifsilent

[Code]
// The same places the app looks: SOUNDOFF_HOME, %LOCALAPPDATA%\SoundOff, %USERPROFILE%\SoundOff.
function RuntimeInstalled: Boolean;
var
  Home: String;
begin
  Home := GetEnv('SOUNDOFF_HOME');
  Result := ((Home <> '') and FileExists(Home + '\runtime\venv\Scripts\python.exe'))
    or FileExists(ExpandConstant('{localappdata}\SoundOff\runtime\venv\Scripts\python.exe'))
    or FileExists(GetEnv('USERPROFILE') + '\SoundOff\runtime\venv\Scripts\python.exe');
end;
