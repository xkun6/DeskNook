using System.Runtime.InteropServices;
using DeskNext.Native;
using DeskNext.Services;

namespace DeskNext.Desktop;

/// <summary>
/// 普通格子（非映射）空白处/标题栏的右键菜单：只有 MenuExtensions 里 Box != null 的自定义项，不涉及 Shell 菜单。
/// 与 ShellContextMenu 一样用原生弹出菜单（深色模式跟随系统），命令 ID 从 0x8000 起。
/// </summary>
internal static class BoxMenu
{
    public static void Show(MenuContext ctx)
    {
        var hmenu = Win32.CreatePopupMenu();
        try
        {
            var mi0 = new Win32.MENUINFO { cbSize = Marshal.SizeOf<Win32.MENUINFO>(), fMask = 0x10, dwStyle = 0x04000000 }; // MNS_CHECKORBMP
            Win32.SetMenuInfo(hmenu, ref mi0);

            var map = new Dictionary<uint, CustomMenuItem>();
            uint nextId = MenuExtensions.FirstCommandId;
            var pos = 0u;
            var items = MenuExtensions.Items.Where(i => i.Position == MenuPosition.Top && i.Applies != null && i.Applies(ctx)).ToList();
            // 去掉末尾多余的分隔线
            while (items.Count > 0 && items[^1].IsSeparator) items.RemoveAt(items.Count - 1);
            foreach (var it in items) Insert(hmenu, ref pos, it, ctx, map, ref nextId);

            Win32.SetForegroundWindow(ctx.Hwnd);
            try
            {
                Win32.SetPreferredAppMode(1);
                Win32.AllowDarkModeForWindow(ctx.Hwnd, true);
                Win32.FlushMenuThemes();
            }
            catch { /* 旧系统无此序号 */ }
            var cmd = Win32.TrackPopupMenuEx(hmenu, Win32.TPM_RETURNCMD | Win32.TPM_RIGHTBUTTON,
                ctx.ScreenPoint.X, ctx.ScreenPoint.Y, ctx.Hwnd, IntPtr.Zero);
            Win32.PostMessage(ctx.Hwnd, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (cmd != 0 && map.TryGetValue(cmd, out var chosen))
            {
                Log.Info($"格子菜单项：{chosen.Title}{chosen.DynamicTitle?.Invoke(ctx)}");
                chosen.Handler?.Invoke(ctx);
            }
        }
        catch (Exception ex)
        {
            Log.Error("显示格子菜单异常", ex);
        }
        finally
        {
            Win32.DestroyMenu(hmenu);
        }
    }

    private static void Insert(IntPtr hmenu, ref uint pos, CustomMenuItem it, MenuContext ctx, Dictionary<uint, CustomMenuItem> map, ref uint nextId)
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
                        Insert(sub, ref subPos, child, ctx, map, ref nextId);
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
}
