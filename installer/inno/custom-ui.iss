// The native DLL presents controls; Inno exclusively owns installation changes.
function UiPath: String;
var Buffer: String; Count: Integer;
begin
  Buffer := StringOfChar(' ', 1024); Count := UI_GetPath(Buffer, 1024);
  Result := Copy(Buffer, 1, Count);
end;

function ParameterPresent(Prefix: String): Boolean;
var I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do if Pos(Uppercase(Prefix), Uppercase(ParamStr(I))) = 1 then begin Result := True; exit; end;
end;

function RegisteredAt(Root: Integer): Boolean;
begin
  Result := RegKeyExists(Root, InnoKey) or RegKeyExists(Root, LegacyKey);
end;

procedure UiSaveDraft;
var I: Integer;
begin
  I := Ord(UiAllUsers); UiPaths[I] := UiPath;
  UiDesktop[I] := UI_GetDesktop <> 0; UiStart[I] := UI_GetStart <> 0;
end;

procedure UiLoadScope;
var I: Integer; Value: Cardinal; Existing: String;
begin
  I := Ord(UiAllUsers); Existing := ExistingDirectory;
  if not UiLoaded[I] then begin
    UiPaths[I] := DefaultInstallDir(''); UiDesktop[I] := True; UiStart[I] := True;
    if Existing <> '' then begin
      if RegQueryDWordValue(SelectedRoot, InnoKey, 'CinnaDesktopShortcut', Value) then UiDesktop[I] := Value <> 0
      else begin
#ifdef QaRoot
        UiDesktop[I] := FileExists('{#QaRoot}\shortcuts\desktop\{#ProductName}.lnk');
        UiStart[I] := FileExists('{#QaRoot}\shortcuts\start\{#ProductName}\{#ProductName}.lnk');
#else
        if UiAllUsers then begin
          UiDesktop[I] := FileExists(ExpandConstant('{commondesktop}\{#ProductName}.lnk'));
          UiStart[I] := FileExists(ExpandConstant('{commonprograms}\{#ProductName}\{#ProductName}.lnk'));
        end else begin
          UiDesktop[I] := FileExists(ExpandConstant('{userdesktop}\{#ProductName}.lnk'));
          UiStart[I] := FileExists(ExpandConstant('{userprograms}\{#ProductName}\{#ProductName}.lnk'));
        end;
#endif
      end;
      if RegQueryDWordValue(SelectedRoot, InnoKey, 'CinnaStartMenuShortcut', Value) then UiStart[I] := Value <> 0;
    end;
    UiLoaded[I] := True;
  end;
  UI_SetOptions(I, UiPaths[I], Ord(UiDesktop[I]), Ord(UiStart[I]), Ord(Existing <> ''));
  if Existing <> '' then UI_SetNote('升级沿用原位置。更换位置请先卸载，资料会保留。')
  else UI_SetNote('{#UiRequirements}');
end;

procedure UiError(Message: String);
begin
  UiPhase := 4; UI_SetPhase(4, Message); Log('Custom UI: ' + Message);
#ifdef QaRoot
  if HasParameter('CINNAQAEXIT') then UI_AutoCancel;
#endif
end;

procedure UiFinishController(Code: Integer);
begin
  UiActive := False; UI_Destroy; ReleaseMaintenanceLock; ExitProcess(Code);
end;

procedure UiAction(Action: Integer);
var Error, Arguments, Tasks, Directory: String; ExitCode: Integer;
begin
  if not UiActive then exit;
  case Action of
    1: begin
      if (UI_BackendRunning <> 0) or (UiPhase = 3) then exit;
      UiCancelRequested := False;
      UiSaveDraft; Directory := UiPath;
      if (MaintenanceLock = 0) and not AcquireMaintenanceLock then begin UiError('另一个安装或卸载程序正在运行，请等待完成后重试。'); exit; end;
      Error := ValidateDirectory(Directory);
      if Error <> '' then begin UiError(Error); exit; end;
      if UI_GetDesktop <> 0 then Tasks := 'desktopicon' else Tasks := '!desktopicon';
      if UI_GetStart <> 0 then Tasks := Tasks + ',startmenuicon' else Tasks := Tasks + ',!startmenuicon';
      WizardForm.DirEdit.Text := Directory; WizardSelectTasks(Tasks);
      Arguments := '/NORESTART /DIR="' + Directory + '" /TASKS="' + Tasks + '"';
      if UiAllUsers then Arguments := '/ALLUSERS ' + Arguments else Arguments := '/CURRENTUSER ' + Arguments;
#ifdef QaRoot
      if ExpandConstant('{param:CINNAQACANCEL|}') <> '' then Arguments := Arguments + ' /CINNAQACANCEL=' + ExpandConstant('{param:CINNAQACANCEL|}');
      Arguments := Arguments + ' /LOG="{#QaRoot}\backend.log"';
#endif
      ReleaseMaintenanceLock; UiPhase := 5;
      Log('Background Inno transaction: ' + Arguments);
      if UI_StartBackend(ExpandConstant('{srcexe}'), Arguments) = 0 then begin
        if not AcquireMaintenanceLock then UiError('另一个安装或卸载程序正在运行，请关闭后重新打开安装包。')
        else UiError('无法启动安装事务，程序文件未修改。请重试。');
      end;
    end;
    2: begin
      if UI_BackendRunning <> 0 then begin
        if (UiPhase <> 6) and (UI_ConfirmCancel <> 0) then begin UiPhase := 6; UI_BackendCancel; end;
        exit;
      end;
      UiFinishController(UiHandoffResult);
    end;
    3: begin
      if UiPhase <> 3 then exit;
#ifdef QaRoot
#if ProductKind == "network"
      exit;
#endif
#endif
      try
        Directory := UiPaths[Ord(UiAllUsers)];
        if not ExecAsOriginalUser(AddBackslash(Directory) + '{#Executable}', '{#LaunchCode}', Directory, SW_SHOWNORMAL, ewNoWait, ExitCode) then
          SuppressibleMsgBox('应用未能打开。请从开始菜单或安装目录重试。', mbError, MB_OK, IDOK)
        else UiFinishController(0);
      except
        SuppressibleMsgBox('应用未能打开。请从开始菜单或安装目录重试。', mbError, MB_OK, IDOK);
      end;
    end;
    4: begin UiSaveDraft; UiAllUsers := UI_GetScope <> 0; UiLoadScope; end;
    5: begin
      Directory := UiPath;
      if BrowseForFolder('选择 {#ProductName} 的安装位置', Directory, True) then begin
        UiSaveDraft; UiPaths[Ord(UiAllUsers)] := Directory; UiLoadScope;
      end;
    end;
    7: begin
      if not AcquireMaintenanceLock then UiError('另一个安装或卸载程序正在运行，请关闭后重新打开安装包。')
      else UiError('未获得安装权限，程序文件未修改。你可以重试或更改安装范围。');
    end;
    8: begin
      UiHandoffDone := True; UiHandoffResult := UI_BackendExitCode;
      UiPhase := UI_BackendPhase;
      Log('Background installation completed; exit code: ' + IntToStr(UiHandoffResult));
#ifdef QaRoot
      if HasParameter('CINNAQAEXIT') then UI_AutoFinish;
#endif
    end;
    9: begin
#ifdef QaRoot
      if ExpandConstant('{param:CINNAQACLOSEAPP|}') <> '' then
        UI_BackendAnswer(Ord(CompareText(ExpandConstant('{param:CINNAQACLOSEAPP|}'), 'accept') = 0))
      else
#endif
        UI_BackendAnswer(Ord(SuppressibleMsgBox('{#ClosePrompt}', mbInformation, MB_OKCANCEL, IDCANCEL) = IDOK));
    end;
    10: begin
      UiPhase := UI_BackendPhase;
#ifdef QaRoot
      if (UiPhase = 2) and (StrToIntDef(ExpandConstant('{param:CINNAQACANCEL|0}'), 0) > 0) and
        (UI_BackendProgress >= StrToIntDef(ExpandConstant('{param:CINNAQACANCEL|0}'), 0)) then begin
        Log('Foreground cancellation request sent to backend');UiPhase := 6;UI_BackendCancel;
      end;
#endif
    end;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := not WizardSilent and ((PageID = wpWelcome) or (PageID = wpSelectDir) or
    (PageID = wpSelectTasks) or (PageID = wpSelectProgramGroup) or (PageID = wpSelectComponents));
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if UiEngine then begin Cancel := True; Confirm := False; exit; end;
  if not UiActive then exit;
  Confirm := False;
  if UiHandoffDone then begin Cancel := True; exit; end;
  if UiPhase = 5 then begin Cancel := False; exit; end;
#ifdef QaRoot
  if ExpandConstant('{param:CINNAQACANCEL|}') <> '' then begin Cancel := True; exit; end;
#endif
  if (UiPhase = 1) or (UiPhase = 2) then begin
    Cancel := UI_ConfirmCancel <> 0;
    if Cancel then begin UiPhase := 6; UI_SetPhase(6, '正在撤销本次程序变更，保留用户资料'); end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  InstallStage(1, '正在检查安装环境');
  if InstallCancelled then begin Result := '安装已取消，程序文件未修改。'; exit; end;
  Result := EnginePrepareToInstall(NeedsRestart);
  if UiEngine and (Result <> '') then UI_EngineStage(4, Result);
  if UiActive and (Result <> '') then UiError(Result);
  if UiActive and UiCancelRequested then UI_AutoCancel;
end;

procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
var CancelDeadline: UInt64;
begin
  if UiActive and (UiPhase = 2) and (MaxProgress > 0) then UI_SetProgress((Int64(CurProgress) * 100) div MaxProgress);
  if UiEngine and (MaxProgress > 0) then begin
    UI_EngineProgress((Int64(CurProgress) * 100) div MaxProgress);
#ifdef QaRoot
    if (StrToIntDef(ExpandConstant('{param:CINNAQACANCEL|0}'), 0) > 0) and
      ((Int64(CurProgress) * 100) div MaxProgress >= StrToIntDef(ExpandConstant('{param:CINNAQACANCEL|0}'), 0)) then begin
      CancelDeadline := GetTickCount64 + 3000;
      while (UI_EngineCancelled = 0) and (GetTickCount64 < CancelDeadline) do Sleep(20);
      if UI_EngineCancelled = 0 then begin Log('QA foreground cancellation timeout');UI_EngineRequestCancel;end;
    end;
#endif
    if UI_EngineCancelled <> 0 then begin
      Log('Background cancellation requested at ' + IntToStr((Int64(CurProgress) * 100) div MaxProgress));
      WizardForm.CancelButton.OnClick(WizardForm.CancelButton);
    end;
  end;
#ifdef QaRoot
  if UiActive and (UiPhase = 2) and (MaxProgress > 0) and
    (StrToIntDef(ExpandConstant('{param:CINNAQACANCEL|0}'), 0) > 0) and
    ((Int64(CurProgress) * 100) div MaxProgress >= StrToIntDef(ExpandConstant('{param:CINNAQACANCEL|0}'), 0)) then UI_AutoCancel;
#endif
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if not UiActive then exit;
  Log('Custom UI page: ' + IntToStr(CurPageID));
  if (CurPageID = wpReady) and (UiPhase = 0) then UI_SetPhase(0, '');
  if CurPageID = wpFinished then begin
    UiPhase := 3; UI_SetPhase(3, '{#ProductName} 已安装。');
#ifdef QaRoot
    if HasParameter('CINNAQAEXIT') then UI_AutoFinish;
#endif
  end;
  if (CurPageID = wpReady) and HasParameter('CINNAAUTO') and not UiAutoStarted then begin
    UiAutoStarted := True; UI_AutoStart;
  end;
end;

procedure InitializeWizard;
var I: Integer; Handoff, TaskParameter: String;
begin
  if UiEngine then UI_EngineMaskWindow(WizardForm.Handle);
  if WizardSilent then exit;
  ExtractTemporaryFile('scene-day.jpg'); ExtractTemporaryFile('scene-night.jpg'); ExtractTemporaryFile('installer-icon.png');
  for I := 0 to WizardForm.ControlCount - 1 do WizardForm.Controls[I].Visible := False;
  WizardForm.BorderStyle := bsNone;
  WizardForm.ClientWidth := ScaleX(800); WizardForm.ClientHeight := ScaleY(530);
  if UI_Create(WizardForm.Handle, '{#ProductName}', '{#AppVersion} · Windows x64', '{#UiDescription}',
    ExpandConstant('{tmp}\scene-day.jpg'), ExpandConstant('{tmp}\scene-night.jpg'),
    ExpandConstant('{tmp}\installer-icon.png'), {#UiTheme}, CreateCallback(@UiAction)) = 0 then
    RaiseException('无法初始化安装界面。请重新下载安装包。');
  UiActive := True; UiController := True; UiPhase := 0; UiHandoffResult := 2;
#ifdef QaRoot
  if HasParameter('CINNAQAPROBE') then UI_StartProbe('{#QaRoot}\responsiveness.json');
#endif
#ifdef QaRoot
#if ProductKind == "network"
  UI_AllowLaunch(0);
#endif
#endif
  Log('Custom native installer UI initialized.');
  UiAllUsers := IsAdminInstallMode;
  if not ParameterPresent('/CURRENTUSER') and not ParameterPresent('/ALLUSERS') then
    if not RegisteredAt(HKCU64) and RegisteredAt(HKLM64) then UiAllUsers := True;
  UiLoadScope;
  if ParameterPresent('/DIR=') then begin
    UiPaths[Ord(UiAllUsers)] := WizardDirValue;
    UiLoadScope;
  end;
  // Inno's task list has not been populated yet during InitializeWizard.
  // Preserve explicit command-line choices directly instead of reading it early.
  if ParameterPresent('/TASKS=') or HasParameter('NOICONS') then begin
    TaskParameter := ',' + Lowercase(ExpandConstant('{param:TASKS|}')) + ',';
    UiDesktop[Ord(UiAllUsers)] := not HasParameter('NOICONS') and (Pos(',desktopicon,', TaskParameter) > 0);
    UiStart[Ord(UiAllUsers)] := not HasParameter('NOICONS') and (Pos(',startmenuicon,', TaskParameter) > 0);
    UiLoadScope;
  end;
  Handoff := ExpandConstant('{param:CINNAHANDOFF|}');
  if Handoff <> '' then UI_Ready(Handoff);
end;
