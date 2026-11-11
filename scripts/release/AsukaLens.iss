; Compiler: pinned Inno Setup 7.1.1. No downloads, services or user-data removal.
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef LocalTest
  #define LocalTest 0
#endif
; Command-line defines can be strings: "0" must not select the test identity.
#if (Int(LocalTest) != 0) && (Int(LocalTest) != 1)
  #error LocalTest must be 0 or 1
#endif
#if Int(LocalTest)
  #define ProductName "AsukaLens (LOCAL TEST)"
  #define ProductId "AsukaLens.LocalTest"
#else
  #define ProductName "AsukaLens"
  #define ProductId "AsukaLens.8E3208AB-7151-4AC5-983C-22C5529B8746"
#endif

[Setup]
AppId={#ProductId}
AppName={#ProductName}
AppVersion={#AppVersion}
AppVerName={#ProductName} v{#AppVersion}
AppPublisher=M66Y
AppPublisherURL=https://github.com/M66Y/Hard-Drive-Space-Visualization
DefaultDirName={localappdata}\Programs\{#ProductName}
DefaultGroupName={#ProductName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
LicenseFile={#PayloadDir}\LICENSE
InfoBeforeFile={#PayloadDir}\INSTALL-NOTICE.txt
SetupIconFile={#PayloadDir}\app.ico
UninstallDisplayIcon={app}\AsukaLens.exe
OutputDir={#OutputDir}
OutputBaseFilename=AsukaLens.v{#AppVersion}
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName={#ProductName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
AllowNoIcons=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
AppMutex=Local\AsukaLens.Running
SetupMutex=Local\AsukaLens.Setup
Uninstallable=yes
UninstallLogging=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[CustomMessages]
chinesesimplified.BadVersion=无法读取已安装版本。请先修复或卸载该版本，再重新安装。
english.BadVersion=Cannot read the installed version. Repair or uninstall it before proceeding.
chinesesimplified.Downgrade=已经安装了更新版本。已取消降级，没有修改程序文件。
english.Downgrade=A newer AsukaLens version is installed. Downgrade was cancelled; no files were changed.
chinesesimplified.CloseFirst=请关闭所有 AsukaLens 窗口后重试。安装程序不会结束应用进程。
english.CloseFirst=Close all AsukaLens windows, then retry. Setup will not terminate applications.
chinesesimplified.UnsafeDirectory=目标文件夹已包含其他文件。请选择空文件夹，或保留已有 AsukaLens 的安装位置。
english.UnsafeDirectory=Choose an empty folder or keep the registered AsukaLens installation directory.
chinesesimplified.BackupFailed=无法备份原有程序文件，已停止安装。请关闭占用文件的程序并检查可用空间后重试。
english.BackupFailed=Cannot back up an existing program file. Setup stopped. Close programs using it and check free space before retrying.
chinesesimplified.RecoveryPending=存在未清理的安装恢复文件。请先根据 destinations.txt 恢复或检查备份，再重试。备份位置：%1
english.RecoveryPending=Installation recovery files remain. Restore or review the backups using destinations.txt before retrying. Backup location: %1

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; BeforeInstall: PreserveOriginal

[Icons]
Name: "{autoprograms}\{#ProductName}"; Filename: "{app}\AsukaLens.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\{#ProductName}"; Filename: "{app}\AsukaLens.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\AsukaLens.exe"; Description: "{cm:LaunchProgram,AsukaLens}"; Flags: nowait postinstall skipifsilent

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#ProductId}_is1';

var
  RecoveryDir: String;
  Originals, Backups: TArrayOfString;
  InstallCommitted: Boolean;

function SetFileAttributesW(const Filename: String; Attributes: Cardinal): Boolean;
  external 'SetFileAttributesW@kernel32.dll stdcall';

function RecoveryParent(): String;
begin
  Result := ExpandConstant('{localappdata}\AsukaLens\InstallerRecovery-{#ProductId}');
end;

procedure PreserveOriginal();
var
  Destination, Backup, CandidateDir: String;
  Count: Integer;
begin
  Destination := ExpandConstant(CurrentFilename);
  if not FileExists(Destination) then Exit;
  if RecoveryDir = '' then
  begin
    if not ForceDirectories(RecoveryParent()) then
      RaiseException(CustomMessage('BackupFailed'));
    CandidateDir := GenerateUniqueName(AddBackslash(RecoveryParent()), '.backup');
    if not CreateDir(CandidateDir) then
      RaiseException(CustomMessage('BackupFailed'));
    RecoveryDir := CandidateDir;
  end;
  Count := GetArrayLength(Originals);
  Backup := AddBackslash(RecoveryDir) + IntToStr(Count) + '.bak';
  if not CopyFile(Destination, Backup, True) then
  begin
    DeleteFile(Backup);
    RaiseException(CustomMessage('BackupFailed'));
  end;
  SetArrayLength(Originals, Count + 1);
  SetArrayLength(Backups, Count + 1);
  Originals[Count] := Destination;
  Backups[Count] := Backup;
  // Index N in this local-only file maps N.bak to its original destination.
  if not SaveStringsToUTF8File(AddBackslash(RecoveryDir) + 'destinations.txt', Originals, False) then
    RaiseException(CustomMessage('BackupFailed'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then InstallCommitted := True;
end;

procedure DeinitializeSetup();
var
  I: Integer;
  Restored, Cleaned: Boolean;
begin
  if RecoveryDir = '' then Exit;
  Restored := True;
  if not InstallCommitted then
  begin
    // Inno has rolled back new files/registration by this point, but does not
    // restore overwritten files. Restore only the originals we actually backed up.
    for I := GetArrayLength(Originals) - 1 downto 0 do
    begin
      if FileExists(Originals[I]) then SetFileAttributesW(Originals[I], $80);
      if not CopyFile(Backups[I], Originals[I], False) then Restored := False;
    end;
  end;
  if Restored then
  begin
    // No recursive deletion: only files created by this transaction are removed.
    Cleaned := True;
    for I := 0 to GetArrayLength(Backups) - 1 do
    begin
      SetFileAttributesW(Backups[I], $80);
      if not DeleteFile(Backups[I]) then Cleaned := False;
    end;
    if Cleaned then
    begin
      DeleteFile(AddBackslash(RecoveryDir) + 'destinations.txt');
      RemoveDir(RecoveryDir);
      RemoveDir(RecoveryParent());
    end;
  end
  else
    SuppressibleMsgBox(FmtMessage(CustomMessage('RecoveryPending'), [RecoveryDir]), mbError, MB_OK, IDOK);
end;

function InitializeSetup(): Boolean;
var
  Previous: String;
  OldVersion, NewVersion: Int64;
begin
  Result := False;
  if RegQueryStringValue(HKCU64, UninstallKey, 'DisplayVersion', Previous) then
  begin
    if not StrToVersion(Previous, OldVersion) then
    begin
      SuppressibleMsgBox(CustomMessage('BadVersion'), mbError, MB_OK, IDOK);
      Exit;
    end;
    StrToVersion('{#AppVersion}', NewVersion);
    if ComparePackedVersion(OldVersion, NewVersion) > 0 then
    begin
      SuppressibleMsgBox(CustomMessage('Downgrade'), mbError, MB_OK, IDOK);
      Exit;
    end;
  end;
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  RegisteredDir, TargetDir: String;
  Item: TFindRec;
  IsUpgrade: Boolean;
begin
  Result := '';
  if DirExists(RecoveryParent()) then
  begin
    Result := FmtMessage(CustomMessage('RecoveryPending'), [RecoveryParent()]);
    Exit;
  end;
  if CheckForMutexes('Local\AsukaLens.Running') then
  begin
    Result := CustomMessage('CloseFirst');
    Exit;
  end;
  TargetDir := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
  IsUpgrade := RegQueryStringValue(HKCU64, UninstallKey, 'Inno Setup: App Path', RegisteredDir);
  if IsUpgrade then
    IsUpgrade := CompareText(TargetDir, RemoveBackslashUnlessRoot(RegisteredDir)) = 0;
  if (not IsUpgrade) and FindFirst(AddBackslash(TargetDir) + '*', Item) then
  begin
    try
      repeat
        if (Item.Name <> '.') and (Item.Name <> '..') then
        begin
          Result := CustomMessage('UnsafeDirectory');
          Break;
        end;
      until not FindNext(Item);
    finally
      FindClose(Item);
    end;
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := not CheckForMutexes('Local\AsukaLens.Running');
  if not Result then
    SuppressibleMsgBox(CustomMessage('CloseFirst'), mbError, MB_OK, IDOK);
end;
