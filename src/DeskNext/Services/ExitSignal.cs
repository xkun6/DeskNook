using System.Diagnostics;
using System.Security.Principal;

namespace DeskNext.Services;

/// <summary>
/// 跨会话的“请求退出”信号：首实例创建 Global 命名事件，--exit 置位它。
/// 安装包的自定义动作跑在 Windows Installer 服务（会话 0）里，FindWindow/PostMessage 够不到用户桌面会话里的窗口，
/// 命名内核对象则不受会话隔离影响（事件名带当前用户 SID，同一用户的模拟令牌可以打开）。
/// </summary>
internal sealed class ExitSignal : IDisposable
{
    private readonly EventWaitHandle _event;
    private readonly RegisteredWaitHandle _registration;

    private ExitSignal(EventWaitHandle ev, Action onSignal)
    {
        _event = ev;
        _registration = ThreadPool.RegisterWaitForSingleObject(ev, (_, _) => onSignal(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    private static string Name => @"Global\DeskNext.Exit." + (WindowsIdentity.GetCurrent().User?.Value ?? "user");

    /// <summary>首实例调用：创建事件并监听；失败返回 null（退回窗口消息通道）。onSignal 在线程池线程上触发。</summary>
    public static ExitSignal? Listen(Action onSignal)
    {
        try
        {
            var ev = new EventWaitHandle(false, EventResetMode.ManualReset, Name);
            ev.Reset();
            return new ExitSignal(ev, onSignal);
        }
        catch (Exception ex)
        {
            Log.Info($"创建退出信号失败（忽略，仍可用窗口消息退出）：{ex.Message}");
            return null;
        }
    }

    /// <summary>--exit：置位已运行实例的退出事件。没有运行中的实例（事件不存在）返回 false。</summary>
    public static bool TrySignal()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(Name);
            return ev.Set();
        }
        catch { return false; }
    }

    /// <summary>等待除自己以外的 DeskNext 进程退出（最长 timeoutMs 毫秒），避免卸载时 exe 仍被占用。</summary>
    public static void WaitOthersExit(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        try
        {
            foreach (var p in Process.GetProcessesByName("DeskNext"))
            {
                using (p)
                {
                    if (p.Id == Environment.ProcessId) continue;
                    var left = (int)Math.Max(0, deadline - Environment.TickCount64);
                    try { p.WaitForExit(left); } catch { /* 无权访问：忽略 */ }
                }
            }
        }
        catch { /* 忽略 */ }
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        _event.Dispose();
    }
}
