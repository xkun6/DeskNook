#include "xkext.h"

namespace xk {
HMODULE g_hMod = nullptr;
bool g_isExplorer = false;
volatile LONG g_objCount = 0;
volatile LONG g_proxyAlive = 0;
HRESULT CreateFactory(REFIID riid, void** ppv);
}  // namespace xk

using namespace xk;

extern "C" const GUID CLSID_XkContextMenu = {0xb6f5c3a1, 0x7d2e, 0x4e0b, {0x9c, 0x48, 0x5a, 0x1e, 0x3f, 0x7d, 0x2b, 0x90}};

BOOL APIENTRY DllMain(HMODULE hMod, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_hMod = hMod;
        DisableThreadLibraryCalls(hMod);
        g_isExplorer = HostProcessName() == L"explorer.exe";
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv) {
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    HRESULT hr = CLASS_E_CLASSNOTAVAILABLE;
    __try {
        if (IsEqualCLSID(rclsid, CLSID_XkContextMenu)) {
            EnsureProxy(); // 只在 explorer.exe 里生效
            hr = CreateFactory(riid, ppv);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        hr = E_UNEXPECTED;
    }
    return hr;
}

STDAPI DllCanUnloadNow() {
    return (g_objCount == 0 && g_proxyAlive == 0) ? S_OK : S_FALSE;
}

// 备用加载方式：XkDesk 用 SetWindowsHookEx(WH_GETMESSAGE) 把本 DLL 带进 Explorer 桌面线程，回调里启动代理。
extern "C" LRESULT CALLBACK XkHookProc(int code, WPARAM wp, LPARAM lp) {
    __try {
        if (g_isExplorer && !g_proxyAlive) OnDesktopThreadHook();
    } __except (EXCEPTION_EXECUTE_HANDLER) {
    }
    return CallNextHookEx(nullptr, code, wp, lp);
}
