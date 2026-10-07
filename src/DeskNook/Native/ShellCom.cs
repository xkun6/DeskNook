using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace DeskNook.Native;

// 手写的 Shell COM 接口（vtable 顺序严格按 SDK 头文件；未使用的方法用占位签名保持槽位）。

[StructLayout(LayoutKind.Explicit, Size = 272)]
internal struct STRRET
{
    [FieldOffset(0)] public uint uType;
    [FieldOffset(8)] public IntPtr pOleStr;
}

[ComImport, Guid("000214F2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumIDList
{
    [PreserveSig] int Next(uint celt, out IntPtr rgelt, out uint pceltFetched);
    [PreserveSig] int Skip(uint celt);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(out IEnumIDList ppenum);
}

[ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellFolder
{
    [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
    [PreserveSig] int EnumObjects(IntPtr hwnd, uint grfFlags, out IEnumIDList ppenumIDList);
    [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
    [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
    [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
    [PreserveSig] int CreateViewObject(IntPtr hwndOwner, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
    [PreserveSig] int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
    [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, IntPtr rgfReserved, out IntPtr ppv);
    [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, out STRRET pName);
    [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
}

[StructLayout(LayoutKind.Sequential)]
internal struct CMINVOKECOMMANDINFOEX
{
    public int cbSize;
    public uint fMask;
    public IntPtr hwnd;
    public IntPtr lpVerb;
    public IntPtr lpParameters;
    public IntPtr lpDirectory;
    public int nShow;
    public uint dwHotKey;
    public IntPtr hIcon;
    public IntPtr lpTitle;
    public IntPtr lpVerbW;
    public IntPtr lpParametersW;
    public IntPtr lpDirectoryW;
    public IntPtr lpTitleW;
    public Win32.POINT ptInvoke;
}

[ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu
{
    [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
    [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
    [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
}

[ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu2 : IContextMenu
{
    [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
}

[ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu3 : IContextMenu2
{
    [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
}

[ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
    [PreserveSig] int GetImage(Win32.SIZE size, uint flags, out IntPtr phbm);
}

/// <summary>
/// 系统图像列表（SHGetImageList 返回）。必须用 COM 接口声明而不是 P/Invoke comctl32 的 ImageList_*：
/// 本程序可能没有加载 comctl32 v6，混用会崩。vtable 顺序与数量必须与系统一致（前 14 个）；用不到的方法参数用 IntPtr 占位。
/// </summary>
[ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IImageList
{
    [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);
    [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, out int pi);
    [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
    [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
    [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, out int pi);
    [PreserveSig] int Draw(IntPtr pimldp);
    [PreserveSig] int Remove(int i);
    [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
    [PreserveSig] int GetImageInfo(int i, IntPtr pImageInfo);
    [PreserveSig] int Copy(int iDst, IntPtr punkSrc, int iSrc, uint uFlags);
    [PreserveSig] int Merge(int i1, IntPtr punk2, int i2, int dx, int dy, IntPtr riid, out IntPtr ppv);
    [PreserveSig] int Clone(IntPtr riid, out IntPtr ppv);
    [PreserveSig] int GetImageRect(int i, IntPtr prc);
    [PreserveSig] int GetIconSize(out int cx, out int cy);
}

[ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellBrowser
{
    // IOleWindow
    [PreserveSig] int GetWindow(out IntPtr phwnd);
    [PreserveSig] int ContextSensitiveHelp(bool fEnterMode);
    // IShellBrowser
    [PreserveSig] int InsertMenusSB();
    [PreserveSig] int SetMenuSB();
    [PreserveSig] int RemoveMenusSB();
    [PreserveSig] int SetStatusTextSB();
    [PreserveSig] int EnableModelessSB();
    [PreserveSig] int TranslateAcceleratorSB();
    [PreserveSig] int BrowseObject();
    [PreserveSig] int GetViewStateStream();
    [PreserveSig] int GetControlWindow();
    [PreserveSig] int SendControlMsg();
    [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object ppshv);
}

[ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellView
{
    // IOleWindow
    [PreserveSig] int GetWindow(out IntPtr phwnd);
    [PreserveSig] int ContextSensitiveHelp(bool fEnterMode);
    // IShellView
    [PreserveSig] int TranslateAccelerator(IntPtr pmsg);
    [PreserveSig] int EnableModeless(bool fEnable);
    [PreserveSig] int UIActivate(uint uState);
    [PreserveSig] int Refresh();
    [PreserveSig] int CreateViewWindow();
    [PreserveSig] int DestroyViewWindow();
    [PreserveSig] int GetCurrentInfo();
    [PreserveSig] int AddPropertySheetPages();
    [PreserveSig] int SaveViewState();
    [PreserveSig] int SelectItem(IntPtr pidlItem, uint uFlags);
    [PreserveSig] int GetItemObject(uint uItem, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
}

/// <summary>COM 的 IServiceProvider（System.IServiceProvider 同名，所以加 Com 后缀）。</summary>
[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IServiceProviderCom
{
    [PreserveSig] int QueryService([MarshalAs(UnmanagedType.LPStruct)] Guid guidService, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppvObject);
}

[ComImport, Guid("FC4801A3-2BA9-11CF-A229-00AA003D7352"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IObjectWithSite
{
    [PreserveSig] int SetSite([MarshalAs(UnmanagedType.IUnknown)] object? pUnkSite);
    [PreserveSig] int GetSite([MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppvSite);
}

[StructLayout(LayoutKind.Sequential)]
internal struct ItemPoint
{
    public int X, Y;
}

/// <summary>IFolderView2：只用到 GetItemPosition/GetSpacing/GetViewModeAndIconSize，其余为占位。</summary>
[ComImport, Guid("1AF3A467-214F-4298-908E-06B03E0B39F9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFolderView2
{
    // IFolderView
    [PreserveSig] int GetCurrentViewMode(out uint pViewMode);
    [PreserveSig] int SetCurrentViewMode(uint viewMode);
    [PreserveSig] int GetFolder();
    [PreserveSig] int Item();
    [PreserveSig] int ItemCount();
    [PreserveSig] int Items();
    [PreserveSig] int GetSelectionMarkedItem();
    [PreserveSig] int GetFocusedItem();
    [PreserveSig] int GetItemPosition(IntPtr pidl, out ItemPoint ppt);
    [PreserveSig] int GetSpacing(out ItemPoint ppt);
    [PreserveSig] int GetDefaultSpacing(out ItemPoint ppt);
    [PreserveSig] int GetAutoArrange();
    [PreserveSig] int SelectItem();
    [PreserveSig] int SelectAndPositionItems(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, IntPtr apt, uint dwFlags);
    // IFolderView2
    [PreserveSig] int SetGroupBy();
    [PreserveSig] int GetGroupBy();
    [PreserveSig] int SetViewProperty();
    [PreserveSig] int GetViewProperty();
    [PreserveSig] int SetTileViewProperties();
    [PreserveSig] int SetExtendedTileViewProperties();
    [PreserveSig] int SetText();
    [PreserveSig] int SetCurrentFolderFlags();
    [PreserveSig] int GetCurrentFolderFlags();
    [PreserveSig] int GetSortColumnCount();
    [PreserveSig] int SetSortColumns();
    [PreserveSig] int GetSortColumns();
    [PreserveSig] int GetItem();
    [PreserveSig] int GetVisibleItem();
    [PreserveSig] int GetSelectedItem();
    [PreserveSig] int GetSelection();
    [PreserveSig] int GetSelectionState();
    [PreserveSig] int InvokeVerbOnSelection();
    [PreserveSig] int SetViewModeAndIconSize();
    [PreserveSig] int GetViewModeAndIconSize(out uint pViewMode, out int pIconSize);
}

/// <summary>IShellWindows（IDispatch 派生，前 4 个槽位为 IDispatch）。</summary>
[ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellWindows
{
    [PreserveSig] int GetTypeInfoCount();
    [PreserveSig] int GetTypeInfo();
    [PreserveSig] int GetIDsOfNames();
    [PreserveSig] int Invoke();
    [PreserveSig] int get_Count();
    [PreserveSig] int Item();
    [PreserveSig] int _NewEnum();
    [PreserveSig] int Register();
    [PreserveSig] int RegisterPending();
    [PreserveSig] int Revoke();
    [PreserveSig] int OnNavigate();
    [PreserveSig] int OnActivated();
    [PreserveSig] int FindWindowSW(ref object pvarLoc, ref object pvarLocRoot, int swClass, out int phwnd, int swfwOptions,
        [MarshalAs(UnmanagedType.IDispatch)] out object ppdispOut);
}

[ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDropTarget
{
    [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject pDataObj, uint grfKeyState, Win32.POINT pt, ref uint pdwEffect);
    [PreserveSig] int DragOver(uint grfKeyState, Win32.POINT pt, ref uint pdwEffect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject pDataObj, uint grfKeyState, Win32.POINT pt, ref uint pdwEffect);
}

[ComImport, Guid("4657278B-411B-11D2-839A-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDropTargetHelper
{
    [PreserveSig] int DragEnter(IntPtr hwndTarget, [MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject pDataObject, ref Win32.POINT ppt, uint dwEffect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int DragOver(ref Win32.POINT ppt, uint dwEffect);
    [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject pDataObject, ref Win32.POINT ppt, uint dwEffect);
    [PreserveSig] int Show(bool fShow);
}

[StructLayout(LayoutKind.Sequential)]
internal struct DEFCONTEXTMENU
{
    public IntPtr hwnd;
    public IntPtr pcmcb;
    public IntPtr pidlFolder;
    public IntPtr psf;
    public uint cidl;
    public IntPtr apidl;
    public IntPtr punkAssociationInfo;
    public uint cKeys;
    public IntPtr aKeys;
}

/// <summary>Shell 常量与 PIDL / 名称辅助。</summary>
internal static class ShellApi
{
    public static readonly Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");
    public static readonly Guid IID_IDataObject = new("0000010E-0000-0000-C000-000000000046");
    public static readonly Guid IID_IDropTarget = new("00000122-0000-0000-C000-000000000046");
    public static readonly Guid IID_IShellItemImageFactory = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");
    public static readonly Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");
    public static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    public static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    public static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    public static readonly Guid CLSID_DragDropHelper = new("4657278A-411B-11D2-839A-00C04FD918D0");
    public static readonly Guid FOLDERID_Desktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    public static readonly Guid FOLDERID_PublicDesktop = new("C4AA340D-F20F-4863-AFEF-F87EF2E6BA25");

    // SHCONTF
    public const uint SHCONTF_FOLDERS = 0x20;
    public const uint SHCONTF_NONFOLDERS = 0x40;
    public const uint SHCONTF_INCLUDEHIDDEN = 0x80;
    public const uint SHCONTF_INCLUDESUPERHIDDEN = 0x10000;

    // SHGDN
    public const uint SHGDN_NORMAL = 0;
    public const uint SHGDN_INFOLDER = 1;
    public const uint SHGDN_FOREDITING = 0x1000;
    public const uint SHGDN_FORPARSING = 0x8000;

    // SIGDN
    public const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;
    public const uint SIGDN_FILESYSPATH = 0x80058000;

    // SFGAO
    public const uint SFGAO_CANRENAME = 0x00000010;
    public const uint SFGAO_DROPTARGET = 0x00000100;
    public const uint SFGAO_GHOSTED = 0x00008000;
    public const uint SFGAO_LINK = 0x00010000;
    public const uint SFGAO_FILESYSTEM = 0x40000000;
    public const uint SFGAO_HIDDEN = 0x00080000;
    public const uint SFGAO_FOLDER = 0x20000000;
    public const uint SFGAO_BROWSABLE = 0x08000000;

    // SHCNE
    public const int SHCNE_RENAMEITEM = 0x1;
    public const int SHCNE_CREATE = 0x2;
    public const int SHCNE_DELETE = 0x4;
    public const int SHCNE_MKDIR = 0x8;
    public const int SHCNE_RMDIR = 0x10;
    public const int SHCNE_ATTRIBUTES = 0x800;
    public const int SHCNE_UPDATEDIR = 0x1000;
    public const int SHCNE_UPDATEITEM = 0x2000;
    public const int SHCNE_UPDATEIMAGE = 0x8000;
    public const int SHCNE_RENAMEFOLDER = 0x20000;
    public const int SHCNE_ALLEVENTS = 0x7FFFFFFF;
    public const int SHCNE_INTERRUPT = unchecked((int)0x80000000);
    public const int SHCNRF_InterruptLevel = 0x1;
    public const int SHCNRF_ShellLevel = 0x2;
    public const int SHCNRF_NewDelivery = 0x8000;

    // IContextMenu
    public const uint CMF_NORMAL = 0, CMF_DEFAULTONLY = 1, CMF_EXPLORE = 4, CMF_CANRENAME = 0x10, CMF_ITEMMENU = 0x80, CMF_EXTENDEDVERBS = 0x100;
    public const uint CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000, CMIC_MASK_SHIFT_DOWN = 0x10000000, CMIC_MASK_CONTROL_DOWN = 0x40000000;
    public const uint GCS_VERBW = 4;

    // DROPEFFECT
    public const uint DROPEFFECT_NONE = 0, DROPEFFECT_COPY = 1, DROPEFFECT_MOVE = 2, DROPEFFECT_LINK = 4;

    // SIIGBF
    public const uint SIIGBF_BIGGERSIZEOK = 0x1;

    public const int S_OK = 0;
    public const int S_FALSE = 1;

    private static IShellFolder? _desktop;

    /// <summary>桌面根文件夹（调用线程需为创建它的 STA 线程，即 UI 线程）。</summary>
    public static IShellFolder Desktop
    {
        get
        {
            if (_desktop == null)
            {
                Marshal.ThrowExceptionForHR(Win32.SHGetDesktopFolder(out var f));
                _desktop = f;
            }
            return _desktop;
        }
    }

    public static string GetDisplayName(IShellFolder folder, IntPtr childPidl, uint flags)
    {
        var hr = folder.GetDisplayNameOf(childPidl, flags, out var sr);
        if (hr < 0) return "";
        if (Win32.StrRetToStrW(ref sr, childPidl, out var p) < 0 || p == IntPtr.Zero) return "";
        try { return Marshal.PtrToStringUni(p) ?? ""; }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    /// <summary>绝对 PIDL → 名称（SIGDN）。</summary>
    public static string? GetNameFromPidl(IntPtr absPidl, uint sigdn)
    {
        if (absPidl == IntPtr.Zero) return null;
        if (Win32.SHGetNameFromIDList(absPidl, sigdn, out var p) < 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    public static byte[] PidlToBytes(IntPtr pidl)
    {
        var size = (int)Win32.ILGetSize(pidl);
        var bytes = new byte[size];
        Marshal.Copy(pidl, bytes, 0, size);
        return bytes;
    }

    /// <summary>字节 → CoTaskMem PIDL，调用方用 Marshal.FreeCoTaskMem 释放。</summary>
    public static IntPtr PidlFromBytes(byte[] bytes)
    {
        var p = Marshal.AllocCoTaskMem(bytes.Length);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        return p;
    }

    /// <summary>一组 PIDL 字节的临时非托管拷贝。</summary>
    public sealed class PidlArray : IDisposable
    {
        public IntPtr[] Ptrs { get; }

        public PidlArray(IEnumerable<byte[]> pidls) => Ptrs = pidls.Select(PidlFromBytes).ToArray();

        public void Dispose()
        {
            foreach (var p in Ptrs) Marshal.FreeCoTaskMem(p);
        }
    }

    public static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");

    /// <summary>PIDL 是否只有一级（即桌面根的直接子项）。</summary>
    public static bool IsSingleId(IntPtr pidl)
    {
        var cb = (ushort)Marshal.ReadInt16(pidl);
        return cb == 0 || (ushort)Marshal.ReadInt16(pidl, cb) == 0;
    }

    /// <summary>
    /// 绝对 PIDL 的父文件夹：一级 PIDL 的父就是桌面根；多级（映射格子里的项）用 SHBindToParent。
    /// lastId 指向 absPidl 内部最后一级（不要释放）。调用方负责 ReleaseParent。
    /// </summary>
    public static IShellFolder? BindParent(IntPtr absPidl, out IntPtr lastId)
    {
        if (IsSingleId(absPidl)) { lastId = absPidl; return Desktop; }
        lastId = IntPtr.Zero;
        if (Win32.SHBindToParent(absPidl, IID_IShellFolder, out var ppv, out lastId) < 0 || ppv == IntPtr.Zero) return null;
        try { return Marshal.GetObjectForIUnknown(ppv) as IShellFolder; }
        finally { Marshal.Release(ppv); }
    }

    public static void ReleaseParent(IShellFolder? parent)
    {
        if (parent != null && !ReferenceEquals(parent, _desktop)) Marshal.ReleaseComObject(parent);
    }

    /// <summary>按路径绑定目录的 IShellFolder，同时返回其绝对 PIDL（调用方 ILFree）。失败返回 null。</summary>
    public static IShellFolder? BindFolder(string path, out IntPtr absPidl)
    {
        absPidl = IntPtr.Zero;
        if (Win32.SHParseDisplayName(path, IntPtr.Zero, out var abs, 0, out _) < 0 || abs == IntPtr.Zero) return null;
        var hr = Desktop.BindToObject(abs, IntPtr.Zero, IID_IShellFolder, out var ppv);
        if (hr < 0 || ppv == IntPtr.Zero) { Win32.ILFree(abs); return null; }
        try
        {
            var f = Marshal.GetObjectForIUnknown(ppv) as IShellFolder;
            if (f == null) { Win32.ILFree(abs); return null; }
            absPidl = abs;
            return f;
        }
        finally { Marshal.Release(ppv); }
    }

    /// <summary>取绝对 PIDL 项的 SFGAO 属性。</summary>
    public static uint GetAttributes(byte[] pidlBytes, uint mask)
    {
        var p = PidlFromBytes(pidlBytes);
        var parent = BindParent(p, out var last);
        try
        {
            if (parent == null) return 0;
            return parent.GetAttributesOf(1, new[] { last }, ref mask) >= 0 ? mask : 0;
        }
        finally
        {
            ReleaseParent(parent);
            Marshal.FreeCoTaskMem(p);
        }
    }

    /// <summary>取项的 COM 对象（IContextMenu / IDataObject / IDropTarget 等）。PIDL 为绝对 PIDL，且应属于同一父文件夹（以第一个为准）。</summary>
    public static T? GetUIObjectOf<T>(IReadOnlyList<byte[]> pidls, Guid iid, IntPtr hwnd) where T : class
    {
        using var arr = new PidlArray(pidls);
        if (arr.Ptrs.Length == 0) return null;
        var parent = BindParent(arr.Ptrs[0], out _);
        try
        {
            if (parent == null) return null;
            var last = arr.Ptrs.Select(Win32.ILFindLastID).ToArray();
            var hr = parent.GetUIObjectOf(hwnd, (uint)last.Length, last, iid, IntPtr.Zero, out var ppv);
            if (hr < 0 || ppv == IntPtr.Zero) return null;
            try { return Marshal.GetObjectForIUnknown(ppv) as T; }
            finally { Marshal.Release(ppv); }
        }
        finally
        {
            ReleaseParent(parent);
        }
    }

    public static T? CreateViewObject<T>(Guid iid, IntPtr hwnd) where T : class
    {
        var hr = Desktop.CreateViewObject(hwnd, iid, out var ppv);
        if (hr < 0 || ppv == IntPtr.Zero) return null;
        try { return Marshal.GetObjectForIUnknown(ppv) as T; }
        finally { Marshal.Release(ppv); }
    }
}
