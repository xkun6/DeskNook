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

    /// <summary>宿主窗口过程调用：菜单期间转发菜单消息。返回 true 表示已处理。</summary>
    public static bool TryHandleMessage(int msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (_msg3 == null && _msg2 == null) return false;
        if (msg != Win32.WM_INITMENUPOPUP && msg != Win32.WM_DRAWITEM && msg != Win32.WM_MEASUREITEM &&
            msg != Win32.WM_MENUCHAR && msg != Win32.WM_MENUSELECT) return false;
        try
        {
            if (_msg3 != null)
                return _msg3.HandleMenuMsg2((uint)msg, wParam, lParam, out result) == ShellApi.S_OK && msg != Win32.WM_MENUSELECT;
            return _msg2!.HandleMenuMsg((uint)msg, wParam, lParam) == ShellApi.S_OK && msg != Win32.WM_MENUSELECT;
        }
        catch (Exception ex)
        {
            Log.Error("菜单消息转发异常", ex);
            return false;
        }
    }

    private static IContextMenu? Create(IReadOnlyList<DesktopItem> items, IntPtr hwnd) =>
        items.Count == 0
            ? ShellApi.CreateViewObject<IContextMenu>(ShellApi.IID_IContextMenu, hwnd)
            : ShellApi.GetUIObjectOf<IContextMenu>(items.Select(i => i.Pidl).ToList(), ShellApi.IID_IContextMenu, hwnd);

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

            var custom = new Dictionary<uint, CustomMenuItem>();
            uint nextId = MenuExtensions.FirstCommandId;
            var all = MenuExtensions.Items.Where(i => i.Applies == null || i.Applies(ctx)).ToList();

            var pos = 0u;
            foreach (var it in all.Where(i => i.Position == MenuPosition.Top))
                InsertCustom(hmenu, ref pos, it, ctx, custom, ref nextId);

            var flags = ShellApi.CMF_NORMAL | ShellApi.CMF_EXPLORE;
            if (!ctx.IsBackground) flags |= ShellApi.CMF_CANRENAME | ShellApi.CMF_ITEMMENU;
            if (ctx.Shift) flags |= ShellApi.CMF_EXTENDEDVERBS;
            var hr = cm.QueryContextMenu(hmenu, pos, ShellFirst, ShellLast, flags);
            if (hr < 0) Log.Error($"QueryContextMenu 失败 hr=0x{hr:X}");
            pos = (uint)Win32.GetMenuItemCount(hmenu);

            foreach (var it in all.Where(i => i.Position == MenuPosition.Bottom))
                InsertCustom(hmenu, ref pos, it, ctx, custom, ref nextId);

            _msg3 = cm as IContextMenu3;
            _msg2 = cm as IContextMenu2;

            // 标准做法：弹菜单前置前台，之后 PostMessage(WM_NULL)，防止菜单不消失
            Win32.SetForegroundWindow(ctx.Hwnd);
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
                title = Marshal.StringToHGlobalUni(it.Title);
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
