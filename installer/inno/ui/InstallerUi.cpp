// Shared native presentation for the Inno transaction. No web or managed runtime.
#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <windowsx.h>
#include <commctrl.h>
#include <objidl.h>
#include <gdiplus.h>
#include <dwmapi.h>
#include <uxtheme.h>
#include <shlobj.h>
#include <oleacc.h>
#include <stdio.h>
#include <initializer_list>
using namespace Gdiplus;

typedef void (__stdcall *ActionCallback)(int);
enum { INSTALL=101, SETTINGS, CLOSE, MINIMIZE, SCOPE=110, PATH, BROWSE,
       DESKTOP, STARTMENU, SAVESETTINGS, DONE, CANCELINSTALL, DETAILS, PROGRESS };
enum { HOME, PREPARING, INSTALLING, COMPLETE, ERRORSTATE, HANDOFF, ROLLINGBACK };
struct Ui {
  HWND parent, host, controls[256]; HINSTANCE module; HHOOK keyboard;
  HBITMAP backing, originalBitmap; HDC memory; int width, height, phase, progress, theme;
  HBITMAP sceneBacking, sceneOriginal; HDC sceneMemory; bool sceneDirty;
  unsigned sceneGeneration;
  float scale; bool drawer, dark, contrast, locked, modal, dirty, keyboardFocus, comInitialized, allowLaunch;
  int hot; IAccPropServices *accessibility;
  HFONT font, small, title, heading; HBRUSH fieldBrush;
  Bitmap *day, *night, *icon; ULONG_PTR gdip; ActionCallback callback;
  wchar_t name[80], version[80], description[180], path[1024], detail[2048];
  HANDLE child, ready; bool childReady; DWORD exitedAt;
  DWORD childExitCode;
  wchar_t handoffName[160]; WNDPROC parentProc;
};
static Ui u;
static void redrawInteractive(HWND window);
#include "ResponsivenessProbe.h"
static void pollBackend();
static void background(HDC dc);
static void ensureBackground(){if(u.memory&&u.dirty){background(u.memory);u.dirty=false;}}
static int px(float n) { return (int)(n*u.scale+0.5f); }
static UINT dpi(){typedef UINT (WINAPI *GetDpi)(HWND);GetDpi f=(GetDpi)GetProcAddress(GetModuleHandleW(L"user32.dll"),"GetDpiForWindow");if(f)return f(u.parent);HDC dc=GetDC(u.parent);UINT value=GetDeviceCaps(dc,LOGPIXELSX);ReleaseDC(u.parent,dc);return value;}
static Color color(COLORREF c) { return Color(255,GetRValue(c),GetGValue(c),GetBValue(c)); }
static COLORREF surface() { return u.contrast?GetSysColor(COLOR_WINDOW):(u.dark?RGB(30,33,37):RGB(255,252,246)); }
static COLORREF field() { return u.contrast?GetSysColor(COLOR_WINDOW):(u.dark?RGB(26,29,32):RGB(255,255,253)); }
static COLORREF ink() { return u.contrast?GetSysColor(COLOR_WINDOWTEXT):(u.dark?RGB(244,241,236):RGB(43,48,53)); }
static COLORREF muted() { return u.contrast?GetSysColor(COLOR_WINDOWTEXT):(u.dark?RGB(190,194,197):RGB(91,101,110)); }
static void fonts(){
  HFONT old[]={u.font,u.small,u.title,u.heading};
  u.font=CreateFontW(-px(14),0,0,0,FW_NORMAL,0,0,0,DEFAULT_CHARSET,0,0,CLEARTYPE_QUALITY,0,L"Microsoft YaHei UI");
  u.small=CreateFontW(-px(12),0,0,0,FW_NORMAL,0,0,0,DEFAULT_CHARSET,0,0,CLEARTYPE_QUALITY,0,L"Microsoft YaHei UI");
  u.title=CreateFontW(-px(31),0,0,0,FW_SEMIBOLD,0,0,0,DEFAULT_CHARSET,0,0,CLEARTYPE_QUALITY,0,L"Segoe UI");
  u.heading=CreateFontW(-px(18),0,0,0,FW_SEMIBOLD,0,0,0,DEFAULT_CHARSET,0,0,CLEARTYPE_QUALITY,0,L"Microsoft YaHei UI");
  for(int i=0;i<256;i++)if(u.controls[i])SendMessageW(u.controls[i],WM_SETFONT,(WPARAM)u.font,TRUE);
  for(HFONT f:old)if(f)DeleteObject(f);
}
static void rounded(Graphics &g, RectF r, Color fill, Color border, float radius=9) {
  GraphicsPath p; float d=px(radius)*2;
  if(d>r.Height)d=r.Height;
  p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.GetRight()-d,r.Y,d,d,270,90);
  p.AddArc(r.GetRight()-d,r.GetBottom()-d,d,d,0,90);p.AddArc(r.X,r.GetBottom()-d,d,d,90,90);p.CloseFigure();
  SolidBrush b(fill);g.FillPath(&b,&p);if(border.GetA()){Pen pen(border,(REAL)px(1));g.DrawPath(&pen,&p);}
}
static void copyBackground(HDC dc, HWND w) {
  if(u.modal){RECT r;GetClientRect(w,&r);HBRUSH b=CreateSolidBrush(surface());FillRect(dc,&r,b);DeleteObject(b);return;}
  RECT r;GetWindowRect(w,&r);MapWindowPoints(HWND_DESKTOP,u.host,(POINT*)&r,2);
  ensureBackground();
  if(u.memory)BitBlt(dc,0,0,r.right-r.left,r.bottom-r.top,u.memory,r.left,r.top,SRCCOPY);
}
static void labelText(HDC dc, RECT r, const wchar_t *text, HFONT font, COLORREF c, UINT flags) {
  HGDIOBJ old=SelectObject(dc,font);SetTextColor(dc,c);SetBkMode(dc,TRANSPARENT);
  DrawTextW(dc,text,-1,&r,flags|DT_NOPREFIX);SelectObject(dc,old);
}
static bool systemDark() {
  DWORD v=1,sz=sizeof(v);RegGetValueW(HKEY_CURRENT_USER,L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize",L"AppsUseLightTheme",RRF_RT_REG_DWORD,0,&v,&sz);
  return v==0;
}
static void theme() {
  u.sceneDirty=true;
  HIGHCONTRASTW hc={sizeof(hc)};SystemParametersInfoW(SPI_GETHIGHCONTRAST,sizeof(hc),&hc,0);
  u.contrast=(hc.dwFlags&HCF_HIGHCONTRASTON)!=0;u.dark=u.theme==2||(u.theme==0&&systemDark());
  if(u.fieldBrush)DeleteObject(u.fieldBrush);u.fieldBrush=CreateSolidBrush(field());
  BOOL dark=u.dark;DwmSetWindowAttribute(u.parent,20,&dark,sizeof(dark));
  for(int i=0;i<256;i++)if(u.controls[i])SetWindowTheme(u.controls[i],u.dark?L"DarkMode_Explorer":L"Explorer",0);
}
static void refresh() { u.dirty=true;if(u.host){InvalidateRect(u.host,0,FALSE);RedrawWindow(u.host,0,0,RDW_INVALIDATE|RDW_ALLCHILDREN);} }
static void redrawInteractive(HWND window){if(window)RedrawWindow(window,0,0,RDW_INVALIDATE|RDW_NOERASE|RDW_UPDATENOW);}
static void sceneBackground(HDC dc) {
  ++u.sceneGeneration;
  Graphics g(dc);g.SetSmoothingMode(SmoothingModeAntiAlias);g.Clear(color(surface()));
  Bitmap *scene=u.dark?u.night:u.day;
  if(!u.contrast&&scene&&scene->GetLastStatus()==Ok){
    g.SetInterpolationMode(InterpolationModeHighQualityBicubic);
    REAL sourceWidth=(REAL)scene->GetWidth(),sourceHeight=(REAL)scene->GetHeight(),aspect=(REAL)u.width/u.height;
    if(sourceWidth/sourceHeight>aspect)sourceWidth=sourceHeight*aspect;else sourceHeight=sourceWidth/aspect;
    g.DrawImage(scene,RectF(0,0,(REAL)u.width,(REAL)u.height),(scene->GetWidth()-sourceWidth)/2,(scene->GetHeight()-sourceHeight)/2,sourceWidth,sourceHeight,UnitPixel);
    Color base=color(surface());
    LinearGradientBrush mask(Point(0,0),Point(px(540),0),base,Color(0,base.GetR(),base.GetG(),base.GetB()));
    // Mirror the transparent edge instead of sampling the next opaque tile.
    mask.SetWrapMode(WrapModeTileFlipX);
    REAL positions[]={0,0.56f,1};Color colors[]={base,Color(245,base.GetR(),base.GetG(),base.GetB()),Color(0,base.GetR(),base.GetG(),base.GetB())};
    mask.SetInterpolationColors(colors,positions,3);g.FillRectangle(&mask,0,0,px(540),u.height);
  }
  if(u.icon&&u.icon->GetLastStatus()==Ok)g.DrawImage(u.icon,Rect(px(28),px(24),px(27),px(27)));
}
static void background(HDC dc) {
  if(u.sceneMemory){
    if(u.sceneDirty){sceneBackground(u.sceneMemory);u.sceneDirty=false;}
    BitBlt(dc,0,0,u.width,u.height,u.sceneMemory,0,0,SRCCOPY);
  }else sceneBackground(dc);
  Graphics g(dc);g.SetSmoothingMode(SmoothingModeAntiAlias);
  if(u.drawer){
    SolidBrush shade(Color(55,0,0,0));g.FillRectangle(&shade,0,0,u.width-px(352),u.height);
    SolidBrush panel(color(surface()));g.FillRectangle(&panel,u.width-px(352),0,px(352),u.height);
    Pen line(Color(80,u.dark?150:90,u.dark?160:100,u.dark?170:110));g.DrawLine(&line,u.width-px(352),0,u.width-px(352),u.height);
    rounded(g,RectF((REAL)(u.width-px(324)),(REAL)px(u.height/u.scale<520?194:210),(REAL)px(296),(REAL)px(44)),color(field()),Color(150,105,117,125));
  }
  rounded(g,RectF(.5f,.5f,(REAL)u.width-1,(REAL)u.height-1),Color(0,0,0,0),u.dark?Color(210,116,129,139):Color(210,143,156,161),16);
}
static void drawButton(DRAWITEMSTRUCT *d) {
  copyBackground(d->hDC,d->hwndItem);Graphics g(d->hDC);g.SetSmoothingMode(SmoothingModeAntiAlias);
  bool disabled=(d->itemState&ODS_DISABLED)!=0,pressed=(d->itemState&ODS_SELECTED)!=0;
  COLORREF fg=ink();Color fill=color(field()),border(u.dark?130:90,120,136,145);
  if(d->CtlID==INSTALL){fill=Color(255,168,223,241);fg=RGB(36,73,85);border=Color(0,0,0,0);}
  if(d->CtlID==CANCELINSTALL){fill=Color(255,190,55,65);fg=RGB(255,255,255);border=Color(0,0,0,0);}
  if(d->CtlID==DETAILS){fill=Color(0,0,0,0);border=Color(0,0,0,0);fg=muted();}
  if(d->CtlID==CLOSE||d->CtlID==MINIMIZE){fill=u.dark?Color(225,28,31,35):Color(235,255,252,246);border=Color(0,0,0,0);}
  if(u.hot==(int)d->CtlID){
    if(d->CtlID==INSTALL)fill=Color(255,189,234,247);
    else if(d->CtlID==CLOSE){fill=Color(255,196,43,28);fg=RGB(255,255,255);}
    else if(d->CtlID==MINIMIZE){fill=u.dark?Color(255,73,88,103):Color(255,203,217,228);border=u.dark?Color(255,140,160,176):Color(255,106,130,150);}
    else if(d->CtlID==CANCELINSTALL)fill=Color(255,166,41,52);
    else fill=u.dark?Color(255,48,52,58):Color(255,232,231,226);
  }
  if(u.contrast){fill=color(GetSysColor(COLOR_BTNFACE));border=color(GetSysColor(COLOR_BTNTEXT));fg=GetSysColor(COLOR_BTNTEXT);}
  if(disabled){fill=Color(255,u.dark?57:222,u.dark?63:225,u.dark?67:225);fg=muted();}
  if(pressed&&!disabled)fill=Color(fill.GetA(),(BYTE)(fill.GetR()*.87f),(BYTE)(fill.GetG()*.87f),(BYTE)(fill.GetB()*.87f));
  rounded(g,RectF(1,1,(REAL)d->rcItem.right-2,(REAL)d->rcItem.bottom-2),fill,border);
  if((d->itemState&ODS_FOCUS)&&u.keyboardFocus){Pen p(color(u.contrast?GetSysColor(COLOR_HIGHLIGHT):RGB(77,155,185)),(REAL)px(2));g.DrawRectangle(&p,px(4),px(4),d->rcItem.right-px(8),d->rcItem.bottom-px(8));}
  RECT r=d->rcItem;wchar_t t[160];GetWindowTextW(d->hwndItem,t,160);
  if(d->CtlID==CLOSE||d->CtlID==MINIMIZE){
    Pen p(color(fg),(REAL)px(1.3f));float cx=r.right/2.f,cy=r.bottom/2.f,v=(REAL)px(5);
    if(d->CtlID==CLOSE){g.DrawLine(&p,cx-v,cy-v,cx+v,cy+v);g.DrawLine(&p,cx+v,cy-v,cx-v,cy+v);}
    else g.DrawLine(&p,cx-v,cy,cx+v,cy);
  }else labelText(d->hDC,r,t,u.font,fg,DT_CENTER|DT_VCENTER|DT_SINGLELINE);
  probePainted(d->CtlID);
}
static LRESULT CALLBACK controlProc(HWND w,UINT msg,WPARAM wp,LPARAM lp,UINT_PTR id,DWORD_PTR) {
  if(msg==WM_LBUTTONDOWN){u.keyboardFocus=false;InvalidateRect(w,0,FALSE);}
  if(msg==WM_MOUSEMOVE&&u.hot!=(int)id){HWND previous=GetDlgItem(GetParent(w),u.hot);u.hot=(int)id;redrawInteractive(previous);TRACKMOUSEEVENT track={sizeof(track),TME_LEAVE,w,0};TrackMouseEvent(&track);redrawInteractive(w);}
  if(msg==WM_MOUSELEAVE){if(u.hot==(int)id)u.hot=0;redrawInteractive(w);}
  if(msg==WM_ERASEBKGND)return 1;
  if(msg==WM_PAINT&&(id==SCOPE||id==DESKTOP||id==STARTMENU||id==PROGRESS)){
    PAINTSTRUCT ps;HDC dc=BeginPaint(w,&ps);copyBackground(dc,w);RECT r;GetClientRect(w,&r);Graphics g(dc);g.SetSmoothingMode(SmoothingModeAntiAlias);
    if(id==PROGRESS){
      rounded(g,RectF(0,0,(REAL)r.right,(REAL)r.bottom),u.dark?Color(255,54,69,75):Color(255,216,225,228),Color(0,0,0,0),5);
      if(u.progress>0)rounded(g,RectF(0,0,r.right*u.progress/100.f,(REAL)r.bottom),Color(255,168,223,241),Color(0,0,0,0),5);
    }else if(id==SCOPE){
      rounded(g,RectF(1,1,(REAL)r.right-2,(REAL)r.bottom-2),color(field()),color(GetFocus()==w?RGB(88,168,197):RGB(122,134,141)));
      wchar_t s[100];int n=(int)SendMessageW(w,CB_GETCURSEL,0,0);SendMessageW(w,CB_GETLBTEXT,n,(LPARAM)s);
      RECT tr={px(12),0,r.right-px(44),r.bottom};labelText(dc,tr,s,u.font,ink(),DT_VCENTER|DT_SINGLELINE|DT_END_ELLIPSIS);
      // Chevron down (Lucide geometry): a separate 16 DIP glyph, inset 12 DIP.
      Pen p(color(ink()),(REAL)px(1.5f));float cx=(REAL)(r.right-px(20)),cy=r.bottom/2.f;
      g.DrawLine(&p,cx-px(4),cy-px(2),cx,cy+px(2));g.DrawLine(&p,cx,cy+px(2),cx+px(4),cy-px(2));
    }else{
      bool checked=SendMessageW(w,BM_GETCHECK,0,0)==BST_CHECKED;float y=(r.bottom-px(18))/2.f;
      rounded(g,RectF(1,y,(REAL)px(18),(REAL)px(18)),checked?Color(255,168,223,241):color(field()),color(muted()),4);
      if(checked){Pen p(Color(255,36,73,85),(REAL)px(2));g.DrawLine(&p,(REAL)px(5),y+px(9),(REAL)px(8),y+px(12));g.DrawLine(&p,(REAL)px(8),y+px(12),(REAL)px(15),y+px(5));}
      wchar_t s[100];GetWindowTextW(w,s,100);RECT tr={px(29),0,r.right,r.bottom};labelText(dc,tr,s,u.font,ink(),DT_VCENTER|DT_SINGLELINE);
      if(GetFocus()==w&&u.keyboardFocus){RECT f=r;InflateRect(&f,-px(1),-px(1));DrawFocusRect(dc,&f);}
    }
    EndPaint(w,&ps);return 0;
  }
  LRESULT result=DefSubclassProc(w,msg,wp,lp);
  if(msg==WM_SETFOCUS||msg==WM_KILLFOCUS||msg==WM_LBUTTONUP||msg==WM_KEYUP||msg==BM_SETCHECK||msg==CB_SETCURSEL)InvalidateRect(w,0,FALSE);
  return result;
}
static HWND add(int id,const wchar_t *cls,const wchar_t *text,DWORD style){
  HWND w=CreateWindowExW(0,cls,text,WS_CHILD|style,0,0,0,0,u.host,(HMENU)(INT_PTR)id,u.module,0);u.controls[id]=w;SendMessageW(w,WM_SETFONT,(WPARAM)u.font,FALSE);return w;
}
static void text(int id,const wchar_t *value){
  HWND control=u.controls[id];if(!control)return;
  wchar_t previous[2048];GetWindowTextW(control,previous,2048);
  if(lstrcmpW(previous,value)==0)return;
  SetWindowTextW(control,value);InvalidateRect(control,0,FALSE);
}
static int statusTextHeight(int width){
  wchar_t value[2048];GetWindowTextW(u.controls[207],value,2048);
  RECT bounds={0,0,px(width),0};HDC dc=CreateCompatibleDC(0);HGDIOBJ previous=SelectObject(dc,u.font);
  DrawTextW(dc,value,-1,&bounds,DT_CALCRECT|DT_WORDBREAK|DT_NOPREFIX);
  SelectObject(dc,previous);DeleteDC(dc);
  int logical=(int)(bounds.bottom/u.scale);while(px(logical)<bounds.bottom)++logical;return logical;
}
struct Placement {bool visible;int x,y,width,height;};
static Placement applied[256];
static void visibility(){
  int width=(int)(u.width/u.scale),height=(int)(u.height/u.scale),dx=width-352;
  Placement desired[256]={};
  auto place=[&](int id,int x,int y,int w,int h,bool visible=true){desired[id]={visible,px(x),px(y),px(w),px(h)};};
  place(200,64,22,220,32);place(MINIMIZE,width-96,12,38,34);place(CLOSE,width-52,12,38,34);
  EnableWindow(u.controls[CLOSE],u.phase!=HANDOFF&&u.phase!=ROLLINGBACK);
  place(201,34,111,380,54);place(202,34,170,370,42);place(203,34,height-40,300,24);
  if(u.phase==HOME){
    place(204,34,236,320,25);place(205,34,268,320,45);place(INSTALL,34,335,148,48);place(SETTINGS,194,337,130,44);
  }else{
    int bodyHeight=u.phase==ERRORSTATE?(height<520?56:81):40;
    bool running=u.phase==INSTALLING||u.phase==PREPARING||u.phase==HANDOFF;
    bool overflow=false;
    if(running||u.phase==ROLLINGBACK){
      int required=statusTextHeight(310),maximum=height-423;
      if(maximum<40)maximum=40;
      if(required>bodyHeight)bodyHeight=required;
      if(bodyHeight>maximum){bodyHeight=maximum;overflow=true;}
    }
    int progressTop=279+bodyHeight+7,cancelTop=progressTop+29;
    place(206,34,236,310,30);place(207,34,279,310,bodyHeight);
    if(running)place(PROGRESS,34,progressTop,310,9);
    if(u.phase==COMPLETE){place(INSTALL,34,336,148,48);place(DONE,194,336,148,48);}
    if(u.phase==PREPARING||u.phase==INSTALLING){place(CANCELINSTALL,34,cancelTop,132,44);}
    if(overflow)place(DETAILS,178,cancelTop,132,44);
    if(u.phase==ERRORSTATE){place(INSTALL,34,height<520?348:372,132,44);place(SETTINGS,178,height<520?348:372,132,44);place(DETAILS,34,height<520?400:428,132,32);}
  }
  if(u.drawer&&u.phase==HOME){
    bool compact=height<520;
    // Hide left actions behind the modal settings surface; retain the summary.
    desired[INSTALL].visible=false;desired[SETTINGS].visible=false;
    place(210,dx+28,31,240,32);place(211,dx+28,compact?80:90,296,24);place(SCOPE,dx+28,compact?108:120,296,180);
    place(212,dx+28,compact?164:178,296,24);place(PATH,dx+40,compact?205:221,272,24);place(BROWSE,dx+210,compact?250:266,114,36,!(compact&&u.locked));
    place(213,dx+28,compact?(u.locked?250:288):308,296,compact?40:48);place(DESKTOP,dx+28,compact?334:368,296,34);place(STARTMENU,dx+28,compact?374:410,296,34);place(SAVESETTINGS,dx+28,height-65,296,44);
  }
  // Prepare the new composite before exposing controls that read from it.
  // Persistent controls retain their handles, visibility and geometry.
  u.dirty=true;ensureBackground();
  HDWP batch=BeginDeferWindowPos(64);bool deferred=batch!=0;
  for(int id=0;id<256;id++)if(u.controls[id]){
    HWND control=u.controls[id];const Placement &p=desired[id];
    bool visible=(GetWindowLongPtrW(control,GWL_STYLE)&WS_VISIBLE)!=0;
    bool moved=p.visible&&(applied[id].x!=p.x||applied[id].y!=p.y||applied[id].width!=p.width||applied[id].height!=p.height);
    if(visible==p.visible&&!moved)continue;
    UINT flags=SWP_NOZORDER|SWP_NOACTIVATE|SWP_NOREDRAW;
    flags|=p.visible?SWP_SHOWWINDOW:SWP_HIDEWINDOW;
    if(!moved)flags|=SWP_NOMOVE|SWP_NOSIZE;
    if(deferred){batch=DeferWindowPos(batch,control,0,p.x,p.y,p.width,p.height,flags);if(!batch)deferred=false;}
    if(!deferred)SetWindowPos(control,0,p.x,p.y,p.width,p.height,flags);
  }
  if(deferred&&!EndDeferWindowPos(batch))deferred=false;
  if(!deferred){
    for(int id=0;id<256;id++)if(u.controls[id]){const Placement &p=desired[id];SetWindowPos(u.controls[id],0,p.x,p.y,p.width,p.height,SWP_NOZORDER|SWP_NOACTIVATE|SWP_NOREDRAW|(p.visible?SWP_SHOWWINDOW:SWP_HIDEWINDOW));}
  }
  for(int id=0;id<256;id++)if(u.controls[id])applied[id]=desired[id];
  if(u.host)RedrawWindow(u.host,0,0,RDW_INVALIDATE|RDW_ALLCHILDREN|RDW_NOERASE|RDW_UPDATENOW);
}
static void summary(){
  bool all=SendMessageW(u.controls[SCOPE],CB_GETCURSEL,0,0)==1;
  text(204,all?L"为所有用户安装":L"仅为我安装");
  GetWindowTextW(u.controls[PATH],u.path,1024);text(205,u.path);
}
static void toggleDrawer(bool show){
  u.drawer=show;summary();visibility();SetFocus(u.controls[show?SCOPE:SETTINGS]);
}
static LRESULT CALLBACK parentProc(HWND w,UINT msg,WPARAM wp,LPARAM lp){
  if(msg==WM_CLOSE&&u.callback){u.callback(2);return 0;}
  if(msg==WM_SETTINGCHANGE||msg==WM_THEMECHANGED){LRESULT r=CallWindowProcW(u.parentProc,w,msg,wp,lp);theme();refresh();return r;}
  if(msg==WM_SIZE&&wp!=SIZE_MINIMIZED&&u.host){LRESULT r=CallWindowProcW(u.parentProc,w,msg,wp,lp);RECT rClient;GetClientRect(w,&rClient);SetWindowPos(u.host,HWND_TOP,0,0,rClient.right,rClient.bottom,SWP_NOACTIVATE);return r;}
  if(msg==WM_DPICHANGED){
    // Inno owns the top-level DPI change; relayout follows its resized client.
    LRESULT r=CallWindowProcW(u.parentProc,w,msg,wp,lp);RECT rc;GetClientRect(w,&rc);SetWindowPos(u.host,HWND_TOP,0,0,rc.right,rc.bottom,SWP_NOACTIVATE);return r;
  }
  return CallWindowProcW(u.parentProc,w,msg,wp,lp);
}
static LRESULT CALLBACK keyboardProc(int n,WPARAM wp,LPARAM lp){
  if(n>=0&&wp==PM_REMOVE){MSG *m=(MSG*)lp;
    if(m->message>=WM_KEYFIRST&&m->message<=WM_KEYLAST&&(m->hwnd==u.host||IsChild(u.host,m->hwnd))){
      bool drop=SendMessageW(u.controls[SCOPE],CB_GETDROPPEDSTATE,0,0)!=0;
      if(m->message==WM_KEYDOWN&&(m->wParam==VK_TAB||m->wParam==VK_UP||m->wParam==VK_DOWN||m->wParam==VK_SPACE)){u.keyboardFocus=true;InvalidateRect(GetFocus(),0,FALSE);}
      if(m->message==WM_KEYDOWN&&m->wParam==VK_ESCAPE&&!drop){if(u.drawer)toggleDrawer(false);else PostMessageW(u.host,WM_COMMAND,CLOSE,0);m->message=WM_NULL;}
      else if(m->message==WM_KEYDOWN&&m->wParam==VK_RETURN&&!drop&&GetClassLongPtrW(m->hwnd,GCW_ATOM)!=0){
        wchar_t cls[40];GetClassNameW(m->hwnd,cls,40);
        if(lstrcmpiW(cls,L"Button")==0)SendMessageW(m->hwnd,BM_CLICK,0,0);
        else if(u.drawer)toggleDrawer(false);else if(u.phase==HOME||u.phase==ERRORSTATE)PostMessageW(u.host,WM_COMMAND,INSTALL,0);
        m->message=WM_NULL;
      }else if(IsDialogMessageW(u.host,m))m->message=WM_NULL;
    }
  }
  return CallNextHookEx(u.keyboard,n,wp,lp);
}
static void disposeChild(){if(u.child)CloseHandle(u.child);if(u.ready)CloseHandle(u.ready);u.child=0;u.ready=0;KillTimer(u.host,4);}
static LRESULT CALLBACK windowProc(HWND w,UINT msg,WPARAM wp,LPARAM lp){
  if(msg==ProbeMessage){probeDispatched(wp);return 0;}
  if(msg==WM_TIMER&&wp==7){pollBackend();return 0;}
  switch(msg){
    case WM_ERASEBKGND:return 1;
    case WM_SIZE:{u.width=LOWORD(lp);u.height=HIWORD(lp);float previousScale=u.scale;u.scale=dpi()/96.f;if(previousScale!=u.scale)fonts();
      SetWindowRgn(u.parent,CreateRoundRectRgn(0,0,u.width+1,u.height+1,px(32),px(32)),TRUE);
      if(u.backing){SelectObject(u.memory,u.originalBitmap);DeleteObject(u.backing);}
      if(u.sceneBacking){SelectObject(u.sceneMemory,u.sceneOriginal);DeleteObject(u.sceneBacking);}
      HDC dc=GetDC(w);if(!u.memory)u.memory=CreateCompatibleDC(dc);u.backing=CreateCompatibleBitmap(dc,u.width,u.height);HBITMAP old=(HBITMAP)SelectObject(u.memory,u.backing);if(!u.originalBitmap)u.originalBitmap=old;
      if(!u.sceneMemory)u.sceneMemory=CreateCompatibleDC(dc);u.sceneBacking=CreateCompatibleBitmap(dc,u.width,u.height);old=(HBITMAP)SelectObject(u.sceneMemory,u.sceneBacking);if(!u.sceneOriginal)u.sceneOriginal=old;u.sceneDirty=true;
      ReleaseDC(w,dc);visibility();return 0;}
    case WM_PAINT:{PAINTSTRUCT ps;HDC dc=BeginPaint(w,&ps);ensureBackground();BitBlt(dc,0,0,u.width,u.height,u.memory,0,0,SRCCOPY);EndPaint(w,&ps);return 0;}
    case WM_SETTINGCHANGE:case WM_THEMECHANGED:theme();refresh();return 0;
    case WM_LBUTTONDOWN:if(!u.drawer){ReleaseCapture();SendMessageW(u.parent,WM_NCLBUTTONDOWN,HTCAPTION,0);}return 0;
    case WM_CTLCOLORSTATIC:case WM_CTLCOLOREDIT:SetTextColor((HDC)wp,ink());SetBkColor((HDC)wp,field());return (LRESULT)u.fieldBrush;
    case WM_MEASUREITEM:((MEASUREITEMSTRUCT*)lp)->itemHeight=px(32);return TRUE;
    case WM_DRAWITEM:{DRAWITEMSTRUCT *d=(DRAWITEMSTRUCT*)lp;
      if(d->CtlType==ODT_BUTTON){drawButton(d);return TRUE;}
      if(d->CtlType==ODT_COMBOBOX){
        HBRUSH b=CreateSolidBrush((d->itemState&ODS_SELECTED)?(u.dark?RGB(56,86,101):RGB(220,239,246)):field());FillRect(d->hDC,&d->rcItem,b);DeleteObject(b);
        wchar_t s[100]={0};if(d->itemID!=(UINT)-1)SendMessageW(d->hwndItem,CB_GETLBTEXT,d->itemID,(LPARAM)s);RECT r=d->rcItem;r.left+=px(12);labelText(d->hDC,r,s,u.font,ink(),DT_SINGLELINE|DT_VCENTER);return TRUE;
      }
      if(d->CtlType==ODT_STATIC){
        copyBackground(d->hDC,d->hwndItem);wchar_t s[2048];GetWindowTextW(d->hwndItem,s,2048);
        HFONT f=d->CtlID==201?u.title:(d->CtlID==206||d->CtlID==210?u.heading:((d->CtlID==203||d->CtlID==213)?u.small:u.font));
        bool secondary=d->CtlID==202||d->CtlID==203||d->CtlID==205||d->CtlID==207||d->CtlID==213;
        UINT format=(d->CtlID==202||d->CtlID==207||d->CtlID==213)?DT_WORDBREAK|(d->CtlID==207?DT_END_ELLIPSIS|DT_EDITCONTROL:0):DT_SINGLELINE|DT_VCENTER|DT_PATH_ELLIPSIS;
        labelText(d->hDC,d->rcItem,s,f,secondary?muted():ink(),format);return TRUE;
      }break;}
    case WM_COMMAND:{int id=LOWORD(wp),notification=HIWORD(wp);
      if(id==PATH&&notification==EN_CHANGE){summary();return 0;}
      if(id==SCOPE&&notification==CBN_SELCHANGE){u.callback(4);summary();refresh();return 0;}
      if(notification!=BN_CLICKED)return 0;
      switch(id){
        case MINIMIZE:ShowWindow(u.parent,SW_MINIMIZE);break;
        case CLOSE:case CANCELINSTALL:case DONE:u.callback(2);break;
        case INSTALL:if(u.phase==COMPLETE)u.callback(3);else u.callback(1);break;
        case SETTINGS:if(u.phase==ERRORSTATE){u.phase=HOME;text(INSTALL,L"安装");}toggleDrawer(true);break;
        case SAVESETTINGS:toggleDrawer(false);break;
        case BROWSE:u.callback(5);break;
        case DETAILS:MessageBoxW(u.parent,u.detail,L"安装详情",MB_OK|MB_ICONINFORMATION);break;
      }return 0;}
    case WM_TIMER:if(wp==4&&u.child){
      if(!u.childReady&&WaitForSingleObject(u.ready,0)==WAIT_OBJECT_0){u.childReady=true;ShowWindow(u.parent,SW_HIDE);}
      if(WaitForSingleObject(u.child,0)==WAIT_OBJECT_0){
        if(!u.exitedAt)u.exitedAt=GetTickCount();
        if(u.childReady){GetExitCodeProcess(u.child,&u.childExitCode);disposeChild();u.callback(8);}
        else if(GetTickCount()-u.exitedAt>4000){disposeChild();u.callback(7);}
      }return 0;
    }break;
  }
  return DefWindowProcW(w,msg,wp,lp);
}
#define API extern "C" __declspec(dllexport)
API void __stdcall UI_StartProbe(const wchar_t *path){
  if(probeData.active)return;probeData=ProbeData{};lstrcpynW(probeData.path,path,1024);
  probeData.host=u.host;probeData.stop=CreateEventW(0,TRUE,FALSE,0);probeData.active=true;
  probeData.thread=CreateThread(0,0,probeThread,0,0,0);
}
API int __stdcall UI_Create(HWND parent,const wchar_t *name,const wchar_t *version,const wchar_t *description,const wchar_t *day,const wchar_t *night,const wchar_t *icon,int forcedTheme,ActionCallback callback){
  if(u.host)return 0;u.parent=parent;u.callback=callback;u.theme=forcedTheme;u.scale=dpi()/96.f;u.module=GetModuleHandleW(0);u.allowLaunch=true;
  lstrcpynW(u.name,name,80);lstrcpynW(u.version,version,80);lstrcpynW(u.description,description,180);
  GdiplusStartupInput input;if(GdiplusStartup(&u.gdip,&input,0)!=Ok)return 0;
  u.comInitialized=SUCCEEDED(CoInitializeEx(0,COINIT_APARTMENTTHREADED));
  INITCOMMONCONTROLSEX common={sizeof(common),ICC_STANDARD_CLASSES|ICC_PROGRESS_CLASS};InitCommonControlsEx(&common);
  CoCreateInstance(CLSID_AccPropServices,0,CLSCTX_INPROC_SERVER,IID_IAccPropServices,(void**)&u.accessibility);
  u.day=Bitmap::FromFile(day);u.night=Bitmap::FromFile(night);u.icon=Bitmap::FromFile(icon);
  if(u.day->GetLastStatus()!=Ok||u.night->GetLastStatus()!=Ok||u.icon->GetLastStatus()!=Ok)return 0;
  fonts();
  MONITORINFO monitor={sizeof(monitor)};if(GetMonitorInfoW(MonitorFromWindow(parent,MONITOR_DEFAULTTONEAREST),&monitor)){
    int availableWidth=monitor.rcWork.right-monitor.rcWork.left-px(24),availableHeight=monitor.rcWork.bottom-monitor.rcWork.top-px(24);
    int width=px(800)<availableWidth?px(800):availableWidth,height=px(530)<availableHeight?px(530):availableHeight;
    SetWindowPos(parent,0,monitor.rcWork.left+(monitor.rcWork.right-monitor.rcWork.left-width)/2,monitor.rcWork.top+(monitor.rcWork.bottom-monitor.rcWork.top-height)/2,width,height,SWP_NOZORDER|SWP_NOACTIVATE);
  }
  WNDCLASSW wc={};wc.lpfnWndProc=windowProc;wc.hInstance=u.module;wc.lpszClassName=L"CinnaInstallerCanvas";wc.hCursor=LoadCursorW(0,IDC_ARROW);RegisterClassW(&wc);
  u.host=CreateWindowExW(WS_EX_CONTROLPARENT|WS_EX_COMPOSITED,wc.lpszClassName,L"安装",WS_CHILD|WS_CLIPCHILDREN,0,0,1,1,parent,0,u.module,0);
  for(int id: {INSTALL,SETTINGS,CLOSE,MINIMIZE,BROWSE,SAVESETTINGS,DONE,CANCELINSTALL,DETAILS}){add(id,L"Button",L"",BS_OWNERDRAW|WS_TABSTOP);SetWindowSubclass(u.controls[id],controlProc,id,0);}
  text(INSTALL,L"安装");text(SETTINGS,L"安装设置");text(CLOSE,L"关闭");text(MINIMIZE,L"最小化");text(BROWSE,L"浏览…");text(SAVESETTINGS,L"保存设置");text(DONE,L"完成");text(CANCELINSTALL,L"取消安装");
  text(DETAILS,L"查看详情");
  add(SCOPE,L"ComboBox",L"安装范围",CBS_DROPDOWNLIST|CBS_OWNERDRAWFIXED|CBS_HASSTRINGS|WS_TABSTOP|WS_VSCROLL);
  SendMessageW(u.controls[SCOPE],CB_ADDSTRING,0,(LPARAM)L"仅为我安装");SendMessageW(u.controls[SCOPE],CB_ADDSTRING,0,(LPARAM)L"为所有用户安装");SendMessageW(u.controls[SCOPE],CB_SETCURSEL,0,0);
  SendMessageW(u.controls[SCOPE],CB_SETITEMHEIGHT,(WPARAM)-1,px(42));SendMessageW(u.controls[SCOPE],CB_SETITEMHEIGHT,0,px(34));
  add(PATH,L"Edit",L"",ES_AUTOHSCROLL|WS_TABSTOP);SendMessageW(u.controls[PATH],EM_SETLIMITTEXT,150,0);
  add(DESKTOP,L"Button",L"创建桌面快捷方式",BS_AUTOCHECKBOX|WS_TABSTOP);add(STARTMENU,L"Button",L"创建开始菜单快捷方式",BS_AUTOCHECKBOX|WS_TABSTOP);
  add(PROGRESS,PROGRESS_CLASSW,L"安装进度",PBS_SMOOTH);SendMessageW(u.controls[PROGRESS],PBM_SETRANGE32,0,100);
  for(int id:{SCOPE,DESKTOP,STARTMENU,PROGRESS})SetWindowSubclass(u.controls[id],controlProc,id,0);
  if(u.accessibility){
    u.accessibility->SetHwndPropStr(u.controls[SCOPE],OBJID_CLIENT,CHILDID_SELF,PROPID_ACC_NAME,L"安装范围");
    u.accessibility->SetHwndPropStr(u.controls[PATH],OBJID_CLIENT,CHILDID_SELF,PROPID_ACC_NAME,L"安装位置");
    u.accessibility->SetHwndPropStr(u.controls[PROGRESS],OBJID_CLIENT,CHILDID_SELF,PROPID_ACC_NAME,L"安装进度");
  }
  for(int id=200;id<=213;id++)add(id,L"Static",L"",SS_OWNERDRAW);
  text(200,name);text(201,name);text(202,description);text(203,version);text(210,L"安装设置");text(211,L"安装范围");text(212,L"安装位置");text(213,L"升级沿用原位置。更换位置请先卸载，资料会保留。");
  theme();u.parentProc=(WNDPROC)SetWindowLongPtrW(parent,GWLP_WNDPROC,(LONG_PTR)parentProc);
  u.keyboard=SetWindowsHookExW(WH_GETMESSAGE,keyboardProc,0,GetCurrentThreadId());
  RECT r;GetClientRect(parent,&r);SetWindowPos(u.host,HWND_TOP,0,0,r.right,r.bottom,SWP_SHOWWINDOW);
  SetFocus(u.controls[INSTALL]);return 1;
}
API void __stdcall UI_SetOptions(int all,const wchar_t *path,int desktop,int start,int locked){
  SendMessageW(u.controls[SCOPE],CB_SETCURSEL,all,0);text(PATH,path);SendMessageW(u.controls[DESKTOP],BM_SETCHECK,desktop?BST_CHECKED:BST_UNCHECKED,0);SendMessageW(u.controls[STARTMENU],BM_SETCHECK,start?BST_CHECKED:BST_UNCHECKED,0);
  u.locked=locked!=0;SendMessageW(u.controls[PATH],EM_SETREADONLY,u.locked,0);EnableWindow(u.controls[BROWSE],!u.locked);summary();visibility();
}
API int __stdcall UI_GetScope(){return SendMessageW(u.controls[SCOPE],CB_GETCURSEL,0,0)==1;}
API int __stdcall UI_GetDesktop(){return SendMessageW(u.controls[DESKTOP],BM_GETCHECK,0,0)==BST_CHECKED;}
API int __stdcall UI_GetStart(){return SendMessageW(u.controls[STARTMENU],BM_GETCHECK,0,0)==BST_CHECKED;}
API int __stdcall UI_GetPath(wchar_t *value,int size){return GetWindowTextW(u.controls[PATH],value,size);}
API void __stdcall UI_SetNote(const wchar_t *value){text(213,value);}
API void __stdcall UI_AllowLaunch(int allowed){u.allowLaunch=allowed!=0;}
API void __stdcall UI_SetPhase(int phase,const wchar_t *detail){
  u.phase=phase;u.drawer=false;lstrcpynW(u.detail,detail,2048);
  if(phase==HOME||phase==PREPARING||phase==HANDOFF){u.progress=0;SendMessageW(u.controls[PROGRESS],PBM_SETPOS,0,0);}
  const wchar_t *heading=phase==PREPARING?L"正在准备":phase==INSTALLING?L"正在安装":phase==COMPLETE?L"安装完成":phase==HANDOFF?L"等待授权":phase==ROLLINGBACK?L"正在取消":L"安装未完成";
  text(206,heading);text(207,detail);text(INSTALL,phase==COMPLETE?L"打开应用":phase==ERRORSTATE?L"重试":L"安装");visibility();
  EnableWindow(u.controls[INSTALL],phase!=COMPLETE||u.allowLaunch);
  if(phase==HOME||phase==COMPLETE||phase==ERRORSTATE)SetFocus(u.controls[phase==COMPLETE&&!u.allowLaunch?DONE:INSTALL]);
  UpdateWindow(u.host);
}
API void __stdcall UI_SetProgress(int value){if(u.progress==value)return;u.progress=value;wchar_t s[80];swprintf(s,80,L"写入程序文件   %d%%",value);text(207,s);SendMessageW(u.controls[PROGRESS],PBM_SETPOS,value,0);InvalidateRect(u.controls[PROGRESS],0,FALSE);UpdateWindow(u.controls[207]);UpdateWindow(u.controls[PROGRESS]);}
#include "BackendBridge.h"
static int confirmation=-1;
static LRESULT CALLBACK confirmProc(HWND w,UINT m,WPARAM wp,LPARAM lp){
  if(m==WM_CLOSE){confirmation=0;return 0;}
  if(m==WM_COMMAND&&HIWORD(wp)==BN_CLICKED){confirmation=LOWORD(wp)==CANCELINSTALL?1:0;return 0;}
  if(m==WM_DRAWITEM){drawButton((DRAWITEMSTRUCT*)lp);return TRUE;}
  if(m==WM_CTLCOLORSTATIC){SetTextColor((HDC)wp,ink());SetBkColor((HDC)wp,surface());SetBkMode((HDC)wp,TRANSPARENT);return (LRESULT)GetStockObject(HOLLOW_BRUSH);}
  if(m==WM_ERASEBKGND)return 1;
  if(m==WM_PAINT){PAINTSTRUCT p;HDC dc=BeginPaint(w,&p);Graphics g(dc);g.Clear(color(surface()));g.SetSmoothingMode(SmoothingModeAntiAlias);RECT r;GetClientRect(w,&r);rounded(g,RectF(.5f,.5f,(REAL)r.right-1,(REAL)r.bottom-1),Color(0,0,0,0),color(muted()),12);EndPaint(w,&p);return 0;}
  return DefWindowProcW(w,m,wp,lp);
}
API int __stdcall UI_ConfirmCancel(){
  if(u.modal)return 0;HWND previousFocus=GetFocus();u.modal=true;confirmation=-1;
  WNDCLASSW wc={};wc.lpfnWndProc=confirmProc;wc.hInstance=u.module;wc.lpszClassName=L"CinnaInstallerConfirmation";wc.hCursor=LoadCursorW(0,IDC_ARROW);RegisterClassW(&wc);
  RECT p;GetWindowRect(u.parent,&p);int width=px(390),height=px(214);
  HWND dialog=CreateWindowExW(WS_EX_CONTROLPARENT,wc.lpszClassName,L"取消安装？",WS_POPUP|WS_CLIPCHILDREN,p.left+(p.right-p.left-width)/2,p.top+(p.bottom-p.top-height)/2,width,height,u.parent,0,u.module,0);
  SetWindowRgn(dialog,CreateRoundRectRgn(0,0,width+1,height+1,px(24),px(24)),FALSE);
  HWND title=CreateWindowExW(0,L"Static",L"取消安装？",WS_CHILD|WS_VISIBLE,px(24),px(22),px(342),px(28),dialog,0,u.module,0);SendMessageW(title,WM_SETFONT,(WPARAM)u.heading,FALSE);
  HWND body=CreateWindowExW(0,L"Static",L"将撤销本次程序文件变更，已保存的资料会保留。",WS_CHILD|WS_VISIBLE,px(24),px(65),px(342),px(48),dialog,0,u.module,0);SendMessageW(body,WM_SETFONT,(WPARAM)u.font,FALSE);
  HWND cancel=CreateWindowExW(0,L"Button",L"取消安装",WS_CHILD|WS_VISIBLE|WS_TABSTOP|BS_OWNERDRAW,px(24),px(142),px(164),px(44),dialog,(HMENU)CANCELINSTALL,u.module,0);
  HWND resume=CreateWindowExW(0,L"Button",L"继续安装",WS_CHILD|WS_VISIBLE|WS_TABSTOP|BS_OWNERDRAW,px(202),px(142),px(164),px(44),dialog,(HMENU)INSTALL,u.module,0);
  for(HWND b:{cancel,resume}){SendMessageW(b,WM_SETFONT,(WPARAM)u.font,FALSE);SetWindowSubclass(b,controlProc,GetDlgCtrlID(b),0);}
  EnableWindow(u.parent,FALSE);ShowWindow(dialog,SW_SHOW);SetFocus(resume);
  MSG m;while(confirmation<0){BOOL ok=GetMessageW(&m,0,0,0);if(ok<=0){confirmation=0;if(!ok)PostQuitMessage((int)m.wParam);break;}
    if(m.message==WM_KEYDOWN&&(m.wParam==VK_TAB||m.wParam==VK_SPACE)){u.keyboardFocus=true;InvalidateRect(GetFocus(),0,FALSE);}
    if(m.message==WM_KEYDOWN&&m.wParam==VK_ESCAPE){confirmation=0;continue;}
    if(m.message==WM_KEYDOWN&&m.wParam==VK_RETURN){confirmation=GetFocus()==cancel?1:0;continue;}
    if(!IsDialogMessageW(dialog,&m)){TranslateMessage(&m);DispatchMessageW(&m);}
  }
  DestroyWindow(dialog);EnableWindow(u.parent,TRUE);SetActiveWindow(u.parent);if(IsWindow(previousFocus)&&IsWindowEnabled(previousFocus))SetFocus(previousFocus);u.modal=false;u.hot=0;refresh();return confirmation;
}
API void __stdcall UI_Pump(){MSG m;int count=0;while(count++<30&&PeekMessageW(&m,0,0,0,PM_REMOVE)){if(m.message==WM_QUIT){PostQuitMessage((int)m.wParam);break;}TranslateMessage(&m);DispatchMessageW(&m);}}
API int __stdcall UI_Handoff(const wchar_t *executable,const wchar_t *arguments){
  if(u.child)return 0;swprintf(u.handoffName,160,L"Local\\Cinna.Installer.Handoff.%lu.%llu",GetCurrentProcessId(),GetTickCount64());u.ready=CreateEventW(0,TRUE,FALSE,u.handoffName);
  if(!u.ready)return 0;wchar_t command[4096];swprintf(command,4096,L"\"%ls\" %ls /CINNAHANDOFF=\"%ls\"",executable,arguments,u.handoffName);
  STARTUPINFOW si={sizeof(si)};PROCESS_INFORMATION pi={};
  if(!CreateProcessW(executable,command,0,0,FALSE,0,0,0,&si,&pi)){disposeChild();return 0;}
  CloseHandle(pi.hThread);u.child=pi.hProcess;u.childReady=false;u.exitedAt=0;UI_SetPhase(HANDOFF,L"请在 Windows 提示中确认安装权限。");SetTimer(u.host,4,200,0);return 1;
}
API void __stdcall UI_Ready(const wchar_t *name){
  if(wcsncmp(name,L"Local\\Cinna.Installer.Handoff.",29)!=0)return;HANDLE event=OpenEventW(EVENT_MODIFY_STATE,FALSE,name);if(event){SetEvent(event);CloseHandle(event);}
}
API int __stdcall UI_GetHandoffResult(){return (int)u.childExitCode;}
API void __stdcall UI_AutoStart(){PostMessageW(u.host,WM_COMMAND,INSTALL,0);}
API void __stdcall UI_AutoFinish(){PostMessageW(u.host,WM_COMMAND,DONE,0);}
API void __stdcall UI_AutoCancel(){PostMessageW(u.host,WM_COMMAND,CANCELINSTALL,0);}
API void __stdcall UI_Destroy(){
  stopProbe();
  if(backend.active&&backend.status)InterlockedExchange(&backend.status->cancel,1);
  closeBackendTransport();
  if(u.parentProc){SetWindowLongPtrW(u.parent,GWLP_WNDPROC,(LONG_PTR)u.parentProc);u.parentProc=0;}
  if(u.keyboard)UnhookWindowsHookEx(u.keyboard);u.keyboard=0;
  disposeChild();if(u.host)DestroyWindow(u.host);u.host=0;
  if(u.memory){SelectObject(u.memory,u.originalBitmap);DeleteDC(u.memory);}if(u.backing)DeleteObject(u.backing);
  if(u.sceneMemory){SelectObject(u.sceneMemory,u.sceneOriginal);DeleteDC(u.sceneMemory);}if(u.sceneBacking)DeleteObject(u.sceneBacking);
  DeleteObject(u.font);DeleteObject(u.small);DeleteObject(u.title);DeleteObject(u.heading);DeleteObject(u.fieldBrush);
  if(u.accessibility)u.accessibility->Release();u.accessibility=0;
  if(u.comInitialized)CoUninitialize();
  delete u.day;delete u.night;delete u.icon;if(u.gdip)GdiplusShutdown(u.gdip);
  for(auto &p:applied)p=Placement{};
}
