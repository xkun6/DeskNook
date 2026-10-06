using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeskNext.Native;
using DeskNext.Services;
using DeskNext.Views;

namespace DeskNext.Desktop;

/// <summary>挂载方式：owner = 以 Progman 为 owner 置底；child = 作为 DefView 同级子窗口置于其上。</summary>
internal enum AttachMode { Owner, Child }

/// <summary>透明方案：layered = AllowsTransparency 分层窗口；dwm = DWM 扩展边框（非分层）。</summary>
internal enum TransparencyMode { Layered, Dwm }

/// <summary>每个显示器一个的桌面层宿主窗口，内容是本显示器的图标画布 DesktopSurface。</summary>
public partial class DesktopHostWindow : Window
{
    private readonly MonitorInfo _monitor;
    private readonly DesktopInfo _desktop;
    private readonly AttachMode _attach;
    private readonly TransparencyMode _transparency;
    private readonly DispatcherTimer _zTimer;
    private readonly DesktopController _controller;
    private readonly DesktopSurface _surface;
    private DesktopDropTarget? _dropTarget;

    // 委托必须保存为字段，防止被 GC 回收导致回调崩溃
    private readonly Win32.WinEventProc _winEventProc;
    private IntPtr _eventHook;
    private IntPtr _hwnd;
    private int _zFixLogCount;

    internal DesktopHostWindow(MonitorInfo monitor, DesktopInfo desktop, AttachMode attach, TransparencyMode transparency, DesktopController controller)
    {
        _controller = controller;
        _monitor = monitor;
        _desktop = desktop;
        _attach = attach;
        _transparency = transparency;
        _winEventProc = OnForegroundChanged;

        InitializeComponent();

        // 透明方案必须在窗口显示前设置
        AllowsTransparency = transparency == TransparencyMode.Layered;

        UseLayoutRounding = true;
        _surface = new DesktopSurface(controller, monitor);
        Content = _surface;
        Activated += (_, _) => _surface.SetWindowActive(true);
        Deactivated += (_, _) => _surface.SetWindowActive(false);
        PreviewKeyDown += (_, e) =>
        {
            if (_surface.HandleKey(e)) e.Handled = true;
        };

        _zTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _zTimer.Tick += (_, _) =>
        {
            _zTimer.Stop();
            ReassertBottom("前台变化(延迟复查)");
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(_hwnd);
        source.AddHook(WndProc);

        // 工具窗口 + 不激活，不出现在 Alt+Tab、不抢焦点
        var ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64();
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, (IntPtr)(ex | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE));

        if (_transparency == TransparencyMode.Dwm)
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            var margins = new Win32.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            var hr = Win32.DwmExtendFrameIntoClientArea(_hwnd, ref margins);
            Log.Info($"[{_monitor.DeviceName}] DwmExtendFrameIntoClientArea hr=0x{hr:X}");
        }

        if (_attach == AttachMode.Owner) AttachAsOwner();
        else AttachAsChild();

        try { Win32.AllowDarkModeForWindow(_hwnd, true); } catch { /* 忽略 */ }
        _surface.AttachWindow(_hwnd);
        _dropTarget = new DesktopDropTarget(_controller, _surface, _hwnd);

        Log.Info($"[{_monitor.DeviceName}] 宿主窗口 hwnd=0x{_hwnd:X} 挂载={_attach} 透明={_transparency} " +
                 $"物理矩形=({_monitor.Bounds.Left},{_monitor.Bounds.Top},{_monitor.Bounds.Width}x{_monitor.Bounds.Height})");
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        ApplyBounds();
    }

    // WPF 在 DPI 变化时会按建议矩形改尺寸，这里强制恢复为整块显示器的物理像素矩形
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyBounds();
    }

    private void AttachAsOwner()
    {
        var owner = _desktop.DefViewParent;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWLP_HWNDPARENT, owner);
        ApplyBounds();
        Win32.SetWindowPos(_hwnd, Win32.HWND_BOTTOM, 0, 0, 0, 0, Win32.SWP_NOACTIVATE | Win32.SWP_NOMOVE | Win32.SWP_NOSIZE);
        _eventHook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);
        Log.Info($"[{_monitor.DeviceName}] owner=0x{owner:X}({Win32.GetClassNameString(owner)}) 已置底，WinEventHook=0x{_eventHook:X}");
    }

    private void AttachAsChild()
    {
        var parent = _desktop.DefViewParent;
        Win32.SetParent(_hwnd, parent);
        var style = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_STYLE).ToInt64();
        style = (style & ~(Win32.WS_POPUP | Win32.WS_CAPTION | Win32.WS_THICKFRAME)) | Win32.WS_CHILD;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_STYLE, (IntPtr)style);

        var (x, y) = ToParentClient(parent);
        // 放在 SHELLDLL_DefView 之上（同级 HWND_TOP）
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOP, x, y, _monitor.Bounds.Width, _monitor.Bounds.Height,
            Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);
        Log.Info($"[{_monitor.DeviceName}] child 父窗口=0x{parent:X} 相对坐标=({x},{y})");
    }

    /// <summary>显示器左上角物理坐标换算为父窗口客户区坐标（父窗口覆盖虚拟屏幕，原点可能为负）。</summary>
    private (int x, int y) ToParentClient(IntPtr parent)
    {
        var pt = new Win32.POINT { X = _monitor.Bounds.Left, Y = _monitor.Bounds.Top };
        Win32.ScreenToClient(parent, ref pt);
        return (pt.X, pt.Y);
    }

    /// <summary>按物理像素设置位置与大小（不使用 WPF 的 Left/Top，避免 DPI 换算误差）。</summary>
    private void ApplyBounds()
    {
        if (_hwnd == IntPtr.Zero || !Win32.IsWindow(_hwnd)) return;
        var (x, y) = _attach == AttachMode.Child ? ToParentClient(_desktop.DefViewParent) : (_monitor.Bounds.Left, _monitor.Bounds.Top);
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, x, y, _monitor.Bounds.Width, _monitor.Bounds.Height,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }

    private void ReassertBottom(string reason)
    {
        if (_hwnd == IntPtr.Zero || !Win32.IsWindow(_hwnd)) return;
        Win32.SetWindowPos(_hwnd, Win32.HWND_BOTTOM, 0, 0, 0, 0, Win32.SWP_NOACTIVATE | Win32.SWP_NOMOVE | Win32.SWP_NOSIZE);
        Log.Info($"[{_monitor.DeviceName}] Z 序修正：{reason}");
    }

    private void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (!DesktopShell.IsDesktopWindow(hwnd)) return;
            ReassertBottom($"前台变为桌面窗口 0x{hwnd:X}");
            // Explorer 可能在事件之后才调整 Z 序，延迟再复查一次
            _zTimer.Stop();
            _zTimer.Start();
        }
        catch (Exception ex)
        {
            Log.Error("OnForegroundChanged 异常", ex);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 右键菜单期间：把菜单消息转给 IContextMenu2/3（“发送到”“新建”等子菜单、图标绘制）
        if (ShellContextMenu.TryHandleMessage(msg, wParam, lParam, out var menuResult))
        {
            handled = true;
            return menuResult;
        }

        // owner 模式：有人想把本窗口提到上层时，强制改为 HWND_BOTTOM 插入（仅改结构体，不再调 SetWindowPos，无递归）
        if (msg == Win32.WM_WINDOWPOSCHANGING && _attach == AttachMode.Owner)
        {
            var wp = Marshal.PtrToStructure<Win32.WINDOWPOS>(lParam);
            if ((wp.flags & Win32.SWP_NOZORDER) == 0 && wp.hwndInsertAfter != Win32.HWND_BOTTOM)
            {
                wp.hwndInsertAfter = Win32.HWND_BOTTOM;
                Marshal.StructureToPtr(wp, lParam, false);
                if (++_zFixLogCount <= 20)
                    Log.Info($"[{_monitor.DeviceName}] Z 序修正：WM_WINDOWPOSCHANGING 强制 HWND_BOTTOM（第 {_zFixLogCount} 次）");
            }
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        _zTimer.Stop();
        _dropTarget?.Dispose();
        _dropTarget = null;
        _surface.Detach();
        if (_eventHook != IntPtr.Zero)
        {
            Win32.UnhookWinEvent(_eventHook);
            _eventHook = IntPtr.Zero;
        }
        Log.Info($"[{_monitor.DeviceName}] 宿主窗口已关闭 hwnd=0x{_hwnd:X}");
        base.OnClosed(e);
    }
}
