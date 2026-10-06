using System.Diagnostics;
using XkDesk.Model;
using XkDesk.Native;

namespace XkDesk.Desktop;

/// <summary>右键菜单的上下文：谁、在哪、点了什么。</summary>
internal sealed class MenuContext
{
    public required DesktopController Controller { get; init; }
    /// <summary>选中的项；为空表示桌面空白处（背景菜单）。</summary>
    public required IReadOnlyList<DesktopItem> Items { get; init; }
    public required IntPtr Hwnd { get; init; }
    /// <summary>弹出位置（屏幕物理像素）。</summary>
    public required Win32.POINT ScreenPoint { get; init; }
    /// <summary>空白处右键所在显示器（设备名）。</summary>
    public string Monitor { get; init; } = "";
    public bool Shift { get; init; }
    public bool IsBackground => Items.Count == 0;
    /// <summary>即将执行的 Shell 动词（仅动词拦截时有值）。</summary>
    public string? Verb { get; set; }
}

internal enum MenuPosition { Top, Bottom }

/// <summary>一个自定义菜单项。Children 非空则为子菜单；IsSeparator 为分隔线。</summary>
internal sealed class CustomMenuItem
{
    public string Title { get; init; } = "";
    public MenuPosition Position { get; init; } = MenuPosition.Bottom;
    /// <summary>适用条件；null = 总是出现。</summary>
    public Func<MenuContext, bool>? Applies { get; init; }
    public Action<MenuContext>? Handler { get; init; }
    public Func<MenuContext, bool>? Checked { get; init; }
    /// <summary>是否可用；null = 可用。</summary>
    public Func<MenuContext, bool>? Enabled { get; init; }
    public bool Radio { get; init; }
    public bool IsSeparator { get; init; }
    public IReadOnlyList<CustomMenuItem>? Children { get; init; }
}

/// <summary>
/// 魔改入口：自定义菜单项 + 动词拦截表，集中在这一个文件。
/// 自定义项命令 ID 从 0x8000 起（Shell 占 1..0x7FFF）；新增项只需往 <see cref="Items"/> 里加。
/// </summary>
internal static class MenuExtensions
{
    public const uint FirstCommandId = 0x8000;

    private static bool ClipboardHasFiles()
    {
        try { return System.Windows.Clipboard.ContainsFileDropList(); }
        catch { return false; }
    }

    private static CustomMenuItem Sep(MenuPosition pos, Func<MenuContext, bool>? applies) =>
        new() { IsSeparator = true, Position = pos, Applies = applies };

    private static CustomMenuItem IconSize(string title, int size) => new()
    {
        Title = title, Radio = true,
        Checked = c => c.Controller.Layout.View.IconSize == size,
        Handler = c => c.Controller.SetIconSize(size),
    };

    private static CustomMenuItem Sort(string title, string key) => new()
    {
        Title = title, Radio = true,
        Checked = c => c.Controller.Layout.View.SortKey == key,
        Handler = c => c.Controller.SortBy(key),
    };

    public static readonly IReadOnlyList<CustomMenuItem> Items = new List<CustomMenuItem>
    {
        // ---- 空白处：顶部补上 Explorer 桌面同款的 查看 / 排序方式 / 刷新 ----
        new()
        {
            Title = "查看(&V)", Position = MenuPosition.Top, Applies = c => c.IsBackground,
            Children = new[] { IconSize("大图标(&R)", 96), IconSize("中等图标(&M)", 48), IconSize("小图标(&N)", 32) },
        },
        new()
        {
            Title = "排序方式(&O)", Position = MenuPosition.Top, Applies = c => c.IsBackground,
            Children = new[] { Sort("名称", "name"), Sort("大小", "size"), Sort("项目类型", "type"), Sort("修改日期", "date") },
        },
        new() { Title = "刷新(&E)", Position = MenuPosition.Top, Applies = c => c.IsBackground, Handler = c => c.Controller.Refresh() },
        Sep(MenuPosition.Top, c => c.IsBackground),
        new()
        {
            Title = "粘贴(&P)", Position = MenuPosition.Top, Applies = c => c.IsBackground,
            Enabled = _ => ClipboardHasFiles(), Handler = c => c.Controller.Paste(),
        },
        new()
        {
            Title = "粘贴快捷方式(&S)", Position = MenuPosition.Top, Applies = c => c.IsBackground,
            Enabled = _ => ClipboardHasFiles(), Handler = c => c.Controller.PasteShortcut(),
        },
        Sep(MenuPosition.Top, c => c.IsBackground),

        // ---- 空白处：末尾 退出（阶段 4 托盘做好后移除）----
        Sep(MenuPosition.Bottom, c => c.IsBackground),
        new()
        {
            Title = "退出 xk-desk", Position = MenuPosition.Bottom, Applies = c => c.IsBackground,
            Handler = _ => ((App)System.Windows.Application.Current).ExitApp(),
        },

        // ---- 图标：打开所在位置 ----
        Sep(MenuPosition.Bottom, c => !c.IsBackground && c.Items.Count == 1 && c.Items[0].FilePath != null),
        new()
        {
            Title = "打开所在位置(&L)", Position = MenuPosition.Bottom,
            Applies = c => !c.IsBackground && c.Items.Count == 1 && c.Items[0].FilePath != null,
            Handler = c => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{c.Items[0].FilePath}\"") { UseShellExecute = true }),
        },
    };

    /// <summary>动词拦截表：返回 true 表示已处理，不再交给 Shell。</summary>
    public static readonly Dictionary<string, Func<MenuContext, bool>> VerbInterceptors = new(StringComparer.OrdinalIgnoreCase)
    {
        // 没有 DefView 宿主，系统的重命名不生效：改为本程序原位重命名
        ["rename"] = c =>
        {
            if (c.Items.Count == 0) return false;
            c.Controller.BeginRename(c.Items[0].Key);
            return true;
        },
    };
}
