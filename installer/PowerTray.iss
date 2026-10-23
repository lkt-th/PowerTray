; Inno Setup script for PowerTray. Build with build-installer.ps1 at the repo root.
; Installs per user (no admin prompt) into %LOCALAPPDATA%\Programs\PowerTray.

#define AppName "PowerTray"
#define AppExe "PowerTray.exe"
#define SourceExe AddBackslash(SourcePath) + "..\publish\" + AppExe
#define AppVersion GetStringFileInfo(SourceExe, "ProductVersion")
#define AppPublisher GetStringFileInfo(SourceExe, "CompanyName")
#define AppCopyright GetStringFileInfo(SourceExe, "LegalCopyright")

[Setup]
AppId={{8F3C2B1A-6D4E-4B7A-9C25-3E1F7A9D4B60}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright={#AppCopyright}
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=Output
OutputBaseFilename=PowerTraySetup-{#AppVersion}
SetupIconFile=..\assets\PowerTray.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; Close a running copy before replacing it
CloseApplications=force
RestartApplications=no

[Tasks]
Name: "startup"; Description: "Start PowerTray when I sign in"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Same value the app's own "Start with Windows" toggle writes
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PowerTray"; ValueData: """{app}\{#AppExe}"""; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#AppExe}"; Flags: runhidden; RunOnceId: "StopPowerTray"

[Code]
// The app is framework-dependent: it needs the .NET Desktop Runtime 8 or newer
function IsDesktopRuntimeInstalled: Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\*'), FindRec) then
  try
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and
         (StrToIntDef(Copy(FindRec.Name, 1, Pos('.', FindRec.Name) - 1), 0) >= 8) then
        Result := True;
    until Result or not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not IsDesktopRuntimeInstalled then
    if SuppressibleMsgBox('PowerTray needs the .NET Desktop Runtime 8 or newer, which is not installed on this PC.' + #13#10#13#10 +
        'Open the download page now? Setup will continue; start PowerTray once the runtime is installed.',
        mbConfirmation, MB_YESNO, IDNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RunValue: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;

  // Remove the startup entry only if it points at this install
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'PowerTray', RunValue) and
     (Pos(Lowercase(ExpandConstant('{app}')), Lowercase(RunValue)) > 0) then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'PowerTray');

  // Settings are kept unless the user asks to remove them (and always kept on silent uninstall)
  if not UninstallSilent and
     (MsgBox('Also remove your PowerTray settings, background image and screen time history?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
  begin
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\PowerTray');
    DelTree(ExpandConstant('{localappdata}\PowerTray'), True, True, True);
  end;
end;
