using System.Runtime.InteropServices;

namespace XkDesk.Native;

/// <summary>托盘图标（Shell_NotifyIcon）与图标创建相关 P/Invoke。</summary>
internal static partial class Win32
{
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    public const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    public const uint NIIF_INFO = 1;
    public const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_LBUTTONDBLCLK = 0x0203;
    public const uint MF_STRING = 0, MF_GRAYED = 1, MF_CHECKED = 8, MF_SEPARATOR = 0x800;
    public const uint TPM_BOTTOMALIGN = 0x20;
    public const int SM_CXSMICON = 49;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    public static extern IntPtr CreateIconFromResourceEx(byte[] presbits, uint dwResSize, [MarshalAs(UnmanagedType.Bool)] bool fIcon, uint dwVer, int cxDesired, int cyDesired, uint flags);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);
}
