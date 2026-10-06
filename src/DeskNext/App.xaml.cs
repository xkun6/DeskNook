using System.IO;
using System.Windows;
using System.Windows.Threading;
using DeskNext.Desktop;
using DeskNext.Native;
using DeskNext.Services;
using DeskNext.Views;

namespace DeskNext;

public partial class App : Application
{
    private static readonly TimeSpan ReattachTimeout = TimeSpan.FromSeconds(30);

    private Mutex? _mutex;
    private AttachMode _attach = AttachMode.Owner;
    private TransparencyMode _transparency = TransparencyMode.Dwm; // 阶段 0 实测选定 owner + dwm
    private DesktopController? _controller;
    private readonly List<DesktopHostWindow> _hosts = new();
    private ShellMessageWindow? _messageWindow;
    private ExplorerMenuProxy? _menuProxy;
    private MenuPipeServer? _pipeServer;
    private TrayIcon? _tray;
    private static readonly string RunningFlag = AppPaths.RunningFlag;
    private DispatcherTimer? _reattachTimer;
    private DispatcherTimer? _displayTimer;
    private DateTime _reattachDeadline;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 菜单深色模式（须在创建任何窗口前）：AllowDark = 跟随系统设置，与 Explorer 一致
        try { Win32.SetPreferredAppMode(1); Win32.FlushMenuThemes(); }
        catch (Exception ex) { Log.Info($"设置菜单深色模式失败（忽略）：{ex.Message}"); }
        base.OnStartup(e);

        // DeskNext.exe --exit：通知已运行的实例退出（自动化测试用），自己不启动
        if (e.Args.Any(a => a.Equals("--exit", StringComparison.OrdinalIgnoreCase)))
        {
            var target = Win32.FindWindow(null, ShellMessageWindow.WindowName);
            if (target != IntPtr.Zero) Win32.PostMessage(target, Win32.RegisterWindowMessage(ShellMessageWindow.ExitMessageName), IntPtr.Zero, IntPtr.Zero);
            Shutdown();
            return;
        }

        // DeskNext.exe --unregister：删除 Shell 扩展的全部注册项后退出（用户卸载用）
        if (e.Args.Any(a => a.Equals("--unregister", StringComparison.OrdinalIgnoreCase)))
        {
            ShellExtRegistrar.Unregister();
            Shutdown();
            return;
        }

        // 单实例：第二个实例直接退出
        _mutex = new Mutex(true, @"Local\DeskNext.SingleInstance", out var created);
        if (!created)
        {
            _mutex.Dispose();
            _mutex = null;
            NotifyFirstInstance();
            Shutdown();
            return;
        }

        CheckLastRunAndMarkRunning();
        ParseArgs(e.Args);
        Log.Info($"桌面整理 启动：挂载={_attach} 透明={_transparency} 参数=[{string.Join(" ", e.Args)}]");

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
        _messageWindow.TaskbarCreated += () => _tray?.Recreate();
        _messageWindow.SettingsRequested += () =>
        {
            Log.Info("收到第二个实例的请求：打开设置窗口");
            if (_controller != null) SettingsWindow.ShowSingleton(_controller);
        };
        _messageWindow.DisplayChanged += OnDisplayChanged;
        _messageWindow.ExitRequested += () =>
        {
            Log.Info("收到 --exit 退出请求");
            ExitApp();
        };

        Win32.OleInitialize(IntPtr.Zero); // 剪贴板 / 拖放需要 OLE
        _controller = new DesktopController(Dispatcher);
        _controller.Initialize(DesktopShell.FindDesktop().ListView);

        try { Win32.AllowDarkModeForWindow(_messageWindow.Handle, true); } catch { /* 忽略 */ }
        MenuIcons.EnsureExtracted();
        _tray = new TrayIcon(_messageWindow, _controller, AutoStart.Default, () => SettingsWindow.ShowSingleton(_controller), ExitApp);
        _tray.Recreate();

        // 右键菜单 v2：注册 Shell 扩展、启动菜单管道、确保 Explorer 里的菜单代理已加载
        if (!ExplorerMenuProxy.Disabled)
        {
            var registered = ShellExtRegistrar.EnsureRegistered();
            _menuProxy = new ExplorerMenuProxy(Dispatcher, _controller, _messageWindow.Handle);
            _controller.MenuProxy = _menuProxy;
            _menuProxy.ComponentOutdated += (_, _) => Dispatcher.BeginInvoke(() =>
                _tray?.ShowBalloon("桌面整理", "已更新右键菜单组件，重启资源管理器后生效"));
            _pipeServer = new MenuPipeServer(Dispatcher, _controller, _menuProxy);
            _pipeServer.Start();
            if (registered) _menuProxy.EnsureLoadedAsync("启动");
            // 预热：首次评估菜单/JSON 序列化会 JIT，容易超过扩展 150ms 的查询超时
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _pipeServer?.WarmUp());
        }

        if (!RebuildHosts("启动")) StartReattach("启动时未找到桌面窗口");
    }

    /// <summary>第二个实例：让首实例打开设置窗口（并把前台权限让给它）。</summary>
    private static void NotifyFirstInstance()
    {
        try
        {
            var target = Win32.FindWindow(null, ShellMessageWindow.WindowName);
            if (target == IntPtr.Zero) return;
            Win32.GetWindowThreadProcessId(target, out var pid);
            Win32.AllowSetForegroundWindow(pid);
            Win32.PostMessage(target, Win32.RegisterWindowMessage(ShellMessageWindow.ShowSettingsMessageName), IntPtr.Zero, IntPtr.Zero);
        }
        catch { /* 忽略 */ }
    }

    /// <summary>崩溃兜底：上次若异常退出（标记文件残留）只记日志；然后写入本次的运行标记。</summary>
    private static void CheckLastRunAndMarkRunning()
    {
        try
        {
            if (File.Exists(RunningFlag))
                Log.Info($"检测到上次异常退出（运行标记残留：{File.GetLastWriteTime(RunningFlag):yyyy-MM-dd HH:mm:ss}），正常继续启动");
            Directory.CreateDirectory(Path.GetDirectoryName(RunningFlag)!);
            File.WriteAllText(RunningFlag, Environment.ProcessId.ToString());
        }
        catch (Exception ex) { Log.Info($"写入运行标记失败（忽略）：{ex.Message}"); }
    }

    private static void ClearRunningFlag()
    {
        try { File.Delete(RunningFlag); } catch { /* 忽略 */ }
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
            else if (a == "--no-proxy") ExplorerMenuProxy.Disabled = true;
            else if (a == "--simulate-outdated-proxy") ExplorerMenuProxy.SimulateOutdated = true;
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
        _controller!.UpdateMonitors();
        foreach (var monitor in DesktopShell.GetMonitors())
        {
            try
            {
                var host = new DesktopHostWindow(monitor, info, _attach, _transparency, _controller!);
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

            // 仅轮询查找
            if (DesktopShell.FindDesktop().IsValid)
            {
                _reattachTimer?.Stop();
                if (!RebuildHosts("Explorer 重启后重挂"))
                    Log.Error("重挂失败：桌面窗口已出现但挂载未成功");
                else _menuProxy?.EnsureLoadedAsync("Explorer 重启后重挂");
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
        _tray?.Dispose();
        _tray = null;
        _pipeServer?.Dispose();
        _pipeServer = null;
        _controller?.Dispose();
        _controller = null;
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Log.Info("SessionEnding");
        DesktopShell.RestoreIcons();
        ClearRunningFlag();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        DesktopShell.RestoreIcons();
        _tray?.Dispose();
        _messageWindow?.Dispose();
        if (_mutex != null) ClearRunningFlag(); // 只有首实例（持有互斥量）才会写过标记
        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { /* 非拥有线程等情况忽略 */ }
            _mutex.Dispose();
        }
        Log.Info("桌面整理 已退出");
        base.OnExit(e);
    }
}
