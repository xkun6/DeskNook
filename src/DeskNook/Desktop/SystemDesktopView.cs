using System.Runtime.InteropServices;
using DeskNook.Model;
using DeskNook.Native;
using DeskNook.Services;

namespace DeskNook.Desktop;

/// <summary>系统桌面（Explorer DefView）当前的图标大小、间距和各项位置（屏幕物理像素）。</summary>
internal sealed class SystemDesktopInfo
{
    public int IconSizePx { get; init; }
    public int SpacingXPx { get; init; }
    public int SpacingYPx { get; init; }
    /// <summary>key → 图标格左上角的屏幕物理坐标。</summary>
    public Dictionary<string, (int X, int Y)> Positions { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 通过 IShellWindows.FindWindowSW(SWC_DESKTOP) → IServiceProvider → IShellBrowser → IShellView → IFolderView2
/// 读取系统桌面视图的设置与图标位置（用于首次运行导入）。读取失败返回 null。
/// </summary>
internal static class SystemDesktopView
{
    public static IFolderView2? Acquire() => AcquireView() as IFolderView2;

    /// <summary>取系统桌面视图对象（同时实现 IShellView / IFolderView2；Explorer 不在时返回 null）。</summary>
    public static object? AcquireView()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(ShellApi.CLSID_ShellWindows)!;
            var shellWindows = (IShellWindows)Activator.CreateInstance(type)!;
            object loc = 0; // CSIDL_DESKTOP
            object empty = Type.Missing;
            var hr = shellWindows.FindWindowSW(ref loc, ref empty, 8 /* SWC_DESKTOP */, out _, 1 /* SWFO_NEEDDISPATCH */, out var disp);
            if (hr != 0 || disp == null)
            {
                Log.Info($"FindWindowSW 失败 hr=0x{hr:X}");
                return null;
            }
            var sp = (IServiceProviderCom)disp;
            if (sp.QueryService(ShellApi.SID_STopLevelBrowser, ShellApi.IID_IShellBrowser, out var pBrowser) < 0 || pBrowser == IntPtr.Zero)
                return null;
            var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(pBrowser);
            Marshal.Release(pBrowser);
            if (browser.QueryActiveShellView(out var view) < 0) return null;
            return view;
        }
        catch (Exception ex)
        {
            Log.Error("获取系统桌面视图失败", ex);
            return null;
        }
    }

    /// <summary>系统桌面视图是否显示该项（用于过滤系统桌面不显示的虚拟/命名空间项）。</summary>
    public static bool IsShown(IFolderView2 fv, byte[] pidlBytes)
    {
        var pidl = ShellApi.PidlFromBytes(pidlBytes);
        try { return fv.GetItemPosition(pidl, out _) == 0; }
        finally { Marshal.FreeCoTaskMem(pidl); }
    }

    /// <summary>只读系统桌面当前的图标大小与间距（物理像素）；失败返回 null。</summary>
    public static (int IconSizePx, int SpacingX, int SpacingY)? ReadMetrics()
    {
        try
        {
            var fv = Acquire();
            if (fv == null) return null;
            fv.GetViewModeAndIconSize(out _, out var iconSize);
            fv.GetSpacing(out var spacing);
            return (iconSize, spacing.X, spacing.Y);
        }
        catch (Exception ex)
        {
            Log.Error("读取系统桌面图标大小失败", ex);
            return null;
        }
    }

    public static SystemDesktopInfo? Read(IReadOnlyList<DesktopItem> items, IntPtr listViewHwnd)
    {
        try
        {
            var fv = Acquire();
            if (fv == null) return null;

            fv.GetViewModeAndIconSize(out var mode, out var iconSize);
            fv.GetSpacing(out var spacing);
            Log.Info($"系统桌面视图：模式={mode} 图标={iconSize}px 间距=({spacing.X},{spacing.Y})");

            var info = new SystemDesktopInfo { IconSizePx = iconSize, SpacingXPx = spacing.X, SpacingYPx = spacing.Y };
            foreach (var it in items)
            {
                var pidl = ShellApi.PidlFromBytes(it.Pidl);
                try
                {
                    if (fv.GetItemPosition(pidl, out var pt) != 0) { Log.Info($"系统桌面不显示：{it.Key}"); continue; }
                    var p = new Win32.POINT { X = pt.X, Y = pt.Y };
                    if (listViewHwnd != IntPtr.Zero) Win32.ClientToScreen(listViewHwnd, ref p);
                    info.Positions[it.Key] = (p.X, p.Y);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pidl);
                }
            }
            Log.Info($"系统桌面位置读取：{info.Positions.Count}/{items.Count} 项");
            return info;
        }
        catch (Exception ex)
        {
            Log.Error("读取系统桌面视图失败", ex);
            return null;
        }
    }
}
