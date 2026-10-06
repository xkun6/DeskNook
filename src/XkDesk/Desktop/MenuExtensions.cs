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
    /// <summary>在格子上右键（空白处/标题栏）时为该格子；其余为 null。</summary>
    public BoxState? Box { get; init; }
    public bool IsBackground => Items.Count == 0;
    /// <summary>桌面自由区空白处（不含格子）。</summary>
    public bool IsDesktopBackground => Items.Count == 0 && Box == null;
    /// <summary>即将执行的 Shell 动词（仅动词拦截时有值）。</summary>
    public string? Verb { get; set; }
}

/// <summary>Top = 在 Shell 菜单项之前；AfterRefresh = 在“刷新”后的分隔线之后；BeforeNew = 在“新建”子菜单（及其前面的分隔线）之前；Bottom = 末尾。</summary>
internal enum MenuPosition { Top, AfterRefresh, BeforeNew, Bottom }

/// <summary>一个自定义菜单项。Children 非空则为子菜单；IsSeparator 为分隔线。</summary>
internal sealed class CustomMenuItem
{
    public string Title { get; init; } = "";
    /// <summary>动态标题；非 null 时优先于 Title。</summary>
    public Func<MenuContext, string>? DynamicTitle { get; init; }
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

    /// <summary>当前正在弹出的菜单上下文（供动态子菜单读取；菜单是模态的，同一时刻只有一个）。</summary>
    public static MenuContext? Current { get; set; }

    /// <summary>子菜单项在菜单弹出时才生成（遍历时读取 <see cref="Current"/>）。</summary>
    private sealed class LazyChildren : IReadOnlyList<CustomMenuItem>
    {
        private readonly Func<MenuContext, IReadOnlyList<CustomMenuItem>> _make;
        public LazyChildren(Func<MenuContext, IReadOnlyList<CustomMenuItem>> make) => _make = make;
        private IReadOnlyList<CustomMenuItem> Cur => Current == null ? Array.Empty<CustomMenuItem>() : _make(Current);
        public CustomMenuItem this[int index] => Cur[index];
        public int Count => Cur.Count;
        public IEnumerator<CustomMenuItem> GetEnumerator() => Cur.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static CustomMenuItem BoxSort(string title, string key) => new()
    {
        Title = title, Radio = true,
        Checked = c => c.Box!.SortMode == key,
        Handler = c => c.Controller.SetBoxSort(c.Box!, key),
    };

    private static bool InBoxMenu(MenuContext c) => c.Box != null;

    private static bool IsDesktopIconSelection(MenuContext c) =>
        !c.IsBackground && c.Items.All(i => i.Container.Length == 0);

    public static readonly IReadOnlyList<CustomMenuItem> Items = new List<CustomMenuItem>
    {
        // ---- 格子菜单（格子空白处/标题栏；映射格子并入该目录的原生背景菜单）----
        new() { Title = "重命名(&R)", Position = MenuPosition.Top, Applies = InBoxMenu, Handler = c => c.Controller.BeginRenameBox(c.Box!.Id) },
        new()
        {
            Title = "", DynamicTitle = c => c.Box!.Collapsed ? "展开(&E)" : "折叠(&C)", Position = MenuPosition.Top, Applies = InBoxMenu,
            Handler = c => c.Controller.ToggleBoxCollapsed(c.Box!),
        },
        new()
        {
            Title = "", DynamicTitle = c => c.Box!.Locked ? "解除锁定(&L)" : "锁定(&L)", Position = MenuPosition.Top, Applies = InBoxMenu,
            Handler = c => c.Controller.ToggleBoxLocked(c.Box!),
        },
        new()
        {
            Title = "排序方式(&O)", Position = MenuPosition.Top, Applies = InBoxMenu,
            Children = new[]
            {
                new CustomMenuItem
                {
                    Title = "手动顺序", Radio = true, Applies = c => c.Box!.Kind == BoxKind.Normal,
                    Checked = c => c.Box!.SortMode == "", Handler = c => c.Controller.SetBoxSort(c.Box!, ""),
                },
                BoxSort("名称", "name"), BoxSort("修改时间", "date"), BoxSort("大小", "size"), BoxSort("类型", "type"),
            },
        },
        Sep(MenuPosition.Top, InBoxMenu),
        new() { Title = "解散格子(&D)", Position = MenuPosition.Top, Applies = InBoxMenu, Handler = c => c.Controller.DissolveBox(c.Box!) },
        Sep(MenuPosition.Top, InBoxMenu),

        // ---- 空白处：顶部补上 Explorer 桌面同款的 查看 / 排序方式 / 刷新 ----
        new()
        {
            Title = "查看(&V)", Position = MenuPosition.Top, Applies = c => c.IsDesktopBackground,
            Children = new[] { IconSize("大图标(&R)", 96), IconSize("中等图标(&M)", 48), IconSize("小图标(&N)", 32) },
        },
        new()
        {
            Title = "排序方式(&O)", Position = MenuPosition.Top, Applies = c => c.IsDesktopBackground,
            Children = new[] { Sort("名称", "name"), Sort("大小", "size"), Sort("项目类型", "type"), Sort("修改日期", "date") },
        },
        new() { Title = "刷新(&E)", Position = MenuPosition.Top, Applies = c => c.IsDesktopBackground, Handler = c => c.Controller.Refresh() },
        Sep(MenuPosition.Top, c => c.IsDesktopBackground),
        new()
        {
            Title = "粘贴(&P)", Position = MenuPosition.AfterRefresh, Applies = c => c.IsDesktopBackground,
            Enabled = _ => ClipboardHasFiles(), Handler = c => c.Controller.Paste(),
        },
        new()
        {
            Title = "粘贴快捷方式(&S)", Position = MenuPosition.AfterRefresh, Applies = c => c.IsDesktopBackground,
            Enabled = _ => ClipboardHasFiles(), Handler = c => c.Controller.PasteShortcut(),
        },
        // 撤销：和 Explorer 一样，只有存在可撤销操作时才出现
        new()
        {
            Title = "", Position = MenuPosition.AfterRefresh, Applies = c => c.IsDesktopBackground && c.Controller.UndoLabel != null,
            DynamicTitle = c => $"撤消 {c.Controller.UndoLabel}(&U)	Ctrl+Z", Handler = c => c.Controller.Undo(),
        },

        // ---- 空白处：新建格子 ----
        Sep(MenuPosition.BeforeNew, c => c.IsDesktopBackground),
        new()
        {
            Title = "新建格子(&B)", Position = MenuPosition.BeforeNew, Applies = c => c.IsDesktopBackground,
            Handler = c => c.Controller.NewBox(c.Hwnd, c.ScreenPoint, c.Monitor),
        },
        new()
        {
            Title = "新建映射格子(&M)…", Position = MenuPosition.BeforeNew, Applies = c => c.IsDesktopBackground,
            Handler = c => c.Controller.NewMappedBox(c.Hwnd, c.ScreenPoint, c.Monitor),
        },

        // ---- 空白处：末尾 退出（阶段 4 托盘做好后移除）----
        Sep(MenuPosition.Bottom, c => c.IsDesktopBackground),
        new()
        {
            Title = "退出 xk-desk", Position = MenuPosition.Bottom, Applies = c => c.IsDesktopBackground,
            Handler = _ => ((App)System.Windows.Application.Current).ExitApp(),
        },

        // ---- 图标：格子相关 ----
        Sep(MenuPosition.Bottom, c => !c.IsBackground && (IsDesktopIconSelection(c) || c.Items.Any(i => c.Controller.BoxOfKey(i.Key) != null))),
        new()
        {
            Title = "移动到格子(&M)", Position = MenuPosition.Bottom,
            Applies = c => !c.IsBackground && c.Items.All(i => i.Container.Length == 0) && c.Controller.Layout.Boxes.Any(b => b.Kind == BoxKind.Normal),
            Children = new LazyChildren(c => c.Controller.Layout.Boxes.Where(b => b.Kind == BoxKind.Normal).Select(b => new CustomMenuItem
            {
                Title = b.Name.Replace("&", "&&"),
                Handler = cc => cc.Controller.MoveItemsToBox(cc.Items, b),
            }).ToList()),
        },
        new()
        {
            Title = "移出格子(&O)", Position = MenuPosition.Bottom,
            Applies = c => !c.IsBackground && c.Items.Any(i => c.Controller.BoxOfKey(i.Key) != null),
            Handler = c => c.Controller.MoveItemsOutOfBoxes(c.Items),
        },
        new()
        {
            Title = "用选中项新建格子(&G)", Position = MenuPosition.Bottom, Applies = IsDesktopIconSelection,
            Handler = c => c.Controller.NewBoxFromItems(c.Hwnd, c.ScreenPoint, c.Monitor, c.Items),
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
