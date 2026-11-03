// QA-only opt-in measurements of real message dispatch and owner-draw latency.
// The probe posts only to its own installer window and never sends user input.
static constexpr UINT ProbeMessage=WM_APP+61;
struct ProbeData {
  HANDLE stop,thread; HWND host; volatile LONG pending;
  bool active,paintPending; DWORD sent; unsigned dispatched,painted;
  DWORD dispatchMax,paintMax; unsigned slowPaints;
  wchar_t path[1024];
};
static ProbeData probeData;
static DWORD WINAPI probeThread(void*){
  while(WaitForSingleObject(probeData.stop,20)==WAIT_TIMEOUT){
    LONG stamp=(LONG)GetTickCount();if(!stamp)stamp=1;
    if(InterlockedCompareExchange(&probeData.pending,stamp,0)==0)
      if(!PostMessageW(probeData.host,ProbeMessage,(WPARAM)(DWORD)stamp,0))InterlockedExchange(&probeData.pending,0);
  }
  return 0;
}
static void probeDispatched(WPARAM timestamp){
  if(!probeData.active)return;
  probeData.sent=(DWORD)timestamp;DWORD lag=GetTickCount()-probeData.sent;
  if(lag>probeData.dispatchMax)probeData.dispatchMax=lag;
  probeData.dispatched++;probeData.paintPending=true;
  u.hot=u.hot==MINIMIZE?0:MINIMIZE;redrawInteractive(u.controls[MINIMIZE]);
}
static void probePainted(UINT id){
  if(!probeData.active||!probeData.paintPending||id!=MINIMIZE)return;
  DWORD lag=GetTickCount()-probeData.sent;
  if(lag>probeData.paintMax)probeData.paintMax=lag;
  if(lag>100)probeData.slowPaints++;
  probeData.painted++;probeData.paintPending=false;InterlockedExchange(&probeData.pending,0);
}
static void stopProbe(){
  if(!probeData.active)return;SetEvent(probeData.stop);WaitForSingleObject(probeData.thread,2000);
  CloseHandle(probeData.thread);CloseHandle(probeData.stop);probeData.active=false;
  FILE *file=_wfopen(probeData.path,L"wb");if(file){
    fprintf(file,"{\"dispatched\":%u,\"painted\":%u,\"dispatchMaxMs\":%lu,\"hoverPaintMaxMs\":%lu,\"hoverPaintOver100Ms\":%u}\n",probeData.dispatched,probeData.painted,probeData.dispatchMax,probeData.paintMax,probeData.slowPaints);fclose(file);
  }
}
