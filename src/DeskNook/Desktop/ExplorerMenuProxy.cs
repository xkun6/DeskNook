using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using DeskNook.Native;
using DeskNook.Services;

namespace DeskNook.Desktop;

/// <summary>
/// 菜单代理的 DeskNook 端：保证 DeskNookShellExt 代理已加载进 explorer.exe，通过 WM_COPYDATA 让它在 Explorer 进程里弹出右键菜单，
/// 并接收代理回报的事件（菜单关闭、被拦截的动词、已执行的命令）。
/// 注意：WM_COPYDATA 一律在后台线程发送——菜单弹出期间扩展会经命名管道回调 DeskNook，应答需要 UI 线程，在 UI 线程同步发送会死锁。
/// </summary>
internal sealed class ExplorerMenuProxy
{
    /// <summary>已发给代理、尚未关闭的菜单请求。Closed 由代理回报 closed 事件时置位。</summary>
    internal sealed record Pending(MenuContext Context, DateTime Created, string Kind) { public bool Closed { get; set; } }

    /// <summary>扩展查询不带 req 时，最多认领多久以内发起的请求。</summary>
    internal static readonly TimeSpan UntaggedWindow = TimeSpan.FromSeconds(5);

    private readonly Dispatcher _dispatcher;
    private readonly DesktopController _c;
    private readonly IntPtr _sender;
    private readonly Dictionary<string, Pending> _pending = new();
    private int _ensuring;
    private long _seq;

    /// <summary>--no-proxy：不使用代理，菜单一律走进程内回退路径。</summary>
    public static bool Disabled { get; set; }

    /// <summary>--simulate-outdated-proxy：测试用，把检测到的代理版本当作旧版（验证日志警告与托盘气泡）。</summary>
    public static bool SimulateOutdated { get; set; }

    /// <summary>代理最近一次加载是哪种方式生效：shellview / hook / 已存在 / 失败。</summary>
    public string LoadMethod { get; private set; } = "未加载";

    /// <summary>菜单关闭（用户点选或取消）后触发，UI 线程。</summary>
    public event Action? MenuClosed;

    public ExplorerMenuProxy(Dispatcher dispatcher, DesktopController controller, IntPtr senderHwnd)
    {
        _dispatcher = dispatcher;
        _c = controller;
        _sender = senderHwnd;
    }

    public static IntPtr FindProxyWindow() => Win32.FindWindow(MenuProtocol.ProxyWindowClass, null);

    // ------------------------------------------------------------ 加载

    /// <summary>后台确保代理已加载（幂等；已在进行则跳过）。</summary>
    public void EnsureLoadedAsync(string reason)
    {
        if (Disabled) return;
        if (Interlocked.Exchange(ref _ensuring, 1) == 1) return;
        var t = new Thread(() =>
        {
            try { EnsureLoaded(reason); }
            catch (Exception ex) { Log.Error("确保菜单代理加载异常", ex); }
            finally { Interlocked.Exchange(ref _ensuring, 0); }
        }) { IsBackground = true, Name = "DeskNookMenuProxyLoader" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// <summary>Explorer 里的代理窗口标题是它所属 DLL 的文件名（含内容哈希），与我们注册的不一致就说明加载的是旧版。</summary>
    internal static bool IsOutdated(string? proxyTitle, string wantDllName) =>
        wantDllName.Length > 0 && !string.IsNullOrEmpty(proxyTitle) && !string.Equals(proxyTitle, wantDllName, StringComparison.OrdinalIgnoreCase);

    /// <summary>检测到 Explorer 内仍是旧版代理组件时触发一次（参数：Explorer 内版本，期望版本），在后台线程。</summary>
    public event Action<string, string>? ComponentOutdated;
    private bool _outdatedReported;

    private void CheckVersion(string reason)
    {
        var dll = ShellExtRegistrar.RegisteredDll;
        var want = dll == null ? "" : Path.GetFileName(dll);
        var hwnd = FindProxyWindow();
        if (hwnd == IntPtr.Zero) return;
        var title = SimulateOutdated ? "DeskNookShellExt.00000000.dll" : Win32.GetWindowTextString(hwnd);
        if (!IsOutdated(title, want)) { Log.Info($"菜单组件版本一致（{reason}）：{title}"); return; }
        Log.Info($"警告：Explorer 内的菜单组件是旧版（{title}），当前版本为 {want}；重启资源管理器后生效");
        if (_outdatedReported) return;
        _outdatedReported = true;
        ComponentOutdated?.Invoke(title, want);
    }

    private void EnsureLoaded(string reason)
    {
        EnsureLoadedCore(reason);
        CheckVersion(reason);
    }

    private void EnsureLoadedCore(string reason)
    {
        var dll = ShellExtRegistrar.RegisteredDll;
        var want = dll == null ? "" : Path.GetFileName(dll);
        var hwnd = FindProxyWindow();
        if (hwnd != IntPtr.Zero)
        {
            var title = Win32.GetWindowTextString(hwnd);
            if (want.Length == 0 || string.Equals(title, want, StringComparison.OrdinalIgnoreCase))
            {
                if (LoadMethod == "未加载") LoadMethod = "已存在";
                Log.Info($"菜单代理已就绪（{reason}）hwnd=0x{hwnd:X} 模块={title} 方式={LoadMethod}");
                return;
            }
            // Explorer 里是旧版本的代理：让它退出，改由新版本重新加载
            Log.Info($"Explorer 内代理模块为旧版本（{title} ≠ {want}），请求其退出后重新加载");
            SendCopyData(hwnd, "{\"t\":\"quit\"}", 1000);
            WaitFor(() => FindProxyWindow() == IntPtr.Zero, 1500);
        }

        // 方式一：跨进程取桌面 IShellView 的背景菜单并 QueryContextMenu，让 Explorer 自己实例化我们的 handler
        if (TryLoadViaShellView(want))
        {
            LoadMethod = "shellview";
            Log.Info($"菜单代理已加载（{reason}）：方式=shellview（桌面 IShellView 背景菜单触发 handler）");
            return;
        }
        Log.Info("shellview 方式未能加载代理，改用 WH_GETMESSAGE 钩子");

        // 方式二：把 DLL 钩进桌面 DefView 所在线程
        if (TryLoadViaHook(dll, want))
        {
            LoadMethod = "hook";
            Log.Info($"菜单代理已加载（{reason}）：方式=hook（SetWindowsHookEx）");
            return;
        }
        LoadMethod = "失败";
        Log.Error($"菜单代理加载失败（{reason}）：两种方式都未生效，菜单将使用进程内回退路径");
    }

    private static bool WaitFor(Func<bool> cond, int timeoutMs)
    {
        var end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end)
        {
            if (cond()) return true;
            Thread.Sleep(50);
        }
        return cond();
    }

    private static bool ProxyUp(string want)
    {
        var h = FindProxyWindow();
        return h != IntPtr.Zero && (want.Length == 0 || string.Equals(Win32.GetWindowTextString(h), want, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryLoadViaShellView(string want)
    {
        IntPtr hmenu = IntPtr.Zero;
        try
        {
            var viewObj = SystemDesktopView.AcquireView();
            if (viewObj is not IShellView view) return false;
            var hr = view.GetItemObject(0 /* SVGIO_BACKGROUND */, ShellApi.IID_IContextMenu, out var ppv);
            if (hr < 0 || ppv == IntPtr.Zero)
            {
                Log.Info($"shellview：GetItemObject(BACKGROUND) 失败 hr=0x{hr:X}");
                return false;
            }
            try
            {
                var cm = (IContextMenu)Marshal.GetObjectForIUnknown(ppv);
                hmenu = Win32.CreatePopupMenu();
                var qhr = cm.QueryContextMenu(hmenu, 0, 1, 0x7FFF, ShellApi.CMF_NORMAL | ShellApi.CMF_EXPLORE);
                Log.Info($"shellview：QueryContextMenu hr=0x{qhr:X}");
                Marshal.ReleaseComObject(cm);
            }
            finally { Marshal.Release(ppv); }
            return WaitFor(() => ProxyUp(want), 2500);
        }
        catch (Exception ex)
        {
            Log.Error("shellview 方式加载代理异常", ex);
            return false;
        }
        finally
        {
            if (hmenu != IntPtr.Zero) Win32.DestroyMenu(hmenu);
        }
    }

    private static bool TryLoadViaHook(string? dll, string want)
    {
        if (dll == null || !File.Exists(dll)) return false;
        var lib = IntPtr.Zero;
        var hook = IntPtr.Zero;
        try
        {
            var info = DesktopShell.FindDesktop();
            if (info.DefView == IntPtr.Zero) return false;
            var tid = Win32.GetWindowThreadProcessId(info.DefView, out _);
            lib = Win32.LoadLibraryW(dll);
            if (lib == IntPtr.Zero) { Log.Info($"hook：LoadLibrary 失败 err={Marshal.GetLastWin32Error()}"); return false; }
            var proc = Win32.GetProcAddress(lib, "DnHookProc");
            if (proc == IntPtr.Zero) { Log.Info("hook：找不到 DnHookProc 导出"); return false; }
            hook = Win32.SetWindowsHookEx(Win32.WH_GETMESSAGE, proc, lib, tid);
            if (hook == IntPtr.Zero) { Log.Info($"hook：SetWindowsHookEx 失败 err={Marshal.GetLastWin32Error()}"); return false; }
            for (var i = 0; i < 20 && !ProxyUp(want); i++)
            {
                Win32.PostMessage(info.DefView, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(100);
            }
            return ProxyUp(want);
        }
        catch (Exception ex)
        {
            Log.Error("hook 方式加载代理异常", ex);
            return false;
        }
        finally
        {
            if (hook != IntPtr.Zero) Win32.UnhookWindowsHookEx(hook);
            if (lib != IntPtr.Zero) Win32.FreeLibrary(lib);
        }
    }

    // ------------------------------------------------------------ 发请求

    /// <summary>通过代理在 Explorer 进程内弹菜单。返回 false 表示代理不可用，调用方应走进程内回退路径。UI 线程调用，立即返回。</summary>
    public bool TryShow(MenuContext ctx, ProxyRequest req)
    {
        if (Disabled) return false;
        var hwnd = FindProxyWindow();
        if (hwnd == IntPtr.Zero)
        {
            Log.Info("菜单代理不存在：本次使用进程内回退菜单，并在后台重新加载代理");
            EnsureLoadedAsync("菜单请求时代理不存在");
            return false;
        }

        req.Id = $"r{Interlocked.Increment(ref _seq)}";
        req.InterceptVerbs = MenuExtensions.VerbInterceptors.Keys.ToList();
        req.IconsVisible = _c.IconsVisible;
        Purge();
        _pending[req.Id] = new Pending(ctx, DateTime.UtcNow, req.Kind);

        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        Win32.AllowSetForegroundWindow(pid);

        var json = MenuProtocol.SerializeRequest(req);
        var id = req.Id;
        Task.Run(() =>
        {
            var ok = SendCopyData(hwnd, json, 1000);
            if (ok) return;
            _dispatcher.BeginInvoke(() =>
            {
                if (!_pending.Remove(id)) return;
                Log.Error($"菜单代理 1s 内无响应（请求 {id}），回退进程内菜单");
                ShellContextMenu.Show(ctx);
            });
        });
        return true;
    }

    private bool SendCopyData(IntPtr hwnd, string json, uint timeoutMs)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var buf = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buf, bytes.Length);
            var cds = new Win32.COPYDATASTRUCT { dwData = (UIntPtr)(ulong)MenuProtocol.CopyDataMagic, cbData = bytes.Length, lpData = buf };
            var r = Win32.SendMessageTimeout(hwnd, Win32.WM_COPYDATA, (UIntPtr)(ulong)(long)_sender, ref cds,
                Win32.SMTO_ABORTIFHUNG, timeoutMs, out var result);
            return r != IntPtr.Zero && result != UIntPtr.Zero;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private void Purge()
    {
        var old = DateTime.UtcNow - TimeSpan.FromMinutes(5);
        foreach (var k in _pending.Where(kv => kv.Value.Created < old).Select(kv => kv.Key).ToList()) _pending.Remove(k);
    }

    /// <summary>DeskNook 发起的请求对应的上下文（Shell 扩展查询时用）；UI 线程。</summary>
    public MenuContext? ContextOf(string requestId) => _pending.TryGetValue(requestId, out var p) ? p.Context : null;

    /// <summary>
    /// 扩展查询没带 req 时的认领：Explorer 里可能同时有新旧两份 DeskNookShellExt（旧 handler 被 COM 缓存、新代理已换新 DLL），
    /// 请求 Id 存在各自 DLL 的线程局部变量里，旧 handler 读不到新代理设置的 Id。此时把查询归给最近一个尚未关闭、类型一致的请求。UI 线程。
    /// </summary>
    public MenuContext? ContextOfUntagged(string kind) => FindUntagged(_pending, kind, DateTime.UtcNow)?.Context;

    /// <summary>纯函数：在 <paramref name="window"/> 内选最近一个未关闭且 Kind 一致的请求；没有则 null。</summary>
    internal static Pending? FindUntagged(IReadOnlyDictionary<string, Pending> pending, string kind, DateTime now)
    {
        Pending? best = null;
        foreach (var p in pending.Values)
        {
            if (p.Closed || p.Kind != kind || now - p.Created > UntaggedWindow) continue;
            if (best == null || p.Created > best.Created) best = p;
        }
        return best;
    }

    // ------------------------------------------------------------ 收事件（UI 线程）

    public void OnEvent(ProxyEvent ev)
    {
        _pending.TryGetValue(ev.Req, out var pending);
        var ctx = pending?.Context;
        switch (ev.Type)
        {
            case "closed":
                if (pending != null) pending.Closed = true;
                if (ev.Error.Length > 0)
                {
                    Log.Error($"代理菜单失败：{ev.Error}（请求 {ev.Req}）");
                    if (ctx != null && ev.Error != "busy" && _pending.Remove(ev.Req)) ShellContextMenu.Show(ctx); // 取不到菜单对象等：回退
                }
                else Log.Info($"代理菜单已关闭（请求 {ev.Req}，选择={ev.Picked}）");
                MenuClosed?.Invoke();
                break;

            case "pick":
                Log.Info($"代理菜单选择：动词={ev.Verb} 标题={ev.Title} 父菜单={ev.Parent}");
                if (IsNewSubmenu(ev.Parent)) _c.ExpectNewItem();
                if (ev.Verb is "paste" or "pastelink" || ev.Title.StartsWith("粘贴", StringComparison.Ordinal))
                    _c.ArmUndo(ev.Verb == "pastelink" ? "创建快捷方式" : "复制");
                break;

            case "verb":
                Log.Info($"代理拦截动词：{ev.Verb}（请求 {ev.Req}）");
                if (ev.Verb == "showdesktopicons") _c.SetIconsVisible(!_c.IconsVisible);
                else if (ctx != null && MenuExtensions.VerbInterceptors.TryGetValue(ev.Verb, out var intercept))
                {
                    ctx.Verb = ev.Verb;
                    intercept(ctx);
                }
                break;

            case "invoked":
                Log.Info($"代理已执行：动词={ev.Verb} 标题={ev.Title} 父菜单={ev.Parent} DefView={ev.DefView} hr=0x{ev.Hr:X}");
                OnNativeInvoked(ev);
                break;
        }
    }

    private static bool IsNewSubmenu(string parent) =>
        parent.Equals("新建", StringComparison.Ordinal) || parent.Equals("New", StringComparison.OrdinalIgnoreCase);

    /// <summary>桌面背景菜单里“查看 / 排序方式 / 刷新”作用在隐藏的系统 ListView 上：执行后让 DeskNook 同步。</summary>
    private void OnNativeInvoked(ProxyEvent ev)
    {
        // 只对桌面背景菜单生效：格子/映射目录菜单里自定义项（排序方式/查看）与原生项同在一个菜单，标题会撞上
        if (!_pending.TryGetValue(ev.Req, out var pending) || !pending.Context.IsDesktopBackground) return;
        if (ev.Parent.StartsWith("排序方式", StringComparison.Ordinal) || ev.Parent.StartsWith("Sort by", StringComparison.OrdinalIgnoreCase))
        {
            var key = ev.Title switch
            {
                "名称" or "Name" => "name",
                "大小" or "Size" => "size",
                "项目类型" or "Item type" => "type",
                "修改日期" or "Date modified" => "date",
                _ => null,
            };
            if (key != null) _c.SortBy(key);
        }
        else if (ev.Parent.StartsWith("查看", StringComparison.Ordinal) || ev.Parent.StartsWith("View", StringComparison.OrdinalIgnoreCase))
        {
            // DefView 在自己的线程里应用，稍等再读
            var t = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (s, _) =>
            {
                ((DispatcherTimer)s!).Stop();
                _c.SyncFromSystemView();
            }, _dispatcher);
            t.Start();
        }
        else if (ev.Verb == "refresh" || ev.Title is "刷新" or "Refresh")
        {
            _c.Refresh();
        }
    }
}
