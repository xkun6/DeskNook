// DeskNookShellExt：桌面整理 的 Shell 扩展 + Explorer 内菜单代理。保持极简，业务逻辑都在 DeskNook.exe。
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

namespace dn {

// ---------- 全局状态 ----------
extern HMODULE g_hMod;
extern bool g_isExplorer;          // 宿主进程是 explorer.exe
extern volatile LONG g_objCount;   // 存活的 COM 对象数
extern volatile LONG g_proxyAlive; // 代理线程是否存活

// ---------- 日志（<数据根>\logs\shellext.log） ----------
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
// 当前线程正在处理的“代理请求 Id”（扩展在代理线程里被调用时用来告诉 DeskNook 是哪次菜单）
const wchar_t* CurrentRequestId();

// ---------- 扩展对象工厂 ----------
HRESULT CreateExtension(REFIID riid, void** ppv);

// ---------- 菜单位图 ----------
HBITMAP LoadMenuBitmap(const std::wstring& path); // 32bpp PARGB，带缓存，失败返回 nullptr

// 进程名（小写，不含路径）
std::wstring HostProcessName();

}  // namespace dn

// {EA0A2ED4-03C2-402D-A461-E558E4D20973}
DEFINE_GUID(CLSID_DeskNookContextMenu, 0xea0a2ed4, 0x03c2, 0x402d, 0xa4, 0x61, 0xe5, 0x58, 0xe4, 0xd2, 0x09, 0x73);

#define DN_PIPE_NAME L"\\\\.\\pipe\\DeskNook.Menu"
#define DN_PROXY_CLASS L"DeskNook.MenuProxy"
// 单实例互斥量（= App.xaml.cs 的 Local\DeskNook.SingleInstance，会话级，不含 SID）；打不开 = DeskNook 未运行
#define DN_MUTEX_NAME L"Local\\DeskNook.SingleInstance"
// DeskNook 启动时写入的 exe 路径（ShellExtRegistrar.AppKey / ExePathValue）
#define DN_REG_KEY L"Software\\DeskNook"
#define DN_REG_EXEPATH L"ExePath"
