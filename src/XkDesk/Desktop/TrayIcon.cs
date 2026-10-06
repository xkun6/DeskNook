using System.Runtime.InteropServices;
using XkDesk.Native;
using XkDesk.Services;

namespace XkDesk.Desktop;

/// <summary>
/// 托盘图标（自写 Shell_NotifyIcon，复用 ShellMessageWindow 的窗口）：左键切换隐藏/显示，右键弹原生深色菜单。
/// Explorer 重启（TaskbarCreated）后调用 <see cref="Recreate"/> 重建。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = Win32.WM_APP + 10;
    private const uint IconId = 1;

    private enum Cmd : uint { Organize = 1, UndoOrganize, NewBox, ToggleIcons, Settings, AutoStart, Exit }

    private readonly ShellMessageWindow _window;
    private readonly DesktopController _c;
    private readonly Action _openSettings;
    private readonly Action _exit;
    private readonly AutoStart _autoStart;
    private IntPtr _hIcon;
    private bool _added;
    private bool _menuOpen;

    public TrayIcon(ShellMessageWindow window, DesktopController controller, AutoStart autoStart, Action openSettings, Action exit)
    {
        _window = window;
        _c = controller;
        _autoStart = autoStart;
        _openSettings = openSettings;
        _exit = exit;
        _window.AddHook(WndProc);
        _hIcon = LoadAppIcon(Win32.GetSystemMetrics(Win32.SM_CXSMICON));
    }

    /// <summary>从嵌入的多帧 ICO 里挑不小于 size 的最小帧（没有则取最大帧）创建 HICON。</summary>
    internal static IntPtr LoadAppIcon(int size)
    {
        try
        {
            var data = MenuIcons.ReadResource("app.ico");
            if (data == null || data.Length < 22) return IntPtr.Zero;
            int count = BitConverter.ToUInt16(data, 4);
            int bestOff = 0, bestLen = 0, bestW = 0;
            for (var i = 0; i < count; i++)
            {
                var e = 6 + i * 16;
                var w = data[e] == 0 ? 256 : data[e];
                var len = BitConverter.ToInt32(data, e + 8);
                var off = BitConverter.ToInt32(data, e + 12);
                var better = bestW == 0 || (bestW < size && w > bestW) || (w >= size && w < bestW);
                if (better) { bestW = w; bestOff = off; bestLen = len; }
            }
            if (bestLen == 0) return IntPtr.Zero;
            var frame = new byte[bestLen];
            Array.Copy(data, bestOff, frame, 0, bestLen);
            return Win32.CreateIconFromResourceEx(frame, (uint)bestLen, true, 0x00030000, size, size, 0);
        }
        catch (Exception ex)
        {
            Log.Error("创建托盘图标失败", ex);
            return IntPtr.Zero;
        }
    }

    private Win32.NOTIFYICONDATA NewData() => new()
    {
        cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP,
        uCallbackMessage = CallbackMessage,
        hIcon = _hIcon,
        szTip = "xk-desk",
        szInfo = "",
        szInfoTitle = "",
    };

    /// <summary>添加图标（已添加则先删除重建，Explorer 重启后调用）。</summary>
    public void Recreate()
    {
        var d = NewData();
        if (_added) Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref d);
        _added = Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref d);
        Log.Info($"托盘图标{(_added ? "已添加" : "添加失败")}");
    }

    /// <summary>气泡提示。</summary>
    public void ShowBalloon(string title, string text)
    {
        if (!_added) return;
        var d = NewData();
        d.uFlags = Win32.NIF_INFO;
        d.szInfoTitle = title;
        d.szInfo = text;
        d.dwInfoFlags = Win32.NIIF_INFO;
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref d);
        Log.Info($"托盘气泡：{title} - {text}");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != (int)CallbackMessage) return IntPtr.Zero;
        handled = true;
        try
        {
            switch ((int)lParam & 0xFFFF)
            {
                case Win32.WM_LBUTTONUP:
                    _c.SetIconsVisible(!_c.IconsVisible);
                    break;
                case Win32.WM_RBUTTONUP:
                    ShowMenu();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("托盘消息处理异常", ex);
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        if (_menuOpen) return;
        _menuOpen = true;
        var menu = Win32.CreatePopupMenu();
        try
        {
            Win32.AppendMenu(menu, Win32.MF_STRING, (UIntPtr)(uint)Cmd.Organize, "一键整理(&Z)");
            Win32.AppendMenu(menu, Win32.MF_STRING | (_c.CanUndoOrganize ? 0 : Win32.MF_GRAYED), (UIntPtr)(uint)Cmd.UndoOrganize, "撤销整理(&U)");
            Win32.AppendMenu(menu, Win32.MF_STRING, (UIntPtr)(uint)Cmd.NewBox, "新建格子(&B)");
            Win32.AppendMenu(menu, Win32.MF_SEPARATOR, UIntPtr.Zero, null);
            Win32.AppendMenu(menu, Win32.MF_STRING, (UIntPtr)(uint)Cmd.ToggleIcons, _c.IconsVisible ? "隐藏桌面图标(&H)" : "显示桌面图标(&H)");
            Win32.AppendMenu(menu, Win32.MF_SEPARATOR, UIntPtr.Zero, null);
            Win32.AppendMenu(menu, Win32.MF_STRING, (UIntPtr)(uint)Cmd.Settings, "设置(&S)…");
            Win32.AppendMenu(menu, Win32.MF_STRING | (_autoStart.IsEnabled ? Win32.MF_CHECKED : 0), (UIntPtr)(uint)Cmd.AutoStart, "开机自启(&A)");
            Win32.AppendMenu(menu, Win32.MF_SEPARATOR, UIntPtr.Zero, null);
            Win32.AppendMenu(menu, Win32.MF_STRING, (UIntPtr)(uint)Cmd.Exit, "退出(&X)");

            Win32.GetCursorPos(out var pt);
            Win32.SetForegroundWindow(_window.Handle); // 否则点菜单外不会关闭
            var cmd = Win32.TrackPopupMenuEx(menu, Win32.TPM_RETURNCMD | Win32.TPM_RIGHTBUTTON | Win32.TPM_BOTTOMALIGN, pt.X, pt.Y, _window.Handle, IntPtr.Zero);
            Win32.PostMessage(_window.Handle, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (cmd != 0) Execute((Cmd)cmd);
        }
        finally
        {
            Win32.DestroyMenu(menu);
            _menuOpen = false;
        }
    }

    private void Execute(Cmd cmd)
    {
        Log.Info($"托盘菜单：{cmd}");
        switch (cmd)
        {
            case Cmd.Organize: _c.SetIconsVisible(true); _c.OrganizeAll(); break;
            case Cmd.UndoOrganize: _c.UndoOrganize(); break;
            case Cmd.NewBox: _c.NewBoxCentered(); break;
            case Cmd.ToggleIcons: _c.SetIconsVisible(!_c.IconsVisible); break;
            case Cmd.Settings: _openSettings(); break;
            case Cmd.AutoStart: _autoStart.Toggle(); break;
            case Cmd.Exit: _exit(); break;
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            var d = NewData();
            Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref d);
            _added = false;
        }
        if (_hIcon != IntPtr.Zero) { Win32.DestroyIcon(_hIcon); _hIcon = IntPtr.Zero; }
    }
}
