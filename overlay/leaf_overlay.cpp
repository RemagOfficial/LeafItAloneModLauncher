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
//
// Since M4 the same DLL also acts as safe-base's on-screen reporter. When the
// [safebase] section of LeafOverlay.ini has Enabled=true, the overlay runs in
// a MINIMAL banner-only mode: the HUD panel (title/clock/uptime/metrics) is
// NOT drawn at all, F9 is ignored, and instead a persistent plain-text
// banner ("OFFLINE · MODDED PLAY · SCORES WILL NOT BE SUBMITTED") is drawn
// with a black outline so it stays readable over any scene without a boxed
// backdrop. On startup the DLL runs a one-shot preflight check (Steam proxy
// file size, Saves/ isolation marker, live outbound connect() probe). If any
// check fails the text turns red. This still does not police the game
// process mid-session.

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef _CRT_SECURE_NO_WARNINGS
#define _CRT_SECURE_NO_WARNINGS
#endif
#include <windows.h>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <psapi.h>
#include <strsafe.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "psapi.lib")
#pragma comment(lib, "ws2_32.lib")

static HMODULE g_self = nullptr;
static FILE*   g_log = nullptr;
static const wchar_t* kOverlayClass = L"LeafItAloneOverlayWnd";
static HWND    g_hwnd = nullptr;      // our overlay window
static HFONT   g_font = nullptr;
static HFONT   g_fontSmall = nullptr;
static HFONT   g_fontBanner = nullptr;  // NONANTIALIASED - see banner notes

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

// ---- safe-base mode (M4) ----
// When [safebase] Enabled=true in LeafOverlay.ini, we draw a persistent
// non-hideable banner (OFFLINE · MODDED PLAY · SCORES WILL NOT BE SUBMITTED)
// and run a one-shot startup preflight from inside the game process:
//   * the Steam proxy DLL at <GameFolder>/Leaf it Alone_Data/Plugins/x86_64/
//     steam_api64.dll is present and matches the expected size,
//   * the Saves/ isolation marker is present at
//     %USERPROFILE%/AppData/LocalLow/Eternity/Leaf it Alone/Saves/.safebase-empty,
//   * a live connect() to 1.1.1.1:443 does NOT complete (proof that the
//     program-scoped firewall rule for THIS process is enforcing).
// We do not police mid-session tampering. If any check fails the banner
// turns red and the details go to LeafOverlay.log.
static bool      g_safebase           = false;
static bool      g_sbBanner           = true;         // draw the banner at all
static int       g_sbBannerCorner     = 0;            // 0=TopCenter 1=TopLeft 2=TopRight 3=BottomCenter
static int       g_sbBannerMargin     = 14;           // px from the anchor edge
static wchar_t   g_sbBannerText[192]  = L"OFFLINE \x00B7 MODDED PLAY \x00B7 SCORES WILL NOT BE SUBMITTED";
static bool      g_sbCheckProxy       = true;
static bool      g_sbCheckSaves       = true;
static bool      g_sbCheckOutbound    = true;
static ULONGLONG g_sbExpectedProxySize = 0;           // bytes; install.ps1 writes this after M2
static int       g_sbProbeTimeoutMs   = 1500;
// Runtime results.
static bool      g_sbRanChecks        = false;
static bool      g_sbOk               = true;
static wchar_t   g_sbFailSummary[320] = L"";          // compact one-liner for banner fallback

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

    // ---- [safebase] section: M4 banner + preconditions ----
    GetPrivateProfileStringW(L"safebase", L"Enabled", L"false", buf, 128, ini);
    g_safebase = ParseBool(buf, false);
    GetPrivateProfileStringW(L"safebase", L"Banner", L"true", buf, 128, ini);
    g_sbBanner = ParseBool(buf, true);
    GetPrivateProfileStringW(L"safebase", L"BannerText", L"", buf, 128, ini);
    if (buf[0]) { lstrcpynW(g_sbBannerText, buf, 192); }
    GetPrivateProfileStringW(L"safebase", L"BannerCorner", L"TopCenter", buf, 128, ini);
    if      (_wcsicmp(buf, L"TopLeft")     == 0) g_sbBannerCorner = 1;
    else if (_wcsicmp(buf, L"TopRight")    == 0) g_sbBannerCorner = 2;
    else if (_wcsicmp(buf, L"BottomCenter")== 0) g_sbBannerCorner = 3;
    else                                        g_sbBannerCorner = 0;
    g_sbBannerMargin = GetPrivateProfileIntW(L"safebase", L"BannerMargin", 14, ini);
    if (g_sbBannerMargin < 0) g_sbBannerMargin = 0;
    GetPrivateProfileStringW(L"safebase", L"CheckSteamProxy",  L"true", buf, 128, ini); g_sbCheckProxy    = ParseBool(buf, true);
    GetPrivateProfileStringW(L"safebase", L"CheckSaveIsolation", L"true", buf, 128, ini); g_sbCheckSaves   = ParseBool(buf, true);
    GetPrivateProfileStringW(L"safebase", L"CheckOutbound",    L"true", buf, 128, ini); g_sbCheckOutbound = ParseBool(buf, true);
    {
        wchar_t num[32];
        GetPrivateProfileStringW(L"safebase", L"ExpectedSteamProxySize", L"0", num, 32, ini);
        g_sbExpectedProxySize = (ULONGLONG)_wtoi64(num);
    }
    g_sbProbeTimeoutMs = GetPrivateProfileIntW(L"safebase", L"ProbeTimeoutMs", 1500, ini);
    if (g_sbProbeTimeoutMs < 100) g_sbProbeTimeoutMs = 100;
    if (g_sbProbeTimeoutMs > 5000) g_sbProbeTimeoutMs = 5000;
    if (g_safebase) OLog("safebase enabled: banner=%d corner=%d margin=%d expProxySize=%llu checks[proxy=%d saves=%d outbound=%d]",
        g_sbBanner, g_sbBannerCorner, g_sbBannerMargin, g_sbExpectedProxySize,
        g_sbCheckProxy, g_sbCheckSaves, g_sbCheckOutbound);
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
// safe-base preconditions (M4). Runs once at startup, before the message loop.
// Records pass/fail in g_sbOk and a compact fail summary in g_sbFailSummary.
// Details go to LeafOverlay.log. Never blocks the game.
// ---------------------------------------------------------------------------
static bool PathExistsW(const wchar_t* p) {
    DWORD a = GetFileAttributesW(p);
    return a != INVALID_FILE_ATTRIBUTES;
}

static bool GetFileSizeW(const wchar_t* p, ULONGLONG* outSize) {
    WIN32_FILE_ATTRIBUTE_DATA fad{};
    if (!GetFileAttributesExW(p, GetFileExInfoStandard, &fad)) return false;
    ULARGE_INTEGER u; u.HighPart = fad.nFileSizeHigh; u.LowPart = fad.nFileSizeLow;
    *outSize = u.QuadPart;
    return true;
}

// Live outbound probe. Attempts a non-blocking TCP connect to 1.1.1.1:443 and
// waits up to `timeoutMs` for the handshake. Returns true if the outbound
// traffic appears to be BLOCKED (silent drop / timeout / unreachable /
// policy-denied) and false if it succeeded or got refused/reset by a host.
static bool ProbeOutboundBlocked(int timeoutMs) {
    WSADATA wsa;
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) {
        OLog("probe: WSAStartup failed gle=%d (assuming blocked)", WSAGetLastError());
        return true;
    }
    SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == INVALID_SOCKET) {
        OLog("probe: socket() INVALID_SOCKET gle=%d (assuming blocked)", WSAGetLastError());
        WSACleanup();
        return true;
    }
    u_long nonBlock = 1;
    ioctlsocket(s, FIONBIO, &nonBlock);

    sockaddr_in a{};
    a.sin_family = AF_INET;
    a.sin_port = htons(443);
    // 1.1.1.1 in little-endian byte order (bytes 1.1.1.1 -> 0x01010101).
    a.sin_addr.S_un.S_addr = 0x01010101;

    bool blocked = true;
    int rc = connect(s, (sockaddr*)&a, sizeof(a));
    if (rc == 0) {
        blocked = false;                                // immediate success (unlikely, but "reachable")
        OLog("probe: connect() returned 0 immediately - outbound NOT blocked");
    } else {
        int gle = WSAGetLastError();
        if (gle == WSAEWOULDBLOCK) {
            fd_set wfds; FD_ZERO(&wfds); FD_SET(s, &wfds);
            timeval tv; tv.tv_sec = timeoutMs / 1000; tv.tv_usec = (timeoutMs % 1000) * 1000;
            int sr = select(0, nullptr, &wfds, nullptr, &tv);
            if (sr > 0) {
                int err = 0; int len = sizeof(err);
                getsockopt(s, SOL_SOCKET, SO_ERROR, (char*)&err, &len);
                if (err == 0 || err == WSAECONNREFUSED) {
                    blocked = false;
                    OLog("probe: TCP handshake completed or refused (err=%d) - outbound NOT blocked", err);
                } else {
                    OLog("probe: writable with err=%d (treated as blocked)", err);
                }
            } else if (sr == 0) {
                OLog("probe: select() timed out after %dms - outbound blocked (SYN dropped)", timeoutMs);
            } else {
                OLog("probe: select() error gle=%d (treated as blocked)", WSAGetLastError());
            }
        } else if (gle == WSAEACCES || gle == WSAEHOSTUNREACH ||
                   gle == WSAENETUNREACH || gle == WSAETIMEDOUT) {
            OLog("probe: immediate WSA error %d (treated as blocked)", gle);
        } else {
            OLog("probe: unexpected WSA error %d (treated as blocked)", gle);
        }
    }
    closesocket(s);
    WSACleanup();
    return blocked;
}

static void RunPreconditions() {
    g_sbRanChecks = true;
    if (!g_safebase) return;                            // nothing to verify

    bool ok = true;
    wchar_t reasons[512]; reasons[0] = 0;
    size_t rpos = 0;
    auto appendReason = [&](const wchar_t* txt) {
        size_t used = wcslen(reasons);
        if (used > 0 && rpos + 3 < 512) {
            reasons[used++] = L';';
            reasons[used++] = L' ';
            rpos = used;
        }
        StringCchCopyW(reasons + used, 512 - used, txt);
        rpos = wcslen(reasons);
    };

    wchar_t dir[MAX_PATH] = L"";
    GetModuleFileNameW(g_self, dir, MAX_PATH);
    wchar_t* slash = wcsrchr(dir, L'\\'); if (slash) *slash = 0;   // dir = game folder

    // --- Steam proxy DLL ---
    if (g_sbCheckProxy) {
        wchar_t dllPath[MAX_PATH];
        _snwprintf_s(dllPath, MAX_PATH, _TRUNCATE,
                     L"%s\\Leaf it Alone_Data\\Plugins\\x86_64\\steam_api64.dll", dir);
        ULONGLONG sz = 0;
        if (!GetFileSizeW(dllPath, &sz)) {
            ok = false;
            appendReason(L"steam proxy missing");
            OLog("PRECOND FAIL: no steam_api64.dll at %ls", dllPath);
        } else if (g_sbExpectedProxySize != 0 && sz != g_sbExpectedProxySize) {
            ok = false;
            wchar_t msg[128];
            swprintf(msg, 128, L"steam proxy size %llu (expected %llu)", sz, g_sbExpectedProxySize);
            appendReason(msg);
            OLog("PRECOND FAIL: %ls", msg);
        } else {
            OLog("precond: steam proxy at %ls size=%llu OK", dllPath, sz);
        }
    }

    // --- Saves/ isolation marker ---
    if (g_sbCheckSaves) {
        wchar_t env[MAX_PATH];
        DWORD n = GetEnvironmentVariableW(L"USERPROFILE", env, MAX_PATH);
        if (n == 0 || n >= MAX_PATH) {
            ok = false;
            appendReason(L"USERPROFILE not readable");
            OLog("PRECOND FAIL: USERPROFILE env var missing");
        } else {
            wchar_t marker[MAX_PATH];
            _snwprintf_s(marker, MAX_PATH, _TRUNCATE,
                         L"%s\\AppData\\LocalLow\\Eternity\\Leaf it Alone\\Saves\\.safebase-empty", env);
            if (!PathExistsW(marker)) {
                ok = false;
                appendReason(L"Saves/ not isolated");
                OLog("PRECOND FAIL: no marker at %ls", marker);
            } else {
                OLog("precond: save isolation marker OK (%ls)", marker);
            }
        }
    }

    // --- Live outbound probe ---
    if (g_sbCheckOutbound) {
        OLog("precond: probing outbound (connect 1.1.1.1:443, %dms cap)", g_sbProbeTimeoutMs);
        bool blocked = ProbeOutboundBlocked(g_sbProbeTimeoutMs);
        if (!blocked) {
            ok = false;
            appendReason(L"outbound NOT blocked");
        } else {
            OLog("precond: outbound probe OK (blocked)");
        }
    }

    g_sbOk = ok;
    if (!ok) {
        StringCchCopyW(g_sbFailSummary, 320, reasons);
        OLog("PRECONDITIONS FAILED: %ls", reasons);
    } else {
        OLog("PRECONDITIONS OK");
    }
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

    SetBkMode(dc, TRANSPARENT);

    // ---- safe-base banner (persistent, plain text, non-hideable) ----
    // Minimal mode: no rounded rect, no border. Text only, with a 1px black
    // outline so it stays readable over bright or busy scenes without adding
    // a distracting backdrop.
    //
    // IMPORTANT: this uses a NONANTIALIASED font on purpose. The overlay
    // window is transparent via SetLayeredWindowAttributes COLORKEY on
    // magenta RGB(255,0,255). GDI blends antialiased/ClearType glyph edges
    // against whatever is in the DC - which is that magenta key - and the
    // resulting intermediate pixels are NOT the key colour, so they render
    // as opaque pink/purple (and ClearType adds red/blue subpixel fringing).
    // Inside the old opaque panel this was invisible; over plain game it
    // shows as coloured halos. Non-antialiased pixels are exact colours, so
    // text + 8-way outline composite cleanly with zero fringing.
    if (g_safebase && g_sbBanner) {
        SelectObject(dc, g_fontBanner);
        const wchar_t* txt = g_sbOk ? g_sbBannerText : g_sbFailSummary[0] ? g_sbFailSummary : g_sbBannerText;
        SIZE bs{}; GetTextExtentPoint32W(dc, txt, (int)wcslen(txt), &bs);
        int bpx, bpy;
        switch (g_sbBannerCorner) {
            case 1: bpx = g_sbBannerMargin;                bpy = g_sbBannerMargin; break;
            case 2: bpx = cw - bs.cx - g_sbBannerMargin;   bpy = g_sbBannerMargin; break;
            case 3: bpx = (cw - bs.cx) / 2;                bpy = ch - bs.cy - g_sbBannerMargin; break;
            default: bpx = (cw - bs.cx) / 2;               bpy = g_sbBannerMargin; break;
        }
        if (bpx < 0) bpx = 0; if (bpy < 0) bpy = 0;
        COLORREF textC = g_sbOk ? RGB(196, 226, 200) : RGB(255, 110, 110);
        // Black outline: draw the string once at each of the 8 neighbours.
        SetTextColor(dc, RGB(0, 0, 0));
        for (int ox = -1; ox <= 1; ++ox) {
            for (int oy = -1; oy <= 1; ++oy) {
                if (ox == 0 && oy == 0) continue;
                RECT o = { bpx + ox, bpy + oy, bpx + bs.cx + ox, bpy + bs.cy + oy };
                DrawTextW(dc, txt, -1, &o, DT_LEFT | DT_TOP | DT_SINGLELINE | DT_NOPREFIX);
            }
        }
        RECT bp = { bpx, bpy, bpx + bs.cx, bpy + bs.cy };
        SetTextColor(dc, textC);
        DrawTextW(dc, txt, -1, &bp, DT_LEFT | DT_TOP | DT_SINGLELINE | DT_NOPREFIX);
    }

    // safe-base mode is banner-only: the HUD panel and F9 are skipped entirely here.
    if (g_safebase) { EndPaint(h, &ps); return; }

    if (!g_visible) { EndPaint(h, &ps); return; }

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
        if (!g_safebase) UpdateMetrics();   // banner-only mode never shows metrics
        {
            // F9 toggles the HUD panel only. safe-base mode is banner-only and
            // the banner must never be hideable, so the toggle is a no-op there.
            if (!g_safebase) {
                bool down = (GetAsyncKeyState(g_toggleVk) & 0x8000) != 0;
                if (down && !g_prevToggle) {
                    g_visible = !g_visible;
                    OLog("toggle -> %s", g_visible ? "visible" : "hidden");
                    ShowWindow(g_hwnd, g_visible ? SW_SHOWNA : SW_HIDE);
                }
                g_prevToggle = down;
            }
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

    // safe-base preconditions: probe outbound (may block for up to
    // ProbeTimeoutMs), read the file system for the marker + proxy DLL.
    // Runs off the game's render thread (this is our own thread), so it
    // never freezes the game.
    if (g_safebase) RunPreconditions();

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
    // Banner font: same size as the small font but NONANTIALIASED. The
    // color-key transparency cannot carry blended edge pixels (they pick up
    // the magenta key as a coloured halo), so the plain-text safe-base
    // banner must be drawn with exact-colour pixels only.
    g_fontBanner = CreateFontW(-22, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
                         DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                         NONANTIALIASED_QUALITY, DEFAULT_PITCH, L"Segoe UI");

    RECT r; GetWindowRect(game, &r);
    g_hwnd = CreateWindowExW(
        WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        kOverlayClass, L"", WS_POPUP,
        r.left, r.top, r.right - r.left, r.bottom - r.top,
        nullptr, nullptr, hi, nullptr);
    if (!g_hwnd) { OLog("CreateWindowEx failed gle=%lu", GetLastError()); return 0; }

    // color key -> everything but the drawn pixels become transparent.
    // LWA_ALPHA makes the HUD panel slightly translucent; safe-base's plain
    // text banner uses full opacity so it stays crisp.
    BYTE winAlpha = g_safebase ? (BYTE)255 : (BYTE)g_alpha;
    SetLayeredWindowAttributes(g_hwnd, kColorKey, winAlpha, LWA_COLORKEY | LWA_ALPHA);
    InitMetrics();
    SetTimer(g_hwnd, 1, 50, nullptr);
    ShowWindow(g_hwnd, g_safebase ? SW_SHOWNA : (g_visible ? SW_SHOWNA : SW_HIDE));
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
