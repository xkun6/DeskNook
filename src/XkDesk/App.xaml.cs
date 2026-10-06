using System.Windows;
using System.Windows.Threading;
using XkDesk.Desktop;
using XkDesk.Services;

namespace XkDesk;

public partial class App : Application
{
    private static readonly TimeSpan ReattachTimeout = TimeSpan.FromSeconds(30);

    private Mutex? _mutex;
    private AttachMode _attach = AttachMode.Owner;
    private TransparencyMode _transparency = TransparencyMode.Layered;
    private readonly List<DesktopHostWindow> _hosts = new();
    private ShellMessageWindow? _messageWindow;
    private DispatcherTimer? _reattachTimer;
    private DispatcherTimer? _displayTimer;
    private DateTime _reattachDeadline;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例：第二个实例直接退出
        _mutex = new Mutex(true, @"Local\XkDesk.SingleInstance", out var created);
        if (!created)
        {
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        ParseArgs(e.Args);
        Log.Info($"xk-desk 启动：挂载={_attach} 透明={_transparency} 参数=[{string.Join(" ", e.Args)}]");

        // 所有异常/退出路径都恢复系统图标（RestoreIcons 幂等）
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Error("AppDomain 未处理异常", args.ExceptionObject as Exception);
            DesktopShell.RestoreIcons();
        };
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Dispatcher 未处理异常", args.Exception);
            DesktopShell.RestoreIcons(); // 不标记 Handled：恢复图标后让进程崩溃，避免桌面处于半接管状态
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("未观察到的 Task 异常", args.Exception);
            DesktopShell.RestoreIcons();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DesktopShell.RestoreIcons();

        _messageWindow = new ShellMessageWindow();
        _messageWindow.TaskbarCreated += () => StartReattach("收到 TaskbarCreated（Explorer 重启）");
        _messageWindow.DisplayChanged += OnDisplayChanged;

        if (!RebuildHosts("启动")) StartReattach("启动时未找到桌面窗口");
    }

    private void ParseArgs(string[] args)
    {
        foreach (var arg in args)
        {
            var a = arg.ToLowerInvariant();
            if (a == "--attach=child") _attach = AttachMode.Child;
            else if (a == "--attach=owner") _attach = AttachMode.Owner;
            else if (a == "--transparency=dwm") _transparency = TransparencyMode.Dwm;
            else if (a == "--transparency=layered") _transparency = TransparencyMode.Layered;
            else Log.Info($"忽略未知参数：{arg}");
        }
    }

    /// <summary>关闭旧宿主窗口 → 定位桌面 → 隐藏图标 → 每个显示器建一个宿主窗口。</summary>
    private bool RebuildHosts(string reason)
    {
        CloseHosts();

        var info = DesktopShell.FindDesktop();
        Log.Info($"重建宿主窗口（{reason}）：{info}");
        if (!info.IsValid) return false;

        DesktopShell.HideIcons(info);
        foreach (var monitor in DesktopShell.GetMonitors())
        {
            try
            {
                var host = new DesktopHostWindow(monitor, info, _attach, _transparency);
                host.Closed += OnHostClosed;
                _hosts.Add(host);
                host.Show();
            }
            catch (Exception ex)
            {
                Log.Error($"创建宿主窗口失败 {monitor.DeviceName}", ex);
            }
        }

        if (_hosts.Count == 0)
        {
            DesktopShell.RestoreIcons();
            return false;
        }
        return true;
    }

    /// <summary>关闭旧宿主窗口；窗口可能已被系统销毁（Explorer 被杀导致 owner/parent 消失），全部吞掉异常。</summary>
    private void CloseHosts()
    {
        foreach (var host in _hosts.ToArray())
        {
            host.Closed -= OnHostClosed;
            try { host.Close(); }
            catch (Exception ex) { Log.Info($"关闭旧宿主窗口时忽略异常：{ex.GetType().Name} {ex.Message}"); }
        }
        _hosts.Clear();
    }

    private void OnHostClosed(object? sender, EventArgs e)
    {
        if (sender is DesktopHostWindow host) _hosts.Remove(host);
        // 不退出：多半是 Explorer 被杀导致窗口被系统销毁，随后会收到 TaskbarCreated 重挂
        if (!_exiting) Log.Info("宿主窗口被外部关闭/销毁（不会导致程序退出）");
    }

    /// <summary>Explorer 重启：每 500ms 轮询，最多 30s，等新的桌面窗口出现后重挂。</summary>
    private void StartReattach(string reason)
    {
        if (_exiting) return;
        Log.Info($"开始重挂：{reason}");
        CloseHosts();
        _reattachDeadline = DateTime.UtcNow + ReattachTimeout;

        if (_reattachTimer == null)
        {
            _reattachTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _reattachTimer.Tick += OnReattachTick;
        }
        _reattachTimer.Start();
    }

    private void OnReattachTick(object? sender, EventArgs e)
    {
        try
        {
            if (_exiting) { _reattachTimer?.Stop(); return; }

            // 仅轮询，不发 0x052C，避免打扰尚未就绪的新 Explorer
            if (DesktopShell.FindDesktop(spawnWorkerW: false).IsValid)
            {
                _reattachTimer?.Stop();
                if (!RebuildHosts("Explorer 重启后重挂"))
                    Log.Error("重挂失败：桌面窗口已出现但挂载未成功");
            }
            else if (DateTime.UtcNow > _reattachDeadline)
            {
                _reattachTimer?.Stop();
                Log.Error("重挂超时（30s）：未等到桌面窗口");
            }
        }
        catch (Exception ex)
        {
            Log.Error("重挂轮询异常", ex);
        }
    }

    /// <summary>WM_DISPLAYCHANGE 会连发多条，去抖 600ms 后按新布局重建。</summary>
    private void OnDisplayChanged()
    {
        if (_exiting) return;
        if (_displayTimer == null)
        {
            _displayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _displayTimer.Tick += (_, _) =>
            {
                _displayTimer.Stop();
                try
                {
                    if (_exiting || _reattachTimer is { IsEnabled: true }) return;
                    if (!RebuildHosts("显示器布局变化")) StartReattach("显示器变化后未找到桌面窗口");
                }
                catch (Exception ex)
                {
                    Log.Error("显示器变化重建异常", ex);
                }
            };
        }
        _displayTimer.Stop();
        _displayTimer.Start();
    }

    /// <summary>统一退出流程：恢复图标 → 关闭所有窗口 → Shutdown。</summary>
    internal void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Info("退出流程开始");
        _reattachTimer?.Stop();
        _displayTimer?.Stop();
        DesktopShell.RestoreIcons();
        CloseHosts();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Log.Info("SessionEnding");
        DesktopShell.RestoreIcons();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        DesktopShell.RestoreIcons();
        _messageWindow?.Dispose();
        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { /* 非拥有线程等情况忽略 */ }
            _mutex.Dispose();
        }
        Log.Info("xk-desk 已退出");
        base.OnExit(e);
    }
}
