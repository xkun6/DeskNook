using DeskNook.Native;
using DeskNook.Services;

namespace DeskNook.Desktop;

/// <summary>桌面相关窗口句柄。DefView 在 Win10 下通常挂在 WorkerW，Win11 24H2 可能一直在 Progman 下。</summary>
internal readonly record struct DesktopInfo(IntPtr Progman, IntPtr DefView, IntPtr DefViewParent, IntPtr ListView)
{
    public bool IsValid => Progman != IntPtr.Zero && DefView != IntPtr.Zero && ListView != IntPtr.Zero;

    public override string ToString() =>
        $"Progman=0x{Progman:X}, DefView=0x{DefView:X}(父=0x{DefViewParent:X} {Win32.GetClassNameString(DefViewParent)}), ListView=0x{ListView:X}";
}

/// <summary>显示器信息：Bounds/Work 为物理像素的虚拟屏幕坐标，Scale 为 DPI 缩放（1.0 = 100%）。</summary>
internal readonly record struct MonitorInfo(string DeviceName, Win32.RECT Bounds, bool IsPrimary, Win32.RECT Work, double Scale);

/// <summary>定位桌面窗口、隐藏/恢复系统桌面图标（仅 ShowWindow，不写注册表）。</summary>
internal static class DesktopShell
{
    private static readonly object Gate = new();
    private static bool _hidden;
    private static IntPtr _lastListView;

    /// <summary>查找桌面窗口（不发送任何消息，不改变 Explorer 原生窗口结构）。</summary>
    public static DesktopInfo FindDesktop()
    {
        var progman = Win32.FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return default;

        // 先查 Progman 子窗口，再找含 DefView 的 WorkerW
        var defView = Win32.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        var parent = progman;
        if (defView == IntPtr.Zero)
        {
            IntPtr foundParent = IntPtr.Zero, foundView = IntPtr.Zero;
            Win32.EnumWindows((hwnd, _) =>
            {
                if (Win32.GetClassNameString(hwnd) != "WorkerW") return true;
                var dv = Win32.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (dv == IntPtr.Zero) return true;
                foundParent = hwnd;
                foundView = dv;
                return false;
            }, IntPtr.Zero);
            defView = foundView;
            parent = foundParent;
        }

        if (defView == IntPtr.Zero) return new DesktopInfo(progman, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        var listView = Win32.FindWindowEx(defView, IntPtr.Zero, "SysListView32", "FolderView");
        return new DesktopInfo(progman, defView, parent, listView);
    }

    /// <summary>隐藏系统桌面图标层。</summary>
    public static void HideIcons(DesktopInfo info)
    {
        if (info.ListView == IntPtr.Zero) return;
        lock (Gate)
        {
            Win32.ShowWindow(info.ListView, Win32.SW_HIDE);
            _lastListView = info.ListView;
            _hidden = true;
        }
        Log.Info($"已隐藏系统桌面图标 ListView=0x{info.ListView:X}");
    }

    /// <summary>恢复系统桌面图标（幂等；句柄可能已变，重新查找一次）。</summary>
    public static void RestoreIcons()
    {
        try
        {
            lock (Gate)
            {
                if (!_hidden) return;
                _hidden = false;

                var target = FindDesktop().ListView;
                if (target == IntPtr.Zero && Win32.IsWindow(_lastListView)) target = _lastListView;
                if (target != IntPtr.Zero) Win32.ShowWindow(target, Win32.SW_SHOW);
                Log.Info($"已恢复系统桌面图标 ListView=0x{target:X}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("RestoreIcons 失败", ex);
        }
    }

    /// <summary>hwnd 是否属于桌面层（Progman，或带 DefView 的 WorkerW）。</summary>
    public static bool IsDesktopWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var root = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
        if (root != IntPtr.Zero) hwnd = root;
        return Win32.GetClassNameString(hwnd) switch
        {
            "Progman" => true,
            "WorkerW" => Win32.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero,
            _ => false,
        };
    }

    /// <summary>枚举所有显示器（物理像素矩形，覆盖整块屏幕）。</summary>
    public static List<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref Win32.RECT rc, IntPtr data) =>
        {
            var mi = new Win32.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFOEX>() };
            if (Win32.GetMonitorInfo(hMon, ref mi))
            {
                var scale = Win32.GetDpiForMonitor(hMon, 0, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
                list.Add(new MonitorInfo(mi.szDevice, mi.rcMonitor, (mi.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0, mi.rcWork, scale));
            }
            return true;
        }, IntPtr.Zero);
        list.Sort((a, b) => b.IsPrimary.CompareTo(a.IsPrimary)); // 主显示器排第一（布局分配新项用）
        return list;
    }
}
