// Shell 扩展：IShellExtInit + IContextMenu。把上下文经命名管道交给 DeskNook，DeskNook 返回要插入的项。
#include "desknook.h"
#include <vector>

namespace dn {

namespace {

struct CmdEntry {
    int itemId; // DeskNook 项 Id；kStartCmd = 本扩展自带的“开启桌面整理”（DeskNook 未运行时）
};

const int kStartCmd = -1;

// DeskNook 是否未运行：单实例互斥量只在“确认不存在”时才算未运行（权限等其他错误按运行处理，宁可不加项）
bool IsDeskNookNotRunning() {
    HANDLE h = OpenMutexW(SYNCHRONIZE, FALSE, DN_MUTEX_NAME);
    if (h) { CloseHandle(h); return false; }
    return GetLastError() == ERROR_FILE_NOT_FOUND;
}

// 是不是“桌面”这个文件夹（用户桌面 / 公共桌面 / 桌面根）
bool IsDesktopFolder(PCIDLIST_ABSOLUTE pidl, const std::wstring& name) {
    if (!pidl || ILIsEmpty(pidl)) return true;
    if (name.empty() || _wcsicmp(name.c_str(), L"::desktop") == 0 ||
        _wcsicmp(name.c_str(), L"::{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}") == 0)
        return true;
    const KNOWNFOLDERID* ids[] = {&FOLDERID_Desktop, &FOLDERID_PublicDesktop};
    for (auto id : ids) {
        PIDLIST_ABSOLUTE k = nullptr;
        if (SUCCEEDED(SHGetKnownFolderIDList(*id, 0, nullptr, &k)) && k) {
            bool eq = ILIsEqual(pidl, k) != FALSE;
            CoTaskMemFree(k);
            if (eq) return true;
        }
        PWSTR p = nullptr;
        if (SUCCEEDED(SHGetKnownFolderPath(*id, 0, nullptr, &p)) && p) {
            bool eq = _wcsicmp(p, name.c_str()) == 0;
            CoTaskMemFree(p);
            if (eq) return true;
        }
    }
    return false;
}

// 鼠标下的窗口是否在真桌面（Progman/WorkerW 之下）：区分“桌面”与“资源管理器窗口里打开的桌面文件夹”
bool CursorOverRealDesktop() {
    POINT pt;
    if (!GetCursorPos(&pt)) return false;
    HWND w = WindowFromPoint(pt);
    for (int i = 0; w && i < 16; i++, w = GetParent(w)) {
        wchar_t cls[64] = {};
        if (GetClassNameW(w, cls, ARRAYSIZE(cls)) > 0 && (wcscmp(cls, L"Progman") == 0 || wcscmp(cls, L"WorkerW") == 0)) return true;
    }
    return false;
}

// <数据根>\icons\<name>：本 DLL 在 <数据根>\shellext\ 下；取不到返回空（菜单项就不带图标）
std::wstring IconPath(const wchar_t* name) {
    wchar_t buf[MAX_PATH * 2];
    DWORD n = GetModuleFileNameW(g_hMod, buf, ARRAYSIZE(buf));
    if (n == 0 || n >= ARRAYSIZE(buf)) return L"";
    std::wstring dir(buf);
    for (int i = 0; i < 2; ++i) { // 去掉文件名，再去掉 shellext 目录
        size_t p = dir.find_last_of(L'\\');
        if (p == std::wstring::npos) return L"";
        dir.resize(p);
    }
    return dir + L"\\icons\\" + name;
}

// 读 HKCU\Software\DeskNook\ExePath；不存在/不是文件返回空
std::wstring ReadExePath() {
    wchar_t buf[2048];
    memset(buf, 0, sizeof(buf));
    DWORD cb = sizeof(buf) - sizeof(wchar_t); // 留出结尾 NUL
    if (RegGetValueW(HKEY_CURRENT_USER, DN_REG_KEY, DN_REG_EXEPATH, RRF_RT_REG_SZ, nullptr, buf, &cb) != ERROR_SUCCESS) return L"";
    DWORD attr = GetFileAttributesW(buf);
    if (attr == INVALID_FILE_ATTRIBUTES || (attr & FILE_ATTRIBUTE_DIRECTORY)) return L"";
    return buf;
}

// 启动 DeskNook（不等待）；工作目录 = exe 所在目录
bool StartDeskNook() {
    std::wstring exe = ReadExePath();
    if (exe.empty()) { Log(L"开启桌面整理：注册表里没有可用的 ExePath，放弃"); return false; }
    std::wstring dir = exe;
    size_t p = dir.find_last_of(L'\\');
    if (p != std::wstring::npos) dir.resize(p);
    std::wstring cmd = L"\"" + exe + L"\"";
    STARTUPINFOW si = {};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi = {};
    if (!CreateProcessW(exe.c_str(), &cmd[0], nullptr, nullptr, FALSE, 0, nullptr, dir.c_str(), &si, &pi)) {
        Log(L"开启桌面整理：CreateProcess 失败 err=%lu", GetLastError());
        return false;
    }
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    Log(L"开启桌面整理：已启动 DeskNook");
    return true;
}

class DnExt : public IShellExtInit, public IContextMenu {
public:
    DnExt() : ref_(1) { InterlockedIncrement(&g_objCount); }
    ~DnExt() { InterlockedDecrement(&g_objCount); }

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == IID_IShellExtInit) *ppv = static_cast<IShellExtInit*>(this);
        else if (riid == IID_IContextMenu) *ppv = static_cast<IContextMenu*>(this);
        else return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return (ULONG)InterlockedIncrement(&ref_); }
    STDMETHODIMP_(ULONG) Release() override {
        LONG r = InterlockedDecrement(&ref_);
        if (r == 0) delete this;
        return (ULONG)r;
    }

    // IShellExtInit
    STDMETHODIMP Initialize(PCIDLIST_ABSOLUTE pidlFolder, IDataObject* pdtobj, HKEY) override {
        try {
            items_.clear();
            folder_.clear();
            background_ = false;
            desktop_ = false;
            if (pdtobj) {
                IShellItemArray* arr = nullptr;
                if (SUCCEEDED(SHCreateShellItemArrayFromDataObject(pdtobj, IID_PPV_ARGS(&arr))) && arr) {
                    DWORD n = 0;
                    arr->GetCount(&n);
                    if (n > 4096) n = 4096;
                    for (DWORD i = 0; i < n; i++) {
                        IShellItem* it = nullptr;
                        if (FAILED(arr->GetItemAt(i, &it)) || !it) continue;
                        PWSTR name = nullptr;
                        if (SUCCEEDED(it->GetDisplayName(SIGDN_DESKTOPABSOLUTEPARSING, &name)) && name) {
                            items_.push_back(name);
                            CoTaskMemFree(name);
                        }
                        it->Release();
                    }
                    arr->Release();
                }
            } else if (pidlFolder) {
                background_ = true;
                PWSTR name = nullptr;
                if (SUCCEEDED(SHGetNameFromIDList(pidlFolder, SIGDN_DESKTOPABSOLUTEPARSING, &name)) && name) {
                    folder_ = name;
                    CoTaskMemFree(name);
                }
                desktop_ = IsDesktopFolder(pidlFolder, folder_);
            } else {
                return E_INVALIDARG;
            }
            return S_OK;
        } catch (...) {
            return E_FAIL;
        }
    }

    // IContextMenu
    STDMETHODIMP QueryContextMenu(HMENU hmenu, UINT indexMenu, UINT idCmdFirst, UINT idCmdLast, UINT uFlags) override {
        if (uFlags & CMF_DEFAULTONLY) return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, 0);
        HRESULT hr = MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, 0);
        __try { hr = Query(hmenu, indexMenu, idCmdFirst, idCmdLast, uFlags); }
        __except (EXCEPTION_EXECUTE_HANDLER) { Log(L"QueryContextMenu 异常 0x%08X", GetExceptionCode()); hr = MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, 0); }
        return hr;
    }

    STDMETHODIMP InvokeCommand(CMINVOKECOMMANDINFO* pici) override {
        if (!pici) return E_INVALIDARG;
        HRESULT hr = E_FAIL;
        __try { hr = Invoke(pici); }
        __except (EXCEPTION_EXECUTE_HANDLER) { Log(L"InvokeCommand 异常 0x%08X", GetExceptionCode()); hr = E_FAIL; }
        return hr;
    }

    STDMETHODIMP GetCommandString(UINT_PTR idCmd, UINT uType, UINT*, CHAR* pszName, UINT cchMax) override {
        if (idCmd >= cmds_.size()) return E_INVALIDARG;
        if (uType == GCS_VALIDATEA || uType == GCS_VALIDATEW) return S_OK;
        if (!pszName || cchMax == 0) return E_INVALIDARG;
        if (uType == GCS_VERBW) { if (cmds_[idCmd].itemId == kStartCmd) wcsncpy_s((wchar_t*)pszName, cchMax, L"desknook.start", _TRUNCATE); else _snwprintf_s((wchar_t*)pszName, cchMax, _TRUNCATE, L"desknook.%d", cmds_[idCmd].itemId); return S_OK; }
        if (uType == GCS_VERBA) { if (cmds_[idCmd].itemId == kStartCmd) strncpy_s(pszName, cchMax, "desknook.start", _TRUNCATE); else _snprintf_s(pszName, cchMax, _TRUNCATE, "desknook.%d", cmds_[idCmd].itemId); return S_OK; }
        if (uType == GCS_HELPTEXTW) { ((wchar_t*)pszName)[0] = 0; return S_OK; }
        if (uType == GCS_HELPTEXTA) { pszName[0] = 0; return S_OK; }
        return E_INVALIDARG;
    }

private:
    LONG ref_;
    std::vector<std::wstring> items_;
    std::wstring folder_;
    bool background_ = false;
    bool desktop_ = false; // 背景菜单且文件夹是桌面（用户桌面/公共桌面/桌面根）

    std::vector<CmdEntry> cmds_;
    long long queryId_ = 0;
    std::wstring req_;

    // ---- 把 DeskNook 返回的项插入菜单 ----
    // 返回新增的命令数
    bool InsertItems(HMENU menu, const JV& arr, UINT& topPos, UINT& handlerPos, UINT first, UINT last, bool isSub) {
        for (auto& it : arr.a) {
            if (it.t != JV::Obj) continue;
            std::string pos = it.Get("pos") && it.Get("pos")->t == JV::Str ? it.Get("pos")->s : "";
            UINT* at = isSub ? &handlerPos : (pos == "top" ? &topPos : &handlerPos);

            MENUITEMINFOW mi = {};
            mi.cbSize = sizeof(mi);
            if (it.GetBool("sep")) {
                mi.fMask = MIIM_FTYPE;
                mi.fType = MFT_SEPARATOR;
                if (InsertMenuItemW(menu, *at, TRUE, &mi)) {
                    (*at)++;
                    if (!isSub && at == &topPos) handlerPos++;
                }
                continue;
            }

            std::wstring title = it.WStr("title");
            const JV* children = it.Get("children");
            mi.fMask = MIIM_STRING | MIIM_STATE | MIIM_FTYPE;
            mi.dwTypeData = const_cast<wchar_t*>(title.c_str());
            mi.fType = MFT_STRING | (it.GetBool("radio") ? MFT_RADIOCHECK : 0);
            mi.fState = (it.GetBool("enabled", true) ? MFS_ENABLED : MFS_DISABLED) | (it.GetBool("checked") ? MFS_CHECKED : 0);

            std::wstring icon = it.WStr("icon");
            if (!icon.empty()) {
                HBITMAP bmp = LoadMenuBitmap(icon);
                if (bmp) { mi.fMask |= MIIM_BITMAP; mi.hbmpItem = bmp; }
            }

            HMENU sub = nullptr;
            if (children && children->t == JV::Arr && !children->a.empty()) {
                sub = CreatePopupMenu();
                UINT subTop = 0, subPos = 0;
                InsertItems(sub, *children, subTop, subPos, first, last, true);
                mi.fMask |= MIIM_SUBMENU;
                mi.hSubMenu = sub;
            } else {
                if (cmds_.size() >= 0x1000 || first + (UINT)cmds_.size() > last) continue;
                mi.fMask |= MIIM_ID;
                mi.wID = first + (UINT)cmds_.size();
                cmds_.push_back({(int)it.GetNum("id")});
            }
            if (InsertMenuItemW(menu, *at, TRUE, &mi)) {
                (*at)++;
                if (!isSub && at == &topPos) handlerPos++;
            } else {
                if (sub) DestroyMenu(sub);
                if (!sub && !cmds_.empty() && (mi.fMask & MIIM_ID)) cmds_.pop_back();
            }
        }
        return true;
    }

    HRESULT Query(HMENU hmenu, UINT indexMenu, UINT idCmdFirst, UINT idCmdLast, UINT uFlags) {
        cmds_.clear();
        queryId_ = 0;
        Log(L"QueryContextMenu flags=0x%X index=%u first=%u last=%u %s folder=%s items=%u", uFlags, indexMenu, idCmdFirst, idCmdLast,
            background_ ? L"背景" : L"项", folder_.c_str(), (unsigned)items_.size());
        req_ = CurrentRequestId();

        std::string req = "{\"t\":\"query\",\"kind\":\"";
        req += background_ ? "background" : "item";
        req += "\",\"folder\":" + JsonQuote(folder_) + ",\"items\":[";
        for (size_t i = 0; i < items_.size(); i++) {
            if (i) req += ',';
            req += JsonQuote(items_[i]);
        }
        req += "],\"shift\":";
        req += (uFlags & CMF_EXTENDEDVERBS) ? "true" : "false";
        req += ",\"proc\":" + JsonQuote(HostProcessName());
        req += ",\"pid\":" + std::to_string(GetCurrentProcessId());
        req += ",\"req\":" + JsonQuote(req_) + "}";

        std::string resp;
        if (!PipeRoundTrip(req, resp, 200)) return QueryNotRunning(hmenu, indexMenu, idCmdFirst, idCmdLast);

        JV root;
        if (!JsonParse(resp, root) || root.t != JV::Obj) {
            Log(L"查询应答无法解析");
            return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, 0);
        }
        queryId_ = (long long)root.GetNum("q");
        const JV* arr = root.Get("items");
        if (!arr || arr->t != JV::Arr || arr->a.empty()) return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, 0);

        UINT topPos = 0, handlerPos = indexMenu;
        InsertItems(hmenu, *arr, topPos, handlerPos, idCmdFirst, idCmdLast, false);
        Log(L"插入 %u 个命令（宿主=%s，%s，req=%s）", (unsigned)cmds_.size(), HostProcessName().c_str(),
            background_ ? L"背景" : L"项", req_.c_str());
        return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, (USHORT)cmds_.size());
    }

    // DeskNook 未运行时（互斥量不存在）在真桌面背景菜单里加“桌面整理 ▸ 开启桌面整理”；其他情况什么都不加
    HRESULT QueryNotRunning(HMENU hmenu, UINT indexMenu, UINT idCmdFirst, UINT idCmdLast) {
        const HRESULT none = MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, 0);
        if (!background_ || !desktop_ || idCmdFirst > idCmdLast) return none;
        if (!IsDeskNookNotRunning() || !CursorOverRealDesktop()) return none;

        HMENU sub = CreatePopupMenu();
        if (!sub) return none;
        std::wstring childTitle = L"开启桌面整理";
        MENUITEMINFOW child = {};
        child.cbSize = sizeof(child);
        child.fMask = MIIM_STRING | MIIM_STATE | MIIM_FTYPE | MIIM_ID;
        child.fType = MFT_STRING;
        child.fState = MFS_ENABLED;
        child.wID = idCmdFirst;
        child.dwTypeData = const_cast<wchar_t*>(childTitle.c_str());
        if (!InsertMenuItemW(sub, 0, TRUE, &child)) { DestroyMenu(sub); return none; }

        std::wstring topTitle = L"桌面整理(&D)";
        MENUITEMINFOW top = {};
        top.cbSize = sizeof(top);
        top.fMask = MIIM_STRING | MIIM_STATE | MIIM_FTYPE | MIIM_SUBMENU;
        top.fType = MFT_STRING;
        top.fState = MFS_ENABLED;
        top.hSubMenu = sub;
        top.dwTypeData = const_cast<wchar_t*>(topTitle.c_str());
        std::wstring icon = IconPath(L"menu-app.ico"); // 取不到/解码失败就不带图标
        if (!icon.empty()) {
            HBITMAP bmp = LoadMenuBitmap(icon);
            if (bmp) { top.fMask |= MIIM_BITMAP; top.hbmpItem = bmp; }
        }
        if (!InsertMenuItemW(hmenu, indexMenu, TRUE, &top)) { DestroyMenu(sub); return none; }

        cmds_.push_back({kStartCmd});
        Log(L"DeskNook 未运行：插入“开启桌面整理”（宿主=%s）", HostProcessName().c_str());
        return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, 1);
    }

    HRESULT Invoke(CMINVOKECOMMANDINFO* pici) {
        UINT offset;
        if (HIWORD(pici->lpVerb) != 0) {
            // 动词字符串：desknook.<id> / desknook.start
            const char* v = pici->lpVerb;
            if (strcmp(v, "desknook.start") == 0) return StartDeskNook() ? S_OK : E_FAIL;
            if (strncmp(v, "desknook.", 7) != 0) return E_INVALIDARG;
            int id = atoi(v + 7);
            return Send(id);
        }
        offset = LOWORD(pici->lpVerb);
        if (offset >= cmds_.size()) return E_INVALIDARG;
        if (cmds_[offset].itemId == kStartCmd) return StartDeskNook() ? S_OK : E_FAIL;
        return Send(cmds_[offset].itemId);
    }

    HRESULT Send(int itemId) {
        std::string req = "{\"t\":\"invoke\",\"q\":" + std::to_string(queryId_) + ",\"id\":" + std::to_string(itemId) +
                          ",\"req\":" + JsonQuote(req_) + "}";
        std::string resp;
        bool ok = PipeRoundTrip(req, resp, 500);
        Log(L"调用 q=%lld id=%d %s", queryId_, itemId, ok ? L"已发送" : L"失败（DeskNook 未响应）");
        return ok ? S_OK : E_FAIL;
    }
};

class DnFactory : public IClassFactory {
public:
    DnFactory() : ref_(1) { InterlockedIncrement(&g_objCount); }
    ~DnFactory() { InterlockedDecrement(&g_objCount); }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IClassFactory) { *ppv = static_cast<IClassFactory*>(this); AddRef(); return S_OK; }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return (ULONG)InterlockedIncrement(&ref_); }
    STDMETHODIMP_(ULONG) Release() override {
        LONG r = InterlockedDecrement(&ref_);
        if (r == 0) delete this;
        return (ULONG)r;
    }
    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        return CreateExtension(riid, ppv);
    }
    STDMETHODIMP LockServer(BOOL lock) override {
        if (lock) InterlockedIncrement(&g_objCount); else InterlockedDecrement(&g_objCount);
        return S_OK;
    }

private:
    LONG ref_;
};

}  // namespace

HRESULT CreateExtension(REFIID riid, void** ppv) {
    try {
        DnExt* e = new DnExt();
        HRESULT hr = e->QueryInterface(riid, ppv);
        e->Release();
        return hr;
    } catch (...) {
        return E_OUTOFMEMORY;
    }
}

HRESULT CreateFactory(REFIID riid, void** ppv) {
    try {
        DnFactory* f = new DnFactory();
        HRESULT hr = f->QueryInterface(riid, ppv);
        f->Release();
        return hr;
    } catch (...) {
        return E_OUTOFMEMORY;
    }
}

}  // namespace dn
