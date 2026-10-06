#include "desknook.h"

namespace dn {
HMODULE g_hMod = nullptr;
bool g_isExplorer = false;
volatile LONG g_objCount = 0;
volatile LONG g_proxyAlive = 0;
HRESULT CreateFactory(REFIID riid, void** ppv);
}  // namespace dn

using namespace dn;

extern "C" const GUID CLSID_DeskNookContextMenu = {0xea0a2ed4, 0x03c2, 0x402d, {0xa4, 0x61, 0xe5, 0x58, 0xe4, 0xd2, 0x09, 0x73}};

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
        if (IsEqualCLSID(rclsid, CLSID_DeskNookContextMenu)) {
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

// 备用加载方式：DeskNook 用 SetWindowsHookEx(WH_GETMESSAGE) 把本 DLL 带进 Explorer 桌面线程，回调里启动代理。
extern "C" LRESULT CALLBACK DnHookProc(int code, WPARAM wp, LPARAM lp) {
    __try {
        if (g_isExplorer && !g_proxyAlive) OnDesktopThreadHook();
    } __except (EXCEPTION_EXECUTE_HANDLER) {
    }
    return CallNextHookEx(nullptr, code, wp, lp);
}
