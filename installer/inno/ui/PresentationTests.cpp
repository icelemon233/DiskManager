// Exercise the actual GDI+ renderer and the UI's own transitions, without an
// installer transaction or any access to a user's installation or app data.
#include "InstallerUi.cpp"
#include <stdlib.h>

static int failures;
static void check(bool ok,const char *name){printf("%s %s\n",ok?"PASS":"FAIL",name);if(!ok)failures++;}
struct Surface {
  HDC dc; HBITMAP bitmap,original;
  Surface(int width,int height){dc=CreateCompatibleDC(0);BITMAPINFO info={};info.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);info.bmiHeader.biWidth=width;info.bmiHeader.biHeight=-height;info.bmiHeader.biPlanes=1;info.bmiHeader.biBitCount=32;info.bmiHeader.biCompression=BI_RGB;void *bits;bitmap=CreateDIBSection(dc,&info,DIB_RGB_COLORS,&bits,0,0);original=(HBITMAP)SelectObject(dc,bitmap);}
  ~Surface(){SelectObject(dc,original);DeleteObject(bitmap);DeleteDC(dc);}
};
static double difference(COLORREF a,COLORREF b){return (abs((int)GetRValue(a)-GetRValue(b))+abs((int)GetGValue(a)-GetGValue(b))+abs((int)GetBValue(a)-GetBValue(b)))/3.0;}
static void gradientTests(){
  GdiplusStartupInput input;GdiplusStartup(&u.gdip,&input,0);
  Bitmap *solid=new Bitmap(16,16,PixelFormat32bppARGB);{Graphics g(solid);g.Clear(Color::White);}
  u.day=solid;u.night=solid;u.icon=0;
  for(bool dark:{false,true})for(float scale:{1.f,1.25f,1.5f,2.f}){
    u.dark=dark;u.scale=scale;u.width=px(800);u.height=px(530);u.drawer=false;
    Surface s(u.width,u.height);background(s.dc);GdiFlush();
    int edge=px(540),row=u.height/2;double maximum=0;
    for(int x=edge-3;x<=edge+2;x++){double delta=difference(GetPixel(s.dc,x,row),GetPixel(s.dc,x+1,row));if(delta>maximum)maximum=delta;}
    printf("gradient theme=%s scale=%.2f boundary_delta=%.2f\n",dark?"dark":"light",scale,maximum);
    check(maximum<=3,"gradient has no one-pixel edge at its transparent endpoint");
  }
  u.day=0;u.night=0;delete solid;GdiplusShutdown(u.gdip);u=Ui{};
}
static int persistentVisibilityEvents;
static LRESULT CALLBACK probe(HWND w,UINT msg,WPARAM wp,LPARAM lp,UINT_PTR,DWORD_PTR){if(msg==WM_SHOWWINDOW)persistentVisibilityEvents++;return DefSubclassProc(w,msg,wp,lp);}
static void __stdcall action(int){}
static DWORD WINAPI confirmExitForTest(void *text){return (DWORD)UI_EngineConfirmExit((const wchar_t*)text);}
static void transitionTests(const wchar_t *assets,const wchar_t *icon){
  wchar_t day[1024],night[1024];swprintf(day,1024,L"%ls\\scene-day.jpg",assets);swprintf(night,1024,L"%ls\\scene-night.jpg",assets);
  HWND parent=CreateWindowExW(0,L"Static",L"Native presentation regression",WS_POPUP,0,0,800,530,0,0,GetModuleHandleW(0),0);
  check(UI_Create(parent,L"NetworkToolbox",L"test",L"界面绘制回归",day,night,icon,2,action)==1,"native UI initializes");
  if(!u.host){DestroyWindow(parent);return;}
  UI_SetOptions(0,L"D:\\Apps\\中文 路径",1,0,0);
  const int homepage[]={200,201,202,INSTALL,SETTINGS};
  for(int id:homepage){RECT bounds;GetClientRect(u.controls[id],&bounds);check((GetWindowLongPtrW(u.controls[id],GWL_STYLE)&WS_VISIBLE)&&bounds.right>0&&bounds.bottom>0,"homepage control has a real visible layout rectangle");}
  HWND title=u.controls[201],subtitle=u.controls[202];SetWindowSubclass(u.controls[200],probe,3,0);SetWindowSubclass(title,probe,1,0);SetWindowSubclass(subtitle,probe,2,0);
  persistentVisibilityEvents=0;
  unsigned sceneGeneration=u.sceneGeneration;
  LARGE_INTEGER start,finish,frequency;QueryPerformanceFrequency(&frequency);QueryPerformanceCounter(&start);
  for(int i=0;i<20;i++){toggleDrawer(true);toggleDrawer(false);}
  QueryPerformanceCounter(&finish);
  printf("40 drawer transitions: persistent_visibility_events=%d time_ms=%.2f\n",persistentVisibilityEvents,1000.0*(finish.QuadPart-start.QuadPart)/frequency.QuadPart);
  check(persistentVisibilityEvents==0,"persistent header, title and subtitle are never hidden during drawer transitions");
  check(u.sceneGeneration==sceneGeneration,"drawer transitions reuse the scene raster without rendering it again");
  check(title==u.controls[201]&&subtitle==u.controls[202],"drawer transitions retain the actual native controls");
  wchar_t path[1024];UI_GetPath(path,1024);check(lstrcmpW(path,L"D:\\Apps\\中文 路径")==0&&UI_GetDesktop()==1&&UI_GetStart()==0,"drawer transitions retain destination and shortcut drafts");
  UI_SetPhase(PREPARING,L"LanDrop 正在运行。继续后会正常退出，未完成的传输会中断。已经保存的文件和共享设置会保留。取消可继续使用当前版本。");
  RECT body,progress,cancel,footer;GetWindowRect(u.controls[207],&body);GetWindowRect(u.controls[PROGRESS],&progress);GetWindowRect(u.controls[CANCELINSTALL],&cancel);GetWindowRect(u.controls[203],&footer);
  check(statusTextHeight(310)*u.scale<=body.bottom-body.top,"multiline preparation text fits its actual native control");
  check(progress.top>=body.bottom+px(7)&&cancel.top>=progress.bottom+px(20),"long preparation text moves progress and cancel below the full text");
  check(cancel.bottom+px(20)<=footer.top,"expanded preparation content preserves footer spacing");
  BackendStatus status={};backend.status=&status;
  writeBackendStatus(PREPARING,0,L"等待应用正常退出");
  HANDLE promptThread=CreateThread(0,0,confirmExitForTest,(void*)L"LanDrop 正在运行。继续后会正常退出，未完成的传输会中断。已经保存的文件和共享设置会保留。取消可继续使用当前版本。",0,0);
  DWORD deadline=GetTickCount()+3000;
  while(InterlockedCompareExchange(&status.prompt,0,0)==0&&GetTickCount()<deadline)Sleep(1);
  check(status.prompt==1,"exit confirmation request reaches the foreground protocol");
  check(lstrlenW(status.detail)<30,"exit confirmation warning is not copied into the compact status text");
  UI_BackendAnswer(1);WaitForSingleObject(promptThread,3000);DWORD accepted=0;GetExitCodeThread(promptThread,&accepted);CloseHandle(promptThread);
  check(accepted==1,"shortened waiting status preserves exit confirmation behavior");
  backend.status=0;
  UI_SetPhase(PREPARING,L"正在等待应用正常退出");
  for(int mode:{1,2}){
    u.theme=mode;theme();refresh();UpdateWindow(u.host);
    Surface s(px(38),px(34));DRAWITEMSTRUCT item={};item.CtlType=ODT_BUTTON;item.CtlID=MINIMIZE;item.hwndItem=u.controls[MINIMIZE];item.hDC=s.dc;item.rcItem={0,0,px(38),px(34)};
    u.hot=0;drawButton(&item);GdiFlush();COLORREF normal=GetPixel(s.dc,px(19),px(10));
    u.hot=MINIMIZE;drawButton(&item);GdiFlush();COLORREF hover=GetPixel(s.dc,px(19),px(10));
    double delta=difference(normal,hover);printf("minimize theme=%s hover_delta=%.2f\n",mode==2?"dark":"light",delta);
    check(delta>=30,"minimize hover has a clearly distinct neutral surface");
  }
  UI_Destroy();DestroyWindow(parent);u=Ui{};
}
int wmain(int argc,wchar_t **argv){setvbuf(stdout,0,_IONBF,0);if(argc!=3){fwprintf(stderr,L"Usage: presentation-tests <scene-assets> <icon-png>\n");return 2;}gradientTests();transitionTests(argv[1],argv[2]);return failures?1:0;}
