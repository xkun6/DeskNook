using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using XkDesk.Model;
using XkDesk.Native;
using XkDesk.Services;

namespace XkDesk.Desktop;

/// <summary>
/// 桌面项来源：枚举桌面根文件夹（用户桌面 + 公共桌面 + 虚拟项），
/// 用 SHChangeNotifyRegister 监听变化，300ms 去抖后重新枚举并做 diff。
/// 全部在 UI(STA) 线程使用。
/// </summary>
internal sealed class DesktopItemSource : IDisposable
{
    private const int WmNotify = Win32.WM_APP + 1;

    private readonly HwndSource _window;
    private readonly DispatcherTimer _debounce;
    private readonly List<IntPtr> _registeredPidls = new();
    private readonly Dictionary<string, string> _renameHints = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<uint> _notifyIds = new();
    private List<DesktopItem> _items = new();

    public IReadOnlyList<DesktopItem> Items => _items;

    /// <summary>去抖后的增量结果（UI 线程）。</summary>
    public event Action<ItemDiffResult>? Changed;

    /// <summary>图标需要重新加载：key 为 null 表示全部。</summary>
    public event Action<string?>? IconInvalidated;

    public DesktopItemSource()
    {
        var p = new HwndSourceParameters("XkDeskItemNotify")
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
            try { Refresh(); }
            catch (Exception ex) { Log.Error("桌面项刷新失败", ex); }
        };

        _items = Enumerate();
        Log.Info($"桌面项枚举完成：{_items.Count} 项");
        Register();
    }

    /// <summary>立即重新枚举并与旧集合 diff，有变化则触发 Changed。</summary>
    public void Refresh()
    {
        var hints = _renameHints.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        _renameHints.Clear();
        var fresh = Enumerate();
        var diff = ItemDiff.Compute(_items, fresh, hints);
        _items = fresh;
        if (diff.IsEmpty) return;
        Log.Info($"桌面项变化：新增 {diff.Added.Count}，删除 {diff.Removed.Count}，更新 {diff.Updated.Count}，改名 {diff.Renamed.Count}（共 {fresh.Count} 项）");
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

                Win32.SHGetSpecialFolderLocation(IntPtr.Zero, 0, out var root); // CSIDL_DESKTOP：虚拟项（回收站等）
                Add(root, "桌面根");
                Win32.SHGetKnownFolderIDList(ShellApi.FOLDERID_Desktop, 0, IntPtr.Zero, out var user);
                Add(user, "用户桌面");
                Win32.SHGetKnownFolderIDList(ShellApi.FOLDERID_PublicDesktop, 0, IntPtr.Zero, out var pub);
                Add(pub, "公共桌面");
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
                return;
            }

            string? n1 = NameOf(p1), n2 = NameOf(p2);
            Log.Info($"桌面通知：事件 0x{evt:X} [{n1}] [{n2}]");

            if ((evt & (ShellApi.SHCNE_RENAMEITEM | ShellApi.SHCNE_RENAMEFOLDER)) != 0 && n1 != null && n2 != null)
                _renameHints[n1] = n2;
            if ((evt & (ShellApi.SHCNE_UPDATEITEM | ShellApi.SHCNE_ATTRIBUTES)) != 0 && n1 != null)
                IconInvalidated?.Invoke(n1);
            Schedule();
        }
        finally
        {
            Win32.SHChangeNotification_Unlock(hLock);
        }
    }

    /// <summary>绝对 PIDL → 与 DesktopItem.Key 一致的解析名。</summary>
    private static string? NameOf(IntPtr pidl) =>
        ShellApi.GetNameFromPidl(pidl, ShellApi.SIGDN_DESKTOPABSOLUTEPARSING) ?? ShellApi.GetNameFromPidl(pidl, ShellApi.SIGDN_FILESYSPATH);

    /// <summary>枚举桌面根的所有项。</summary>
    public static List<DesktopItem> Enumerate()
    {
        var list = new List<DesktopItem>();
        var desktop = ShellApi.Desktop;

        var flags = ShellApi.SHCONTF_FOLDERS | ShellApi.SHCONTF_NONFOLDERS;
        if (ReadAdvanced("Hidden") == 1) flags |= ShellApi.SHCONTF_INCLUDEHIDDEN;
        if (ReadAdvanced("ShowSuperHidden") == 1) flags |= ShellApi.SHCONTF_INCLUDESUPERHIDDEN;

        // 系统桌面不显示的项（用户文件夹、OneDrive、网盘等命名空间项）：只保留系统视图里有的
        var userDesk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var pubDesk = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        IFolderView2? sysView = null;
        var sysViewTried = false;

        if (desktop.EnumObjects(IntPtr.Zero, flags, out var enumerator) < 0) return list;
        try
        {
            while (enumerator.Next(1, out var child, out var fetched) == ShellApi.S_OK && fetched == 1)
            {
                try
                {
                    var key = ShellApi.GetDisplayName(desktop, child, ShellApi.SHGDN_FORPARSING);
                    if (key.Length == 0) continue;

                    uint attrs = ShellApi.SFGAO_FOLDER | ShellApi.SFGAO_LINK | ShellApi.SFGAO_HIDDEN | ShellApi.SFGAO_GHOSTED |
                                 ShellApi.SFGAO_FILESYSTEM | ShellApi.SFGAO_CANRENAME;
                    desktop.GetAttributesOf(1, new[] { child }, ref attrs);

                    string? path = null;
                    long size = 0;
                    var modified = DateTime.MinValue;
                    var ext = "";
                    if ((attrs & ShellApi.SFGAO_FILESYSTEM) != 0 && !key.StartsWith("::", StringComparison.Ordinal))
                    {
                        path = key;
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

                    var onDesktopDir = path != null &&
                        (string.Equals(Path.GetDirectoryName(path), userDesk, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(Path.GetDirectoryName(path), pubDesk, StringComparison.OrdinalIgnoreCase));
                    if (!onDesktopDir)
                    {
                        if (!sysViewTried) { sysView = SystemDesktopView.Acquire(); sysViewTried = true; }
                        if (sysView != null && !SystemDesktopView.IsShown(sysView, ShellApi.PidlToBytes(child))) continue;
                    }

                    list.Add(new DesktopItem
                    {
                        Key = key,
                        DisplayName = ShellApi.GetDisplayName(desktop, child, ShellApi.SHGDN_NORMAL),
                        EditName = ShellApi.GetDisplayName(desktop, child, ShellApi.SHGDN_INFOLDER | ShellApi.SHGDN_FOREDITING),
                        Pidl = ShellApi.PidlToBytes(child),
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
        return list;
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
        _debounce.Stop();
        foreach (var id in _notifyIds) if (id != 0) Win32.SHChangeNotifyDeregister(id);
        _notifyIds.Clear();
        foreach (var p in _registeredPidls) Win32.ILFree(p);
        _registeredPidls.Clear();
        _window.Dispose();
    }
}
