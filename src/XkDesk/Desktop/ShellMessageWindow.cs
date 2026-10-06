using System.Windows.Interop;
using XkDesk.Native;
using XkDesk.Services;

namespace XkDesk.Desktop;

/// <summary>
/// 独立的隐藏顶层窗口（无 owner，非 message-only，否则收不到广播），
/// 监听 TaskbarCreated（Explorer 重启）与 WM_DISPLAYCHANGE（显示器布局变化）。
/// </summary>
internal sealed class ShellMessageWindow : IDisposable
{
    private readonly HwndSource _source;
    private readonly uint _taskbarCreatedMsg;
    private readonly uint _exitMsg;

    public const string WindowName = "XkDeskMessageWindow";
    public const string ExitMessageName = "XkDesk.ExitRequest";

    public event Action? TaskbarCreated;
    public event Action? DisplayChanged;
    public event Action? ExitRequested;

    public ShellMessageWindow()
    {
        _taskbarCreatedMsg = Win32.RegisterWindowMessage("TaskbarCreated");
        _exitMsg = Win32.RegisterWindowMessage(ExitMessageName);
        // WS_POPUP 且不带 WS_VISIBLE：隐藏的普通顶层窗口
        var p = new HwndSourceParameters(WindowName)
        {
            WindowStyle = unchecked((int)Win32.WS_POPUP),
            ExtendedWindowStyle = (int)Win32.WS_EX_TOOLWINDOW,
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
        Log.Info($"消息窗口已创建 hwnd=0x{_source.Handle:X}，TaskbarCreated 消息 id={_taskbarCreatedMsg}");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        try
        {
            if (msg == _taskbarCreatedMsg) TaskbarCreated?.Invoke();
            else if (msg == Win32.WM_DISPLAYCHANGE) DisplayChanged?.Invoke();
            else if (msg == _exitMsg) ExitRequested?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("消息窗口处理异常", ex);
        }
        return IntPtr.Zero;
    }

    public void Dispose() => _source.Dispose();
}
