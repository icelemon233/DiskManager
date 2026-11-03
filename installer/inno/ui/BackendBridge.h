// Local, bounded status transport. Only Inno's backend writes program files;
// this process keeps its UI message loop independent of extraction and disk I/O.
#include <sddl.h>
static constexpr LONG BackendMagic=0x43494E4E;
struct BackendStatus {
  LONG magic,version,parentPid;
  volatile LONG sequence,ready,cancel,phase,progress,prompt,response;
  wchar_t detail[2048];
};
struct BackendBridge {
  HANDLE mapping,process,parent,alive; BackendStatus *status;
  DWORD exitCode; bool ready,active,completed,prompting; int lastPhase,lastProgress;
  wchar_t lastDetail[2048];
};
static BackendBridge backend;
static bool validBackendName(const wchar_t *name){return wcsncmp(name,L"Local\\Cinna.Installer.Backend.",30)==0&&wcslen(name)<160;}
static void closeBackendTransport(){
  if(backend.status)UnmapViewOfFile(backend.status);if(backend.mapping)CloseHandle(backend.mapping);
  if(backend.process)CloseHandle(backend.process);if(backend.parent)CloseHandle(backend.parent);
  if(backend.alive)CloseHandle(backend.alive);
  backend.status=0;backend.mapping=0;backend.process=0;backend.parent=0;backend.alive=0;
}
static bool backendCancelled(){
  return backend.status&&(InterlockedCompareExchange(&backend.status->cancel,0,0)!=0||
    (backend.parent&&WaitForSingleObject(backend.parent,0)!=WAIT_TIMEOUT));
}
static void writeBackendStatus(int phase,int progress,const wchar_t *detail){
  if(!backend.status)return;
  InterlockedIncrement(&backend.status->sequence);
  backend.status->phase=phase;backend.status->progress=progress;
  if(detail)lstrcpynW(backend.status->detail,detail,2048);
  InterlockedIncrement(&backend.status->sequence);
}
static bool readBackendStatus(BackendStatus &copy){
  if(!backend.status)return false;
  for(int attempt=0;attempt<4;attempt++){
    LONG before=InterlockedCompareExchange(&backend.status->sequence,0,0);if(before&1)continue;
    CopyMemory(&copy,backend.status,sizeof(copy));MemoryBarrier();
    if(before==InterlockedCompareExchange(&backend.status->sequence,0,0))return true;
  }
  return false;
}
API int __stdcall UI_EngineConnect(const wchar_t *name){
  if(!validBackendName(name)||backend.status)return 0;
  backend.mapping=OpenFileMappingW(FILE_MAP_READ|FILE_MAP_WRITE,FALSE,name);if(!backend.mapping)return 0;
  backend.status=(BackendStatus*)MapViewOfFile(backend.mapping,FILE_MAP_READ|FILE_MAP_WRITE,0,0,sizeof(BackendStatus));
  if(!backend.status||backend.status->magic!=BackendMagic||backend.status->version!=1){closeBackendTransport();return 0;}
  wchar_t aliveName[180];swprintf(aliveName,180,L"%ls.Owner",name);
  backend.parent=OpenMutexW(SYNCHRONIZE,FALSE,aliveName);
  if(!backend.parent){closeBackendTransport();return 0;}
  writeBackendStatus(PREPARING,0,L"正在检查安装环境");InterlockedExchange(&backend.status->ready,1);return 1;
}
API void __stdcall UI_EngineStage(int phase,const wchar_t *detail){writeBackendStatus(phase,backend.status?backend.status->progress:0,detail);}
API void __stdcall UI_EngineProgress(int progress){if(backend.status)writeBackendStatus(INSTALLING,progress,0);}
API int __stdcall UI_EngineCancelled(){return backendCancelled()?1:0;}
API void __stdcall UI_EngineRequestCancel(){if(backend.status)InterlockedExchange(&backend.status->cancel,1);}
API void __stdcall UI_EngineMaskWindow(HWND window){
  // Inno's normal cancellation path requires a visible, enabled wizard.
  // Keep its logical visibility in /SILENT mode, but never present or activate
  // this backend window; the foreground process owns all user interaction.
  LONG_PTR style=GetWindowLongPtrW(window,GWL_EXSTYLE);
  SetWindowLongPtrW(window,GWL_EXSTYLE,(style|WS_EX_LAYERED|WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE)&~WS_EX_APPWINDOW);
  SetLayeredWindowAttributes(window,0,0,LWA_ALPHA);
}
API int __stdcall UI_EngineConfirmExit(const wchar_t * /* confirmation text belongs to the foreground dialog */){
  if(!backend.status)return 0;
  writeBackendStatus(PREPARING,backend.status->progress,L"正在等待应用正常退出");
  InterlockedExchange(&backend.status->response,0);InterlockedExchange(&backend.status->prompt,1);
  while(!backendCancelled()){
    LONG response=InterlockedCompareExchange(&backend.status->response,0,0);
    if(response){InterlockedExchange(&backend.status->prompt,0);return response==1?1:0;}
    Sleep(20);
  }
  return 0;
}
API void __stdcall UI_BackendAnswer(int accepted){if(backend.status)InterlockedExchange(&backend.status->response,accepted?1:2);}
API int __stdcall UI_BackendRunning(){return backend.active?1:0;}
API int __stdcall UI_BackendExitCode(){return (int)backend.exitCode;}
API int __stdcall UI_BackendPhase(){return u.phase;}
API int __stdcall UI_BackendProgress(){return u.progress;}
API void __stdcall UI_BackendCancel(){
  if(!backend.active||!backend.status)return;
  InterlockedExchange(&backend.status->cancel,1);UI_SetPhase(ROLLINGBACK,L"正在取消，等待安装事务撤销程序变更");
}
API int __stdcall UI_StartBackend(const wchar_t *executable,const wchar_t *arguments){
  if(backend.active)return 0;closeBackendTransport();backend=BackendBridge{};
  HANDLE token=0;BYTE tokenBuffer[512];DWORD needed=0;LPWSTR sid=0;
  if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token))return 0;
  BOOL got=GetTokenInformation(token,TokenUser,tokenBuffer,sizeof(tokenBuffer),&needed);CloseHandle(token);
  if(!got||!ConvertSidToStringSidW(((TOKEN_USER*)tokenBuffer)->User.Sid,&sid))return 0;
  wchar_t sddl[512];swprintf(sddl,512,L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;%ls)",sid);LocalFree(sid);
  PSECURITY_DESCRIPTOR descriptor=0;
  if(!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl,SDDL_REVISION_1,&descriptor,0))return 0;
  SECURITY_ATTRIBUTES security={sizeof(security),descriptor,FALSE};GUID id;
  if(FAILED(CoCreateGuid(&id))){LocalFree(descriptor);return 0;}
  wchar_t name[160];swprintf(name,160,L"Local\\Cinna.Installer.Backend.%lu.%08lx%04x%04x%02x%02x%02x%02x%02x%02x%02x%02x",GetCurrentProcessId(),id.Data1,id.Data2,id.Data3,id.Data4[0],id.Data4[1],id.Data4[2],id.Data4[3],id.Data4[4],id.Data4[5],id.Data4[6],id.Data4[7]);
  backend.mapping=CreateFileMappingW(INVALID_HANDLE_VALUE,&security,PAGE_READWRITE,0,sizeof(BackendStatus),name);
  wchar_t aliveName[180];swprintf(aliveName,180,L"%ls.Owner",name);backend.alive=CreateMutexW(&security,TRUE,aliveName);LocalFree(descriptor);
  if(!backend.mapping){closeBackendTransport();return 0;}
  if(!backend.alive){closeBackendTransport();return 0;}
  backend.status=(BackendStatus*)MapViewOfFile(backend.mapping,FILE_MAP_READ|FILE_MAP_WRITE,0,0,sizeof(BackendStatus));
  if(!backend.status){closeBackendTransport();return 0;}
  ZeroMemory(backend.status,sizeof(BackendStatus));backend.status->magic=BackendMagic;backend.status->version=1;backend.status->parentPid=GetCurrentProcessId();
  wchar_t command[4096];swprintf(command,4096,L"\"%ls\" %ls /SILENT /SUPPRESSMSGBOXES /CINNAENGINE=\"%ls\"",executable,arguments,name);
  STARTUPINFOW startup={sizeof(startup)};startup.dwFlags=STARTF_USESHOWWINDOW;startup.wShowWindow=SW_HIDE;PROCESS_INFORMATION process={};
  if(!CreateProcessW(executable,command,0,0,FALSE,BELOW_NORMAL_PRIORITY_CLASS,0,0,&startup,&process)){closeBackendTransport();return 0;}
  CloseHandle(process.hThread);backend.process=process.hProcess;backend.active=true;backend.lastPhase=-1;backend.lastProgress=-1;
  UI_SetPhase(HANDOFF,L"正在启动安装事务；需要时请确认 Windows 权限提示");SetTimer(u.host,7,16,0);return 1;
}
static void pollBackend(){
  if(!backend.active)return;BackendStatus snapshot;
  if(readBackendStatus(snapshot)&&snapshot.ready){
    backend.ready=true;
    if(snapshot.prompt&&snapshot.response==0&&!backend.prompting){backend.prompting=true;u.callback(9);backend.prompting=false;return;}
    if(!snapshot.cancel&&snapshot.phase!=ERRORSTATE&&snapshot.phase!=COMPLETE){
      if(snapshot.phase!=backend.lastPhase||lstrcmpW(snapshot.detail,backend.lastDetail)!=0){
        backend.lastPhase=snapshot.phase;lstrcpynW(backend.lastDetail,snapshot.detail,2048);
        UI_SetPhase(snapshot.phase,snapshot.detail);u.callback(10);
      }
      if(snapshot.phase==INSTALLING&&snapshot.progress!=backend.lastProgress){backend.lastProgress=snapshot.progress;UI_SetProgress(snapshot.progress);u.callback(10);}
    }
  }
  if(WaitForSingleObject(backend.process,0)!=WAIT_OBJECT_0)return;
  GetExitCodeProcess(backend.process,&backend.exitCode);backend.active=false;backend.completed=true;KillTimer(u.host,7);
  if(backend.exitCode==0){wchar_t message[160];swprintf(message,160,L"%ls 已安装。",u.name);UI_SetPhase(COMPLETE,message);}
  else if(backend.ready&&backend.status->phase==ERRORSTATE&&backend.status->detail[0])UI_SetPhase(ERRORSTATE,backend.status->detail);
  else if(backend.status&&backend.status->cancel)UI_SetPhase(ERRORSTATE,L"安装已取消，用户资料已保留。");
  else if(backend.ready){wchar_t message[180];swprintf(message,180,L"安装事务未完成（错误 %lu）。用户资料已保留，请重试或查看安装日志。",backend.exitCode);UI_SetPhase(ERRORSTATE,message);}
  else UI_SetPhase(ERRORSTATE,L"安装事务未能启动或未获得权限。程序文件未修改，请重试。");
  u.callback(8);
}
