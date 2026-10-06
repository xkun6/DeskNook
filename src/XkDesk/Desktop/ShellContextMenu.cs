using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using XkDesk.Model;
using XkDesk.Native;
using XkDesk.Services;

namespace XkDesk.Desktop;

/// <summary>
/// 原生右键菜单：向 Shell 要真菜单（IContextMenu），在其前后插入 MenuExtensions 的自定义项，
/// 菜单期间由宿主窗口把 WM_INITMENUPOPUP/DRAWITEM/MEASUREITEM/MENUCHAR 转发给 IContextMenu2/3。
/// 全部在 UI(STA) 线程；COM 对象在命令执行完后释放。
/// </summary>
internal static class ShellContextMenu
{
    private const uint ShellFirst = 1, ShellLast = 0x7FFF;

    // 菜单弹出期间的消息转发目标
    private static IContextMenu2? _msg2;
    private static IContextMenu3? _msg3;
    // 本次菜单期间复制出的位图（菜单结束后释放）
    private static readonly HashSet<IntPtr> Owned = new();

    /// <summary>宿主窗口过程调用：菜单期间转发菜单消息。返回 true 表示已处理。</summary>
    public static bool TryHandleMessage(int msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (_msg3 == null && _msg2 == null) return false;
        if (msg != Win32.WM_INITMENUPOPUP && msg != Win32.WM_DRAWITEM && msg != Win32.WM_MEASUREITEM &&
            msg != Win32.WM_MENUCHAR && msg != Win32.WM_MENUSELECT) return false;
        try
        {
            bool handled;
            if (_msg3 != null)
                handled = _msg3.HandleMenuMsg2((uint)msg, wParam, lParam, out result) == ShellApi.S_OK && msg != Win32.WM_MENUSELECT;
            else
                handled = _msg2!.HandleMenuMsg((uint)msg, wParam, lParam) == ShellApi.S_OK && msg != Win32.WM_MENUSELECT;
            // 扩展在 WM_INITMENUPOPUP 里才补的图标同样需要转换
            if (msg == Win32.WM_INITMENUPOPUP) Reapply(wParam);
            return handled;
        }
        catch (Exception ex)
        {
            Log.Error("菜单消息转发异常", ex);
            return false;
        }
    }

    /// <summary>外部接管取菜单对象（返回 null = 走默认）。映射格子空白处用它换成该目录的背景菜单。</summary>
    public static Func<IReadOnlyList<DesktopItem>, IntPtr, IContextMenu?>? CreateOverride;

    private static IContextMenu? Create(IReadOnlyList<DesktopItem> items, IntPtr hwnd)
    {
        var ov = CreateOverride?.Invoke(items, hwnd);
        if (ov != null) return ov;
        var cm = CreateDefault(items, hwnd);
        if (cm != null) return cm;
        return items.Count == 0
            ? ShellApi.CreateViewObject<IContextMenu>(ShellApi.IID_IContextMenu, hwnd)
            : ShellApi.GetUIObjectOf<IContextMenu>(items.Select(i => i.Pidl).ToList(), ShellApi.IID_IContextMenu, hwnd);
    }

    private static readonly HashSet<string> TracedKeys = new();

    /// <summary>
    /// 与 Explorer 一样用 SHCreateDefaultContextMenu 并显式给出完整的注册表类键，
    /// 这样 HKCR\* / AllFilesystemObjects / 扩展名 / Directory\Background / DesktopBackground 下的扩展都会加载。
    /// </summary>
    private static IContextMenu? CreateDefault(IReadOnlyList<DesktopItem> items, IntPtr hwnd)
    {
        var keys = new List<IntPtr>();
        using var arr = new ShellApi.PidlArray(items.Select(i => i.Pidl));
        try
        {
            var names = new List<string>();
            if (items.Count == 0)
            {
                names.AddRange(new[] { @"DesktopBackground", @"Directory\Background" });
            }
            else
            {
                return null; // 图标菜单：GetUIObjectOf 的结果与原生更接近（SHCreateDefaultContextMenu 会丢项）
            }
            foreach (var n in names)
            {
                if (Win32.RegOpenKeyExW(new IntPtr(unchecked((int)0x80000000)), n, 0, 0x20019, out var h) == 0) keys.Add(h);
            }
            if (TracedKeys.Add(string.Join(",", names))) Log.Info($"菜单类键：{string.Join(", ", names)}（有效 {keys.Count}）");

            var keyBuf = Marshal.AllocHGlobal(IntPtr.Size * Math.Max(1, keys.Count));
            try
            {
                for (var i = 0; i < keys.Count; i++) Marshal.WriteIntPtr(keyBuf, i * IntPtr.Size, keys[i]);
                var desktopPidl = IntPtr.Zero;
                Win32.SHGetSpecialFolderLocation(IntPtr.Zero, 0, out desktopPidl);
                var folderUnk = Marshal.GetComInterfaceForObject<IShellFolder, IShellFolder>(ShellApi.Desktop);
                var pidlBuf = Marshal.AllocHGlobal(IntPtr.Size * Math.Max(1, arr.Ptrs.Length));
                try
                {
                    for (var i = 0; i < arr.Ptrs.Length; i++) Marshal.WriteIntPtr(pidlBuf, i * IntPtr.Size, arr.Ptrs[i]);
                    var dcm = new DEFCONTEXTMENU
                    {
                        hwnd = hwnd, pidlFolder = desktopPidl, psf = folderUnk, cidl = (uint)arr.Ptrs.Length,
                        apidl = items.Count == 0 ? IntPtr.Zero : pidlBuf, cKeys = (uint)keys.Count, aKeys = keyBuf,
                    };
                    var hr = Win32.SHCreateDefaultContextMenu(ref dcm, ShellApi.IID_IContextMenu, out var ppv);
                    if (hr < 0 || ppv == IntPtr.Zero) { Log.Info($"SHCreateDefaultContextMenu 失败 hr=0x{hr:X}"); return null; }
                    try { return Marshal.GetObjectForIUnknown(ppv) as IContextMenu; }
                    finally { Marshal.Release(ppv); }
                }
                finally
                {
                    Marshal.FreeHGlobal(pidlBuf);
                    Marshal.Release(folderUnk);
                    if (desktopPidl != IntPtr.Zero) Win32.ILFree(desktopPidl);
                }
            }
            finally { Marshal.FreeHGlobal(keyBuf); }
        }
        catch (Exception ex)
        {
            Log.Error("SHCreateDefaultContextMenu 异常，回退", ex);
            return null;
        }
        finally
        {
            foreach (var k in keys) Win32.RegCloseKey(k);
        }
    }

    /// <summary>弹出完整右键菜单（Shell 菜单 + 自定义项），并执行用户选的命令。</summary>
    public static void Show(MenuContext ctx)
    {
        var cm = Create(ctx.Items, ctx.Hwnd);
        if (cm == null)
        {
            Log.Error("取 IContextMenu 失败");
            return;
        }

        var hmenu = Win32.CreatePopupMenu();
        object? site = null;
        try
        {
            // 给菜单对象一个站点（系统桌面的 DefView），部分较新的扩展（图标、云盘/手机类入口）依赖它
            site = SystemDesktopView.Acquire();
            var ows = cm as IObjectWithSite;
            Log.Info($"菜单站点：IObjectWithSite={(ows != null)} site={(site != null)} hr=0x{(ows?.SetSite(site) ?? -1):X}");

            var mi0 = new Win32.MENUINFO { cbSize = Marshal.SizeOf<Win32.MENUINFO>(), fMask = 0x10, dwStyle = 0x04000000 }; // MIM_STYLE | MNS_CHECKORBMP
            Win32.SetMenuInfo(hmenu, ref mi0);
            var custom = new Dictionary<uint, CustomMenuItem>();
            uint nextId = MenuExtensions.FirstCommandId;
            var all = MenuExtensions.Items.Where(i => i.Applies == null || i.Applies(ctx)).ToList();

            var flags = ShellApi.CMF_NORMAL | ShellApi.CMF_EXPLORE;
            if (!ctx.IsBackground) flags |= ShellApi.CMF_CANRENAME | ShellApi.CMF_ITEMMENU;
            if (ctx.Shift) flags |= ShellApi.CMF_EXTENDEDVERBS;
            var hr = cm.QueryContextMenu(hmenu, 0, ShellFirst, ShellLast, flags);
            if (hr < 0) Log.Error($"QueryContextMenu 失败 hr=0x{hr:X}");
            Reapply(hmenu);

            // Explorer 里“新建文件夹”（压缩软件扩展）排在背景菜单最前
            var nf = FindItem(hmenu, "新建文件夹", "New folder");
            if (ctx.IsBackground && nf > 0) MoveItem(hmenu, nf, 0);

            // 顶部块（查看/排序方式/刷新…）：Explorer 桌面菜单里“新建文件夹”（压缩软件扩展）排在最前，其后才是它们
            var pos = FindItem(hmenu, "新建文件夹", "New folder") == 0 ? 1u : 0u;
            foreach (var it in all.Where(i => i.Position == MenuPosition.Top))
                InsertCustom(hmenu, ref pos, it, ctx, custom, ref nextId);

            var afterRefresh = all.Where(i => i.Position == MenuPosition.AfterRefresh).ToList();
            if (afterRefresh.Count > 0)
            {
                var r = FindItem(hmenu, "刷新", "Refresh");
                var at = r >= 0 ? (uint)r + 2 : 0u;
                foreach (var it in afterRefresh) InsertCustom(hmenu, ref at, it, ctx, custom, ref nextId);
            }

            // 放在“新建”子菜单之前（其前面的分隔线之前）：粘贴快捷方式、撤消
            var newIdx = FindNewSubmenu(hmenu);
            var beforeNew = all.Where(i => i.Position == MenuPosition.BeforeNew).ToList();
            if (beforeNew.Count > 0)
            {
                var at = newIdx > 0 ? (uint)newIdx - 1 : (uint)Win32.GetMenuItemCount(hmenu);
                foreach (var it in beforeNew) InsertCustom(hmenu, ref at, it, ctx, custom, ref nextId);
            }
            pos = (uint)Win32.GetMenuItemCount(hmenu);

            foreach (var it in all.Where(i => i.Position == MenuPosition.Bottom))
                InsertCustom(hmenu, ref pos, it, ctx, custom, ref nextId);

            _msg3 = cm as IContextMenu3;
            _msg2 = cm as IContextMenu2;

            // 标准做法：弹菜单前置前台，之后 PostMessage(WM_NULL)，防止菜单不消失
            Win32.SetForegroundWindow(ctx.Hwnd);
            // 与 Explorer 一致：桌面背景菜单是深色（跟随系统），带旧式扩展的图标菜单是浅色
            try
            {
                var dark = ctx.IsBackground;
                Win32.SetPreferredAppMode(dark ? 1 : 3); // 1=AllowDark(跟随系统) 3=ForceLight
                Win32.AllowDarkModeForWindow(ctx.Hwnd, dark);
                Win32.FlushMenuThemes();
            }
            catch { /* 旧系统无此序号 */ }
            var cmd = Win32.TrackPopupMenuEx(hmenu, Win32.TPM_RETURNCMD | Win32.TPM_RIGHTBUTTON,
                ctx.ScreenPoint.X, ctx.ScreenPoint.Y, ctx.Hwnd, IntPtr.Zero);
            Win32.PostMessage(ctx.Hwnd, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (cmd == 0) return;

            if (custom.TryGetValue(cmd, out var ci))
            {
                Log.Info($"自定义菜单项：{ci.Title}");
                ci.Handler?.Invoke(ctx);
                return;
            }

            var offset = cmd - ShellFirst;
            var verb = GetVerb(cm, offset);
            Log.Info($"菜单命令 id={cmd} 动词={verb ?? "(无)"}");
            ctx.Verb = verb;
            if (verb != null && MenuExtensions.VerbInterceptors.TryGetValue(verb, out var intercept) && intercept(ctx))
                return;

            if (ctx.IsBackground && IsInNewSubmenu(hmenu, cmd)) ctx.Controller.ExpectNewItem();

            // 菜单消息转发已不需要，先清掉再执行命令（命令可能弹对话框、跑消息循环）
            _msg2 = null;
            _msg3 = null;
            Invoke(cm, ctx.Hwnd, offset, null, ctx.Shift, (Win32.GetKeyState(Win32.VK_CONTROL) & 0x8000) != 0, ctx.ScreenPoint);
        }
        catch (Exception ex)
        {
            Log.Error("显示右键菜单异常", ex);
        }
        finally
        {
            _msg2 = null;
            _msg3 = null;
            try { (cm as IObjectWithSite)?.SetSite(null); } catch { /* 忽略 */ }
            Win32.DestroyMenu(hmenu);
            foreach (var b in Owned) Win32.DeleteObject(b);
            Owned.Clear();
            Marshal.ReleaseComObject(cm);
        }
    }

    /// <summary>执行指定动词（delete/copy/cut/paste/properties…）。items 为空时作用于桌面背景（粘贴）。</summary>
    public static bool InvokeVerb(IReadOnlyList<DesktopItem> items, string verb, IntPtr hwnd, bool shift = false, bool ctrl = false)
    {
        var cm = Create(items, hwnd);
        if (cm == null) return false;
        var hmenu = Win32.CreatePopupMenu();
        try
        {
            var flags = ShellApi.CMF_NORMAL | ShellApi.CMF_EXPLORE | (items.Count > 0 ? ShellApi.CMF_CANRENAME : 0);
            cm.QueryContextMenu(hmenu, 0, ShellFirst, ShellLast, flags);
            Win32.GetCursorPos(out var pt);
            var hr = Invoke(cm, hwnd, 0, verb, shift, ctrl, pt);
            Log.Info($"执行动词 {verb}（{items.Count} 项）hr=0x{hr:X}");
            return hr >= 0;
        }
        catch (Exception ex)
        {
            Log.Error($"执行动词 {verb} 异常", ex);
            return false;
        }
        finally
        {
            Win32.DestroyMenu(hmenu);
            Marshal.ReleaseComObject(cm);
        }
    }

    /// <summary>执行默认菜单项（双击 / Enter）：CMF_DEFAULTONLY + GetMenuDefaultItem。</summary>
    public static void InvokeDefault(IReadOnlyList<DesktopItem> items, IntPtr hwnd)
    {
        if (items.Count == 0) return;
        var cm = Create(items, hwnd);
        if (cm == null) return;
        var hmenu = Win32.CreatePopupMenu();
        try
        {
            cm.QueryContextMenu(hmenu, 0, ShellFirst, ShellLast, ShellApi.CMF_DEFAULTONLY | ShellApi.CMF_EXPLORE);
            var id = Win32.GetMenuDefaultItem(hmenu, 0, Win32.GMDI_USEDISABLED);
            if (id == uint.MaxValue || id < ShellFirst)
            {
                Log.Info("没有默认菜单项");
                return;
            }
            var offset = id - ShellFirst;
            var verb = GetVerb(cm, offset);
            Log.Info($"执行默认项 动词={verb ?? "(无)"}（{items.Count} 项）");
            Win32.GetCursorPos(out var pt);
            Invoke(cm, hwnd, offset, null, false, false, pt);
        }
        catch (Exception ex)
        {
            Log.Error("执行默认项异常", ex);
        }
        finally
        {
            Win32.DestroyMenu(hmenu);
            Marshal.ReleaseComObject(cm);
        }
    }

    private static string? GetVerb(IContextMenu cm, uint offset)
    {
        var buf = Marshal.AllocHGlobal(512 * 2);
        try
        {
            for (var i = 0; i < 512; i++) Marshal.WriteInt16(buf, i * 2, 0);
            var hr = cm.GetCommandString((UIntPtr)offset, ShellApi.GCS_VERBW, IntPtr.Zero, buf, 512);
            if (hr < 0) return null;
            var s = Marshal.PtrToStringUni(buf);
            return string.IsNullOrEmpty(s) ? null : s;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>CMINVOKECOMMANDINFOEX：按偏移（verb 为 null）或动词名执行。</summary>
    private static int Invoke(IContextMenu cm, IntPtr hwnd, uint offset, string? verb, bool shift, bool ctrl, Win32.POINT pt)
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        IntPtr dirW = Marshal.StringToHGlobalUni(dir), dirA = Marshal.StringToHGlobalAnsi(dir);
        IntPtr verbW = IntPtr.Zero, verbA = IntPtr.Zero;
        try
        {
            if (verb != null)
            {
                verbW = Marshal.StringToHGlobalUni(verb);
                verbA = Marshal.StringToHGlobalAnsi(verb);
            }
            var mask = ShellApi.CMIC_MASK_UNICODE | ShellApi.CMIC_MASK_PTINVOKE;
            if (shift) mask |= ShellApi.CMIC_MASK_SHIFT_DOWN;
            if (ctrl) mask |= ShellApi.CMIC_MASK_CONTROL_DOWN;
            var ci = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = mask,
                hwnd = hwnd,
                lpVerb = verb != null ? verbA : (IntPtr)offset,
                lpVerbW = verb != null ? verbW : (IntPtr)offset,
                lpDirectory = dirA,
                lpDirectoryW = dirW,
                nShow = 1, // SW_SHOWNORMAL
                ptInvoke = pt,
            };
            return cm.InvokeCommand(ref ci);
        }
        finally
        {
            Marshal.FreeHGlobal(dirW);
            Marshal.FreeHGlobal(dirA);
            if (verbW != IntPtr.Zero) Marshal.FreeHGlobal(verbW);
            if (verbA != IntPtr.Zero) Marshal.FreeHGlobal(verbA);
        }
    }

    private static void InsertCustom(IntPtr hmenu, ref uint pos, CustomMenuItem it, MenuContext ctx,
        Dictionary<uint, CustomMenuItem> map, ref uint nextId)
    {
        var mii = new Win32.MENUITEMINFO { cbSize = Marshal.SizeOf<Win32.MENUITEMINFO>() };
        var title = IntPtr.Zero;
        try
        {
            if (it.IsSeparator)
            {
                mii.fMask = Win32.MIIM_FTYPE;
                mii.fType = Win32.MFT_SEPARATOR;
            }
            else
            {
                mii.fMask = Win32.MIIM_STRING | Win32.MIIM_STATE | Win32.MIIM_FTYPE;
                title = Marshal.StringToHGlobalUni(it.DynamicTitle?.Invoke(ctx) ?? it.Title);
                mii.dwTypeData = title;
                mii.fType = it.Radio ? Win32.MFT_RADIOCHECK : 0;
                mii.fState = (it.Checked?.Invoke(ctx) == true ? Win32.MFS_CHECKED : 0) |
                             (it.Enabled?.Invoke(ctx) == false ? Win32.MFS_DISABLED : 0);
                if (it.Children != null)
                {
                    var sub = Win32.CreatePopupMenu();
                    var subPos = 0u;
                    foreach (var child in it.Children.Where(c => c.Applies == null || c.Applies(ctx)))
                        InsertCustom(sub, ref subPos, child, ctx, map, ref nextId);
                    mii.fMask |= Win32.MIIM_SUBMENU;
                    mii.hSubMenu = sub;
                }
                else
                {
                    var id = nextId++;
                    mii.fMask |= Win32.MIIM_ID;
                    mii.wID = id;
                    map[id] = it;
                }
            }
            Win32.InsertMenuItem(hmenu, pos++, true, ref mii);
        }
        finally
        {
            if (title != IntPtr.Zero) Marshal.FreeHGlobal(title);
        }
    }

    /// <summary>cmd 是否位于“新建”子菜单内（按顶层子菜单标题判断）。</summary>
    private static bool IsInNewSubmenu(IntPtr hmenu, uint cmd)
    {
        var count = Win32.GetMenuItemCount(hmenu);
        for (var i = 0; i < count; i++)
        {
            var sub = Win32.GetSubMenu(hmenu, i);
            if (sub == IntPtr.Zero || !ContainsCommand(sub, cmd)) continue;
            var sb = new StringBuilder(128);
            Win32.GetMenuString(hmenu, (uint)i, sb, sb.Capacity, Win32.MF_BYPOSITION);
            var text = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\(&.\)|&", "").Trim();
            return text.Equals("新建", StringComparison.Ordinal) || text.Equals("New", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>
    /// 把 Shell 扩展给的 32 位菜单位图复制成自己的 DIB 节再设回菜单。
    /// 扩展提供的位图句柄在本进程的菜单绘制里画不出来（原因未明），复制后可以正常显示。返回需要在菜单结束后释放的位图。
    /// </summary>
    /// <summary>“新建”子菜单在菜单中的位置，没有返回 -1。</summary>
    private static int FindNewSubmenu(IntPtr hmenu)
    {
        var n = Win32.GetMenuItemCount(hmenu);
        for (var i = 0; i < n; i++)
        {
            if (Win32.GetSubMenu(hmenu, i) == IntPtr.Zero) continue;
            var sb = new StringBuilder(128);
            Win32.GetMenuString(hmenu, (uint)i, sb, sb.Capacity, Win32.MF_BYPOSITION);
            var t = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\(&.\)|&", "").Trim();
            if (t is "新建" or "New") return i;
        }
        return -1;
    }

    private static void MoveItem(IntPtr hmenu, int from, int to)
    {
        var buf = Marshal.AllocHGlobal(1024);
        try
        {
            var mi = new Win32.MENUITEMINFO { cbSize = Marshal.SizeOf<Win32.MENUITEMINFO>(), fMask = 0x1 | 0x2 | 0x4 | 0x8 | 0x20 | 0x40 | 0x80 | 0x100, dwTypeData = buf, cch = 511 };
            if (!Win32.GetMenuItemInfo(hmenu, (uint)from, true, ref mi)) return;
            mi.fMask &= ~0x100u; // STRING 与 FTYPE 不能同时使用
            if (!Win32.RemoveMenu(hmenu, (uint)from, Win32.MF_BYPOSITION)) return;
            Win32.InsertMenuItem(hmenu, (uint)to, true, ref mi);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static int FindItem(IntPtr hmenu, params string[] titles)
    {
        var n = Win32.GetMenuItemCount(hmenu);
        for (var i = 0; i < n; i++)
        {
            var sb = new StringBuilder(128);
            if (Win32.GetMenuString(hmenu, (uint)i, sb, sb.Capacity, Win32.MF_BYPOSITION) <= 0) continue;
            var t = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\(&.\)|&", "").Trim();
            if (titles.Contains(t)) return i;
        }
        return -1;
    }

    private static bool Valid(IntPtr h) => (ulong)(long)h != 0 && ((ulong)(long)h & 0xFFFFFFFF) > 0xFFFF;

    private static void Reapply(IntPtr hmenu)
    {
        var n = Win32.GetMenuItemCount(hmenu);
        for (var i = 0; i < n; i++)
        {
            var mi = new Win32.MENUITEMINFO { cbSize = Marshal.SizeOf<Win32.MENUITEMINFO>(), fMask = 0x80 | 0x8 };
            if (!Win32.GetMenuItemInfo(hmenu, (uint)i, true, ref mi)) continue;
            // GDI 句柄是 32 位，可能被符号扩展；0~0xFFFF 的小值是 HBMMENU_* 特殊值
            var src = Valid(mi.hbmpItem) ? mi.hbmpItem : Valid(mi.hbmpChecked) ? mi.hbmpChecked : IntPtr.Zero;
            if (src == IntPtr.Zero || Owned.Contains(src)) continue;
            if (Win32.GetObject(src, Marshal.SizeOf<Win32.BITMAP>(), out var bm) == 0 || bm.bmBitsPixel != 32 || bm.bmWidth <= 0 || bm.bmHeight <= 0) continue;
            mi.fMask = 0x80;

            var bih = new Win32.BITMAPINFOHEADER { biSize = Marshal.SizeOf<Win32.BITMAPINFOHEADER>(), biWidth = bm.bmWidth, biHeight = -bm.bmHeight, biPlanes = 1, biBitCount = 32 };
            var px = new byte[bm.bmWidth * bm.bmHeight * 4];
            var dc = Win32.GetDC(IntPtr.Zero);
            var got = Win32.GetDIBits(dc, src, 0, (uint)bm.bmHeight, px, ref bih, 0);
            var bits = IntPtr.Zero;
            var copy = got == 0 ? IntPtr.Zero : Win32.CreateDIBSection(dc, ref bih, 0, out bits, IntPtr.Zero, 0);
            Win32.ReleaseDC(IntPtr.Zero, dc);
            if (copy == IntPtr.Zero) continue;
            Marshal.Copy(px, 0, bits, px.Length);
            mi.hbmpItem = copy;
            Win32.SetMenuItemInfo(hmenu, (uint)i, true, ref mi);
            Owned.Add(copy);
        }
    }

    private static bool ContainsCommand(IntPtr hmenu, uint cmd)
    {
        var count = Win32.GetMenuItemCount(hmenu);
        for (var i = 0; i < count; i++)
        {
            if (Win32.GetMenuItemID(hmenu, i) == cmd) return true;
            var sub = Win32.GetSubMenu(hmenu, i);
            if (sub != IntPtr.Zero && ContainsCommand(sub, cmd)) return true;
        }
        return false;
    }
}
