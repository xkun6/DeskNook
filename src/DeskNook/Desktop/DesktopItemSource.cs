using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using DeskNook.Model;
using DeskNook.Native;
using DeskNook.Services;

namespace DeskNook.Desktop;

/// <summary>需要问系统桌面视图“是否显示”的虚拟项候选：解析名 + PIDL 字节拷贝（可安全交给后台线程）。</summary>
internal readonly record struct ShownCandidate(string Key, byte[] Pidl);

/// <summary>
/// 桌面项来源：枚举桌面根文件夹（用户桌面 + 公共桌面 + 虚拟项），
/// 用 SHChangeNotifyRegister 监听变化，300ms 去抖后重新枚举并做 diff。
/// 全部在 UI(STA) 线程使用。
/// </summary>
internal sealed class DesktopItemSource : IDisposable
{
    private const int WmNotify = Win32.WM_APP + 1;
    /// <summary>映射格子内项 Key 的前缀分隔符：格子Id + 分隔符 + 路径。</summary>
    public const char KeySeparator = (char)0x1F;

    private readonly HwndSource _window;
    private readonly DispatcherTimer _debounce;
    private readonly List<IntPtr> _registeredPidls = new();
    private readonly Dictionary<string, string> _renameHints = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<uint> _notifyIds = new();
    private readonly string? _mappedPath;
    private readonly string _container;
    private readonly string _prefix;
    /// <summary>非桌面目录项（虚拟项）“系统桌面是否显示”的结果缓存：key = 解析名。询问系统视图是跨进程 COM 调用，Explorer 桌面线程忙时会阻塞数秒（实测 8.6 秒），所以只有构造时的首次枚举同步询问，之后一律只读缓存，由后台线程复核。</summary>
    private readonly Dictionary<string, bool> _shownCache = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>本批通知里出现了可能改变虚拟项显示状态的事件：下次刷新要后台复核（不清缓存）。</summary>
    private bool _resetShownCache;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    /// <summary>最近一次枚举得到的候选项（UI 线程读写）。</summary>
    private List<ShownCandidate> _candidates = new();
    private BlockingCollection<List<ShownCandidate>>? _recheckQueue;
    private Thread? _recheckThread;
    /// <summary>后台复核进行中（UI 线程读写）。</summary>
    private bool _recheckRunning;
    /// <summary>复核进行中又有新请求：完成后再跑一轮。</summary>
    private bool _recheckAgain;
    private bool _disposed;
    private List<DesktopItem> _items = new();

    public IReadOnlyList<DesktopItem> Items => _items;

    /// <summary>去抖后的增量结果（UI 线程）。</summary>
    public event Action<ItemDiffResult>? Changed;

    /// <summary>图标需要重新加载：key 为 null 表示全部。</summary>
    public event Action<string?>? IconInvalidated;

    /// <summary>mappedPath 为 null = 桌面；否则监听并枚举该目录（映射格子），container 为映射格子 Id，其项的 Key 带该前缀。</summary>
    public DesktopItemSource(string? mappedPath = null, string container = "")
    {
        _mappedPath = mappedPath;
        _container = container;
        _prefix = container.Length == 0 ? "" : container + KeySeparator;
        var p = new HwndSourceParameters("DeskNookItemNotify")
        {
            WindowStyle = unchecked((int)Win32.WS_POPUP),
            ExtendedWindowStyle = (int)Win32.WS_EX_TOOLWINDOW,
            Width = 0,
            Height = 0,
        };
        _window = new HwndSource(p);
        _window.AddHook(WndProc);

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            try { Refresh(useShownCache: true); }
            catch (Exception ex) { Log.Error("桌面项刷新失败", ex); }
        };

        var firstCandidates = _mappedPath == null ? new List<ShownCandidate>() : null;
        _items = Load(firstCandidates, askMissing: true);
        if (firstCandidates != null) _candidates = firstCandidates;
        Log.Info($"{(_mappedPath == null ? "桌面项" : "映射目录 " + _mappedPath)} 枚举完成：{_items.Count} 项");
        Register();
    }

    /// <summary>
    /// 立即重新枚举并与旧集合 diff，有变化则触发 Changed。
    /// 枚举只读虚拟项的“系统桌面是否显示”缓存，UI 线程永不等待 Explorer；缓存没有的虚拟项先按“不显示”处理。
    /// useShownCache=false（显式刷新）或本批通知置位了 _resetShownCache 时，另起后台线程复核，有变化再刷一次。
    /// </summary>
    public void Refresh(bool useShownCache = false)
    {
        var sw = Stopwatch.StartNew();
        var recheck = !useShownCache || _resetShownCache;
        _resetShownCache = false;
        var hints = _renameHints.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        _renameHints.Clear();
        var candidates = _mappedPath == null ? new List<ShownCandidate>() : null;
        var fresh = Load(candidates, askMissing: false);
        if (candidates != null) _candidates = candidates;
        if (recheck && _mappedPath == null) RequestRecheck();
        var loadMs = sw.ElapsedMilliseconds;
        var diff = ItemDiff.Compute(_items, fresh, hints);
        _items = fresh;
        if (diff.IsEmpty) return;
        Log.Info($"{(_mappedPath == null ? "桌面项" : "映射目录")}变化：新增 {diff.Added.Count}，删除 {diff.Removed.Count}，更新 {diff.Updated.Count}，改名 {diff.Renamed.Count}（共 {fresh.Count} 项，刷新耗时 {sw.ElapsedMilliseconds} ms，枚举 {loadMs} ms）");
        foreach (var u in diff.Updated) IconInvalidated?.Invoke(u.Key);
        Changed?.Invoke(diff);
    }

    /// <summary>登记一个改名提示（本程序自己改名时用，保证位置保留）。</summary>
    public void AddRenameHint(string oldKey, string newKey) => _renameHints[oldKey] = newKey;

    private void Schedule()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void Register()
    {
        try
        {
            // 这台机器上 cEntries > 1 会访问冲突，所以每个位置单独注册一次
            var size = Marshal.SizeOf<Win32.SHChangeNotifyEntry>();
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                void Add(IntPtr pidl, string what)
                {
                    if (pidl == IntPtr.Zero) return;
                    _registeredPidls.Add(pidl);
                    Marshal.StructureToPtr(new Win32.SHChangeNotifyEntry { pidl = pidl, fRecursive = 0 }, buf, false);
                    var id = Win32.SHChangeNotifyRegister(_window.Handle,
                        ShellApi.SHCNRF_ShellLevel | ShellApi.SHCNRF_InterruptLevel | ShellApi.SHCNRF_NewDelivery,
                        ShellApi.SHCNE_ALLEVENTS | ShellApi.SHCNE_INTERRUPT, (uint)WmNotify, 1, buf);
                    _notifyIds.Add(id);
                    Log.Info($"SHChangeNotifyRegister {what} id={id}");
                }

                if (_mappedPath != null)
                {
                    if (Win32.SHParseDisplayName(_mappedPath, IntPtr.Zero, out var mapped, 0, out _) >= 0) Add(mapped, "映射目录 " + _mappedPath);
                }
                else
                {
                    Win32.SHGetSpecialFolderLocation(IntPtr.Zero, 0, out var root); // CSIDL_DESKTOP：虚拟项（回收站等）
                    Add(root, "桌面根");
                    Win32.SHGetKnownFolderIDList(ShellApi.FOLDERID_Desktop, 0, IntPtr.Zero, out var user);
                    Add(user, "用户桌面");
                    Win32.SHGetKnownFolderIDList(ShellApi.FOLDERID_PublicDesktop, 0, IntPtr.Zero, out var pub);
                    Add(pub, "公共桌面");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch (Exception ex)
        {
            Log.Error("注册桌面变化监听失败", ex);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmNotify)
        {
            handled = true;
            try { HandleNotify(wParam, lParam); }
            catch (Exception ex) { Log.Error("处理桌面变化通知异常", ex); }
        }
        return IntPtr.Zero;
    }

    private void HandleNotify(IntPtr hChange, IntPtr pid)
    {
        var hLock = Win32.SHChangeNotification_Lock(hChange, (uint)pid.ToInt64(), out var ppidl, out var evt);
        if (hLock == IntPtr.Zero) return;
        try
        {
            var p1 = Marshal.ReadIntPtr(ppidl, 0);
            var p2 = Marshal.ReadIntPtr(ppidl, IntPtr.Size);
            evt &= 0x7FFFFFFF;

            if ((evt & ShellApi.SHCNE_UPDATEIMAGE) != 0 || (evt & 0x08000000) != 0) // UPDATEIMAGE / ASSOCCHANGED
            {
                IconInvalidated?.Invoke(null);
                Log.Info($"桌面通知：图标缓存失效（事件 0x{evt:X}）");
                if ((evt & Win32.SHCNE_ASSOCCHANGED) != 0) { _resetShownCache = true; Schedule(); }
                return;
            }

            string? n1 = NameOf(p1), n2 = NameOf(p2);
            Log.Info($"桌面通知：事件 0x{evt:X} [{n1}] [{n2}]");
            if (NeedsShownCacheReset(evt, n1, n2)) _resetShownCache = true;

            if ((evt & (ShellApi.SHCNE_RENAMEITEM | ShellApi.SHCNE_RENAMEFOLDER)) != 0 && n1 != null && n2 != null)
                _renameHints[_prefix + n1] = _prefix + n2;
            if ((evt & (ShellApi.SHCNE_UPDATEITEM | ShellApi.SHCNE_ATTRIBUTES)) != 0 && n1 != null)
                IconInvalidated?.Invoke(_prefix + n1);
            Schedule();
        }
        finally
        {
            Win32.SHChangeNotification_Unlock(hLock);
        }
    }

    /// <summary>
    /// 通知是否可能改变虚拟项（此电脑/回收站等）的显示状态，需要清空 _shownCache：
    /// 解析名以 "::" 开头（虚拟/CLSID 项），或 UPDATEDIR/UPDATEITEM 作用于桌面根（两个 PIDL 都没有解析名）。
    /// 新建文件批次（CREATE/MKDIR [桌面目录下的路径]、UPDATEITEM [桌面目录]、0x4000000 空路径）返回 false。
    /// </summary>
    internal static bool NeedsShownCacheReset(int evt, string? n1, string? n2)
    {
        evt &= 0x7FFFFFFF;
        if ((evt & Win32.SHCNE_ASSOCCHANGED) != 0) return true;
        if (n1 != null && n1.StartsWith("::", StringComparison.Ordinal)) return true;
        if (n2 != null && n2.StartsWith("::", StringComparison.Ordinal)) return true;
        return (evt & (ShellApi.SHCNE_UPDATEDIR | ShellApi.SHCNE_UPDATEITEM)) != 0 &&
               string.IsNullOrEmpty(n1) && string.IsNullOrEmpty(n2);
    }

    /// <summary>绝对 PIDL → 与 DesktopItem.Key 一致的解析名。</summary>
    private static string? NameOf(IntPtr pidl) =>
        ShellApi.GetNameFromPidl(pidl, ShellApi.SIGDN_DESKTOPABSOLUTEPARSING) ?? ShellApi.GetNameFromPidl(pidl, ShellApi.SIGDN_FILESYSPATH);

    private List<DesktopItem> Load(List<ShownCandidate>? candidates, bool askMissing) =>
        _mappedPath == null ? Enumerate(_shownCache, candidates, askMissing) : EnumerateFolder(_mappedPath, _container);

    /// <summary>
    /// 把复核结果并入缓存：新增键或值翻转则更新并返回 true，全部一致返回 false。
    /// </summary>
    internal static bool MergeShown(Dictionary<string, bool> cache, IReadOnlyDictionary<string, bool> results)
    {
        var changed = false;
        foreach (var kv in results)
        {
            if (cache.TryGetValue(kv.Key, out var old) && old == kv.Value) continue;
            cache[kv.Key] = kv.Value;
            changed = true;
        }
        return changed;
    }

    /// <summary>请求后台复核当前候选项（UI 线程）。进行中则合并：完成后若期间又有请求再跑一轮。</summary>
    private void RequestRecheck()
    {
        if (_disposed) return;
        if (_recheckRunning) { _recheckAgain = true; return; }
        try
        {
            if (_recheckQueue == null)
            {
                _recheckQueue = new BlockingCollection<List<ShownCandidate>>();
                _recheckThread = new Thread(RecheckWorker) { IsBackground = true, Name = "ShownRecheckWorker" };
                _recheckThread.SetApartmentState(ApartmentState.STA);
                _recheckThread.Start();
            }
            _recheckRunning = true;
            _recheckQueue.Add(_candidates);
        }
        catch (Exception ex)
        {
            _recheckRunning = false;
            Log.Error("启动虚拟项显示状态后台复核失败", ex);
        }
    }

    /// <summary>专用 STA 线程：逐批问系统桌面视图，结果回投 UI 线程。异常全部捕获。</summary>
    private void RecheckWorker()
    {
        var queue = _recheckQueue!;
        try
        {
            foreach (var batch in queue.GetConsumingEnumerable())
            {
                var sw = Stopwatch.StartNew();
                var results = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var fv = SystemDesktopView.Acquire();
                    if (fv != null)
                    {
                        try
                        {
                            foreach (var c in batch)
                            {
                                try { results[c.Key] = SystemDesktopView.IsShown(fv, c.Pidl); }
                                catch (Exception ex) { Log.Error($"后台复核询问失败 {c.Key}", ex); }
                            }
                        }
                        finally
                        {
                            try { Marshal.ReleaseComObject(fv); } catch { /* 已释放 */ }
                        }
                    }
                }
                catch (Exception ex) { Log.Error("后台复核系统桌面视图异常", ex); }
                var ms = sw.ElapsedMilliseconds;
                var count = batch.Count;
                try { _dispatcher.BeginInvoke(() => OnRecheckDone(results, ms, count)); }
                catch (Exception ex) { Log.Error("后台复核结果回投 UI 失败", ex); }
            }
        }
        catch (Exception ex) { Log.Error("虚拟项复核线程异常退出", ex); }
    }

    private void OnRecheckDone(Dictionary<string, bool> results, long ms, int count)
    {
        _recheckRunning = false;
        if (_disposed) return;
        try
        {
            var diffCount = results.Count(kv => !_shownCache.TryGetValue(kv.Key, out var o) || o != kv.Value);
            var changed = MergeShown(_shownCache, results);
            if (ms > 200) Log.Info($"后台复核系统桌面视图耗时 {ms} ms（{count} 项，变化 {diffCount} 项）");
            if (_recheckAgain)
            {
                _recheckAgain = false;
                RequestRecheck();
            }
            if (changed) Refresh(useShownCache: true);
        }
        catch (Exception ex) { Log.Error("应用后台复核结果失败", ex); }
    }

    /// <summary>
    /// 枚举桌面根的所有项。shownCache 记住虚拟项是否被系统桌面显示：命中不再询问 Explorer；
    /// 未命中时 askMissing=true 同步询问（仅首次枚举），false 按“不显示”处理。
    /// candidates 非空时收集本次所有需要询问的项（供后台复核）。
    /// </summary>
    public static List<DesktopItem> Enumerate(Dictionary<string, bool>? shownCache = null, List<ShownCandidate>? candidates = null, bool askMissing = true)
    {
        var list = new List<DesktopItem>();
        var desktop = ShellApi.Desktop;

        // 系统桌面不显示的项（用户文件夹、OneDrive、网盘等命名空间项）：只保留系统视图里有的
        var userDesk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var pubDesk = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        IFolderView2? sysView = null;
        var sysViewTried = false;

        EnumerateInto(list, desktop, IntPtr.Zero, "", "", (path, rawKey, child) =>
        {
            var onDesktopDir = path != null &&
                (string.Equals(Path.GetDirectoryName(path), userDesk, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Path.GetDirectoryName(path), pubDesk, StringComparison.OrdinalIgnoreCase));
            if (onDesktopDir) return true;
            var pidlBytes = ShellApi.PidlToBytes(child);
            candidates?.Add(new ShownCandidate(rawKey, pidlBytes));
            if (shownCache != null && shownCache.TryGetValue(rawKey, out var cached)) return cached;
            if (!askMissing) return false;
            var sw = Stopwatch.StartNew();
            if (!sysViewTried) { sysView = SystemDesktopView.Acquire(); sysViewTried = true; }
            var shown = sysView == null || SystemDesktopView.IsShown(sysView, pidlBytes);
            if (sw.ElapsedMilliseconds > 200) Log.Info($"询问系统桌面视图耗时 {sw.ElapsedMilliseconds} ms：{rawKey}");
            if (shownCache != null) shownCache[rawKey] = shown;
            return shown;
        });
        return list;
    }

    /// <summary>枚举任意目录（映射格子）：用该目录的 IShellFolder，项的 PIDL 为绝对 PIDL，Key 带 container 前缀。</summary>
    public static List<DesktopItem> EnumerateFolder(string path, string container)
    {
        var list = new List<DesktopItem>();
        var folder = ShellApi.BindFolder(path, out var abs);
        if (folder == null)
        {
            Log.Info($"映射目录无法绑定（可能已不存在）：{path}");
            return list;
        }
        try { EnumerateInto(list, folder, abs, container, container + KeySeparator, null); }
        finally
        {
            Marshal.ReleaseComObject(folder);
            Win32.ILFree(abs);
        }
        return list;
    }

    /// <summary>folderAbs 为 Zero 表示 folder 是桌面根（子 PIDL 本身即绝对 PIDL）。accept 返回 false 的项被跳过。</summary>
    private static void EnumerateInto(List<DesktopItem> list, IShellFolder folder, IntPtr folderAbs, string container, string keyPrefix,
        Func<string?, string, IntPtr, bool>? accept)
    {
        var flags = ShellApi.SHCONTF_FOLDERS | ShellApi.SHCONTF_NONFOLDERS;
        if (ReadAdvanced("Hidden") == 1) flags |= ShellApi.SHCONTF_INCLUDEHIDDEN;
        if (ReadAdvanced("ShowSuperHidden") == 1) flags |= ShellApi.SHCONTF_INCLUDESUPERHIDDEN;

        if (folder.EnumObjects(IntPtr.Zero, flags, out var enumerator) < 0) return;
        try
        {
            while (enumerator.Next(1, out var child, out var fetched) == ShellApi.S_OK && fetched == 1)
            {
                try
                {
                    var rawKey = ShellApi.GetDisplayName(folder, child, ShellApi.SHGDN_FORPARSING);
                    if (rawKey.Length == 0) continue;

                    uint attrs = ShellApi.SFGAO_FOLDER | ShellApi.SFGAO_LINK | ShellApi.SFGAO_HIDDEN | ShellApi.SFGAO_GHOSTED |
                                 ShellApi.SFGAO_FILESYSTEM | ShellApi.SFGAO_CANRENAME;
                    folder.GetAttributesOf(1, new[] { child }, ref attrs);

                    string? path = null;
                    long size = 0;
                    var modified = DateTime.MinValue;
                    var ext = "";
                    if ((attrs & ShellApi.SFGAO_FILESYSTEM) != 0 && !rawKey.StartsWith("::", StringComparison.Ordinal))
                    {
                        path = rawKey;
                        try
                        {
                            if (File.Exists(path))
                            {
                                var fi = new FileInfo(path);
                                size = fi.Length;
                                modified = fi.LastWriteTimeUtc;
                                ext = fi.Extension.ToLowerInvariant();
                            }
                            else if (Directory.Exists(path))
                            {
                                modified = Directory.GetLastWriteTimeUtc(path);
                            }
                        }
                        catch { /* 文件瞬间消失等，按空属性处理 */ }
                    }

                    if (accept != null && !accept(path, rawKey, child)) continue;

                    byte[] pidl;
                    if (folderAbs == IntPtr.Zero) pidl = ShellApi.PidlToBytes(child);
                    else
                    {
                        var full = Win32.ILCombine(folderAbs, child);
                        try { pidl = ShellApi.PidlToBytes(full); }
                        finally { Win32.ILFree(full); }
                    }

                    list.Add(new DesktopItem
                    {
                        Key = keyPrefix + rawKey,
                        Container = container,
                        DisplayName = ShellApi.GetDisplayName(folder, child, ShellApi.SHGDN_NORMAL),
                        EditName = ShellApi.GetDisplayName(folder, child, ShellApi.SHGDN_INFOLDER | ShellApi.SHGDN_FOREDITING),
                        Pidl = pidl,
                        Attributes = attrs,
                        FilePath = path,
                        Size = size,
                        Modified = modified,
                        Extension = ext,
                    });
                }
                finally
                {
                    Marshal.FreeCoTaskMem(child);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static int ReadAdvanced(string name)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return k?.GetValue(name) is int v ? v : 0;
        }
        catch { return 0; }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _recheckQueue?.CompleteAdding(); } catch { /* 已释放 */ }
        _debounce.Stop();
        foreach (var id in _notifyIds) if (id != 0) Win32.SHChangeNotifyDeregister(id);
        _notifyIds.Clear();
        foreach (var p in _registeredPidls) Win32.ILFree(p);
        _registeredPidls.Clear();
        _window.Dispose();
    }
}
