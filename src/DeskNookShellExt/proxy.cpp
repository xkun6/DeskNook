// 菜单代理：在 explorer.exe 进程内的专用 STA 线程 + 隐藏窗口。
// DeskNook 通过 WM_COPYDATA(JSON) 发请求，代理在 Explorer 进程里取真菜单、弹出、执行，并经命名管道回报结果。
#include "desknook.h"
#include <memory>
#include <map>
#include <vector>

namespace dn {

static const ULONG_PTR kCopyDataMagic = 0x4B584D31; // "KXM1"
static const UINT WM_DN_REQ = WM_APP + 1;

static thread_local wchar_t t_req[64] = {0};
const wchar_t* CurrentRequestId() { return t_req; }

static HWND g_hwnd = nullptr;
static volatile LONG g_started = 0;
static bool g_dedicated = false; // true = 代理窗口在自己的专用线程；false = 在 Explorer 桌面线程（可直接使用 DefView）
static HHOOK g_hook = nullptr;
static std::map<UINT, std::wstring> g_titles; // 菜单命令 id → 标题（菜单关闭后项文本可能已被清空，所以在弹出期间记下）
static DWORD g_dnPid = 0; // 最近一次发来请求的 DeskNook 进程
static bool g_busy = false;
static IContextMenu2* g_cm2 = nullptr;
static IContextMenu3* g_cm3 = nullptr;
static bool g_iconsVisible = true;

// ====================================================== 小工具

typedef BOOL(WINAPI* PFN_AllowDarkModeForWindow)(HWND, BOOL);
typedef void(WINAPI* PFN_FlushMenuThemes)();

static void AllowDark(HWND hwnd) {
    HMODULE ux = GetModuleHandleW(L"uxtheme.dll");
    if (!ux) return;
    auto allow = (PFN_AllowDarkModeForWindow)GetProcAddress(ux, MAKEINTRESOURCEA(133));
    auto flush = (PFN_FlushMenuThemes)GetProcAddress(ux, MAKEINTRESOURCEA(136));
    if (allow) allow(hwnd, TRUE);
    if (flush) flush();
}

static std::wstring ImageBaseName(HWND h) {
    DWORD pid = 0;
    GetWindowThreadProcessId(h, &pid);
    if (!pid) return L"";
    HANDLE p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!p) return L"";
    wchar_t buf[MAX_PATH];
    DWORD n = ARRAYSIZE(buf);
    std::wstring r;
    if (QueryFullProcessImageNameW(p, 0, buf, &n)) {
        const wchar_t* s = wcsrchr(buf, L'\\');
        r = s ? s + 1 : buf;
        for (auto& c : r) c = (wchar_t)towlower(c);
    }
    CloseHandle(p);
    return r;
}

static std::wstring NormalizeTitle(const wchar_t* s) {
    std::wstring r;
    for (const wchar_t* p = s; *p && *p != L'\t'; ++p) {
        if (p[0] == L'(' && p[1] == L'&' && p[2] && p[3] == L')') { p += 3; continue; }
        if (*p == L'&') continue;
        r += *p;
    }
    while (!r.empty() && iswspace(r.back())) r.pop_back();
    while (!r.empty() && iswspace(r.front())) r.erase(r.begin());
    return r;
}

static bool IsShowDesktopIcons(const std::wstring& t) {
    return t == L"显示桌面图标" || _wcsicmp(t.c_str(), L"Show desktop icons") == 0;
}

// 递归查找：id 对应的菜单项文本
static bool FindTitleById(HMENU m, UINT id, std::wstring& out, int depth = 0) {
    if (!m || depth > 8) return false;
    int n = GetMenuItemCount(m);
    for (int i = 0; i < n; i++) {
        HMENU sub = GetSubMenu(m, i);
        if (sub) {
            if (FindTitleById(sub, id, out, depth + 1)) return true;
            continue;
        }
        if (GetMenuItemID(m, i) == id) {
            wchar_t buf[256] = {0};
            GetMenuStringW(m, i, buf, ARRAYSIZE(buf), MF_BYPOSITION);
            out = NormalizeTitle(buf);
            return true;
        }
    }
    return false;
}

// 顶层子菜单中包含 id 的那一项的标题（命令在顶层则返回空）
static std::wstring FindTopParentTitle(HMENU m, UINT id) {
    int n = GetMenuItemCount(m);
    for (int i = 0; i < n; i++) {
        HMENU sub = GetSubMenu(m, i);
        if (!sub) continue;
        std::wstring dummy;
        if (FindTitleById(sub, id, dummy)) {
            wchar_t buf[256] = {0};
            GetMenuStringW(m, i, buf, ARRAYSIZE(buf), MF_BYPOSITION);
            return NormalizeTitle(buf);
        }
    }
    return L"";
}

// 递归记录菜单中所有叶子项的标题
static void RecordTitles(HMENU m, int depth = 0) {
    if (!m || depth > 8) return;
    int n = GetMenuItemCount(m);
    for (int i = 0; i < n; i++) {
        HMENU sub = GetSubMenu(m, i);
        if (sub) { RecordTitles(sub, depth + 1); continue; }
        UINT id = GetMenuItemID(m, i);
        wchar_t buf[256] = {0};
        if (id != 0 && id != (UINT)-1 && GetMenuStringW(m, i, buf, ARRAYSIZE(buf), MF_BYPOSITION) > 0) g_titles[id] = NormalizeTitle(buf);
    }
}

// 递归：把“显示桌面图标”项的勾选状态改成 DeskNook 的真实状态
static void FixIconsItem(HMENU m, int depth = 0) {
    if (!m || depth > 8) return;
    int n = GetMenuItemCount(m);
    for (int i = 0; i < n; i++) {
        HMENU sub = GetSubMenu(m, i);
        if (sub) { FixIconsItem(sub, depth + 1); continue; }
        wchar_t buf[256] = {0};
        if (GetMenuStringW(m, i, buf, ARRAYSIZE(buf), MF_BYPOSITION) <= 0) continue;
        if (IsShowDesktopIcons(NormalizeTitle(buf))) CheckMenuItem(m, i, MF_BYPOSITION | (g_iconsVisible ? MF_CHECKED : MF_UNCHECKED));
    }
}

static std::string EventJson(const wchar_t* type, const wchar_t* req, const wchar_t* extra = nullptr) {
    std::string j = "{\"t\":" + JsonQuote(type) + ",\"req\":" + JsonQuote(req);
    if (extra && *extra) { j += ","; j += ToUtf8(extra); }
    j += "}";
    return j;
}

static void SendEvent(const std::string& json) {
    // 把前台权限还给 DeskNook（原位重命名框、对话框需要它抢到焦点）
    if (g_dnPid) AllowSetForegroundWindow(g_dnPid);
    std::string resp;
    if (!PipeRoundTrip(json, resp, 500)) Log(L"事件发送失败（DeskNook 未响应）：%S", json.substr(0, 80).c_str());
}

// ====================================================== 取桌面视图 / 各类 IContextMenu

static IShellView* AcquireDesktopView(HRESULT* hrOut) {
    IShellWindows* sw = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_ShellWindows, nullptr, CLSCTX_ALL, IID_PPV_ARGS(&sw));
    IShellView* view = nullptr;
    if (SUCCEEDED(hr) && sw) {
        VARIANT loc, empty;
        VariantInit(&loc);
        VariantInit(&empty);
        loc.vt = VT_I4;
        loc.lVal = CSIDL_DESKTOP;
        long hwnd = 0;
        IDispatch* disp = nullptr;
        hr = sw->FindWindowSW(&loc, &empty, SWC_DESKTOP, &hwnd, SWFO_NEEDDISPATCH, &disp);
        if (hr == S_OK && disp) {
            IServiceProvider* sp = nullptr;
            hr = disp->QueryInterface(IID_PPV_ARGS(&sp));
            if (SUCCEEDED(hr) && sp) {
                IShellBrowser* br = nullptr;
                hr = sp->QueryService(SID_STopLevelBrowser, IID_PPV_ARGS(&br));
                if (SUCCEEDED(hr) && br) {
                    hr = br->QueryActiveShellView(&view);
                    br->Release();
                }
                sp->Release();
            }
            disp->Release();
        }
        sw->Release();
    }
    if (hrOut) *hrOut = hr;
    return view;
}

static bool NameEquals(const std::wstring& a, const std::wstring& b) { return _wcsicmp(a.c_str(), b.c_str()) == 0; }

// 桌面根里按解析名匹配子 PIDL（与 DeskNook 的 Key 规则一致）
static IContextMenu* DesktopItemsMenu(HWND hwnd, const std::vector<std::wstring>& names) {
    IShellFolder* desk = nullptr;
    if (FAILED(SHGetDesktopFolder(&desk)) || !desk) return nullptr;
    std::vector<PITEMID_CHILD> found;
    IEnumIDList* en = nullptr;
    if (SUCCEEDED(desk->EnumObjects(nullptr, SHCONTF_FOLDERS | SHCONTF_NONFOLDERS | SHCONTF_INCLUDEHIDDEN | SHCONTF_INCLUDESUPERHIDDEN, &en)) && en) {
        PITEMID_CHILD child = nullptr;
        ULONG fetched = 0;
        while (en->Next(1, &child, &fetched) == S_OK && fetched == 1) {
            STRRET sr = {};
            bool keep = false;
            if (SUCCEEDED(desk->GetDisplayNameOf(child, SHGDN_FORPARSING, &sr))) {
                wchar_t buf[2048];
                if (SUCCEEDED(StrRetToBufW(&sr, child, buf, ARRAYSIZE(buf)))) {
                    for (auto& n : names)
                        if (NameEquals(n, buf)) { keep = true; break; }
                }
            }
            if (keep) found.push_back(child); else CoTaskMemFree(child);
        }
        en->Release();
    }
    IContextMenu* cm = nullptr;
    if (!found.empty()) {
        HRESULT hr = desk->GetUIObjectOf(hwnd, (UINT)found.size(), (PCUITEMID_CHILD_ARRAY)found.data(), IID_IContextMenu, nullptr, (void**)&cm);
        if (FAILED(hr)) { Log(L"桌面项 GetUIObjectOf 失败 hr=0x%08X", hr); cm = nullptr; }
    } else {
        Log(L"桌面项匹配不到任何项（请求 %u 项）", (unsigned)names.size());
    }
    for (auto p : found) CoTaskMemFree(p);
    desk->Release();
    return cm;
}

// 其他目录里的项：解析每个路径，以第一个的父文件夹为准
static IContextMenu* FolderItemsMenu(HWND hwnd, const std::vector<std::wstring>& names) {
    std::vector<PIDLIST_ABSOLUTE> abs;
    for (auto& n : names) {
        PIDLIST_ABSOLUTE p = nullptr;
        if (SUCCEEDED(SHParseDisplayName(n.c_str(), nullptr, &p, 0, nullptr)) && p) abs.push_back(p);
    }
    IContextMenu* cm = nullptr;
    if (!abs.empty()) {
        IShellFolder* parent = nullptr;
        PCUITEMID_CHILD last0 = nullptr;
        if (SUCCEEDED(SHBindToParent(abs[0], IID_PPV_ARGS(&parent), &last0)) && parent) {
            std::vector<PCUITEMID_CHILD> lasts;
            for (auto p : abs) lasts.push_back(ILFindLastID(p));
            HRESULT hr = parent->GetUIObjectOf(hwnd, (UINT)lasts.size(), lasts.data(), IID_IContextMenu, nullptr, (void**)&cm);
            if (FAILED(hr)) { Log(L"目录项 GetUIObjectOf 失败 hr=0x%08X", hr); cm = nullptr; }
            parent->Release();
        }
    }
    for (auto p : abs) CoTaskMemFree(p);
    return cm;
}

static IContextMenu* FolderBackgroundMenu(HWND hwnd, const std::wstring& folder) {
    PIDLIST_ABSOLUTE abs = nullptr;
    if (FAILED(SHParseDisplayName(folder.c_str(), nullptr, &abs, 0, nullptr)) || !abs) return nullptr;
    IContextMenu* cm = nullptr;
    IShellFolder* desk = nullptr;
    if (SUCCEEDED(SHGetDesktopFolder(&desk)) && desk) {
        IShellFolder* sf = nullptr;
        HRESULT hr = desk->BindToObject(abs, nullptr, IID_PPV_ARGS(&sf));
        if (SUCCEEDED(hr) && sf) {
            hr = sf->CreateViewObject(hwnd, IID_IContextMenu, (void**)&cm);
            if (FAILED(hr)) { Log(L"目录背景 CreateViewObject 失败 hr=0x%08X", hr); cm = nullptr; }
            sf->Release();
        }
        desk->Release();
    }
    CoTaskMemFree(abs);
    return cm;
}

// 桌面背景：优先真实 DefView 的 GetItemObject(SVGIO_BACKGROUND)，失败再 CreateViewObject
static IContextMenu* DesktopBackgroundMenu(HWND hwnd, IShellView* view, bool* usedDefView) {
    *usedDefView = false;
    if (view) {
        IContextMenu* cm = nullptr;
        HRESULT hr = view->GetItemObject(SVGIO_BACKGROUND, IID_IContextMenu, (void**)&cm);
        if (SUCCEEDED(hr) && cm) { *usedDefView = true; return cm; }
        Log(L"DefView.GetItemObject(BACKGROUND) 失败 hr=0x%08X，改用 CreateViewObject", hr);
    }
    IShellFolder* desk = nullptr;
    IContextMenu* cm = nullptr;
    if (SUCCEEDED(SHGetDesktopFolder(&desk)) && desk) {
        HRESULT hr = desk->CreateViewObject(hwnd, IID_IContextMenu, (void**)&cm);
        if (FAILED(hr)) { Log(L"桌面 CreateViewObject 失败 hr=0x%08X", hr); cm = nullptr; }
        desk->Release();
    }
    return cm;
}

// ====================================================== 执行一次菜单请求

static int Invoke(IContextMenu* cm, HWND hwnd, UINT offset, bool shift, bool ctrl, POINT pt, const std::wstring& dir) {
    CMINVOKECOMMANDINFOEX ci = {};
    ci.cbSize = sizeof(ci);
    ci.fMask = 0x4000u | 0x20000000u | (shift ? 0x10000000u : 0u) | (ctrl ? 0x40000000u : 0u);
    ci.hwnd = hwnd;
    ci.lpVerb = MAKEINTRESOURCEA(offset);
    ci.lpVerbW = MAKEINTRESOURCEW(offset);
    ci.nShow = SW_SHOWNORMAL;
    ci.ptInvoke = pt;
    std::string dirA;
    if (!dir.empty()) {
        int n = WideCharToMultiByte(CP_ACP, 0, dir.c_str(), -1, nullptr, 0, nullptr, nullptr);
        if (n > 0) { dirA.assign((size_t)n, '\0'); WideCharToMultiByte(CP_ACP, 0, dir.c_str(), -1, &dirA[0], n, nullptr, nullptr); }
        ci.lpDirectory = dirA.c_str();
        ci.lpDirectoryW = dir.c_str();
    }
    return (int)cm->InvokeCommand((CMINVOKECOMMANDINFO*)&ci);
}

static void RunMenu(const JV& r, HWND hwnd) {
    std::wstring id = r.WStr("id");
    std::wstring kind = r.WStr("kind");
    std::wstring folder = r.WStr("folder");
    bool shift = r.GetBool("shift");
    POINT pt = {(LONG)r.GetNum("x"), (LONG)r.GetNum("y")};
    g_iconsVisible = r.GetBool("iconsVisible", true);

    std::vector<std::wstring> names;
    if (const JV* a = r.Get("items"))
        if (a->t == JV::Arr)
            for (auto& v : a->a)
                if (v.t == JV::Str) names.push_back(FromUtf8(v.s));
    std::vector<std::wstring> intercept;
    if (const JV* a = r.Get("interceptVerbs"))
        if (a->t == JV::Arr)
            for (auto& v : a->a)
                if (v.t == JV::Str) intercept.push_back(FromUtf8(v.s));

    bool desktop = folder.empty() || folder == L"::desktop";
    bool background = kind == L"background";

    IShellView* view = nullptr;
    HRESULT viewHr = S_OK;
    if (desktop) view = AcquireDesktopView(&viewHr); // 桌面菜单一律拿真实 DefView 作站点
    if (desktop && !view) Log(L"取桌面 IShellView 失败 hr=0x%08X", viewHr);

    bool usedDefView = false;
    IContextMenu* cm = nullptr;
    if (background) cm = desktop ? DesktopBackgroundMenu(hwnd, view, &usedDefView) : FolderBackgroundMenu(hwnd, folder);
    else cm = desktop ? DesktopItemsMenu(hwnd, names) : FolderItemsMenu(hwnd, names);

    if (!cm) {
        Log(L"取 IContextMenu 失败 kind=%s folder=%s", kind.c_str(), folder.c_str());
        SendEvent(EventJson(L"closed", id.c_str(), L"\"error\":\"nocm\""));
        if (view) view->Release();
        return;
    }
    Log(L"请求 %s kind=%s folder=%s 项数=%u DefView背景=%d", id.c_str(), kind.c_str(), folder.c_str(), (unsigned)names.size(), usedDefView ? 1 : 0);

    IObjectWithSite* ows = nullptr;
    if (view && !usedDefView && SUCCEEDED(cm->QueryInterface(IID_PPV_ARGS(&ows))) && ows) {
        HRESULT hr = ows->SetSite(view);
        Log(L"SetSite hr=0x%08X", hr);
    }

    HMENU hmenu = CreatePopupMenu();
    MENUINFO mi = {};
    mi.cbSize = sizeof(mi);
    mi.fMask = MIM_STYLE;
    mi.dwStyle = MNS_CHECKORBMP;
    SetMenuInfo(hmenu, &mi);

    // 与原生桌面右键一致的标志（从原生调用实测：背景菜单 0x20420，图标菜单 0x20490；夸克/NVIDIA 等扩展依赖它们）
    UINT flags = background ? 0x20420u : 0x20490u;
    if (shift) flags |= CMF_EXTENDEDVERBS;
    HRESULT hr = cm->QueryContextMenu(hmenu, 0, 1, 0x7FFF, flags);
    if (FAILED(hr)) Log(L"QueryContextMenu 失败 hr=0x%08X", hr);
    FixIconsItem(hmenu);
    g_titles.clear();
    RecordTitles(hmenu);

    {
        std::wstring titles;
        int n = GetMenuItemCount(hmenu);
        for (int i = 0; i < n && i < 60; i++) {
            wchar_t buf[128] = {0};
            if (GetMenuStringW(hmenu, i, buf, ARRAYSIZE(buf), MF_BYPOSITION) > 0) { titles += NormalizeTitle(buf); titles += L"|"; }
            else titles += L"-|";
        }
        Log(L"菜单项(%d)：%s", n, titles.c_str());
    }

    g_cm3 = nullptr;
    g_cm2 = nullptr;
    cm->QueryInterface(IID_PPV_ARGS(&g_cm3));
    if (!g_cm3) cm->QueryInterface(IID_PPV_ARGS(&g_cm2));

    SetForegroundWindow(hwnd);
    UINT cmd = (UINT)TrackPopupMenuEx(hmenu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_LEFTALIGN | TPM_TOPALIGN, pt.x, pt.y, hwnd, nullptr);
    PostMessageW(hwnd, WM_NULL, 0, 0);

    // 菜单期间的转发目标不再需要（命令可能弹对话框、跑消息循环）
    if (g_cm3) { g_cm3->Release(); g_cm3 = nullptr; }
    if (g_cm2) { g_cm2->Release(); g_cm2 = nullptr; }

    // 先告知 DeskNook 菜单已关闭
    g_busy = false;
    SendEvent(EventJson(L"closed", id.c_str(), cmd ? L"\"picked\":true" : L"\"picked\":false"));

    if (cmd != 0) {
        UINT offset = cmd - 1;
        wchar_t verbBuf[256] = {0};
        if (FAILED(cm->GetCommandString(offset, GCS_VERBW, nullptr, (CHAR*)verbBuf, ARRAYSIZE(verbBuf)))) verbBuf[0] = 0;
        std::wstring verb = verbBuf;
        std::wstring title;
        FindTitleById(hmenu, cmd, title);
        if (title.empty()) { auto it = g_titles.find(cmd); if (it != g_titles.end()) title = it->second; }
        std::wstring parent = FindTopParentTitle(hmenu, cmd);
        Log(L"选择命令 id=%u 动词=%s 标题=%s 父菜单=%s", cmd, verb.empty() ? L"(无)" : verb.c_str(), title.c_str(), parent.c_str());
        {
            std::wstring x = L"\"verb\":" + FromUtf8(JsonQuote(verb)) + L",\"title\":" + FromUtf8(JsonQuote(title)) +
                             L",\"parent\":" + FromUtf8(JsonQuote(parent));
            SendEvent(EventJson(L"pick", id.c_str(), x.c_str()));
        }

        bool intercepted = false;
        if (IsShowDesktopIcons(title)) {
            SendEvent(EventJson(L"verb", id.c_str(), L"\"verb\":\"showdesktopicons\""));
            intercepted = true;
        } else if (!verb.empty()) {
            for (auto& v : intercept)
                if (NameEquals(v, verb)) {
                    SendEvent(EventJson(L"verb", id.c_str(), (L"\"verb\":" + FromUtf8(JsonQuote(verb))).c_str()));
                    intercepted = true;
                    break;
                }
        }

        if (!intercepted) {
            std::wstring dir = folder;
            if (desktop) {
                PWSTR p = nullptr;
                if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_Desktop, 0, nullptr, &p)) && p) { dir = p; CoTaskMemFree(p); }
            }
            bool ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
            int ihr = Invoke(cm, hwnd, offset, shift, ctrl, pt, dir);
            Log(L"InvokeCommand hr=0x%08X", ihr);
            std::wstring extra = L"\"verb\":" + FromUtf8(JsonQuote(verb)) + L",\"title\":" + FromUtf8(JsonQuote(title)) +
                                 L",\"parent\":" + FromUtf8(JsonQuote(parent)) +
                                 L",\"defView\":" + (usedDefView ? L"true" : L"false") + L",\"hr\":" + std::to_wstring(ihr);
            SendEvent(EventJson(L"invoked", id.c_str(), extra.c_str()));
        }
    }

    if (ows) { ows->SetSite(nullptr); ows->Release(); }
    DestroyMenu(hmenu);
    cm->Release();
    if (view) view->Release();
}

static void HandleRequest(const std::string& json, HWND hwnd) {
    JV r;
    if (!JsonParse(json, r) || r.t != JV::Obj) { Log(L"请求 JSON 无法解析"); return; }
    std::wstring type = r.WStr("t");
    if (type == L"quit") {
        Log(L"收到 quit，代理退出");
        DestroyWindow(hwnd);
        return;
    }
    std::wstring id = r.WStr("id");
    if (g_busy) {
        Log(L"代理忙，拒绝请求 %s", id.c_str());
        SendEvent(EventJson(L"closed", id.c_str(), L"\"error\":\"busy\""));
        return;
    }
    wchar_t saved[64];
    wcsncpy_s(saved, t_req, _TRUNCATE);
    wcsncpy_s(t_req, id.c_str(), _TRUNCATE);
    g_busy = true;
    try { RunMenu(r, hwnd); }
    catch (...) {
        Log(L"RunMenu C++ 异常");
        SendEvent(EventJson(L"closed", id.c_str(), L"\"error\":\"exception\""));
    }
    g_busy = false;
    wcsncpy_s(t_req, saved, _TRUNCATE);
}

// ====================================================== 窗口过程

static LRESULT WndProcImpl(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
        case WM_COPYDATA: {
            auto* cds = (COPYDATASTRUCT*)lp;
            if (!cds || cds->dwData != kCopyDataMagic || !cds->lpData || cds->cbData == 0 || cds->cbData > (1u << 20)) return FALSE;
            // 只接受 DeskNook.exe 发来的请求
            if (ImageBaseName((HWND)wp) != L"desknook.exe") { Log(L"拒绝非 DeskNook 的 WM_COPYDATA"); return FALSE; }
            GetWindowThreadProcessId((HWND)wp, &g_dnPid);
            auto* s = new std::string((const char*)cds->lpData, cds->cbData);
            if (!PostMessageW(hwnd, WM_DN_REQ, 0, (LPARAM)s)) { delete s; return FALSE; }
            return TRUE;
        }
        case WM_DN_REQ: {
            std::unique_ptr<std::string> s((std::string*)lp);
            HandleRequest(*s, hwnd);
            return 0;
        }
        case WM_INITMENUPOPUP:
        case WM_DRAWITEM:
        case WM_MEASUREITEM:
        case WM_MENUCHAR:
        case WM_MENUSELECT: {
            LRESULT lr = 0;
            bool handled = false;
            if (g_cm3) handled = SUCCEEDED(g_cm3->HandleMenuMsg2(msg, wp, lp, &lr));
            else if (g_cm2) handled = SUCCEEDED(g_cm2->HandleMenuMsg(msg, wp, lp));
            if (msg == WM_INITMENUPOPUP) { FixIconsItem((HMENU)wp); RecordTitles((HMENU)wp); }
            if (msg == WM_MENUSELECT && lp) {
                UINT id = LOWORD(wp);
                wchar_t buf[256] = {0};
                if (id && !(HIWORD(wp) & MF_POPUP) && GetMenuStringW((HMENU)lp, id, buf, ARRAYSIZE(buf), MF_BYCOMMAND) > 0) g_titles[id] = NormalizeTitle(buf);
            }
            if (handled && msg != WM_MENUSELECT) return (msg == WM_DRAWITEM || msg == WM_MEASUREITEM) ? TRUE : lr;
            break;
        }
        case WM_DESTROY:
            if (g_dedicated) PostQuitMessage(0); // 桌面线程是 Explorer 自己的消息循环，不能退出它
            g_hwnd = nullptr;
            InterlockedExchange(&g_proxyAlive, 0);
            return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

static LRESULT CALLBACK ProxyWndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    LRESULT r = 0;
    __try { r = WndProcImpl(hwnd, msg, wp, lp); }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        Log(L"代理窗口过程异常 0x%08X（msg=0x%X）", GetExceptionCode(), msg);
        g_busy = false;
        r = DefWindowProcW(hwnd, msg, wp, lp);
    }
    return r;
}

// 在当前线程上创建代理窗口（幂等）。返回是否已有可用窗口。
static bool CreateProxyWindowHere() {
    if (g_hwnd) return true;
    // 窗口过程在本 DLL 里：永久驻留。DeskNook 的钩子卸载后 Windows 会把 DLL 从 Explorer 里卸掉，不 PIN 就会崩溃。
    HMODULE pinned = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN | GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, (LPCWSTR)&CreateProxyWindowHere, &pinned);
    wchar_t self[MAX_PATH] = {0};
    GetModuleFileNameW(g_hMod, self, ARRAYSIZE(self));
    const wchar_t* base = wcsrchr(self, L'\\');
    base = base ? base + 1 : self;

    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = ProxyWndProc;
    wc.hInstance = g_hMod;
    wc.lpszClassName = DN_PROXY_CLASS;
    RegisterClassExW(&wc);
    HWND hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, DN_PROXY_CLASS, base, WS_POPUP, 0, 0, 0, 0, nullptr, nullptr, g_hMod, nullptr);
    if (!hwnd) {
        Log(L"代理窗口创建失败 err=%lu", GetLastError());
        return false;
    }
    g_hwnd = hwnd;
    AllowDark(hwnd);
    InterlockedExchange(&g_proxyAlive, 1);
    Log(L"代理已启动 hwnd=0x%p 模块=%s 线程=%s", hwnd, base, g_dedicated ? L"专用线程" : L"Explorer桌面线程");
    return true;
}

// ---- 方式 A：专用 STA 线程（取不到桌面线程时的退路；这时取不到真实 DefView，背景菜单改用 CreateViewObject）----

static void ProxyMain() {
    if (FAILED(OleInitialize(nullptr))) { Log(L"代理线程 OleInitialize 失败"); return; }
    g_dedicated = true;
    if (!CreateProxyWindowHere()) { OleUninitialize(); return; }
    MSG m;
    while (GetMessageW(&m, nullptr, 0, 0) > 0) {
        TranslateMessage(&m);
        DispatchMessageW(&m);
    }
    UnregisterClassW(DN_PROXY_CLASS, g_hMod);
    Log(L"代理已退出");
    OleUninitialize();
}

static DWORD WINAPI ProxyThread(LPVOID) {
    __try { ProxyMain(); }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        InterlockedExchange(&g_proxyAlive, 0);
    }
    return 0;
}

// ---- 方式 B（首选）：把代理窗口建在 Explorer 的桌面（DefView）线程上 ----
// IShellView 属于桌面线程的 COM 套间，别的线程拿到的是代理对象，GetItemObject(SVGIO_BACKGROUND) 会失败（0x80040155）。
// 窗口建在桌面线程上，则菜单、GetItemObject、InvokeCommand 都在该线程内，与原生桌面右键完全一致。

static DWORD g_deskPid = 0;
static DWORD FindDesktopThread() {
    HWND progman = FindWindowW(L"Progman", nullptr);
    HWND dv = progman ? FindWindowExW(progman, nullptr, L"SHELLDLL_DefView", nullptr) : nullptr;
    if (!dv) {
        struct Ctx { HWND dv; } ctx = {nullptr};
        EnumWindows([](HWND h, LPARAM lp) -> BOOL {
            wchar_t cls[32];
            if (GetClassNameW(h, cls, ARRAYSIZE(cls)) && !wcscmp(cls, L"WorkerW")) {
                HWND d = FindWindowExW(h, nullptr, L"SHELLDLL_DefView", nullptr);
                if (d) { ((Ctx*)lp)->dv = d; return FALSE; }
            }
            return TRUE;
        }, (LPARAM)&ctx);
        dv = ctx.dv;
    }
    if (!dv) return 0;
    DWORD tid = GetWindowThreadProcessId(dv, &g_deskPid);
    return tid;
}

// 在桌面线程里被调用（WH_GETMESSAGE 钩子）：建窗口后卸钩
bool OnDesktopThreadHook() {
    if (!g_isExplorer) return false;
    if (!g_hwnd) {
        g_dedicated = false;
        CreateProxyWindowHere();
    }
    if (g_hook) {
        HHOOK h = g_hook;
        g_hook = nullptr;
        UnhookWindowsHookEx(h);
    }
    return g_hwnd != nullptr;
}

static LRESULT CALLBACK SelfHookProc(int code, WPARAM wp, LPARAM lp) {
    __try { OnDesktopThreadHook(); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    return CallNextHookEx(nullptr, code, wp, lp);
}

static bool StartOnDesktopThread() {
    DWORD tid = FindDesktopThread();
    if (!tid) { Log(L"找不到桌面 DefView 线程"); return false; }
    g_hook = SetWindowsHookExW(WH_GETMESSAGE, SelfHookProc, g_hMod, tid);
    if (!g_hook) { Log(L"自装钩子失败 err=%lu", GetLastError()); return false; }
    for (int i = 0; i < 40 && !g_hwnd; i++) {
        PostThreadMessageW(tid, WM_NULL, 0, 0);
        Sleep(25);
    }
    if (!g_hwnd) {
        HHOOK h = g_hook;
        g_hook = nullptr;
        if (h) UnhookWindowsHookEx(h);
        Log(L"桌面线程 1s 内未响应钩子，改用专用线程");
        return false;
    }
    return true;
}

void EnsureProxy() {
    if (!g_isExplorer) return;
    // 只在拥有桌面的那个 explorer.exe（外壳进程）里建代理；独立进程的文件夹窗口不建
    if (!FindDesktopThread() || g_deskPid != GetCurrentProcessId()) return;
    if (InterlockedCompareExchange(&g_started, 1, 0) != 0) return;
    // 本 DLL 里有窗口过程在运行：永久驻留，避免被卸载
    HMODULE pinned = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN | GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, (LPCWSTR)&EnsureProxy, &pinned);

    // 当前线程若就是桌面线程，直接建窗口；否则尝试在桌面线程上建；都不行才开专用线程
    DWORD tid = FindDesktopThread();
    if (tid && tid == GetCurrentThreadId()) {
        g_dedicated = false;
        if (CreateProxyWindowHere()) return;
    } else if (StartOnDesktopThread()) {
        return;
    }
    HANDLE h = CreateThread(nullptr, 0, ProxyThread, nullptr, 0, nullptr);
    if (!h) { InterlockedExchange(&g_started, 0); Log(L"创建代理线程失败 err=%lu", GetLastError()); return; }
    CloseHandle(h);
}

}  // namespace dn
