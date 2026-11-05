// leaf_overlay.cpp  —  native "Hello, world!" overlay for Leaf it Alone.
//
// Why this design (see MODLOG.md): the game build is PROTECTED (encrypted
// global-metadata.dat + all GameAssembly.dll il2cpp exports renamed/virtualized),
// so BepInEx/MelonLoader bootstrap but never initialise the managed runtime and no
// general il2cpp mod API can be built statically. The user's real goal is a cosmetic
// on-screen overlay, so we sidestep il2cpp entirely:
//
//   * We ship a proxy `version.dll` (UnityPlayer imports exactly 6 version.* fns on
//     x64 -> trivial to forward). The OS auto-loads our DLL into the game process.
//   * DllMain spawns a thread that finds the game's main window and paints a
//     topmost, click-through, color-keyed overlay drawing "Hello, world!".
//   * Game runs in Unity FullScreenMode=FullScreenWindow (borderless), so a topmost
//     layered window composites correctly over it regardless of DX11/DX12/Vulkan.
//
// Nothing here touches game memory or the encrypted metadata. It is purely additive.

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef _CRT_SECURE_NO_WARNINGS
#define _CRT_SECURE_NO_WARNINGS
#endif
#include <windows.h>
#include <psapi.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "psapi.lib")

static HMODULE g_self = nullptr;
static FILE*   g_log = nullptr;
static const wchar_t* kOverlayClass = L"LeafItAloneOverlayWnd";
static HWND    g_hwnd = nullptr;      // our overlay window
static HFONT   g_font = nullptr;
static HFONT   g_fontSmall = nullptr;

// HUD state
static bool      g_visible = true;
static bool      g_prevToggle = false;
static ULONGLONG g_startMs = 0;

// ---- configuration (loaded from LeafOverlay.ini next to the DLL) ----
static int      g_toggleVk    = VK_F9;
static wchar_t  g_toggleName[16] = L"F9";
static int      g_corner      = 0;      // 0=TopLeft 1=TopRight 2=BottomLeft 3=BottomRight
static int      g_margin      = 22;     // panel offset from the chosen corner
static int      g_alpha       = 235;    // panel opacity 1..255
static bool     g_showTitle   = true;
static bool     g_showClock   = true;
static bool     g_showUptime  = true;
static bool     g_showMetrics = true;
static wchar_t  g_title[128]    = L"Hello, world!";
static wchar_t  g_subtitle[128] = L"Leaf it Alone - native overlay";

// ---- live in-game runtime metrics (real values, read from THIS process) ----
// Our DLL runs inside the game process, so GetCurrentProcess() IS the game:
// its working set (RAM) and its consumed CPU time are genuine live game values.
static wchar_t  g_metrics[128] = L"";
static ULONGLONG g_lastMetricMs = 0;
static ULARGE_INTEGER g_prevKern{}, g_prevUser{};
static LARGE_INTEGER g_prevQpc{};
static LARGE_INTEGER g_qpcFreq{};

// Unique color key: every pixel painted this color becomes fully transparent,
// so the game shows through everywhere except our HUD panel.
static const COLORREF kColorKey = RGB(255, 0, 255);

static void OLog(const char* fmt, ...) {
    if (!g_log) return;
    SYSTEMTIME st; GetLocalTime(&st);
    fprintf(g_log, "[%02d:%02d:%02d.%03d] ", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
    va_list ap; va_start(ap, fmt); vfprintf(g_log, fmt, ap); va_end(ap);
    fprintf(g_log, "\n"); fflush(g_log);
}

static void OpenLog() {
    char dir[MAX_PATH]; char full[MAX_PATH];
    GetModuleFileNameA(g_self, dir, MAX_PATH);
    char* slash = strrchr(dir, '\\');
    if (slash) *slash = '\0';
    _snprintf_s(full, MAX_PATH, _TRUNCATE, "%s\\LeafOverlay.log", dir);
    g_log = fopen(full, "a");
}

// ---------------------------------------------------------------------------
// Configuration (LeafOverlay.ini, section [overlay]). Everything has a sane
// default, so the overlay works even with no ini present.
// ---------------------------------------------------------------------------
static int ParseKey(const wchar_t* s) {
    if (_wcsnicmp(s, L"F", 1) == 0) { int n = _wtoi(s + 1); if (n >= 1 && n <= 24) return 0x70 + (n - 1); }
    if (s[0] && !s[1]) { wchar_t c = towupper(s[0]); if ((c >= L'A' && c <= L'Z') || (c >= L'0' && c <= L'9')) return c; }
    if (_wcsicmp(s, L"INSERT") == 0) return VK_INSERT;
    if (_wcsicmp(s, L"HOME")    == 0) return VK_HOME;
    if (_wcsicmp(s, L"END")     == 0) return VK_END;
    if (_wcsicmp(s, L"SPACE")   == 0) return VK_SPACE;
    if (_wcsicmp(s, L"TAB")     == 0) return VK_TAB;
    return 0;
}
static bool ParseBool(const wchar_t* s, bool def) {
    if (!s[0]) return def;
    if (_wcsicmp(s, L"true") == 0 || _wcsicmp(s, L"1") == 0 || _wcsicmp(s, L"yes") == 0 || _wcsicmp(s, L"on") == 0) return true;
    if (_wcsicmp(s, L"false")== 0 || _wcsicmp(s, L"0") == 0 || _wcsicmp(s, L"no")  == 0 || _wcsicmp(s, L"off")== 0) return false;
    return def;
}
static int ParseCorner(const wchar_t* s) {
    if (_wcsicmp(s, L"TopRight")    == 0) return 1;
    if (_wcsicmp(s, L"BottomLeft")  == 0) return 2;
    if (_wcsicmp(s, L"BottomRight") == 0) return 3;
    return 0;
}

static void LoadConfig() {
    wchar_t dir[MAX_PATH]; wchar_t ini[MAX_PATH];
    GetModuleFileNameW(g_self, dir, MAX_PATH);
    wchar_t* slash = wcsrchr(dir, L'\\'); if (slash) *slash = 0;
    _snwprintf_s(ini, MAX_PATH, _TRUNCATE, L"%s\\LeafOverlay.ini", dir);
    if (GetFileAttributesW(ini) == INVALID_FILE_ATTRIBUTES) { OLog("no LeafOverlay.ini; using defaults"); return; }

    wchar_t buf[128];
    GetPrivateProfileStringW(L"overlay", L"ToggleKey", L"F9", buf, 128, ini);
    int vk = ParseKey(buf); if (vk) { g_toggleVk = vk; lstrcpynW(g_toggleName, buf, 16); }
    GetPrivateProfileStringW(L"overlay", L"Corner", L"TopLeft", buf, 128, ini); g_corner = ParseCorner(buf);
    g_margin = GetPrivateProfileIntW(L"overlay", L"Margin", 22, ini);
    g_alpha  = GetPrivateProfileIntW(L"overlay", L"Alpha", 235, ini);
    if (g_alpha < 1) g_alpha = 1; if (g_alpha > 255) g_alpha = 255;
    GetPrivateProfileStringW(L"overlay", L"ShowTitle",  L"true", buf, 128, ini); g_showTitle  = ParseBool(buf, true);
    GetPrivateProfileStringW(L"overlay", L"ShowClock",  L"true", buf, 128, ini); g_showClock  = ParseBool(buf, true);
    GetPrivateProfileStringW(L"overlay", L"ShowUptime", L"true", buf, 128, ini); g_showUptime = ParseBool(buf, true);
    GetPrivateProfileStringW(L"overlay", L"ShowMetrics", L"true", buf, 128, ini); g_showMetrics = ParseBool(buf, true);
    GetPrivateProfileStringW(L"overlay", L"Title",    L"Hello, world!", g_title, 128, ini);
    GetPrivateProfileStringW(L"overlay", L"Subtitle", L"Leaf it Alone - native overlay", g_subtitle, 128, ini);
    OLog("config loaded: key=%ls corner=%d margin=%d alpha=%d title='%ls'",
         g_toggleName, g_corner, g_margin, g_alpha, g_title);
}

// ---------------------------------------------------------------------------
// Live in-game metrics. Reads the game process's own working set (RAM) and
// instantaneous CPU utilisation (delta of GetProcessTimes over a wall-clock
// window). Called ~20x/s from WM_TIMER but only resamples every ~1000ms.
// These are genuine values from the running game, obtained through safe,
// documented read-only APIs (no memory poking, no managed runtime).
// ---------------------------------------------------------------------------
static void InitMetrics() {
    QueryPerformanceFrequency(&g_qpcFreq);
    QueryPerformanceCounter(&g_prevQpc);
    FILETIME c, e, k, u;
    if (GetProcessTimes(GetCurrentProcess(), &c, &e, &k, &u)) {
        g_prevKern.LowPart = k.dwLowDateTime; g_prevKern.HighPart = k.dwHighDateTime;
        g_prevUser.LowPart = u.dwLowDateTime; g_prevUser.HighPart = u.dwHighDateTime;
    }
    g_lastMetricMs = GetTickCount64();
}

static void UpdateMetrics() {
    if (!g_showMetrics) return;
    ULONGLONG now = GetTickCount64();
    if (now - g_lastMetricMs < 1000) return;         // resample once per second
    g_lastMetricMs = now;

    // RAM: working set of the game process.
    PROCESS_MEMORY_COUNTERS_EX pmc{}; pmc.cb = sizeof(pmc);
    double ramMB = 0.0;
    if (K32GetProcessMemoryInfo(GetCurrentProcess(), (PROCESS_MEMORY_COUNTERS*)&pmc, sizeof(pmc)))
        ramMB = (double)pmc.WorkingSetSize / (1024.0 * 1024.0);

    // CPU%: (kernel+user 100ns ticks used) / (wall 100ns ticks elapsed) * 100.
    FILETIME c, e, k, u;
    double cpuPct = 0.0;
    LARGE_INTEGER qpc; QueryPerformanceCounter(&qpc);
    if (GetProcessTimes(GetCurrentProcess(), &c, &e, &k, &u)) {
        ULARGE_INTEGER kn, us;
        kn.LowPart = k.dwLowDateTime; kn.HighPart = k.dwHighDateTime;
        us.LowPart = u.dwLowDateTime; us.HighPart = u.dwHighDateTime;
        double procTicks = (double)(kn.QuadPart - g_prevKern.QuadPart)
                         + (double)(us.QuadPart - g_prevUser.QuadPart);   // in 100ns
        double wallTicks = (double)(qpc.QuadPart - g_prevQpc.QuadPart) * 1e7
                         / (double)g_qpcFreq.QuadPart;                     // to 100ns
        g_prevKern = kn; g_prevUser = us;
        if (wallTicks > 0) cpuPct = procTicks / wallTicks * 100.0;         // may exceed 100 on multicore
    }
    g_prevQpc = qpc;

    swprintf(g_metrics, 128, L"RAM %.0f MB    CPU %.0f%%", ramMB, cpuPct);
}

// ---------------------------------------------------------------------------
// version.dll forwarding (the 6 functions UnityPlayer.dll imports). We resolve
// the real System32 version.dll once and trampolines forward to it, so nothing
// that legitimately uses the version APIs breaks.
// ---------------------------------------------------------------------------
static HMODULE  g_realVer = nullptr;
static SRWLOCK  g_verLock = SRWLOCK_INIT;

static FARPROC RealVer(const char* name) {
    AcquireSRWLockShared(&g_verLock);
    HMODULE m = g_realVer;
    ReleaseSRWLockShared(&g_verLock);
    if (!m) {
        AcquireSRWLockExclusive(&g_verLock);
        if (!g_realVer) {
            char path[MAX_PATH];
            GetSystemDirectoryA(path, MAX_PATH);
            lstrcatA(path, "\\version.dll");
            g_realVer = LoadLibraryExA(path, nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        }
        m = g_realVer;
        ReleaseSRWLockExclusive(&g_verLock);
    }
    return m ? GetProcAddress(m, name) : nullptr;
}

// Implemented under internal ov_* names; the public version.dll names are
// attached via version.def (the SDK winver.h already declares the public names
// as dllimport, which would clash with dllexport definitions).
extern "C" {

DWORD WINAPI ov_GetFileVersionInfoSizeA(LPCSTR f, LPDWORD h) {
    auto p = (DWORD(WINAPI*)(LPCSTR, LPDWORD))RealVer("GetFileVersionInfoSizeA");
    return p ? p(f, h) : 0;
}
DWORD WINAPI ov_GetFileVersionInfoSizeW(LPCWSTR f, LPDWORD h) {
    auto p = (DWORD(WINAPI*)(LPCWSTR, LPDWORD))RealVer("GetFileVersionInfoSizeW");
    return p ? p(f, h) : 0;
}
BOOL WINAPI ov_GetFileVersionInfoA(LPCSTR f, DWORD h, DWORD len, LPVOID data) {
    auto p = (BOOL(WINAPI*)(LPCSTR, DWORD, DWORD, LPVOID))RealVer("GetFileVersionInfoA");
    return p ? p(f, h, len, data) : FALSE;
}
BOOL WINAPI ov_GetFileVersionInfoW(LPCWSTR f, DWORD h, DWORD len, LPVOID data) {
    auto p = (BOOL(WINAPI*)(LPCWSTR, DWORD, DWORD, LPVOID))RealVer("GetFileVersionInfoW");
    return p ? p(f, h, len, data) : FALSE;
}
BOOL WINAPI ov_VerQueryValueA(LPCSTR blk, LPCSTR sub, LPVOID* val, PUINT len) {
    auto p = (BOOL(WINAPI*)(LPCSTR, LPCSTR, LPVOID*, PUINT))RealVer("VerQueryValueA");
    return p ? p(blk, sub, val, len) : FALSE;
}
BOOL WINAPI ov_VerQueryValueW(LPCWSTR blk, LPCWSTR sub, LPVOID* val, PUINT len) {
    auto p = (BOOL(WINAPI*)(LPCWSTR, LPCWSTR, LPVOID*, PUINT))RealVer("VerQueryValueW");
    return p ? p(blk, sub, val, len) : FALSE;
}

} // extern "C"

// ---------------------------------------------------------------------------
// Game-window discovery. The game's render window is the largest visible
// top-level window in our process. The real Unity 6 borderless window has NO
// title bar text, so we must NOT filter on title; instead we exclude our own
// overlay (a WS_EX_TOOLWINDOW) and require a substantial size.
// ---------------------------------------------------------------------------
struct FindCtx { DWORD pid; HWND best; LONG bestArea; };

static BOOL CALLBACK EnumProc(HWND h, LPARAM lp) {
    FindCtx* c = (FindCtx*)lp;
    DWORD wp = 0;
    GetWindowThreadProcessId(h, &wp);
    if (wp != c->pid) return TRUE;
    if (h == g_hwnd) return TRUE;                       // never match our own overlay
    if (!IsWindowVisible(h)) return TRUE;
    LONG_PTR ex = GetWindowLongPtrW(h, GWL_EXSTYLE);
    if (ex & WS_EX_TOOLWINDOW) return TRUE;             // our overlay is a tool window
    if (ex & WS_EX_LAYERED) return TRUE;                // only our overlay is layered
    RECT r; GetWindowRect(h, &r);
    if ((r.right - r.left) < 320 || (r.bottom - r.top) < 240) return TRUE;
    LONG area = (r.right - r.left) * (r.bottom - r.top);
    if (area > c->bestArea) { c->bestArea = area; c->best = h; }
    return TRUE;
}

static HWND FindGameWindow() {
    FindCtx c{}; c.pid = GetCurrentProcessId();
    EnumWindows(EnumProc, (LPARAM)&c);
    return c.best;
}

// ---------------------------------------------------------------------------
// Painting + following the game window.
// ---------------------------------------------------------------------------
static void PaintOverlay(HWND h) {
    PAINTSTRUCT ps;
    HDC dc = BeginPaint(h, &ps);
    RECT cr; GetClientRect(h, &cr);
    int cw = cr.right - cr.left, ch = cr.bottom - cr.top;

    // Full-window pass of the color key -> everything is transparent by default.
    {
        HBRUSH key = CreateSolidBrush(kColorKey);
        FillRect(dc, &cr, key);
        DeleteObject(key);
    }
    if (!g_visible) { EndPaint(h, &ps); return; }

    SetBkMode(dc, TRANSPARENT);

    // Live strings.
    SYSTEMTIME now; GetLocalTime(&now);
    int upS = (int)((GetTickCount64() - g_startMs) / 1000);
    wchar_t lineClock[64], lineUp[64], lineHint[64], lineStat[128];
    swprintf(lineClock, 64, L"%02d:%02d:%02d", now.wHour, now.wMinute, now.wSecond);
    swprintf(lineUp,    64, L"uptime %02d:%02d:%02d", upS / 3600, (upS / 60) % 60, upS % 60);
    swprintf(lineHint,  64, L"[%ls]  show / hide", g_toggleName);
    lineStat[0] = 0;
    if (g_showClock && g_showUptime) swprintf(lineStat, 128, L"%ls   %ls", lineClock, lineUp);
    else if (g_showClock)            wcscpy_s(lineStat, 128, lineClock);
    else if (g_showUptime)           wcscpy_s(lineStat, 128, lineUp);

    // Assemble the small-font rows with their colors.
    struct Line { const wchar_t* t; COLORREF c; };
    Line rows[5]; int n = 0;
    if (g_subtitle[0])                       rows[n++] = { g_subtitle, RGB(150, 205, 155) };
    if (lineStat[0])                          rows[n++] = { lineStat,   RGB(225, 230, 235) };
    if (g_showMetrics && g_metrics[0])        rows[n++] = { g_metrics,  RGB(120, 200, 130) };
    rows[n++] = { lineHint, RGB(130, 140, 150) };

    // Measure to size the panel.
    const int padX = 22, padY = 16, gap = 6, rowSmall = 24;
    int contentW = 0, rowsH = 0;
    SIZE ts{}; int titleCy = 0;
    bool showTitle = g_showTitle && g_title[0];
    if (showTitle) {
        HFONT old = (HFONT)SelectObject(dc, g_font);
        GetTextExtentPoint32W(dc, g_title, (int)wcslen(g_title), &ts);
        SelectObject(dc, old);
        if (ts.cx > contentW) contentW = ts.cx;
        titleCy = ts.cy; rowsH += ts.cy + gap;
    }
    SelectObject(dc, g_fontSmall);
    for (int i = 0; i < n; ++i) {
        SIZE s; GetTextExtentPoint32W(dc, rows[i].t, (int)wcslen(rows[i].t), &s);
        if (s.cx > contentW) contentW = s.cx;
        rowsH += rowSmall + gap;
    }

    int panelW = contentW + padX * 2;
    int panelH = rowsH - gap + padY * 2;
    if (panelH < 0) panelH = 0;

    // Corner-anchored placement.
    int px, py;
    switch (g_corner) {
        case 1: px = cw - panelW - g_margin; py = g_margin; break;
        case 2: px = g_margin;               py = ch - panelH - g_margin; break;
        case 3: px = cw - panelW - g_margin; py = ch - panelH - g_margin; break;
        default: px = g_margin;              py = g_margin; break;
    }
    if (px < 0) px = 0; if (py < 0) py = 0;
    RECT panel = { px, py, px + panelW, py + panelH };

    // Panel body (dark) + accent border.
    HBRUSH body = CreateSolidBrush(RGB(18, 22, 26));
    HPEN   edge = CreatePen(PS_SOLID, 2, RGB(120, 200, 130));
    HBRUSH ob = (HBRUSH)SelectObject(dc, body);
    HPEN   op = (HPEN)SelectObject(dc, edge);
    RoundRect(dc, panel.left, panel.top, panel.right, panel.bottom, 26, 26);
    SelectObject(dc, ob); SelectObject(dc, op);
    DeleteObject(body); DeleteObject(edge);

    // Left accent bar.
    HBRUSH accent = CreateSolidBrush(RGB(120, 200, 130));
    RECT bar = { panel.left, panel.top, panel.left + 5, panel.bottom };
    FillRect(dc, &bar, accent);
    DeleteObject(accent);

    int textX = panel.left + padX + 5;
    int y = panel.top + padY;

    // Title (with drop shadow).
    if (showTitle) {
        SelectObject(dc, g_font);
        RECT sh = { textX + 2, y + 2, panel.right, panel.bottom };
        SetTextColor(dc, RGB(0, 0, 0));
        DrawTextW(dc, g_title, -1, &sh, DT_LEFT | DT_TOP | DT_SINGLELINE | DT_NOPREFIX);
        RECT tr = { textX, y, panel.right, panel.bottom };
        SetTextColor(dc, RGB(240, 246, 240));
        DrawTextW(dc, g_title, -1, &tr, DT_LEFT | DT_TOP | DT_SINGLELINE | DT_NOPREFIX);
        y += titleCy + gap;
    }

    // Small rows.
    SelectObject(dc, g_fontSmall);
    for (int i = 0; i < n; ++i) {
        RECT rr = { textX, y, panel.right, panel.bottom };
        SetTextColor(dc, rows[i].c);
        DrawTextW(dc, rows[i].t, -1, &rr, DT_LEFT | DT_TOP | DT_SINGLELINE | DT_NOPREFIX);
        y += rowSmall + gap;
    }

    EndPaint(h, &ps);
}

static void FollowGameWindow() {
    static HWND game = nullptr;
    static ULONGLONG lastLog = 0;
    HWND g = FindGameWindow();
    if (g) game = g;
    if (!game || !IsWindow(game)) return;

    RECT r; GetWindowRect(game, &r);
    SetWindowPos(g_hwnd, HWND_TOPMOST, r.left, r.top,
                 r.right - r.left, r.bottom - r.top,
                 SWP_NOACTIVATE | SWP_SHOWWINDOW);

    if (lastLog == 0) {
        OLog("attached to game window hwnd=%p rect=%ldx%ld @(%ld,%ld)",
             (void*)game, r.right - r.left, r.bottom - r.top, r.left, r.top);
        lastLog = 1;
    }
}

static LRESULT CALLBACK WndProc(HWND h, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
    case WM_PAINT:
        PaintOverlay(h);
        return 0;
    case WM_TIMER:
        FollowGameWindow();
        UpdateMetrics();
        {
            bool down = (GetAsyncKeyState(g_toggleVk) & 0x8000) != 0;
            if (down && !g_prevToggle) {
                g_visible = !g_visible;
                OLog("toggle -> %s", g_visible ? "visible" : "hidden");
                ShowWindow(g_hwnd, g_visible ? SW_SHOWNA : SW_HIDE);
            }
            g_prevToggle = down;
        }
        InvalidateRect(h, nullptr, FALSE);   // repaint so the clock stays live
        return 0;
    case WM_ERASEBKGND:
        return 1; // handled in WM_PAINT; avoid flicker
    case WM_DESTROY:
        KillTimer(h, 1);
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(h, msg, wp, lp);
}

static DWORD WINAPI OverlayThread(LPVOID) {
    g_startMs = GetTickCount64();
    OpenLog();
    OLog("=== Leaf it Alone native overlay starting (pid=%lu) ===", GetCurrentProcessId());
    LoadConfig();

    // Real version.dll must be reachable; log if not (forwarding would then be dead).
    if (!RealVer("GetFileVersionInfoA"))
        OLog("WARN: could not resolve real System32 version.dll");

    // Wait (up to ~60s) for the game render window to appear before creating ours.
    HWND game = nullptr;
    for (int i = 0; i < 600 && !game; ++i) {
        game = FindGameWindow();
        if (!game) Sleep(100);
    }
    if (!game) { OLog("no game window found; overlay thread exiting"); return 0; }
    OLog("game window detected hwnd=%p", (void*)game);

    HINSTANCE hi = (HINSTANCE)g_self;
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = WndProc;
    wc.hInstance = hi;
    wc.lpszClassName = kOverlayClass;
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    RegisterClassExW(&wc);

    g_font = CreateFontW(-64, 0, 0, 0, FW_BOLD, FALSE, FALSE, FALSE,
                         DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                         NONANTIALIASED_QUALITY, DEFAULT_PITCH, L"Segoe UI");
    g_fontSmall = CreateFontW(-22, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
                         DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                         CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");

    RECT r; GetWindowRect(game, &r);
    g_hwnd = CreateWindowExW(
        WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        kOverlayClass, L"", WS_POPUP,
        r.left, r.top, r.right - r.left, r.bottom - r.top,
        nullptr, nullptr, hi, nullptr);
    if (!g_hwnd) { OLog("CreateWindowEx failed gle=%lu", GetLastError()); return 0; }

    // color key -> everything but the panel becomes transparent; LWA_ALPHA makes
    // the panel itself slightly translucent.
    SetLayeredWindowAttributes(g_hwnd, kColorKey, (BYTE)g_alpha, LWA_COLORKEY | LWA_ALPHA);
    InitMetrics();
    SetTimer(g_hwnd, 1, 50, nullptr);
    ShowWindow(g_hwnd, g_visible ? SW_SHOWNA : SW_HIDE);
    OLog("overlay window created, entering message loop");

    MSG m;
    while (GetMessageW(&m, nullptr, 0, 0) > 0) {
        TranslateMessage(&m);
        DispatchMessageW(&m);
    }
    OLog("overlay message loop ended");
    if (g_log) { fclose(g_log); g_log = nullptr; }
    return 0;
}

BOOL APIENTRY DllMain(HMODULE h, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_self = h;
        DisableThreadLibraryCalls(h);
        HANDLE t = CreateThread(nullptr, 0, OverlayThread, nullptr, 0, nullptr);
        if (t) CloseHandle(t);
    }
    return TRUE;
}
