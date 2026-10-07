using System.Runtime.InteropServices;

namespace DeskNook.Native;

/// <summary>阶段 1 新增的 Shell / 菜单 / GDI / OLE P/Invoke（与 Win32.cs 同一个类）。</summary>
internal static partial class Win32
{
    // 窗口消息
    public const int WM_NULL = 0x0000;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_INITMENUPOPUP = 0x0117;
    public const int WM_MENUSELECT = 0x011F;
    public const int WM_MENUCHAR = 0x0120;
    public const int WM_MEASUREITEM = 0x002C;
    public const int WM_DRAWITEM = 0x002B;
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public const int WM_APP = 0x8000;
    public const int MA_ACTIVATE = 1;

    // 菜单
    public const uint MF_BYCOMMAND = 0x0000;
    public const uint MF_BYPOSITION = 0x0400;
    public const uint MIIM_STATE = 0x01;
    public const uint MIIM_ID = 0x02;
    public const uint MIIM_SUBMENU = 0x04;
    public const uint MIIM_STRING = 0x40;
    public const uint MIIM_FTYPE = 0x100;
    public const uint MFT_SEPARATOR = 0x800;
    public const uint MFT_RADIOCHECK = 0x200;
    public const uint MFS_CHECKED = 0x08;
    public const uint MFS_DISABLED = 0x03;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_RETURNCMD = 0x0100;
    public const uint GMDI_USEDISABLED = 0x0001;

    // 虚拟键
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx, cy;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MENUITEMINFO
    {
        public int cbSize;
        public uint fMask, fType, fState, wID;
        public IntPtr hSubMenu, hbmpChecked, hbmpUnchecked;
        public UIntPtr dwItemData;
        public IntPtr dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public short bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SHChangeNotifyEntry
    {
        public IntPtr pidl;
        public int fRecursive; // BOOL，保持可 blit
    }

    // ---- uxtheme 未公开序号：让弹出菜单跟随系统深色模式 ----
    [DllImport("uxtheme.dll", EntryPoint = "#135")] public static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#133")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool AllowDarkModeForWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool allow);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] public static extern void FlushMenuThemes();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMenuItemInfo(IntPtr hMenu, uint item, [MarshalAs(UnmanagedType.Bool)] bool fByPosition, ref MENUITEMINFO lpmii);

    [StructLayout(LayoutKind.Sequential)]
    public struct MENUINFO
    {
        public int cbSize;
        public uint fMask, dwStyle;
        public uint cyMax;
        public IntPtr hbrBack;
        public uint dwContextHelpID;
        public UIntPtr dwMenuData;
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetMenuInfo(IntPtr hMenu, ref MENUINFO lpcmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetMenuItemInfo(IntPtr hMenu, uint item, [MarshalAs(UnmanagedType.Bool)] bool fByPosition, ref MENUITEMINFO lpmii);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool RemoveMenu(IntPtr hMenu, uint uPosition, uint uFlags);

    [DllImport("shell32.dll")] public static extern int SHCreateDefaultContextMenu(ref DEFCONTEXTMENU pdcm, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] public static extern int RegOpenKeyExW(IntPtr hKey, string subKey, int options, int sam, out IntPtr result);
    [DllImport("advapi32.dll")] public static extern int RegCloseKey(IntPtr hKey);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHSTOCKICONINFO
    {
        public uint cbSize;
        public IntPtr hIcon;
        public int iSysImageIndex, iIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szPath;
    }

    [DllImport("shell32.dll")] public static extern int SHGetStockIconInfo(int siid, uint flags, ref SHSTOCKICONINFO psii);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyIcon(IntPtr hIcon);

    // ---- user32 ----
    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr hMenu);
    [DllImport("user32.dll")] public static extern IntPtr GetSubMenu(IntPtr hMenu, int nPos);
    [DllImport("user32.dll")] public static extern uint GetMenuItemID(IntPtr hMenu, int nPos);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetMenuString(IntPtr hMenu, uint uIDItem, System.Text.StringBuilder lpString, int cchMax, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InsertMenuItem(IntPtr hMenu, uint item, [MarshalAs(UnmanagedType.Bool)] bool fByPosition, ref MENUITEMINFO lpmii);
    [DllImport("user32.dll")] public static extern uint GetMenuDefaultItem(IntPtr hMenu, uint fByPos, uint gmdiFlags);
    [DllImport("user32.dll")] public static extern uint TrackPopupMenuEx(IntPtr hMenu, uint flags, int x, int y, IntPtr hwnd, IntPtr lptpm);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] public static extern short GetKeyState(int nVirtKey);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    // ---- gdi32 ----
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DeleteObject(IntPtr ho);
    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, byte[]? lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);
    [DllImport("gdi32.dll")] public static extern int GetObject(IntPtr h, int c, out BITMAP pv);

    // ---- shcore ----
    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    // ---- ole32 ----
    [DllImport("ole32.dll")] public static extern int OleInitialize(IntPtr pvReserved);
    [DllImport("ole32.dll")] public static extern int RegisterDragDrop(IntPtr hwnd, IDropTarget pDropTarget);
    [DllImport("ole32.dll")] public static extern int RevokeDragDrop(IntPtr hwnd);

    // ---- shell32 / shlwapi ----
    [DllImport("shell32.dll")] public static extern int SHGetDesktopFolder(out IShellFolder ppshf);
    [DllImport("shell32.dll")] public static extern int SHGetSpecialFolderLocation(IntPtr hwnd, int csidl, out IntPtr ppidl);
    [DllImport("shell32.dll")] public static extern int SHGetKnownFolderIDList([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr hToken, out IntPtr ppidl);
    [DllImport("shell32.dll")] public static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdnName, out IntPtr ppszName);
    [DllImport("shell32.dll")] public static extern uint ILGetSize(IntPtr pidl);
    [DllImport("shell32.dll")] public static extern void ILFree(IntPtr pidl);
    [DllImport("shell32.dll")] public static extern IntPtr ILCombine(IntPtr pidl1, IntPtr pidl2);
    [DllImport("shell32.dll")] public static extern IntPtr ILFindLastID(IntPtr pidl);
    [DllImport("shell32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ILRemoveLastID(IntPtr pidl);
    [DllImport("shell32.dll")] public static extern int SHBindToParent(IntPtr pidl, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv, out IntPtr ppidlLast);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);
    [DllImport("shell32.dll")] public static extern int SHCreateItemFromIDList(IntPtr pidl, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
    [DllImport("shell32.dll")]
    public static extern int SHDoDragDrop(IntPtr hwnd, [MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject pdtobj, IntPtr pdsrc, uint dwEffect, out uint pdwEffect);
    [DllImport("shell32.dll")]
    public static extern uint SHChangeNotifyRegister(IntPtr hwnd, int fSources, int fEvents, uint wMsg, int cEntries, IntPtr pshcne);
    [DllImport("shell32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SHChangeNotifyDeregister(uint ulID);
    [DllImport("shell32.dll")] public static extern IntPtr SHChangeNotification_Lock(IntPtr hChange, uint dwProcId, out IntPtr pppidl, out int plEvent);
    [DllImport("shell32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SHChangeNotification_Unlock(IntPtr hLock);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] public static extern int StrCmpLogicalW(string psz1, string psz2);
    [DllImport("shlwapi.dll")] public static extern int StrRetToStrW(ref STRRET pstr, IntPtr pidl, out IntPtr ppsz);
}
