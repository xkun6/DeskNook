using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows.Threading;
using DeskNook.Native;
using DeskNook.Services;

namespace DeskNook.Desktop;

/// <summary>
/// 命名管道服务端 \\.\pipe\DeskNook.Menu：Shell 扩展（任意宿主进程里）与菜单代理都通过它回调 DeskNook。
/// 一行 UTF-8 JSON 请求，一行应答。查询在 UI 线程评估 MenuExtensions（150ms 超时返回空列表）；执行类请求 BeginInvoke 后立即应答。
/// </summary>
internal sealed class MenuPipeServer : IDisposable
{
    private const int QueryTimeoutMs = 150;
    private const int MaxSessions = 64;

    private readonly Dispatcher _dispatcher;
    private readonly DesktopController _c;
    private readonly ExplorerMenuProxy _proxy;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Dictionary<long, MenuExtensions.WireSession> _sessions = new();
    private readonly Queue<long> _order = new();
    private long _queryId;
    private int _stopped;

    public MenuPipeServer(Dispatcher dispatcher, DesktopController controller, ExplorerMenuProxy proxy)
    {
        _dispatcher = dispatcher;
        _c = controller;
        _proxy = proxy;
    }

    public void Start()
    {
        // 同时保持多个监听实例，避免并发连接排队
        for (var i = 0; i < 4; i++) _ = Task.Run(() => ListenLoop(_cts.Token));
        Log.Info($"菜单管道服务已启动 \\\\.\\pipe\\{MenuProtocol.PipeName}");
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(MenuProtocol.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var p = pipe;
                pipe = null;
                _ = Task.Run(() => HandleClient(p, ct), ct); // 立即回到监听
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Error("菜单管道监听异常", ex);
                try { await Task.Delay(200, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            finally { pipe?.Dispose(); }
        }
    }

    private async Task HandleClient(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(1500);
                var line = await ReadLine(pipe, timeout.Token).ConfigureAwait(false);
                if (line == null) return;
                var response = await Process(line).ConfigureAwait(false);
                var bytes = Encoding.UTF8.GetBytes(response + "\n");
                await pipe.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
                await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { /* 对端已断开（如扩展超时） */ }
            catch (Exception ex) { Log.Error("处理菜单管道请求异常", ex); }
        }
    }

    private static async Task<string?> ReadLine(Stream s, CancellationToken ct)
    {
        var ms = new MemoryStream();
        var buf = new byte[4096];
        while (ms.Length < 4 << 20)
        {
            var n = await s.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n <= 0) break;
            ms.Write(buf, 0, n);
            if (Array.IndexOf(buf, (byte)'\n', 0, n) >= 0) break;
        }
        if (ms.Length == 0) return null;
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length).TrimEnd('\r', '\n');
    }

    private async Task<string> Process(string line)
    {
        var type = MenuProtocol.ParseMessageType(line);
        switch (type)
        {
            case "query":
            {
                var q = MenuProtocol.ParseQuery(line);
                if (q == null) return MenuProtocol.SerializeQueryResult(0, Array.Empty<WireMenuItem>());
                var work = _dispatcher.InvokeAsync(() => Evaluate(q), DispatcherPriority.Send).Task;
                var done = await Task.WhenAny(work, Task.Delay(QueryTimeoutMs)).ConfigureAwait(false);
                if (done != work)
                {
                    Log.Info($"菜单查询超时（{QueryTimeoutMs}ms），返回空列表（宿主={q.Proc}）");
                    return MenuProtocol.SerializeQueryResult(0, Array.Empty<WireMenuItem>());
                }
                return await work.ConfigureAwait(false);
            }
            case "invoke":
            {
                var inv = MenuProtocol.ParseInvoke(line);
                if (inv == null) return "{\"ok\":false}";
                _ = _dispatcher.BeginInvoke(() => RunInvoke(inv.Value.Query, inv.Value.Item));
                return "{\"ok\":true}";
            }
            case "closed" or "verb" or "invoked" or "pick":
            {
                var ev = MenuProtocol.ParseEvent(line);
                if (ev != null) _ = _dispatcher.BeginInvoke(() => _proxy.OnEvent(ev));
                return "{\"ok\":true}";
            }
            default:
                return "{\"ok\":false}";
        }
    }

    // ------------------------------------------------------------ UI 线程

    private string Evaluate(MenuQuery q)
    {
        try
        {
            MenuContext? ctx;
            if (q.Req.Length > 0)
            {
                // DeskNook 自己发起的菜单：沿用发起时的上下文（选中项、格子、位置）
                ctx = _proxy.ContextOf(q.Req);
                if (ctx == null) return MenuProtocol.SerializeQueryResult(0, Array.Empty<WireMenuItem>());
            }
            else
            {
                Win32.GetCursorPos(out var pt);
                var isBg = q.Kind == "background";
                ctx = new MenuContext
                {
                    Controller = _c, Hwnd = _c.ActiveHwnd, ScreenPoint = pt, Shift = q.Shift, Source = MenuSource.Explorer,
                    Paths = isBg ? Array.Empty<string>() : q.Items, FolderPath = isBg ? q.Folder : "",
                };
            }

            var session = new MenuExtensions.WireSession { Context = ctx };
            var items = MenuExtensions.Evaluate(ctx, session);
            if (items.Count == 0) return MenuProtocol.SerializeQueryResult(0, items);

            long id;
            lock (_gate)
            {
                id = ++_queryId;
                _sessions[id] = session;
                _order.Enqueue(id);
                while (_order.Count > MaxSessions) _sessions.Remove(_order.Dequeue());
            }
            return MenuProtocol.SerializeQueryResult(id, items);
        }
        catch (Exception ex)
        {
            Log.Error("评估菜单自定义项异常", ex);
            return MenuProtocol.SerializeQueryResult(0, Array.Empty<WireMenuItem>());
        }
    }

    private void RunInvoke(long queryId, int itemId)
    {
        MenuExtensions.WireSession? session;
        lock (_gate) _sessions.TryGetValue(queryId, out session);
        if (session == null || !session.Map.TryGetValue(itemId, out var item))
        {
            Log.Info($"菜单调用找不到项 q={queryId} id={itemId}");
            return;
        }
        Log.Info($"自定义菜单项（Shell 扩展）：{item.DynamicTitle?.Invoke(session.Context) ?? item.Title}");
        var prev = MenuExtensions.Current;
        MenuExtensions.Current = session.Context;
        try { item.Handler?.Invoke(session.Context); }
        catch (Exception ex) { Log.Error("自定义菜单项执行异常", ex); }
        finally { MenuExtensions.Current = prev; }
    }

    /// <summary>预热 JIT：评估一次桌面背景与图标菜单并序列化（结果丢弃）。UI 线程。</summary>
    public void WarmUp()
    {
        try
        {
            var bg = new MenuContext { Controller = _c, Source = MenuSource.Desktop };
            var s1 = new MenuExtensions.WireSession { Context = bg };
            MenuProtocol.SerializeQueryResult(0, MenuExtensions.Evaluate(bg, s1));
            var ex = new MenuContext { Controller = _c, Source = MenuSource.Explorer, Paths = new[] { @"C:\warmup" } };
            var s2 = new MenuExtensions.WireSession { Context = ex };
            MenuProtocol.SerializeQueryResult(0, MenuExtensions.Evaluate(ex, s2));
            MenuProtocol.ParseQuery("{\"t\":\"query\",\"items\":[]}");
        }
        catch (Exception e) { Log.Error("菜单预热失败", e); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1) return;
        _cts.Cancel();
    }
}
