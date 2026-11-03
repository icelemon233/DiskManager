; Shared Cinna installer. Product settings are supplied by build.ps1.
; Keep this file byte-identical in FanController, LanDrop and NetworkToolbox.
; DiskManager adds a product adapter for its native helper; transaction/UI logic follows the shared source.
#include BuildConfig

[Setup]
AppId={#SetupId}
AppName={#ProductName}
AppVersion={#AppVersion}
AppVerName={#ProductName} {#AppVersion}
AppPublisher=Cinna
VersionInfoCompany=Cinna
VersionInfoDescription={#ProductName} 安装程序
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName={#ProductName}
DefaultDirName={code:DefaultInstallDir}
DefaultGroupName={#ProductName}
OutputDir={#OutputDirectory}
OutputBaseFilename={#OutputName}
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#ShortcutIcon}
#ifdef QaRoot
UninstallDisplayName={#QaWindowTitle}
#else
UninstallDisplayName={#ProductName}
#endif
; Inno 7.1.0's 64-bit UI misreports checkbox state to screen readers.
; Use its 32-bit wizard host; the payload and installation remain x64.
SetupArchitecture=x86
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion={#MinimumWindows}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
UsePreviousPrivileges=no
DisableWelcomePage=yes
DisableDirPage=no
DisableProgramGroupPage=yes
DisableReadyPage=no
AlwaysShowDirOnReadyPage=yes
AlwaysShowGroupOnReadyPage=no
UsePreviousTasks=yes
AllowNoIcons=yes
WizardStyle=modern {#WizardAppearance} windows11 hidebevels
WizardSizePercent=115,115
WizardImageFile={#ArtworkDirectory}\sidebar-light.png
WizardImageFileDynamicDark={#ArtworkDirectory}\sidebar-dark.png
WizardImageBackColor=#FFFCF6
WizardImageBackColorDynamicDark=#24272B
WizardSmallImageFile={#ArtworkDirectory}\header.png
WizardSmallImageFileDynamicDark={#ArtworkDirectory}\header.png
WizardSmallImageBackColor=none
WizardSmallImageBackColorDynamicDark=none
Compression=lzma2/normal
SolidCompression=yes
CloseApplications=no
RestartApplications=no
AllowCancelDuringInstall=yes
RestartIfNeededByRun=no
; The script holds the maintenance mutex. A built-in SetupMutex would prevent
; the non-elevated homepage from handing the same package to Inno's UAC loader.
UninstallLogging=yes
UninstallLogMode=append

[Languages]
Name: chinese; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Messages]
DialogFontName=Microsoft YaHei UI
DialogFontSize=10
WelcomeFontName=Microsoft YaHei UI
WelcomeFontSize=18
SetupWindowTitle={#ProductName} {#AppVersion} 安装
#ifdef QaRoot
SetupWindowTitle={#QaWindowTitle}
#endif
WelcomeLabel1=安装 {#ProductName} {#AppVersion}
WelcomeLabel2={#WelcomeText}%n%n安装和卸载会保留已保存的设置与资料。
SelectDirLabel3=选择 {#ProductName} 的安装位置。
SelectTasksLabel2=选择需要创建的快捷方式。
WizardSelectTasks=快捷方式
SelectTasksDesc=选择需要创建的入口。
PrivilegesRequiredOverrideTitle={#ProductName} 安装范围
PrivilegesRequiredOverrideInstruction=为哪些用户安装？
FinishedHeadingLabel={#ProductName} 安装完成
FinishedLabel=已安装 {#ProductName} {#AppVersion}。
ConfirmUninstall=是否卸载 %1？%n%n只移除程序文件，已保存的设置、记录和用户另放入安装目录的文件会保留。

[Tasks]
Name: desktopicon; Description: "创建桌面快捷方式(&D)"; Flags: checkedonce
Name: startmenuicon; Description: "创建开始菜单快捷方式(&S)"; Flags: checkedonce

[Files]
Source: "{#UiDirectory}\InstallerUi.dll"; Flags: dontcopy noencryption
Source: "{#SceneDirectory}\scene-day.jpg"; Flags: dontcopy noencryption
Source: "{#SceneDirectory}\scene-night.jpg"; Flags: dontcopy noencryption
Source: "{#ArtworkDirectory}\header.png"; DestName: "installer-icon.png"; Flags: dontcopy noencryption
Source: "{#SourcePath}\licenses\*"; DestDir: "{app}\licenses\installer-ui"; Flags: ignoreversion
Source: "{#PayloadDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#if ProductKind == "landrop"
Source: "{#PayloadDirectory}\LanDrop.exe"; DestName: "maintenance.exe"; Flags: dontcopy
#endif
#if ProductKind == "network"
Source: "{#PayloadDirectory}\resources\native\NetworkToolbox.Native.exe"; DestName: "maintenance.exe"; Flags: dontcopy
#endif
#if ProductKind == "disk"
Source: "{#PayloadDirectory}\resources\native\DiskManager.Native.exe"; DestName: "maintenance.exe"; Flags: dontcopy
#endif
#if ProductKind != "network" && ProductKind != "disk"
Source: "{#WebViewBootstrapper}"; DestName: "WebView2Setup.exe"; Flags: dontcopy
#endif
Source: "{#PayloadManifest}"; DestName: "payload-files.txt"; Flags: dontcopy

[Icons]
Name: "{#DesktopLink}"; Filename: "{app}\{#Executable}"; Parameters: "{#LaunchArguments}"; WorkingDir: "{app}"; IconFilename: "{app}\{#ShortcutIcon}"; AppUserModelID: "{#AppUserModelId}"; Tasks: desktopicon
Name: "{#StartMenuDirectory}\{#ProductName}"; Filename: "{app}\{#Executable}"; Parameters: "{#LaunchArguments}"; WorkingDir: "{app}"; IconFilename: "{app}\{#ShortcutIcon}"; AppUserModelID: "{#AppUserModelId}"; Tasks: startmenuicon
Name: "{#StartMenuDirectory}\卸载 {#ProductName}"; Filename: "{uninstallexe}"; IconFilename: "{app}\{#ShortcutIcon}"; Tasks: startmenuicon

[Code]
const
  LegacyKey = '{#LegacyKey}';
  InnoKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#SetupId}_is1';
  InstallingMutex = '{#InstallingMutex}';
  RunningMutex = '{#RunningMutex}';

var
  Manifest: TArrayOfString;
  ManifestLoaded: Boolean;
  MaintenanceLock: THandle;
  CrossSessionLock: THandle;
  WmiService: Variant;
  WmiReady: Boolean;
  UiActive, UiAllUsers, UiHandoffDone, UiAutoStarted: Boolean;
  UiCancelRequested: Boolean;
  UiEngine, UiController: Boolean;
  UiPhase: Integer;
  UiHandoffResult: Integer;
  UiPaths: array[0..1] of String;
  UiDesktop, UiStart, UiLoaded: array[0..1] of Boolean;

#include "ui-api.iss"

procedure InstallStage(Phase: Integer; Detail: String);
begin
  if UiEngine then UI_EngineStage(Phase, Detail)
  else if UiActive then UI_SetPhase(Phase, Detail);
end;

function InstallCancelled: Boolean;
begin
  Result := UiCancelRequested;
  if UiEngine then Result := UI_EngineCancelled <> 0;
end;

function GetFileAttributesW(FileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function OpenProcess(Access: LongWord; Inherit: Boolean; ProcessId: LongWord): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function QueryFullProcessImageNameW(Process: THandle; Flags: LongWord; FileName: String; var Size: LongWord): Boolean;
  external 'QueryFullProcessImageNameW@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function CreateMutexW(Attributes: NativeUInt; InitialOwner: Boolean; Name: String): THandle;
  external 'CreateMutexW@kernel32.dll stdcall';
function GetLastError: LongWord;
  external 'GetLastError@kernel32.dll stdcall';
procedure ExitProcess(ExitCode: Cardinal);
  external 'ExitProcess@kernel32.dll stdcall';
function GetTickCount64: UInt64;
  external 'GetTickCount64@kernel32.dll stdcall';
function GetWindowThreadProcessId(Window: HWND; var ProcessId: LongWord): LongWord;
  external 'GetWindowThreadProcessId@user32.dll stdcall';
function FindWindowExW(Parent, After: HWND; ClassName: NativeUInt; Title: String): HWND;
  external 'FindWindowExW@user32.dll stdcall';
function RegisterWindowMessageW(Name: String): LongWord;
  external 'RegisterWindowMessageW@user32.dll stdcall';
function SendMessageTimeoutW(Window: HWND; Msg: LongWord; WParam: NativeUInt; LParam: NativeInt; Flags, Timeout: LongWord; var MessageResult: NativeUInt): NativeUInt;
  external 'SendMessageTimeoutW@user32.dll stdcall';

function SamePath(A, B: String): Boolean;
begin
  Result := CompareText(RemoveBackslashUnlessRoot(ExpandFileName(A)), RemoveBackslashUnlessRoot(ExpandFileName(B))) = 0;
end;

function IsWithin(Child, Parent: String): Boolean;
begin
  Result := SamePath(Child, Parent) or
    (Pos(Lowercase(AddBackslash(ExpandFileName(Parent))), Lowercase(AddBackslash(ExpandFileName(Child)))) = 1);
end;

function HasPathLink(Path: String): Boolean;
var Attributes: LongWord;
begin
  Result := False;
  while Length(Path) > 3 do begin
    Attributes := GetFileAttributesW(Path);
    if (Attributes <> $FFFFFFFF) and ((Attributes and $400) <> 0) then begin Result := True; exit; end;
    Path := ExtractFileDir(Path);
  end;
end;

procedure ReleaseMaintenanceLock;
begin
  if CrossSessionLock <> 0 then begin CloseHandle(CrossSessionLock); CrossSessionLock := 0; end;
  if MaintenanceLock <> 0 then begin CloseHandle(MaintenanceLock); MaintenanceLock := 0; end;
end;

function AcquireMaintenanceLock: Boolean;
begin
  MaintenanceLock := CreateMutexW(0, False, InstallingMutex);
  Result := (MaintenanceLock <> 0) and (GetLastError <> 183);
  if Result then begin
#ifdef QaRoot
    CrossSessionLock := CreateMutexW(0, False, '{#InstallingMutex}.CrossSession');
#else
    CrossSessionLock := CreateMutexW(0, False, 'Global\Cinna.{#SetupId}.Maintenance');
#endif
    Result := (CrossSessionLock <> 0) and (GetLastError <> 183);
  end;
  if not Result then ReleaseMaintenanceLock;
end;

function SelectedRoot: Integer;
begin
  if UiActive then begin
    if UiAllUsers then Result := HKLM64 else Result := HKCU64;
  end else if IsAdminInstallMode then Result := HKLM64 else Result := HKCU64;
end;

function QuoteIndex(Value: String): Integer;
var I: Integer;
begin
  // Keep character indexes in the Unicode domain; do not mix an AnyString
  // overload's ANSI byte offsets with Unicode path slicing.
  Result := 0;
  for I := 1 to Length(Value) do if Value[I] = '"' then begin Result := I; exit; end;
end;

function RegisteredDirectory(Key: String): String;
var Value: String; P: Integer;
begin
  Result := '';
  if RegQueryStringValue(SelectedRoot, Key, 'InstallLocation', Value) and (Value <> '') then begin
    Result := RemoveBackslashUnlessRoot(Value); exit;
  end;
  if RegQueryStringValue(SelectedRoot, Key, 'UninstallString', Value) then begin
    if (Length(Value) > 2) and (Value[1] = '"') then begin
      Delete(Value, 1, 1); P := QuoteIndex(Value);
      if P > 0 then Result := ExtractFileDir(Copy(Value, 1, P - 1));
    end;
  end;
end;

function ExistingDirectory: String;
begin
  Result := RegisteredDirectory(InnoKey);
  if Result = '' then Result := RegisteredDirectory(LegacyKey);
end;

function RegisteredUninstaller(Key: String): String;
var Command: String; P: Integer;
begin
  Result := '';
  if not RegQueryStringValue(SelectedRoot, Key, 'UninstallString', Command) then exit;
  if Length(Command) < 3 then exit;
  if Command[1] <> '"' then exit;
  Delete(Command, 1, 1); P := QuoteIndex(Command);
  if P > 0 then Result := Copy(Command, 1, P - 1);
end;

function DefaultInstallDir(Param: String): String;
begin
  Result := ExistingDirectory;
  if Result = '' then begin
#ifdef QaRoot
    Result := '{#QaRoot}\app';
#else
    if SelectedRoot = HKLM64 then Result := ExpandConstant('{commonpf64}\{#ProductName}')
    else Result := ExpandConstant('{localappdata}\Programs\{#UserFolder}');
#endif
  end;
end;

function ValidateDirectory(Dir: String): String;
var Ancestor, Existing, ProtectedPath: String; Attributes: LongWord; I, CharacterCode: Integer;
begin
  Result := '';
  if Length(Dir) < 4 then begin
    Result := '请输入本机磁盘中的完整应用目录，例如 D:\Apps\{#ProductName}。'; exit;
  end;
  if (Dir[2] <> ':') or ((Dir[3] <> '\') and (Dir[3] <> '/')) then begin
    Result := '请输入本机磁盘中的完整应用目录，例如 D:\Apps\{#ProductName}。'; exit;
  end;
  for I := 1 to Length(Dir) do begin
    CharacterCode := Ord(Dir[I]);
    if (CharacterCode < 32) or (CharacterCode = 34) or (CharacterCode = 60) or
      (CharacterCode = 62) or (CharacterCode = 124) or (CharacterCode = 63) or
      (CharacterCode = 42) or ((CharacterCode = 58) and (I <> 2)) then begin
      Log(Format('Invalid path character at %d (Unicode %d)', [I, CharacterCode]));
      Result := '安装路径包含无效字符，请选择单独的应用文件夹。'; exit;
    end;
  end;
  Dir := RemoveBackslashUnlessRoot(ExpandFileName(Dir));
  if (Length(Dir) < 4) or (Length(Dir) > 150) or (Copy(Dir, 1, 2) = '\\') or (Dir[2] <> ':') then begin
    Result := '请选择本机磁盘中单独的应用文件夹，路径不超过 150 个字符。'; exit;
  end;
#ifdef QaRoot
  if not IsWithin(Dir, '{#QaRoot}') then begin Result := '测试安装必须位于独立测试目录。'; exit; end;
#endif
  for I := 0 to 7 do begin
    case I of
      0: ProtectedPath := ExpandConstant('{win}');
      1: ProtectedPath := ExpandConstant('{%USERPROFILE}');
      2: ProtectedPath := ExpandConstant('{localappdata}');
      3: ProtectedPath := ExpandConstant('{userappdata}');
      4: ProtectedPath := ExpandConstant('{commonappdata}');
      5: ProtectedPath := ExpandConstant('{autopf}');
      6: ProtectedPath := ExpandConstant('{userdocs}');
      7: ProtectedPath := ExpandConstant('{autodesktop}');
    end;
    if IsWithin(ProtectedPath, Dir) then begin Result := '不能覆盖系统或个人资料目录，请选择单独的应用文件夹。'; exit; end;
  end;
  if IsWithin(Dir, ExpandConstant('{win}')) or IsWithin(Dir, ExpandConstant('{#DataDirectory}')) then begin
    Result := '安装位置不能位于 Windows 或应用资料目录。'; exit;
  end;
  Ancestor := Dir;
  while Length(Ancestor) > 3 do begin
    Attributes := GetFileAttributesW(Ancestor);
    if (Attributes <> $FFFFFFFF) and ((Attributes and $400) <> 0) then begin
      Result := '安装位置不能经过符号链接或目录联接。'; exit;
    end;
    Ancestor := ExtractFileDir(Ancestor);
  end;
  Existing := ExistingDirectory;
  if (Existing <> '') and not SamePath(Existing, Dir) then begin
    Result := '此范围内已安装到：' + Existing + #13#10 + '升级请使用原位置。需要更换位置时，请先卸载旧版；设置与资料会保留。'; exit;
  end;
  if Existing = '' then begin
    if FileExists(AddBackslash(Dir) + 'Uninstall.exe') or FileExists(AddBackslash(Dir) + 'Uninstall NetworkToolbox.exe') or FileExists(AddBackslash(Dir) + 'unins000.exe') then
      Result := '此文件夹中已有其他安装记录，请选择新的应用文件夹。';
  end;
end;

function HasParameter(Name: String): Boolean;
var I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do if CompareText(ParamStr(I), '/' + Name) = 0 then begin Result := True; exit; end;
end;

function OwnsShortcut(Path, Target: String): Boolean;
var Shell, Folder, Item, Link: Variant; ActualTarget: String;
begin
  Result := False;
  if not FileExists(Path) or HasPathLink(Path) then exit;
  try
    Shell := CreateOleObject('Shell.Application');
    Folder := Shell.NameSpace(ExtractFileDir(Path));
    Item := Folder.ParseName(ExtractFileName(Path)); Link := Item.GetLink;
    ActualTarget := Link.Path; Result := SamePath(ActualTarget, Target);
    if not Result then Log('Shortcut target mismatch: ' + ActualTarget + ' <> ' + Target);
  except
    Log('Shortcut ownership could not be verified: ' + Path);
  end;
end;

procedure RemoveOwnedShortcut(Path, Target: String);
begin
  if OwnsShortcut(Path, Target) then DeleteFile(Path);
end;

function ValidateShortcutChoices: String;
var AppTarget, UninstallTarget, Link: String;
begin
  Result := ''; AppTarget := ExpandConstant('{app}\{#Executable}');
  if WizardIsTaskSelected('desktopicon') then begin
    Link := ExpandConstant('{#DesktopLink}.lnk');
    if FileExists(Link) and not OwnsShortcut(Link, AppTarget) then begin
      Result := '桌面上已有同名的其他快捷方式。请取消创建桌面快捷方式，或先自行为原入口改名。'; exit;
    end;
  end;
  if WizardIsTaskSelected('startmenuicon') then begin
    Link := ExpandConstant('{#StartMenuDirectory}\{#ProductName}.lnk');
    if FileExists(Link) and not OwnsShortcut(Link, AppTarget) then begin
      Result := '开始菜单中已有同名的其他快捷方式。请取消创建该入口，或先自行为原入口改名。'; exit;
    end;
    Link := ExpandConstant('{#StartMenuDirectory}\卸载 {#ProductName}.lnk');
    if FileExists(Link) then begin
      UninstallTarget := RegisteredUninstaller(InnoKey);
      if UninstallTarget = '' then UninstallTarget := RegisteredUninstaller(LegacyKey);
      Log('Registered uninstall target: ' + UninstallTarget);
      if (UninstallTarget = '') or not OwnsShortcut(Link, UninstallTarget) then begin
        Result := '开始菜单中已有同名的其他卸载入口。请取消创建开始菜单快捷方式，或先自行为原入口改名。'; exit;
      end;
    end;
  end;
end;

function ProcessCount(Dir: String): Integer;
var Locator, Processes: Variant; QueryPath, Query: String;
begin
  Result := -1;
  try
    QueryPath := AddBackslash(Dir);
    StringChangeEx(QueryPath, '\', '\\', True);
    StringChangeEx(QueryPath, '''', '\''', True);
    if not WmiReady then begin
      Locator := CreateOleObject('WbemScripting.SWbemLocator');
      WmiService := Locator.ConnectServer('', 'root\cimv2');
      WmiReady := True;
    end;
    Query := 'SELECT ProcessId FROM Win32_Process WHERE ExecutablePath = ''' + QueryPath + '{#Executable}''';
#if ProductKind == "fan"
    Query := Query + ' OR ExecutablePath = ''' + QueryPath + 'FanController.Worker.exe'' OR ExecutablePath = ''' + QueryPath + 'cinna-fanCon.exe''';
#elif ProductKind == "network"
    Query := Query + ' OR ExecutablePath = ''' + QueryPath + 'resources\\native\\NetworkToolbox.Native.exe''';
#elif ProductKind == "disk"
    Query := Query + ' OR ExecutablePath = ''' + QueryPath + 'resources\\native\\DiskManager.Native.exe''';
#endif
    Processes := WmiService.ExecQuery(Query);
    Result := Processes.Count;
  except
    Log('Cannot inspect installation processes: ' + GetExceptionMessage);
  end;
end;

function FanWindow: HWND;
var I: Integer; Window: HWND; Title, Image: String; ProcessId, Size: LongWord; Process: THandle;
begin
  Result := 0;
  for I := 0 to 3 do begin
#ifdef QaRoot
    case I of
      0: Title := '{#QaWindowTitle}';
      1: Title := '{#QaWindowTitle} previous';
      2: Title := '{#QaWindowTitle} older';
      3: Title := '{#QaWindowTitle} legacy';
    end;
#else
    case I of
      0: Title := 'FanController';
      1: Title := 'FanController · 风扇控制';
      2: Title := 'cinna-fanCon · 风扇控制';
      3: Title := 'cinna-fanCon · 智能散热';
    end;
#endif
    Window := 0;
    repeat
      Window := FindWindowExW(0, Window, 0, Title);
      if Window = 0 then break;
      GetWindowThreadProcessId(Window, ProcessId);
      Process := OpenProcess($1000, False, ProcessId);
      if Process <> 0 then try
        Size := 32768; SetLength(Image, Size);
        if QueryFullProcessImageNameW(Process, 0, Image, Size) then begin
          SetLength(Image, Size);
          Log('Maintenance window process: ' + Image + '; expected ' + ExpandConstant('{app}'));
          if SamePath(Image, ExpandConstant('{app}\FanController.exe')) or SamePath(Image, ExpandConstant('{app}\cinna-fanCon.exe')) then begin Result := Window; exit; end;
        end else Log(Format('Cannot read maintenance process path: %d', [GetLastError]));
      finally CloseHandle(Process); end;
    until False;
  end;
end;

function MaintenanceExecutable(Uninstalling: Boolean): String;
begin
#if ProductKind == "landrop"
  if Uninstalling then Result := ExpandConstant('{app}\LanDrop.exe')
  else begin ExtractTemporaryFile('maintenance.exe'); Result := ExpandConstant('{tmp}\maintenance.exe'); end;
#elif ProductKind == "network"
  if Uninstalling then Result := ExpandConstant('{app}\resources\native\NetworkToolbox.Native.exe')
  else begin ExtractTemporaryFile('maintenance.exe'); Result := ExpandConstant('{tmp}\maintenance.exe'); end;
#elif ProductKind == "disk"
  if Uninstalling then Result := ExpandConstant('{app}\resources\native\DiskManager.Native.exe')
  else begin ExtractTemporaryFile('maintenance.exe'); Result := ExpandConstant('{tmp}\maintenance.exe'); end;
#else
  Result := '';
#endif
end;

function EnsureApplicationExit(Uninstalling, Silent: Boolean): String;
var Count, ExitCode: Integer; Helper, Arguments: String; Window: HWND; MessageId: LongWord; MessageResult: NativeUInt; Deadline: UInt64;
begin
  Result := ''; Count := ProcessCount(ExpandConstant('{app}'));
  if Count < 0 then begin Result := '无法确认程序的运行状态，文件未修改。请检查 Windows Management Instrumentation 服务后重试。'; exit; end;
#if ProductKind == "landrop"
  Helper := MaintenanceExecutable(Uninstalling);
  if not FileExists(Helper) then begin
    if Count = 0 then exit;
    Result := '缺少应用维护程序，请先正常退出 {#ProductName} 后重试。'; exit;
  end;
  if not Exec(Helper, '--headless --check-running {#MaintenanceArguments}', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then begin Result := '无法检查 LanDrop 状态，文件未修改。'; exit; end;
  if ExitCode = 10 then Count := Count + 1
  else if ExitCode <> 0 then begin Result := '无法确认 LanDrop 已退出，文件未修改。'; exit; end;
#endif
#if ProductKind == "fan"
  if CheckForMutexes(RunningMutex) then Count := Count + 1;
#endif
  if Count = 0 then exit;
  if Silent and not UiEngine and not HasParameter('CLOSEAPP') then begin
    Result := '{#ProductName} 正在运行。请先正常退出后重试；静默维护不会自动结束程序。'; exit;
  end;
  if UiEngine then begin
    if UI_EngineConfirmExit('{#ClosePrompt}') = 0 then begin Result := '已取消操作，程序文件未修改。'; exit; end;
  end else if not Silent then
    if SuppressibleMsgBox('{#ClosePrompt}', mbInformation, MB_OKCANCEL, IDCANCEL) <> IDOK then begin Result := '已取消操作，程序文件未修改。'; exit; end;
#if ProductKind == "fan"
  Window := FanWindow;
  if Window = 0 then begin Result := '无法找到此安装位置的应用窗口，请先从托盘正常退出。'; exit; end;
  MessageId := RegisterWindowMessageW('CinnaFanCon.RequestExit');
  if SendMessageTimeoutW(Window, MessageId, 0, 0, 3, 3000, MessageResult) = 0 then begin Result := '应用未响应退出请求，文件未修改。'; exit; end;
  if MessageResult <> 1 then
    if SendMessageTimeoutW(Window, $10, 0, 0, 3, 3000, MessageResult) = 0 then begin Result := '应用未响应退出请求，文件未修改。'; exit; end;
#elif ProductKind == "landrop"
  if not Exec(Helper, '--headless --prepare-upgrade {#MaintenanceArguments}', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then begin
    Result := 'LanDrop 未能正常退出，文件未修改。请结束传输并关闭文件选择窗口后重试。'; exit;
  end;
#elif ProductKind == "network" || ProductKind == "disk"
  Helper := MaintenanceExecutable(Uninstalling);
  Arguments := '--ensure-exit --install-dir "' + ExpandConstant('{app}') + '"';
  if not Exec(Helper, Arguments, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then begin
    Result := '{#ProductName} 未能正常退出。请先保存任务并从托盘退出后重试。'; exit;
  end;
#endif
  Deadline := GetTickCount64 + 30000;
  repeat
    Count := ProcessCount(ExpandConstant('{app}'));
#if ProductKind == "fan"
    if (Count >= 0) and CheckForMutexes(RunningMutex) then Count := Count + 1;
#endif
    if Count = 0 then exit;
    if Count < 0 then break;
    if UiActive then begin
      UI_Pump;
      if InstallCancelled then begin Result := '安装已取消，程序文件未修改。'; exit; end;
    end;
    if InstallCancelled then begin Result := '安装已取消，程序文件未修改。'; exit; end;
    Sleep(200);
  until GetTickCount64 >= Deadline;
  Result := '应用或后台服务尚未完全退出，程序文件未修改。请正常退出后重试；其他登录用户运行的程序也需要先退出。';
end;

function WebViewInstalled: Boolean;
var Version: String; I, Root: Integer;
begin
  Result := False;
  for I := 0 to 3 do begin
    case I of 0: Root := HKLM32; 1: Root := HKLM64; 2: Root := HKCU32; 3: Root := HKCU64; end;
    if RegQueryStringValue(Root, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0') then begin Result := True; exit; end;
  end;
end;

function InitializeSetup: Boolean;
var EngineName: String;
begin
  EngineName := ExpandConstant('{param:CINNAENGINE|}');
  if EngineName <> '' then begin
    UiEngine := UI_EngineConnect(EngineName) <> 0;
    if not UiEngine then begin Result := False; exit; end;
  end;
  Result := not (HasParameter('CLOSEAPPLICATIONS') or HasParameter('FORCECLOSEAPPLICATIONS'));
  if not Result then begin SuppressibleMsgBox('此安装器只支持应用正常退出。请移除强制关闭参数；静默维护可使用 /CLOSEAPP 请求正常退出。', mbError, MB_OK, IDOK); exit; end;
  Result := AcquireMaintenanceLock;
  if not Result then begin InstallStage(4, '另一个安装或卸载程序正在运行，请等待完成。'); SuppressibleMsgBox('另一个安装或卸载程序正在运行，请等待完成。', mbError, MB_OK, IDOK); exit; end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var Error: String;
begin
  Result := True;
  if (CurPageID = wpReady) and UiActive and UiController then begin UI_AutoStart; Result := False; exit; end;
  if CurPageID = wpSelectDir then begin
    Error := ValidateDirectory(WizardDirValue);
    Result := Error = '';
    if not Result then SuppressibleMsgBox(Error, mbError, MB_OK, IDOK);
  end;
end;

#include "rollback.iss"

function EnginePrepareToInstall(var NeedsRestart: Boolean): String;
var ExitCode, I: Integer; Release: Cardinal; Target: String;
begin
  Result := ValidateDirectory(ExpandConstant('{app}'));
  if Result <> '' then exit;
  Result := ValidateShortcutChoices;
  if Result <> '' then exit;
#if ProductKind == "fan"
  if not RegQueryDWordValue(HKLM32, 'Software\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) or (Release < 528040) then begin
    Result := '请先安装 Microsoft .NET Framework 4.8，再点击重试。程序文件未修改。'; exit;
  end;
#endif
  if not ManifestLoaded then begin ExtractTemporaryFile('payload-files.txt'); ManifestLoaded := LoadStringsFromFile(ExpandConstant('{tmp}\payload-files.txt'), Manifest); end;
  if not ManifestLoaded then begin Result := '无法读取安装文件清单，程序文件未修改。'; exit; end;
  for I := 0 to GetArrayLength(Manifest) - 1 do begin
    Target := AddBackslash(ExpandConstant('{app}')) + Manifest[I];
    if (ExistingDirectory = '') and FileExists(Target) then begin Result := '目标文件夹中已有同名文件，请选择新的应用文件夹：' + Manifest[I]; exit; end;
    if HasPathLink(Target) then begin Result := '目标路径不能经过符号链接或目录联接：' + Manifest[I]; exit; end;
  end;
  InstallStage(1, '等待应用正常退出');
  Result := EnsureApplicationExit(False, WizardSilent);
  if Result <> '' then exit;
  if InstallCancelled then begin Result := '安装已取消，程序文件未修改。'; exit; end;
#if ProductKind != "network" && ProductKind != "disk"
  if not WebViewInstalled then begin
    InstallStage(1, '正在准备 Microsoft WebView2 运行库');
#ifdef QaRoot
    Result := '测试安装不修改系统运行库。请先安装 WebView2 后重试。'; exit;
#else
    ExtractTemporaryFile('WebView2Setup.exe');
    if CompareText(GetSHA256OfFile(ExpandConstant('{tmp}\WebView2Setup.exe')), '{#WebViewHash}') <> 0 then begin Result := 'WebView2 安装文件校验失败，程序文件未修改。'; exit; end;
    if not Exec(ExpandConstant('{tmp}\WebView2Setup.exe'), '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) or not WebViewInstalled then begin
      Result := 'WebView2 未能安装。请检查网络，或先从 Microsoft 安装运行库后重试。'; exit;
    end;
#endif
  end;
#endif
  InstallStage(1, '正在备份旧程序文件');
  if InstallCancelled then begin Result := '安装已取消，程序文件未修改。'; exit; end;
  Result := PrepareRollback;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var OldDirectory, OldLink: String;
begin
  if CurStep = ssInstall then begin
    InstallWriteStarted := True;
    if UiActive then begin UiPhase := 2; UI_SetPhase(2, '写入程序文件'); end;
    if UiEngine then UI_EngineStage(2, '写入程序文件');
  end;
  if CurStep <> ssPostInstall then exit;
  InstallCommitted := True;
  OldDirectory := RegisteredDirectory(LegacyKey);
  if (OldDirectory <> '') and SamePath(OldDirectory, ExpandConstant('{app}')) then begin
    // Remove legacy registration only after the Inno transaction succeeded.
    // Never invoke the legacy uninstaller after writing the new files.
#include "legacy-cleanup.iss"
#if ProductKind == "landrop"
    OldLink := ExpandConstant('{#StartMenuDirectory}\卸载.lnk');
    RemoveOwnedShortcut(OldLink, AddBackslash(OldDirectory) + 'Uninstall.exe');
#endif
    RegDeleteKeyIncludingSubkeys(SelectedRoot, LegacyKey);
  end;
  if not WizardIsTaskSelected('desktopicon') then RemoveOwnedShortcut(ExpandConstant('{#DesktopLink}.lnk'), ExpandConstant('{app}\{#Executable}'));
  if not WizardIsTaskSelected('startmenuicon') then begin
    RemoveOwnedShortcut(ExpandConstant('{#StartMenuDirectory}\{#ProductName}.lnk'), ExpandConstant('{app}\{#Executable}'));
    RemoveOwnedShortcut(ExpandConstant('{#StartMenuDirectory}\卸载 {#ProductName}.lnk'), ExpandConstant('{uninstallexe}'));
    RemoveDir(ExpandConstant('{#StartMenuDirectory}'));
  end;
  RegWriteDWordValue(SelectedRoot, InnoKey, 'CinnaDesktopShortcut', Ord(WizardIsTaskSelected('desktopicon')));
  RegWriteDWordValue(SelectedRoot, InnoKey, 'CinnaStartMenuShortcut', Ord(WizardIsTaskSelected('startmenuicon')));
  FinishRollback;
  // The application checks this lock at startup; release it before explicit launch.
  ReleaseMaintenanceLock;
  if UiEngine then UI_EngineStage(3, '安装事务已完成');
end;

function InitializeUninstall: Boolean;
var Error: String;
begin
  Result := AcquireMaintenanceLock;
  if not Result then begin SuppressibleMsgBox('另一个安装或卸载程序正在运行，请等待完成。', mbError, MB_OK, IDOK); exit; end;
  Error := ValidateDirectory(ExpandConstant('{app}'));
  if Error = '' then Error := EnsureApplicationExit(True, UninstallSilent);
  Result := Error = '';
  if not Result then SuppressibleMsgBox(Error, mbError, MB_OK, IDOK);
end;

procedure DeinitializeSetup;
begin
  FinishRollback;
  if UiActive then begin UI_Destroy; UiActive := False; end;
  ReleaseMaintenanceLock;
end;

procedure DeinitializeUninstall;
begin
  ReleaseMaintenanceLock;
end;

#include "custom-ui.iss"
