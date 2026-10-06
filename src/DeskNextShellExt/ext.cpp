// Shell 扩展：IShellExtInit + IContextMenu。把上下文经命名管道交给 DeskNext，DeskNext 返回要插入的项。
#include "desknext.h"
#include <vector>

namespace dn {

namespace {

struct CmdEntry {
    int itemId;
};

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
        if (uType == GCS_VERBW) { _snwprintf_s((wchar_t*)pszName, cchMax, _TRUNCATE, L"desknext.%d", cmds_[idCmd].itemId); return S_OK; }
        if (uType == GCS_VERBA) { _snprintf_s(pszName, cchMax, _TRUNCATE, "desknext.%d", cmds_[idCmd].itemId); return S_OK; }
        if (uType == GCS_HELPTEXTW) { ((wchar_t*)pszName)[0] = 0; return S_OK; }
        if (uType == GCS_HELPTEXTA) { pszName[0] = 0; return S_OK; }
        return E_INVALIDARG;
    }

private:
    LONG ref_;
    std::vector<std::wstring> items_;
    std::wstring folder_;
    bool background_ = false;

    std::vector<CmdEntry> cmds_;
    long long queryId_ = 0;
    std::wstring req_;

    // ---- 把 DeskNext 返回的项插入菜单 ----
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
        if (!PipeRoundTrip(req, resp, 200)) return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, 0);

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

    HRESULT Invoke(CMINVOKECOMMANDINFO* pici) {
        UINT offset;
        if (HIWORD(pici->lpVerb) != 0) {
            // 动词字符串：desknext.<id>
            const char* v = pici->lpVerb;
            if (strncmp(v, "desknext.", 7) != 0) return E_INVALIDARG;
            int id = atoi(v + 7);
            return Send(id);
        }
        offset = LOWORD(pici->lpVerb);
        if (offset >= cmds_.size()) return E_INVALIDARG;
        return Send(cmds_[offset].itemId);
    }

    HRESULT Send(int itemId) {
        std::string req = "{\"t\":\"invoke\",\"q\":" + std::to_string(queryId_) + ",\"id\":" + std::to_string(itemId) +
                          ",\"req\":" + JsonQuote(req_) + "}";
        std::string resp;
        bool ok = PipeRoundTrip(req, resp, 500);
        Log(L"调用 q=%lld id=%d %s", queryId_, itemId, ok ? L"已发送" : L"失败（DeskNext 未响应）");
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
