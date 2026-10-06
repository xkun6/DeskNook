using System.IO;
using System.Windows;
using System.Windows.Interop;
using DeskNook.Native;
using DeskNook.Services;

namespace DeskNook.Desktop;

/// <summary>监听剪贴板变化，识别“剪切”状态（Preferred DropEffect = MOVE 且带文件列表）。</summary>
internal sealed class ClipboardWatcher : IDisposable
{
    private readonly HwndSource _window;

    /// <summary>剪贴板内容变化（UI 线程）。</summary>
    public event Action? Changed;

    public ClipboardWatcher()
    {
        var p = new HwndSourceParameters("DeskNookClipboard")
        {
            WindowStyle = unchecked((int)Win32.WS_POPUP),
            ExtendedWindowStyle = (int)Win32.WS_EX_TOOLWINDOW,
            Width = 0,
            Height = 0,
        };
        _window = new HwndSource(p);
        _window.AddHook(WndProc);
        Win32.AddClipboardFormatListener(_window.Handle);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_CLIPBOARDUPDATE)
        {
            try { Changed?.Invoke(); }
            catch (Exception ex) { Log.Error("剪贴板变化处理异常", ex); }
        }
        return IntPtr.Zero;
    }

    /// <summary>当前被“剪切”的文件路径集合（没有则为空）。</summary>
    public static HashSet<string> GetCutPaths()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var data = Clipboard.GetDataObject();
                if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return result;
                if (data.GetData("Preferred DropEffect") is not MemoryStream ms || ms.Length < 4) return result;
                var effect = BitConverter.ToInt32(ms.ToArray(), 0);
                if ((effect & (int)ShellApi.DROPEFFECT_MOVE) == 0) return result;
                if (data.GetData(DataFormats.FileDrop) is string[] files)
                    foreach (var f in files) result.Add(f);
                return result;
            }
            catch (Exception)
            {
                Thread.Sleep(30); // 剪贴板被占用，稍后重试
            }
        }
        return result;
    }

    public void Dispose()
    {
        Win32.RemoveClipboardFormatListener(_window.Handle);
        _window.Dispose();
    }
}
