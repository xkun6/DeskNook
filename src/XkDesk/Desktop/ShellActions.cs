using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using XkDesk.Model;
using XkDesk.Native;
using XkDesk.Services;

namespace XkDesk.Desktop;

/// <summary>Shell 原生操作：重命名、拖出。（打开/删除/复制等走 ShellContextMenu.InvokeVerb。）</summary>
internal static class ShellActions
{
    /// <summary>IShellFolder.SetNameOf 重命名（与 Explorer 一致处理隐藏扩展名、非法字符提示）。成功返回新的子 PIDL 字节与新 Key。</summary>
    public static (byte[] Pidl, string Key)? Rename(DesktopItem item, string newName, IntPtr hwnd)
    {
        var pidl = ShellApi.PidlFromBytes(item.Pidl);
        try
        {
            var hr = ShellApi.Desktop.SetNameOf(hwnd, pidl, newName, ShellApi.SHGDN_INFOLDER | ShellApi.SHGDN_FOREDITING, out var newPidl);
            if (hr < 0 || newPidl == IntPtr.Zero)
            {
                Log.Info($"重命名未完成 {item.Key} → {newName} hr=0x{hr:X}");
                return null;
            }
            try
            {
                var key = ShellApi.GetDisplayName(ShellApi.Desktop, newPidl, ShellApi.SHGDN_FORPARSING);
                return (ShellApi.PidlToBytes(newPidl), key);
            }
            finally
            {
                Marshal.FreeCoTaskMem(newPidl);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>取选中项的 Shell 数据对象（可用于拖出 / 剪贴板）。</summary>
    public static IDataObject? GetDataObject(IReadOnlyList<DesktopItem> items, IntPtr hwnd) =>
        ShellApi.GetUIObjectOf<IDataObject>(items.Select(i => i.Pidl).ToList(), ShellApi.IID_IDataObject, hwnd);

    /// <summary>SHDoDragDrop 拖出（带系统拖拽图像）。阻塞到拖放结束，返回最终效果。</summary>
    public static uint DoDragDrop(IReadOnlyList<DesktopItem> items, IntPtr hwnd)
    {
        var data = GetDataObject(items, hwnd);
        if (data == null) return ShellApi.DROPEFFECT_NONE;
        try
        {
            var hr = Win32.SHDoDragDrop(hwnd, data, IntPtr.Zero,
                ShellApi.DROPEFFECT_COPY | ShellApi.DROPEFFECT_MOVE | ShellApi.DROPEFFECT_LINK, out var effect);
            Log.Info($"SHDoDragDrop 结束 hr=0x{hr:X} 效果={effect}");
            return effect;
        }
        finally
        {
            Marshal.ReleaseComObject(data);
        }
    }
}
