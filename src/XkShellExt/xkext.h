// XkShellExt：xk-desk 的 Shell 扩展 + Explorer 内菜单代理。保持极简，业务逻辑都在 XkDesk.exe。
#pragma once
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <exdisp.h>
#include <string>
#include <vector>
#include <utility>

namespace xk {

// ---------- 全局状态 ----------
extern HMODULE g_hMod;
extern bool g_isExplorer;          // 宿主进程是 explorer.exe
extern volatile LONG g_objCount;   // 存活的 COM 对象数
extern volatile LONG g_proxyAlive; // 代理线程是否存活

// ---------- 日志（%AppData%\XkDesk\logs\shellext.log） ----------
void Log(const wchar_t* fmt, ...);

// ---------- 字符串 ----------
std::string ToUtf8(const std::wstring& s);
std::wstring FromUtf8(const std::string& s);

// ---------- 极简 JSON ----------
struct JV {
    enum T { Null, Bool, Num, Str, Arr, Obj } t = Null;
    bool b = false;
    double n = 0;
    std::string s; // UTF-8
    std::vector<JV> a;
    std::vector<std::pair<std::string, JV>> o;

    const JV* Get(const char* key) const;
    std::wstring WStr(const char* key) const;         // 不存在返回空
    bool GetBool(const char* key, bool def = false) const;
    double GetNum(const char* key, double def = 0) const;
};
bool JsonParse(const std::string& text, JV& out);
std::string JsonQuote(const std::wstring& s); // 含两侧引号
std::string JsonQuoteA(const std::string& utf8);

// ---------- 命名管道客户端 ----------
bool PipeRoundTrip(const std::string& request, std::string& response, DWORD timeoutMs);
// 只发不等应答（用于代理事件）；同样带总超时
bool PipeSend(const std::string& request, DWORD timeoutMs);

// ---------- 代理 ----------
void EnsureProxy();
// 在 Explorer 桌面线程上被钩子回调：创建代理窗口
bool OnDesktopThreadHook();
// 当前线程正在处理的“代理请求 Id”（扩展在代理线程里被调用时用来告诉 XkDesk 是哪次菜单）
const wchar_t* CurrentRequestId();

// ---------- 扩展对象工厂 ----------
HRESULT CreateExtension(REFIID riid, void** ppv);

// ---------- 菜单位图 ----------
HBITMAP LoadMenuBitmap(const std::wstring& path); // 32bpp PARGB，带缓存，失败返回 nullptr

// 进程名（小写，不含路径）
std::wstring HostProcessName();

}  // namespace xk

// {B6F5C3A1-7D2E-4E0B-9C48-5A1E3F7D2B90}
DEFINE_GUID(CLSID_XkContextMenu, 0xb6f5c3a1, 0x7d2e, 0x4e0b, 0x9c, 0x48, 0x5a, 0x1e, 0x3f, 0x7d, 0x2b, 0x90);

#define XK_PIPE_NAME L"\\\\.\\pipe\\XkDesk.Menu"
#define XK_PROXY_CLASS L"XkDesk.MenuProxy"
