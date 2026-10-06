using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DeskNext.Model;
using DeskNext.Native;
using DeskNext.Services;

namespace DeskNext.Desktop;

/// <summary>Shell 原生操作：重命名、拖出。（打开/删除/复制等走 ShellContextMenu.InvokeVerb。）</summary>
internal static class ShellActions
{
    /// <summary>
    /// 在项所在的文件夹（桌面根或映射目录）上用 IShellFolder.SetNameOf 重命名（与 Explorer 一致处理隐藏扩展名、非法字符提示）。
    /// 成功返回新的绝对 PIDL 字节与新的解析名（不含映射格子前缀）。
    /// </summary>
    public static (byte[] Pidl, string Key)? Rename(DesktopItem item, string newName, IntPtr hwnd)
    {
        var pidl = ShellApi.PidlFromBytes(item.Pidl);
        var parent = ShellApi.BindParent(pidl, out var last);
        try
        {
            if (parent == null) return null;
            var hr = parent.SetNameOf(hwnd, last, newName, ShellApi.SHGDN_INFOLDER | ShellApi.SHGDN_FOREDITING, out var newPidl);
            if (hr < 0 || newPidl == IntPtr.Zero)
            {
                Log.Info($"重命名未完成 {item.Key} → {newName} hr=0x{hr:X}");
                return null;
            }
            try
            {
                var key = ShellApi.GetDisplayName(parent, newPidl, ShellApi.SHGDN_FORPARSING);
                if (ShellApi.IsSingleId(pidl)) return (ShellApi.PidlToBytes(newPidl), key);
                // 映射目录里的项：新的绝对 PIDL = 父绝对 PIDL + 新子 PIDL
                Win32.ILRemoveLastID(pidl);
                var full = Win32.ILCombine(pidl, newPidl);
                try { return (ShellApi.PidlToBytes(full), key); }
                finally { Win32.ILFree(full); }
            }
            finally
            {
                Marshal.FreeCoTaskMem(newPidl);
            }
        }
        finally
        {
            ShellApi.ReleaseParent(parent);
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
