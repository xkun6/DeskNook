using System.Diagnostics;
using System.IO;
using DeskNook.Model;
using DeskNook.Native;
using DeskNook.Services;

namespace DeskNook.Desktop;

/// <summary>菜单来源：DeskNook 的桌面自由区 / 普通格子 / 映射格子，或普通资源管理器窗口里经 Shell 扩展发来的查询。</summary>
internal enum MenuSource { Desktop, Box, MappedBox, Explorer }

/// <summary>右键菜单的上下文：谁、在哪、点了什么。</summary>
internal sealed class MenuContext
{
    public required DesktopController Controller { get; init; }
    /// <summary>选中的桌面项/映射项；为空表示空白处（背景菜单）或资源管理器里的选择（见 <see cref="Paths"/>）。</summary>
    public IReadOnlyList<DesktopItem> Items { get; init; } = Array.Empty<DesktopItem>();
    public IntPtr Hwnd { get; init; }
    /// <summary>弹出位置（屏幕物理像素）。</summary>
    public Win32.POINT ScreenPoint { get; init; }
    /// <summary>空白处右键所在显示器（设备名）。</summary>
    public string Monitor { get; init; } = "";
    public bool Shift { get; init; }
    /// <summary>在格子上右键（空白处/标题栏）时为该格子；其余为 null。</summary>
    public BoxState? Box { get; init; }
    public MenuSource Source { get; init; } = MenuSource.Desktop;
    /// <summary>资源管理器里选中项的解析名（文件路径或 ::{CLSID}）。</summary>
    public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();
    /// <summary>资源管理器背景菜单所在的文件夹。</summary>
    public string FolderPath { get; init; } = "";

    public bool IsBackground => Items.Count == 0 && Paths.Count == 0;
    /// <summary>桌面自由区空白处（不含格子，不含资源管理器窗口）。</summary>
    public bool IsDesktopBackground => IsBackground && Box == null && Source == MenuSource.Desktop;
    /// <summary>即将执行的 Shell 动词（仅动词拦截时有值）。</summary>
    public string? Verb { get; set; }

    /// <summary>是 DeskNook 自己弹出的菜单（桌面/格子），而不是普通资源管理器窗口。</summary>
    public bool InDeskNook => Source != MenuSource.Explorer;

    /// <summary>选中项总数。</summary>
    public int SelectionCount => Items.Count > 0 ? Items.Count : Paths.Count;

    /// <summary>选中项里的文件系统路径（虚拟项如“此电脑”不在内）。</summary>
    public IReadOnlyList<string> SelectedPaths => Items.Count > 0
        ? Items.Where(i => i.FilePath != null).Select(i => i.FilePath!).ToList()
        : Paths.Where(IsFileSystemPath).ToList();

    public static bool IsFileSystemPath(string p) =>
        p.Length > 2 && !p.StartsWith("::", StringComparison.Ordinal) && (p[1] == ':' || p.StartsWith(@"\\", StringComparison.Ordinal));
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
    /// <summary>图标文件路径（png/ico 等，由 Shell 扩展解码成 32 位 PARGB 位图）；null = 无图标。</summary>
    public string? Icon { get; init; }
    /// <summary>在普通资源管理器窗口里也出现（经 Shell 扩展注入）。</summary>
    public bool InExplorer { get; init; }
    /// <summary>仅用于进程内回退路径（代理不可用时自己补上的“查看/排序方式/刷新/粘贴/撤消”）；Explorer 内的原生菜单自带这些。</summary>
    public bool FallbackOnly { get; init; }
}

/// <summary>
/// 魔改入口：自定义菜单项 + 动词拦截表，集中在这一个文件。
/// 自定义项命令 ID 从 0x8000 起（Shell 占 1..0x7FFF）；新增项只需往 <see cref="Items"/> 里加。
/// </summary>
internal static class MenuExtensions
{
    public const uint FirstCommandId = 0x8000;

    /// <summary>菜单图标：嵌入资源释放到本地后的 ICO 路径（见 MenuIcons）。</summary>
    private static string I(string name) => MenuIcons.PathOf(name);

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

    private static CustomMenuItem BoxViewItem(string title, string key) => new()
    {
        Title = title, Radio = true,
        Checked = c => c.Box!.ViewMode == key,
        Handler = c => c.Controller.SetBoxView(c.Box!, key),
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
            Title = "查看(&V)", Position = MenuPosition.Top, Applies = InBoxMenu,
            Children = new[]
            {
                BoxViewItem("跟随桌面", ""), BoxViewItem("大图标", "large"), BoxViewItem("中等图标", "medium"), BoxViewItem("小图标", "small"), BoxViewItem("列表", "list"),
            },
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
            Title = "查看(&V)", FallbackOnly = true, Position = MenuPosition.Top, Applies = c => c.IsDesktopBackground,
            Children = new[] { IconSize("大图标(&R)", 96), IconSize("中等图标(&M)", 48), IconSize("小图标(&N)", 32) },
        },
        new()
        {
            Title = "排序方式(&O)", Position = MenuPosition.Top, Applies = c => c.IsDesktopBackground, FallbackOnly = true,
            Children = new[] { Sort("名称", "name"), Sort("大小", "size"), Sort("项目类型", "type"), Sort("修改日期", "date") },
        },
        new() { Title = "刷新(&E)", FallbackOnly = true, Position = MenuPosition.Top, Applies = c => c.IsDesktopBackground, Handler = c => c.Controller.Refresh() },
        new() { IsSeparator = true, Position = MenuPosition.Top, Applies = c => c.IsDesktopBackground, FallbackOnly = true },
        new()
        {
            Title = "粘贴(&P)", FallbackOnly = true, Position = MenuPosition.AfterRefresh, Applies = c => c.IsDesktopBackground,
            Enabled = _ => ClipboardHasFiles(), Handler = c => c.Controller.Paste(),
        },
        new()
        {
            Title = "粘贴快捷方式(&S)", FallbackOnly = true, Position = MenuPosition.AfterRefresh, Applies = c => c.IsDesktopBackground,
            Enabled = _ => ClipboardHasFiles(), Handler = c => c.Controller.PasteShortcut(),
        },
        // 撤销：和 Explorer 一样，只有存在可撤销操作时才出现
        new()
        {
            Title = "", Position = MenuPosition.AfterRefresh, Applies = c => c.IsDesktopBackground && c.Controller.UndoLabel != null,
            FallbackOnly = true, DynamicTitle = c => $"撤消 {c.Controller.UndoLabel}(&U)	Ctrl+Z", Handler = c => c.Controller.Undo(),
        },

        // ---- 桌面空白处：外层平铺“一键整理”，其余收进“桌面整理 ▸”子菜单（腾讯桌面整理同款布局）----
        Sep(MenuPosition.BeforeNew, c => c.IsDesktopBackground),
        new()
        {
            Title = "一键整理(&Z)", Position = MenuPosition.BeforeNew, Icon = I("organize"), Applies = c => c.IsDesktopBackground,
            Handler = c => c.Controller.OrganizeAll(),
        },
        new()
        {
            Title = "桌面整理(&D)", Position = MenuPosition.BeforeNew, Icon = I("menu-app"), Applies = c => c.IsDesktopBackground,
            Children = new CustomMenuItem[]
            {
                new() { Title = "新建格子(&B)", Icon = I("box-new"), Handler = c => c.Controller.NewBox(c.Hwnd, c.ScreenPoint, c.Monitor) },
                new() { Title = "新建映射格子(&M)…", Icon = I("as-box"), Handler = c => c.Controller.NewMappedBox(c.Hwnd, c.ScreenPoint, c.Monitor) },
                new() { Title = "一键整理(&Z)", Icon = I("organize"), Handler = c => c.Controller.OrganizeAll() },
                new() { Title = "撤销整理(&U)", Icon = I("undo"), Enabled = c => c.Controller.CanUndoOrganize, Handler = c => c.Controller.UndoOrganize() },
                new() { IsSeparator = true },
                new() { Title = "设置…", Icon = I("settings"), Handler = c => Views.SettingsWindow.ShowSingleton(c.Controller) },
                new() { Title = "退出桌面整理", Icon = I("exit"), Handler = _ => ((App)System.Windows.Application.Current).ExitApp() },
            },
        },

        // ---- 图标：整理类（桌面、格子、普通资源管理器窗口里都出现）----
        new()
        {
            Title = "整理至新格子(&G)", Position = MenuPosition.Bottom, Icon = I("box-new"), InExplorer = true, Applies = CanOrganizeToBox,
            Handler = c => c.Controller.NewBoxFromItems(c.Hwnd, c.ScreenPoint, c.Monitor, DesktopItemsOf(c)),
        },
        new()
        {
            Title = "整理至新文件夹(&F)", Position = MenuPosition.Bottom, Icon = I("folder-new"), InExplorer = true, Applies = CanOrganizeToFolder,
            Handler = c => c.Controller.MoveToNewFolder(c.SelectedPaths, renameAfter: c.InDeskNook),
        },
        new()
        {
            Title = "作为桌面格子显示(&S)", Position = MenuPosition.Bottom, Icon = I("as-box"), InExplorer = true, Applies = CanShowAsBox,
            Handler = c => c.Controller.NewMappedBoxAt(c.SelectedPaths[0], c.ScreenPoint, c.Monitor),
        },

        // ---- 图标：格子相关（仅 DeskNook 内）----
        new()
        {
            Title = "移动到格子(&M)", Position = MenuPosition.Bottom,
            Applies = c => c.InDeskNook && !c.IsBackground && c.Items.All(i => i.Container.Length == 0) && c.Controller.Layout.Boxes.Any(b => b.Kind == BoxKind.Normal),
            Children = new LazyChildren(c => c.Controller.Layout.Boxes.Where(b => b.Kind == BoxKind.Normal).Select(b => new CustomMenuItem
            {
                Title = b.Name.Replace("&", "&&"),
                Handler = cc => cc.Controller.MoveItemsToBox(cc.Items, b),
            }).ToList()),
        },
        new()
        {
            Title = "移出格子(&O)", Position = MenuPosition.Bottom,
            Applies = c => c.InDeskNook && !c.IsBackground && c.Items.Any(i => c.Controller.BoxOfKey(i.Key) != null),
            Handler = c => c.Controller.MoveItemsOutOfBoxes(c.Items),
        },

        // ---- 图标：打开所在位置（仅 DeskNook 内）----
        new()
        {
            Title = "打开所在位置(&L)", Position = MenuPosition.Bottom, Icon = I("locate"),
            // 只对格子 / 映射格子里的项显示：桌面自由区的项本来就在桌面上
            Applies = c => c.InDeskNook && !c.IsBackground && c.Items.Count == 1 && c.Items[0].FilePath != null
                           && (c.Items[0].Container.Length > 0 || c.Controller.BoxOfKey(c.Items[0].Key) != null),
            Handler = c => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{c.Items[0].FilePath}\"") { UseShellExecute = true }),
        },
    };

    // ---- 适用条件 ----

    /// <summary>选中项对应的桌面项：DeskNook 内直接是 Items；资源管理器里的路径按桌面项解析（解析不到的视为非桌面项）。</summary>
    internal static IReadOnlyList<DesktopItem> DesktopItemsOf(MenuContext c)
    {
        if (c.Items.Count > 0) return c.Items.Where(i => i.Container.Length == 0).ToList();
        return c.Paths.Select(p => c.Controller.ItemOf(p)).Where(i => i != null && i.Container.Length == 0).Select(i => i!).ToList();
    }

    private static bool CanOrganizeToBox(MenuContext c)
    {
        if (c.IsBackground) return false;
        var items = DesktopItemsOf(c);
        return items.Count > 0 && items.Count == c.SelectionCount;
    }

    /// <summary>选中项都是文件系统项且在同一目录。</summary>
    internal static bool InSameFolder(IReadOnlyList<string> paths, out string folder)
    {
        folder = "";
        if (paths.Count == 0) return false;
        var trim = new[] { '\\', '/' };
        var dir = Path.GetDirectoryName(paths[0].TrimEnd(trim));
        if (string.IsNullOrEmpty(dir)) return false;
        if (paths.Any(p => !string.Equals(Path.GetDirectoryName(p.TrimEnd(trim)), dir, StringComparison.OrdinalIgnoreCase))) return false;
        folder = dir;
        return true;
    }

    private static bool CanOrganizeToFolder(MenuContext c) =>
        !c.IsBackground && c.SelectedPaths.Count == c.SelectionCount && InSameFolder(c.SelectedPaths, out _);

    private static bool CanShowAsBox(MenuContext c) =>
        !c.IsBackground && c.SelectionCount == 1 && c.SelectedPaths.Count == 1 && Directory.Exists(c.SelectedPaths[0]);

    // ---- 序列化：把适用的项描述成 JSON 交给 Shell 扩展，由它插入 Explorer 的菜单 ----

    /// <summary>一次查询的结果：项 Id → 自定义项（点击后在 UI 线程执行其 Handler）。</summary>
    internal sealed class WireSession
    {
        public required MenuContext Context { get; init; }
        public Dictionary<int, CustomMenuItem> Map { get; } = new();
    }

    /// <summary>按 ctx 评估全部自定义项，返回要注入的描述，并把 Id 登记到 session。必须在 UI 线程调用。</summary>
    internal static List<WireMenuItem> Evaluate(MenuContext ctx, WireSession session)
    {
        var prev = Current;
        Current = ctx;
        try
        {
            var next = 1;
            return BuildWire(Items, ctx, session.Map, ref next, topLevel: true);
        }
        finally { Current = prev; }
    }

    /// <summary>纯函数（便于单测）：适用条件过滤 → 可序列化描述。</summary>
    internal static List<WireMenuItem> BuildWire(IEnumerable<CustomMenuItem> items, MenuContext ctx,
        Dictionary<int, CustomMenuItem> map, ref int nextId, bool topLevel)
    {
        var result = new List<WireMenuItem>();
        foreach (var it in items)
        {
            if (it.Applies != null && !it.Applies(ctx)) continue;
            if (topLevel && (it.FallbackOnly || (ctx.Source == MenuSource.Explorer && !it.InExplorer))) continue;

            var w = new WireMenuItem { Pos = PositionName(it.Position) };
            if (it.IsSeparator) { w.Sep = true; result.Add(w); continue; }

            w.Title = it.DynamicTitle?.Invoke(ctx) ?? it.Title;
            w.Icon = it.Icon;
            w.Enabled = it.Enabled?.Invoke(ctx) != false;
            w.Checked = it.Checked?.Invoke(ctx) == true;
            w.Radio = it.Radio;
            if (it.Children != null)
            {
                w.Children = BuildWire(it.Children, ctx, map, ref nextId, topLevel: false);
                if (w.Children.Count == 0) continue; // 没有可用子项的子菜单不显示
            }
            else
            {
                w.Id = nextId++;
                map[w.Id] = it;
            }
            result.Add(w);
        }
        return result;
    }

    private static string PositionName(MenuPosition p) => p switch
    {
        MenuPosition.Top => "top",
        MenuPosition.BeforeNew => "beforeNew",
        _ => "bottom",
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
