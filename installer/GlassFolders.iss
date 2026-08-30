; Inno Setup script for Glass Folders.
; Builds a real per-user installer that registers in "Apps & features" /
; Programs and Features with a proper uninstaller. No admin/UAC required.
;
; Build:  "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\GlassFolders.iss
; Output: dist\GlassFolders-Setup.exe   (built from dist\GlassFolders.exe)

#define AppName "Glass Folders"
#define AppExe "GlassFolders.exe"
#ifndef AppVersion
  #define AppVersion "0.1.6"
#endif

[Setup]
; A stable AppId is what lets Windows recognise upgrades and show one uninstall entry.
AppId={{A7F3C2E1-9B4D-4E6A-8C1F-2D5E7A9B3C4D}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Avromiep
AppPublisherURL=https://github.com/Avromiep/GlassFolders
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
; Per-user install -> lands in %LOCALAPPDATA%\Programs, no UAC prompt.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; Close the app automatically if it's running so its exe isn't locked mid-install.
CloseApplications=force
RestartApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
OutputDir=..\dist
OutputBaseFilename=GlassFolders-Setup
SetupIconFile=..\src\Assets\app.ico

[Files]
Source: "..\dist\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; Migration: older installs shipped as LiquidFolders.exe — remove it so the folder isn't left
; with both exes after the rename to Glass Folders.
Type: files; Name: "{app}\LiquidFolders.exe"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Run]
; Interactive install: offer a "Launch" checkbox on the Finished page.
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
; Silent install (the in-app updater): relaunch the app automatically after upgrading.
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: WizardSilent

[Code]
const
  DataRel = '\GlassFolders';           { %LOCALAPPDATA%\GlassFolders }

procedure KillApp;
var
  ResultCode: Integer;
begin
  { Kill the current exe (GlassFolders.exe) and the legacy one (LiquidFolders.exe) so neither
    holds a lock during an upgrade from an old install. }
  Exec(ExpandConstant('{cmd}'), '/C taskkill /IM GlassFolders.exe /F', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{cmd}'), '/C taskkill /IM LiquidFolders.exe /F', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

{ Make sure the app isn't holding its exe or shortcuts open before we touch them. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  KillApp;
  Result := '';
end;

procedure RemoveDesktopShortcuts(dataDir: String);
var
  FindRec: TFindRec;
  fp: String;
begin
  { The app's own manager launcher (and the legacy one from before the rename). }
  DeleteFile(ExpandConstant('{userdesktop}\Glass Folders.lnk'));
  DeleteFile(ExpandConstant('{userdesktop}\Liquid Folders.lnk'));

  { Each folder places a <Name>.lnk on the desktop; remove those too. }
  if DirExists(dataDir + '\Folders') then
  begin
    if FindFirst(dataDir + '\Folders\*', FindRec) then
    begin
      try
        repeat
          if (FindRec.Attributes and 16 <> 0)          { FILE_ATTRIBUTE_DIRECTORY }
             and (FindRec.Name <> '.') and (FindRec.Name <> '..') then
          begin
            fp := ExpandConstant('{userdesktop}\') + FindRec.Name + '.lnk';
            if FileExists(fp) then
              DeleteFile(fp);
          end;
        until not FindNext(FindRec);
      finally
        FindClose(FindRec);
      end;
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  dataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    KillApp;
    dataDir := ExpandConstant('{localappdata}') + DataRel;
    { Only offer to wipe data in an interactive uninstall; a silent uninstall keeps it. }
    if (not UninstallSilent) and
       (MsgBox('Also remove your Glass Folders folders, settings, and their desktop shortcuts?'
        + #13#10 + #13#10
        + 'Choose No to keep them (a reinstall will pick them back up).',
        mbConfirmation, MB_YESNO) = IDYES) then
    begin
      RemoveDesktopShortcuts(dataDir);
      DelTree(dataDir, True, True, True);
    end;
  end;
end;
