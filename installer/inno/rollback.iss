// Preserve replaced program files for a cancelled upgrade. Inno rolls back new
// files; this journal restores the previous bytes after that rollback finishes.
var
  RollbackDirectory, RollbackRegistry: String;
  RollbackTargets: TArrayOfString;
  RollbackReady, InstallWriteStarted, InstallCommitted: Boolean;

function BackupFile(Target: String): Boolean;
var I: Integer; Backup: String;
begin
  Result := True;
  if not FileExists(Target) then exit;
  if HasPathLink(Target) then begin Result := False; exit; end;
  for I := 0 to GetArrayLength(RollbackTargets) - 1 do
    if SamePath(Target, RollbackTargets[I]) then exit;
  I := GetArrayLength(RollbackTargets);
  Backup := AddBackslash(RollbackDirectory) + IntToStr(I) + '.bak';
  Result := FileCopy(Target, Backup, False);
  if not Result then exit;
  SetArrayLength(RollbackTargets, I + 1); RollbackTargets[I] := Target;
  SaveStringsToFile(AddBackslash(RollbackDirectory) + 'files.txt', RollbackTargets, False);
end;

procedure DiscardRollback;
var I: Integer;
begin
  if RollbackDirectory = '' then exit;
  for I := 0 to GetArrayLength(RollbackTargets) - 1 do
    DeleteFile(AddBackslash(RollbackDirectory) + IntToStr(I) + '.bak');
  DeleteFile(AddBackslash(RollbackDirectory) + 'files.txt');
  DeleteFile(AddBackslash(RollbackDirectory) + 'registration.reg');
  RemoveDir(RollbackDirectory);
  RollbackDirectory := ''; SetArrayLength(RollbackTargets, 0); RollbackReady := False;
end;

function PrepareRollback: String;
var I, ExitCode: Integer; Search: TFindRec; Root, Target: String;
begin
  Result := '';
  if RollbackReady then exit;
  // A failed prepare may already have copied some files, but made no changes.
  DiscardRollback;
  if ExistingDirectory = '' then exit;
  RollbackDirectory := AddBackslash(ExtractFileDir(RemoveBackslashUnlessRoot(ExpandConstant('{app}')))) +
    '.{#ProductName}.rollback-' + IntToStr(GetTickCount64);
  if DirExists(RollbackDirectory) or HasPathLink(RollbackDirectory) or not ForceDirectories(RollbackDirectory) then begin
    Result := '无法创建升级恢复目录，程序文件未修改。请检查磁盘权限与可用空间。'; exit;
  end;
  for I := 0 to GetArrayLength(Manifest) - 1 do begin
    if UiActive then begin
      UI_Pump;
      if InstallCancelled then begin Result := '安装已取消，程序文件未修改。'; exit; end;
    end;
    if InstallCancelled then begin Result := '安装已取消，程序文件未修改。'; exit; end;
    if not BackupFile(AddBackslash(ExpandConstant('{app}')) + Manifest[I]) then begin
      Result := '无法备份旧程序文件，程序文件未修改。请检查磁盘空间后重试。'; exit;
    end;
  end;
  if FindFirst(ExpandConstant('{app}\unins*.*'), Search) then try
    repeat
      if (Search.Attributes and FILE_ATTRIBUTE_DIRECTORY = 0) and
        ((Lowercase(ExtractFileExt(Search.Name)) = '.exe') or (Lowercase(ExtractFileExt(Search.Name)) = '.dat')) then
        if not BackupFile(AddBackslash(ExpandConstant('{app}')) + Search.Name) then begin
          Result := '无法备份卸载信息，程序文件未修改。'; exit;
        end;
    until not FindNext(Search);
  finally FindClose(Search); end;
  Target := ExpandConstant('{#DesktopLink}.lnk');
  if OwnsShortcut(Target, ExpandConstant('{app}\{#Executable}')) and not BackupFile(Target) then begin Result := '无法备份桌面快捷方式，程序文件未修改。'; exit; end;
  Target := ExpandConstant('{#StartMenuDirectory}\{#ProductName}.lnk');
  if OwnsShortcut(Target, ExpandConstant('{app}\{#Executable}')) and not BackupFile(Target) then begin Result := '无法备份开始菜单快捷方式，程序文件未修改。'; exit; end;
  Target := ExpandConstant('{#StartMenuDirectory}\卸载 {#ProductName}.lnk');
  Root := RegisteredUninstaller(InnoKey);
  if Root = '' then Root := RegisteredUninstaller(LegacyKey);
  if (Root <> '') and OwnsShortcut(Target, Root) and not BackupFile(Target) then begin Result := '无法备份卸载入口，程序文件未修改。'; exit; end;
  if RegKeyExists(SelectedRoot, InnoKey) then begin
    if IsAdminInstallMode then Root := 'HKLM\' else Root := 'HKCU\';
    RollbackRegistry := Root + InnoKey;
    Target := AddBackslash(RollbackDirectory) + 'registration.reg';
    if not Exec(ExpandConstant('{sys}\reg.exe'), 'export "' + RollbackRegistry + '" "' + Target + '" /y /reg:64', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then begin
      Result := '无法备份安装记录，程序文件未修改。'; exit;
    end;
  end;
  RollbackReady := True;
end;

procedure FinishRollback;
var I, ExitCode: Integer; Success: Boolean; Backup: String;
begin
  if RollbackDirectory = '' then exit;
  Success := True;
  if RollbackReady and InstallWriteStarted and not InstallCommitted then begin
    InstallStage(6, '正在恢复旧程序文件，保留用户资料');
    Log('Restoring the previous installation from ' + RollbackDirectory);
    for I := 0 to GetArrayLength(RollbackTargets) - 1 do begin
      Backup := AddBackslash(RollbackDirectory) + IntToStr(I) + '.bak';
      if HasPathLink(RollbackTargets[I]) or not ForceDirectories(ExtractFileDir(RollbackTargets[I])) or
        not FileCopy(Backup, RollbackTargets[I], False) then Success := False;
    end;
    Backup := AddBackslash(RollbackDirectory) + 'registration.reg';
    if FileExists(Backup) then
      if not Exec(ExpandConstant('{sys}\reg.exe'), 'import "' + Backup + '" /reg:64', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then Success := False;
  end;
  if Success then DiscardRollback
  else begin
    Log('Previous installation recovery is incomplete. Backup retained at ' + RollbackDirectory);
    InstallStage(4, '旧程序尚未完全恢复。请保留恢复目录并重新运行安装包：' + RollbackDirectory);
    SuppressibleMsgBox('旧程序尚未完全恢复。请保留恢复目录并重新运行安装包：' + #13#10 + RollbackDirectory, mbError, MB_OK, IDOK);
  end;
end;
