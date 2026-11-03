#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <windowsx.h>
#include <commctrl.h>
#include <dwmapi.h>
#include <stdbool.h>
#include <stdint.h>
#include <string.h>

/* Stable Node-API opaque types. Resolve the host's C exports rather than linking
   a specific Node/Electron executable or a separate runtime into this DLL. */
typedef struct napi_env__* napi_env;
typedef struct napi_value__* napi_value;
typedef struct napi_callback_info__* napi_callback_info;
typedef napi_value (*napi_callback)(napi_env, napi_callback_info);
static int (*get_info)(napi_env, napi_callback_info, size_t*, napi_value*, napi_value*, void**);
static int (*is_buffer)(napi_env, napi_value, bool*);
static int (*buffer_info)(napi_env, napi_value, void**, size_t*);
static int (*throw_error)(napi_env, const char*, const char*);
static int (*get_boolean)(napi_env, bool, napi_value*);
static int (*create_function)(napi_env, const char*, size_t, napi_callback, void*, napi_value*);
static int (*set_property)(napi_env, napi_value, const char*, napi_value);
static int (*get_int32)(napi_env, napi_value, int32_t*);

static const UINT_PTR subclass_id = 0x4e545746;
static const UINT caption_message = WM_APP + 0x443;
static const wchar_t* state_property = L"NetworkToolbox.FrameState";
typedef struct { int height, width, hovered, pressed; } FrameState;

static int caption_hit(HWND hwnd, FrameState* state, POINT point) {
  RECT client;
  POINT origin = {0, 0};
  GetClientRect(hwnd, &client);
  ClientToScreen(hwnd, &origin);
  int width = MulDiv(state->width, GetDpiForWindow(hwnd), 96);
  int height = MulDiv(state->height, GetDpiForWindow(hwnd), 96);
  int right = origin.x + client.right;
  if (point.y < origin.y || point.y >= origin.y + height || point.x < right - 3 * width || point.x >= right) return 0;
  return point.x < right - 2 * width ? HTMINBUTTON : point.x < right - width ? HTMAXBUTTON : HTCLOSE;
}
static void caption_state(HWND hwnd, FrameState* state, int hovered, int pressed) {
  if (state->hovered == hovered && state->pressed == pressed) return;
  state->hovered = hovered;
  state->pressed = pressed;
  PostMessageW(hwnd, caption_message, (WPARAM)(hovered | (pressed << 8)), 0);
}

/* The reference rectangle is the physical DWM visible frame, not the invisible
   GetWindowRect shadow/input insets. DPI comes from this HWND on every query. */
static LRESULT CALLBACK frame_proc(HWND hwnd, UINT message, WPARAM wparam,
                                   LPARAM lparam, UINT_PTR id, DWORD_PTR data) {
  FrameState* state = (FrameState*)data;
  if (message == WM_NCDESTROY) {
    RemoveWindowSubclass(hwnd, frame_proc, id);
    RemovePropW(hwnd, state_property);
    HeapFree(GetProcessHeap(), 0, state);
    return DefSubclassProc(hwnd, message, wparam, lparam);
  }
  if (message == WM_NCLBUTTONDOWN && (wparam == HTMINBUTTON || wparam == HTMAXBUTTON || wparam == HTCLOSE)) {
    caption_state(hwnd, state, (int)wparam, (int)wparam);
    SetCapture(hwnd);
    return 0;
  }
  if (message == WM_MOUSEMOVE && state->pressed && GetCapture() == hwnd) {
    POINT point = { GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam) };
    ClientToScreen(hwnd, &point);
    caption_state(hwnd, state, caption_hit(hwnd, state, point), state->pressed);
    return 0;
  }
  if (message == WM_LBUTTONUP && state->pressed) {
    POINT point = { GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam) };
    ClientToScreen(hwnd, &point);
    int pressed = state->pressed;
    int hit = caption_hit(hwnd, state, point);
    caption_state(hwnd, state, hit, 0);
    ReleaseCapture();
    if (hit == pressed) PostMessageW(hwnd, WM_SYSCOMMAND,
      hit == HTMINBUTTON ? SC_MINIMIZE : hit == HTMAXBUTTON ? (IsZoomed(hwnd) ? SC_RESTORE : SC_MAXIMIZE) : SC_CLOSE, 0);
    return 0;
  }
  if (message == WM_CAPTURECHANGED && state->pressed) caption_state(hwnd, state, 0, 0);
  if (message == WM_NCMOUSEMOVE) {
    int hit = (wparam == HTMINBUTTON || wparam == HTMAXBUTTON || wparam == HTCLOSE) ? (int)wparam : 0;
    caption_state(hwnd, state, hit, state->pressed);
    TRACKMOUSEEVENT track = { sizeof(track), TME_LEAVE | TME_NONCLIENT, hwnd, 0 };
    TrackMouseEvent(&track);
    LRESULT dwm_result;
    DwmDefWindowProc(hwnd, message, wparam, lparam, &dwm_result);
  }
  if (message == WM_NCMOUSELEAVE && !state->pressed) caption_state(hwnd, state, 0, 0);
  LRESULT original = DefSubclassProc(hwnd, message, wparam, lparam);
  if (message != WM_NCHITTEST) return original;
  POINT point = {GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam)};
  int caption = caption_hit(hwnd, state, point);
  if (IsZoomed(hwnd)) return caption ? caption : original;
  if (!(GetWindowLongPtrW(hwnd, GWL_STYLE) & WS_THICKFRAME)) return original;

  RECT rect;
  if (FAILED(DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &rect, sizeof(rect))) &&
      !GetWindowRect(hwnd, &rect)) return original;
  int x = GET_X_LPARAM(lparam), y = GET_Y_LPARAM(lparam);
  if (x < rect.left || x >= rect.right || y < rect.top || y >= rect.bottom) return original;
  UINT dpi = GetDpiForWindow(hwnd);
  int padding = GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
  int horizontal = GetSystemMetricsForDpi(SM_CXSIZEFRAME, dpi) + padding;
  int vertical = GetSystemMetricsForDpi(SM_CYSIZEFRAME, dpi) + padding;
  int corner = MulDiv(16, dpi, 96);
  bool left = x < rect.left + horizontal, right = x >= rect.right - horizontal;
  bool top = y < rect.top + vertical, bottom = y >= rect.bottom - vertical;
  bool corner_left = x < rect.left + corner, corner_right = x >= rect.right - corner;
  bool corner_top = y < rect.top + corner, corner_bottom = y >= rect.bottom - corner;
  if ((top && corner_left) || (left && corner_top)) return HTTOPLEFT;
  if ((top && corner_right) || (right && corner_top)) return HTTOPRIGHT;
  if ((bottom && corner_left) || (left && corner_bottom)) return HTBOTTOMLEFT;
  if ((bottom && corner_right) || (right && corner_bottom)) return HTBOTTOMRIGHT;
  if (left) return HTLEFT;
  if (right) return HTRIGHT;
  if (top) return HTTOP;
  if (bottom) return HTBOTTOM;
  /* Keep real non-client caption hit codes for Windows Snap Layout. The web
     layer draws the agreed controls; Windows owns resizing, dragging and window
     commands. This module never changes sizes or injects mouse input. */
  return caption ? caption : original;
}

static napi_value attach(napi_env env, napi_callback_info info) {
  size_t argc = 3, size = 0;
  napi_value arguments[3], result;
  void* bytes = NULL;
  bool buffer = false;
  HWND hwnd = NULL;
  DWORD process_id = 0;
  int32_t height, width;
  if (get_info(env, info, &argc, arguments, NULL, NULL) != 0 || argc != 3 ||
      is_buffer(env, arguments[0], &buffer) != 0 || !buffer ||
      buffer_info(env, arguments[0], &bytes, &size) != 0 || size != sizeof(hwnd)) {
    throw_error(env, NULL, "A native window handle buffer is required");
    return NULL;
  }
  if (get_int32(env, arguments[1], &height) != 0 || get_int32(env, arguments[2], &width) != 0 ||
      height < 32 || height > 64 || width < 32 || width > 64) {
    throw_error(env, NULL, "Invalid caption control dimensions");
    return NULL;
  }
  memcpy(&hwnd, bytes, sizeof(hwnd));
  DWORD thread = GetWindowThreadProcessId(hwnd, &process_id);
  if (!IsWindow(hwnd) || process_id != GetCurrentProcessId() || thread != GetCurrentThreadId()) {
    throw_error(env, NULL, "Only the host's own UI-thread window can be attached");
    return NULL;
  }
  if (GetPropW(hwnd, state_property)) {
    get_boolean(env, true, &result);
    return result;
  }
  FrameState* state = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(FrameState));
  if (!state) { throw_error(env, NULL, "Unable to allocate frame state"); return NULL; }
  state->height = height; state->width = width;
  if (!SetWindowSubclass(hwnd, frame_proc, subclass_id, (DWORD_PTR)state) ||
      !SetPropW(hwnd, state_property, (HANDLE)state)) {
    RemoveWindowSubclass(hwnd, frame_proc, subclass_id);
    HeapFree(GetProcessHeap(), 0, state);
    throw_error(env, NULL, "Unable to attach native resize hit testing");
    return NULL;
  }
  get_boolean(env, true, &result);
  return result;
}

__declspec(dllexport) uint32_t node_api_module_get_api_version_v1(void) { return 8; }
__declspec(dllexport) napi_value napi_register_module_v1(napi_env env, napi_value exports) {
  HMODULE host = GetModuleHandleW(NULL);
  get_info = (void*)GetProcAddress(host, "napi_get_cb_info");
  is_buffer = (void*)GetProcAddress(host, "napi_is_buffer");
  buffer_info = (void*)GetProcAddress(host, "napi_get_buffer_info");
  throw_error = (void*)GetProcAddress(host, "napi_throw_error");
  get_boolean = (void*)GetProcAddress(host, "napi_get_boolean");
  create_function = (void*)GetProcAddress(host, "napi_create_function");
  set_property = (void*)GetProcAddress(host, "napi_set_named_property");
  get_int32 = (void*)GetProcAddress(host, "napi_get_value_int32");
  if (!get_info || !is_buffer || !buffer_info || !throw_error || !get_boolean || !create_function || !set_property || !get_int32)
    return NULL;
  napi_value function;
  if (create_function(env, "attach", 6, attach, NULL, &function) != 0 ||
      set_property(env, exports, "attach", function) != 0) return NULL;
  return exports;
}
