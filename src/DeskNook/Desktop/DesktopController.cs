using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using DeskNook.Model;
using DeskNook.Native;
using DeskNook.Services;

namespace DeskNook.Desktop;

internal enum NavDirection { Left, Right, Up, Down }

/// <summary>项当前所在位置：Area = 显示器设备名（自由区）或 "box:格子Id"。</summary>
internal readonly record struct ItemLoc(string Area, int Col, int Row);

/// <summary>映射格子的运行时数据：该目录的项来源（含 SHChangeNotify 监听）。</summary>
internal sealed class MappedRuntime
{
    public required DesktopItemSource Source { get; init; }
    public required string Path { get; init; }
    public Dictionary<string, DesktopItem> ByKey { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 桌面图标区的中枢：桌面项集合、布局（自由区 + 格子）、选择、剪切状态，以及所有操作（打开/删除/重命名/移动…）。
/// 各显示器的 DesktopSurface 只负责画和转发输入。全部在 UI 线程。
/// </summary>
internal sealed class DesktopController : IDisposable
{
    private const double DefaultCellExtra = 27; // 系统间距 75 - 图标 48
    private static readonly TimeSpan NewItemWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DropWindow = TimeSpan.FromSeconds(10);

    private readonly Dispatcher _dispatcher;
    private readonly LayoutStore _store = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly OrganizeUndoStore _undoStore = new();
    private OrganizeUndo? _organizeUndo;
    private readonly DispatcherTimer _saveTimer;
    private readonly ClipboardWatcher _clipboard;
    private readonly Dictionary<string, MappedRuntime> _mapped = new();
    private Dictionary<string, DesktopItem> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, DesktopItem> _mappedByKey = new(StringComparer.OrdinalIgnoreCase);
    private List<MonitorInfo> _monitors = new();
    private double _cellExtraDip = DefaultCellExtra;
    private double _cellExtraYDip = 52; // 系统间距 100 - 图标 48
    private BoxState? _menuBox;          // 菜单/粘贴期间：作用对象是这个映射格子的目录

    private DateTime _expectNewUntil = DateTime.MinValue;
    private PendingDrop? _pendingDrop;

    private sealed record PendingDrop(string Monitor, int Col, int Row, string? BoxId, int Index, DateTime Until);

    /// <summary>可撤销的最近一次操作名（“删除”“复制”“移动”“重命名”）；没有则为 null，此时菜单不显示“撤消”。</summary>
    public string? UndoLabel { get; private set; }
    private string? _pendingOp;
    private DateTime _pendingOpUntil;

    public LayoutState Layout { get; private set; } = new();
    public AppSettings Settings { get; private set; } = new();
    public DesktopItemSource Source { get; }
    public ShellIconCache Icons { get; }
    public IReadOnlyList<MonitorGrid> Grids { get; private set; } = Array.Empty<MonitorGrid>();
    public double CellW { get; private set; } = 75;
    public double CellH { get; private set; } = 75;
    public int IconSize => Layout.View.IconSize;

    public IReadOnlyList<DesktopItem> Items => Source.Items;
    public HashSet<string> Selected { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? AnchorKey { get; private set; }
    public HashSet<string> CutPaths { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前最近交互的宿主窗口（Shell 对话框/菜单的 owner）。</summary>
    public IntPtr ActiveHwnd { get; set; }

    /// <summary>最近一次点击所在的格子（Ctrl+V 等键盘操作的目标）；点桌面空白处为 null。</summary>
    public BoxState? ActiveBox { get; set; }

    /// <summary>本程序发起的拖拽中：被拖的 key；否则为 null。</summary>
    public IReadOnlyList<string>? DragKeys { get; private set; }

    /// <summary>项集合/位置/格子/图标大小变化，需要整体刷新视图。</summary>
    public event Action? ItemsChanged;
    public event Action? SelectionChanged;
    /// <summary>某项（null=全部）图标失效，需要重新取图。</summary>
    public event Action<string?>? IconInvalidated;
    public event Action? CutStateChanged;
    public event Action<string>? RenameRequested;
    /// <summary>“新建”路径投递 RenameRequested 的时刻（Stopwatch 时间戳），订阅者读后清零，仅用于诊断排队耗时；0 表示非新建路径（如 F2）。</summary>
    internal long RenamePostedAt;
    /// <summary>请求原位重命名某个格子的标题（参数为格子 Id）。</summary>
    public event Action<string>? BoxRenameRequested;
    /// <summary>DeskNook 自己的图标和格子整体显示/隐藏状态变化。</summary>
    public event Action? IconsVisibleChanged;

    /// <summary>菜单代理（Explorer 进程内弹菜单）；null 或不可用时走进程内回退菜单。</summary>
    public ExplorerMenuProxy? MenuProxy { get; set; }

    /// <summary>DeskNook 的图标与格子是否显示（“查看 ▸ 显示桌面图标”的状态来源）。系统 ListView 始终保持隐藏。</summary>
    public bool IconsVisible { get; private set; } = true;

    /// <summary>格子背景不透明度（设置里的滑块，可实时预览）。</summary>
    public double BoxOpacity { get; private set; } = AppSettings.DefaultBoxOpacity;
    /// <summary>外观（格子透明度）变化，需要刷新已有格子。</summary>
    public event Action? AppearanceChanged;

    /// <summary>格子跨显示器拖动的目标屏预览变化（null = 清除）。</summary>
    public event Action<BoxGhost?>? BoxGhostChanged;

    public void ShowBoxGhost(BoxGhost? ghost) => BoxGhostChanged?.Invoke(ghost);

    /// <summary>实时预览格子透明度（不保存；取消设置时用已保存的值再预览一次即可还原）。</summary>
    public void PreviewBoxOpacity(double opacity)
    {
        BoxOpacity = Math.Clamp(opacity, AppSettings.MinBoxOpacity, 1.0);
        AppearanceChanged?.Invoke();
    }

    public void SetIconsVisible(bool visible)
    {
        if (IconsVisible == visible) return;
        IconsVisible = visible;
        Layout.View.IconsHidden = !visible;
        ScheduleSave();
        Log.Info($"DeskNook 图标显示状态：{(visible ? "显示" : "隐藏")}");
        IconsVisibleChanged?.Invoke();
    }

    /// <summary>从系统桌面视图重新读取图标大小与间距（“查看 ▸ 大/中/小图标”作用在隐藏的 ListView 上后调用）。</summary>
    public void SyncFromSystemView()
    {
        var m = SystemDesktopView.ReadMetrics();
        if (m == null || _monitors.Count == 0) return;
        var scale = _monitors.FirstOrDefault(x => x.IsPrimary).Scale;
        if (scale <= 0) scale = 1;
        var size = (int)Math.Round(m.Value.IconSizePx / scale);
        _cellExtraDip = Math.Max(8, (m.Value.SpacingX - m.Value.IconSizePx) / scale);
        _cellExtraYDip = Math.Max(8, (m.Value.SpacingY - m.Value.IconSizePx) / scale);
        if (Settings.IconSizeMode != "system") { Settings.IconSizeMode = "system"; _settingsStore.Save(Settings); }
        Log.Info($"同步系统桌面视图：图标={size}（原 {Layout.View.IconSize}）间距=({m.Value.SpacingX},{m.Value.SpacingY})px");
        Layout.View.IconSize = size;
        RebuildGrids();
        Reconcile();
        ScheduleSave();
        ItemsChanged?.Invoke();
    }

    public DesktopController(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Layout = _store.Load();
        Settings = _settingsStore.Load();
        Log.SetRetention(Settings.LogRetentionDays);
        IconsVisible = !Layout.View.IconsHidden;
        BoxOpacity = Settings.BoxOpacity;
        _organizeUndo = _undoStore.Load();

        Source = new DesktopItemSource();
        Icons = new ShellIconCache(dispatcher);
        Source.Changed += OnSourceChanged;
        Source.IconInvalidated += OnIconInvalidated;

        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            _saveTimer!.Stop();
            _store.Save(Layout);
        }, dispatcher);
        _saveTimer.Stop();

        _clipboard = new ClipboardWatcher();
        _clipboard.Changed += UpdateCutState;
        ShellContextMenu.CreateOverride = CreateMenuOverride;
    }

    private void OnIconInvalidated(string? key)
    {
        Icons.Invalidate(key);
        IconInvalidated?.Invoke(key);
    }

    // ------------------------------------------------------------ 初始化与网格

    /// <summary>首次初始化：读取系统桌面设置，必要时导入系统图标位置，并完成布局。</summary>
    public void Initialize(IntPtr systemListView)
    {
        _byKey = Source.Items.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);
        _monitors = DesktopShell.GetMonitors();
        var primaryScale = _monitors.FirstOrDefault(m => m.IsPrimary).Scale;
        if (primaryScale <= 0) primaryScale = 1;

        var sys = SystemDesktopView.Read(Source.Items, systemListView);
        if (sys != null)
        {
            _cellExtraDip = Math.Max(8, (sys.SpacingXPx - sys.IconSizePx) / primaryScale);
            _cellExtraYDip = Math.Max(8, (sys.SpacingYPx - sys.IconSizePx) / primaryScale);
            if (!Layout.SystemPositionsImported) Layout.View.IconSize = (int)Math.Round(sys.IconSizePx / primaryScale);
            else if (AppSettings.IconSizeOf(Settings.IconSizeMode) == null) Layout.View.IconSize = (int)Math.Round(sys.IconSizePx / primaryScale); // 跟随系统
        }
        if (AppSettings.IconSizeOf(Settings.IconSizeMode) is { } fixedSize) Layout.View.IconSize = fixedSize;
        RebuildGrids();

        if (!Layout.SystemPositionsImported && sys != null && sys.Positions.Count > 0)
        {
            var n = 0;
            foreach (var (key, (x, y)) in sys.Positions)
            {
                var cell = GridLayout.FromScreenPixel(x, y, Grids);
                if (cell == null) continue;
                Layout.FreeIcons[key] = new IconSlot { Monitor = cell.Value.Monitor, Col = cell.Value.Col, Row = cell.Value.Row };
                n++;
            }
            Layout.SystemPositionsImported = true;
            Log.Info($"已导入系统桌面图标位置 {n} 项，图标大小={Layout.View.IconSize}");
        }

        Reconcile();
        SyncMapped();
        UpdateCutState();
        ScheduleSave();
    }

    /// <summary>显示器布局变化后调用：重新计算网格并让越界项就近落位。</summary>
    public void UpdateMonitors()
    {
        _monitors = DesktopShell.GetMonitors();
        RebuildGrids();
        Reconcile();
        SyncMapped();
        ScheduleSave();
        ItemsChanged?.Invoke();
    }

    private void RebuildGrids()
    {
        CellW = Layout.View.IconSize + _cellExtraDip;
        CellH = Layout.View.IconSize + _cellExtraYDip;
        Grids = _monitors
            .Select(m => new MonitorGrid(m.DeviceName, m.Scale, m.Work.Left, m.Work.Top, m.Work.Width, m.Work.Height, CellW, CellH))
            .ToList();
    }

    private void Reconcile()
    {
        if (Grids.Count == 0) return;
        var res = LayoutReconciler.Reconcile(Layout, Source.Items.Select(i => i.Key).ToList(), Grids, DateTime.UtcNow);
        if (res.Placed.Count > 0) Log.Info($"布局：新分配 {res.Placed.Count} 项位置");
    }

    /// <summary>让映射格子的目录来源与布局里的映射格子保持一致（新增则监听，删除/改路径则释放）。</summary>
    private void SyncMapped()
    {
        var want = Layout.Boxes.Where(b => b.Kind == BoxKind.Mapped && !string.IsNullOrEmpty(b.MappedPath)).ToDictionary(b => b.Id, b => b.MappedPath!);
        foreach (var id in _mapped.Keys.ToList())
        {
            if (want.TryGetValue(id, out var path) && string.Equals(path, _mapped[id].Path, StringComparison.OrdinalIgnoreCase)) continue;
            _mapped[id].Source.Dispose();
            _mapped.Remove(id);
        }
        foreach (var (id, path) in want)
        {
            if (_mapped.ContainsKey(id)) continue;
            try
            {
                var src = new DesktopItemSource(path, id);
                var rt = new MappedRuntime { Source = src, Path = path };
                rt.ByKey = src.Items.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);
                var boxId = id;
                src.Changed += diff => OnMappedChanged(boxId, diff);
                src.IconInvalidated += OnIconInvalidated;
                _mapped[id] = rt;
            }
            catch (Exception ex)
            {
                Log.Error($"创建映射目录来源失败：{path}", ex);
            }
        }
        _mappedByKey = _mapped.Values.SelectMany(r => r.ByKey).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        Selected.RemoveWhere(k => IsMappedKey(k) && !_mappedByKey.ContainsKey(k));
    }

    private static bool IsMappedKey(string key) => key.IndexOf(DesktopItemSource.KeySeparator) >= 0;

    private void OnMappedChanged(string boxId, ItemDiffResult diff)
    {
        if (!_mapped.TryGetValue(boxId, out var rt)) return;
        rt.ByKey = rt.Source.Items.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);
        _mappedByKey = _mapped.Values.SelectMany(r => r.ByKey).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        foreach (var (o, n) in diff.Renamed)
        {
            if (Selected.Remove(o.Key)) Selected.Add(n.Key);
            if (AnchorKey == o.Key) AnchorKey = n.Key;
        }
        foreach (var r in diff.Removed) Selected.Remove(r.Key);

        string? renameKey = null;
        if (DateTime.UtcNow < _expectNewUntil && diff.Added.Count > 0)
        {
            renameKey = diff.Added.OrderByDescending(a => a.Modified).First().Key;
            _expectNewUntil = DateTime.MinValue;
            Selected.Clear();
            Selected.Add(renameKey);
            AnchorKey = renameKey;
        }

        ItemsChanged?.Invoke();
        SelectionChanged?.Invoke();
        if (renameKey != null)
        {
            RenamePostedAt = Stopwatch.GetTimestamp();
            _dispatcher.BeginInvoke(DispatcherPriority.Background, () => RenameRequested?.Invoke(renameKey));
        }
    }

    public MonitorGrid? GridOf(string monitor) => Grids.FirstOrDefault(g => g.Name == monitor);

    public IconSlot? SlotOf(string key) => Layout.FreeIcons.GetValueOrDefault(key);

    public DesktopItem? ItemOf(string key) => _byKey.GetValueOrDefault(key) ?? _mappedByKey.GetValueOrDefault(key);

    /// <summary>自由区里位于该显示器的项（格子里的项不含）。</summary>
    public IEnumerable<DesktopItem> ItemsOn(string monitor) =>
        Source.Items.Where(i => Layout.FreeIcons.TryGetValue(i.Key, out var s) && s.Monitor == monitor);

    public IReadOnlyList<DesktopItem> SelectedItems =>
        Source.Items.Where(i => Selected.Contains(i.Key))
            .Concat(_mappedByKey.Values.Where(i => Selected.Contains(i.Key)))
            .ToList();

    /// <summary>与第一项同一来源（桌面 / 同一个映射目录）的项：Shell 菜单与文件操作要求同一父文件夹。</summary>
    private static IReadOnlyList<DesktopItem> SameParent(IReadOnlyList<DesktopItem> items) =>
        items.Count == 0 ? items : items.Where(i => i.Container == items[0].Container).ToList();

    public void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void Flush()
    {
        _saveTimer.Stop();
        _store.Save(Layout);
    }

    // ------------------------------------------------------------ 格子：查询

    public BoxState? BoxOf(string id) => BoxOps.Find(Layout, id);

    /// <summary>桌面项 key 所在的普通格子。</summary>
    public BoxState? BoxOfKey(string key) => BoxOps.BoxOfKey(Layout, key);

    /// <summary>格子实际显示的显示器与矩形（显示器缺失时落到主屏；折叠时只有标题栏）。</summary>
    public (MonitorGrid Grid, BoxRect Rect)? EffectiveRect(BoxState box, bool ignoreCollapsed = false) =>
        BoxGeometry.Effective(box, Grids, ignoreCollapsed);

    public IEnumerable<BoxState> BoxesOn(string monitor) =>
        Layout.Boxes.Where(b => EffectiveRect(b)?.Grid.Name == monitor);

    /// <summary>屏幕物理像素点所在显示器的设备名；不在任何显示器上时为 null。</summary>
    public string? MonitorAt(Win32.POINT p) =>
        _monitors.FirstOrDefault(m => p.X >= m.Bounds.Left && p.X < m.Bounds.Right && p.Y >= m.Bounds.Top && p.Y < m.Bounds.Bottom).DeviceName;

    /// <summary>格子当前视图的排布参数（跟随桌面时与桌面图标尺寸一致）。</summary>
    public BoxView ViewOf(BoxState box) => BoxGeometry.ViewFor(box.ViewMode, IconSize, CellW, CellH);

    public int BoxCols(BoxState box)
    {
        var eff = EffectiveRect(box, ignoreCollapsed: true);
        return eff == null ? 1 : BoxGeometry.ViewCols(ViewOf(box), eff.Value.Rect.W);
    }

    /// <summary>格子内按显示顺序排列的项（普通格子：成员顺序或排序模式；映射格子：目录内容，默认按名称）。</summary>
    public IReadOnlyList<DesktopItem> BoxItems(BoxState box)
    {
        if (box.Kind == BoxKind.Mapped)
        {
            if (!_mapped.TryGetValue(box.Id, out var rt)) return Array.Empty<DesktopItem>();
            return ItemSorter.Sort(rt.Source.Items, box.SortMode == "" ? "name" : box.SortMode).ToList();
        }
        var list = box.ItemKeys.Select(k => _byKey.GetValueOrDefault(k)).Where(i => i != null).Select(i => i!).ToList();
        return box.SortMode == "" ? list : ItemSorter.Sort(list, box.SortMode).ToList();
    }

    // ------------------------------------------------------------ 来源变化

    private void OnSourceChanged(ItemDiffResult diff)
    {
        _byKey = Source.Items.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);

        if (_pendingOp != null && DateTime.UtcNow < _pendingOpUntil &&
            ((_pendingOp == "删除" && diff.Removed.Count > 0) || (_pendingOp != "删除" && diff.Added.Count > 0)))
        {
            UndoLabel = _pendingOp;
            _pendingOp = null;
        }

        foreach (var (o, n) in diff.Renamed)
        {
            LayoutReconciler.Rename(Layout, o.Key, n.Key);
            if (Selected.Remove(o.Key)) Selected.Add(n.Key);
            if (AnchorKey == o.Key) AnchorKey = n.Key;
        }
        foreach (var r in diff.Removed) Selected.Remove(r.Key);

        Reconcile();

        // 拖入：新项落在鼠标释放处（自由区的格子，或某个普通格子里的插入位置）
        if (_pendingDrop is { } drop && DateTime.UtcNow < drop.Until && diff.Added.Count > 0)
        {
            var keys = diff.Added.Select(a => a.Key).ToList();
            var box = drop.BoxId != null ? BoxOf(drop.BoxId) : null;
            if (box is { Kind: BoxKind.Normal })
            {
                MaterializeOrder(box);
                BoxOps.MoveToBox(Layout, keys, box, drop.Index);
            }
            else PlaceNear(keys, drop.Monitor, drop.Col, drop.Row);
            _pendingDrop = null;
        }

        // “新建”命令产生的新项：选中并进入重命名
        string? renameKey = null;
        if (DateTime.UtcNow < _expectNewUntil && diff.Added.Count > 0)
        {
            renameKey = diff.Added.OrderByDescending(a => a.Modified).First().Key;
            _expectNewUntil = DateTime.MinValue;
            Selected.Clear();
            Selected.Add(renameKey);
            AnchorKey = renameKey;
        }

        ScheduleSave();
        ItemsChanged?.Invoke();
        SelectionChanged?.Invoke();
        if (renameKey != null)
        {
            RenamePostedAt = Stopwatch.GetTimestamp();
            _dispatcher.BeginInvoke(DispatcherPriority.Background, () => RenameRequested?.Invoke(renameKey));
        }
    }

    /// <summary>用户在“新建”菜单里点了命令：数秒内出现的新项自动选中并重命名。</summary>
    public void ExpectNewItem() => _expectNewUntil = DateTime.UtcNow + NewItemWindow;

    /// <summary>
    /// 桌面“新建”子菜单的文件夹/文件由本进程创建（不交给 Explorer，免得 DefView 重命名让桌面线程忙 1.4~6 秒），
    /// 创建后立即同步刷新来源，新项随即选中并进入重命名。目标是映射格子的目录，否则是用户桌面。失败返回 false。
    /// </summary>
    public bool CreateNewItem(MenuContext ctx, string verb, string title, string parent)
    {
        var sw = Stopwatch.StartNew();
        MappedRuntime? rt = null;
        var folder = ctx.Box is { Kind: BoxKind.Mapped } box && _mapped.TryGetValue(box.Id, out rt)
            ? rt.Path
            : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        ExpectNewItem();
        ArmUndo("新建");
        var path = ShellNewItems.Create(verb, folder, title, parent, ctx.Hwnd);
        if (path == null)
        {
            _expectNewUntil = DateTime.MinValue;
            _pendingOp = null;
            return false;
        }
        (rt?.Source ?? Source).Refresh(useShownCache: true);
        Log.Info($"新建：创建并刷新完成，总耗时 {sw.ElapsedMilliseconds} ms");
        return true;
    }

    /// <summary>拖入（外部）释放位置：随后出现的新项落在这里。</summary>
    public void SetPendingDrop(string monitor, int col, int row) =>
        _pendingDrop = new PendingDrop(monitor, col, row, null, 0, DateTime.UtcNow + DropWindow);

    /// <summary>拖入普通格子：随后出现的新项放进该格子的 index 位置。</summary>
    public void SetPendingDropBox(string boxId, int index) =>
        _pendingDrop = new PendingDrop("", 0, 0, boxId, index, DateTime.UtcNow + DropWindow);

    public void Refresh()
    {
        Icons.Invalidate(null);
        IconInvalidated?.Invoke(null);
        Source.Refresh();
        foreach (var rt in _mapped.Values) rt.Source.Refresh();
        ItemsChanged?.Invoke();
    }

    // ------------------------------------------------------------ 选择

    public void SetSelection(IEnumerable<string> keys, string? anchor = null)
    {
        var set = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        if (set.SetEquals(Selected) && (anchor == null || anchor == AnchorKey)) return;
        Selected.Clear();
        foreach (var k in set) Selected.Add(k);
        if (anchor != null) AnchorKey = anchor;
        SelectionChanged?.Invoke();
    }

    public void SelectOnly(string key) => SetSelection(new[] { key }, key);

    public void ToggleSelection(string key)
    {
        if (!Selected.Remove(key)) Selected.Add(key);
        AnchorKey = key;
        SelectionChanged?.Invoke();
    }

    public void ClearSelection()
    {
        if (Selected.Count == 0) return;
        Selected.Clear();
        SelectionChanged?.Invoke();
    }

    public void SelectAll() => SetSelection(Source.Items.Select(i => i.Key), AnchorKey);

    /// <summary>全部可见项的位置（自由区 + 各格子内的顺序位置）。</summary>
    private Dictionary<string, ItemLoc> BuildLocs()
    {
        var d = new Dictionary<string, ItemLoc>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in Source.Items)
            if (Layout.FreeIcons.TryGetValue(it.Key, out var s)) d[it.Key] = new ItemLoc(s.Monitor, s.Col, s.Row);
        foreach (var box in Layout.Boxes)
        {
            var items = BoxItems(box);
            var cols = BoxCols(box);
            var view = ViewOf(box);
            for (var i = 0; i < items.Count; i++)
            {
                var (c, r) = BoxGeometry.ViewCellOf(view, i, items.Count, cols);
                d[items[i].Key] = new ItemLoc("box:" + box.Id, c, r);
            }
        }
        return d;
    }

    /// <summary>Shift+点击：选中锚点与目标之间矩形区域内的项（同一区域内）。</summary>
    public void SelectRange(string key)
    {
        var locs = BuildLocs();
        if (AnchorKey == null || !locs.TryGetValue(AnchorKey, out var anchor) || !locs.TryGetValue(key, out var target) || anchor.Area != target.Area)
        {
            SelectOnly(key);
            return;
        }
        int c1 = Math.Min(anchor.Col, target.Col), c2 = Math.Max(anchor.Col, target.Col);
        int r1 = Math.Min(anchor.Row, target.Row), r2 = Math.Max(anchor.Row, target.Row);
        var keys = locs.Where(kv => kv.Value.Area == target.Area && kv.Value.Col >= c1 && kv.Value.Col <= c2 && kv.Value.Row >= r1 && kv.Value.Row <= r2)
            .Select(kv => kv.Key);
        SetSelection(keys); // 保持锚点
    }

    /// <summary>方向键：移动选择到该方向最近的项（同一区域内）。</summary>
    public void Navigate(NavDirection dir, bool extend)
    {
        var locs = BuildLocs();
        if (locs.Count == 0) return;
        var fromKey = AnchorKey != null && locs.ContainsKey(AnchorKey) ? AnchorKey : SelectedItems.FirstOrDefault(i => locs.ContainsKey(i.Key))?.Key;
        if (fromKey == null)
        {
            var first = locs.OrderBy(kv => kv.Value.Area != Grids.FirstOrDefault()?.Name)
                .ThenBy(kv => kv.Value.Col).ThenBy(kv => kv.Value.Row).First();
            SelectOnly(first.Key);
            return;
        }

        var cur = locs[fromKey];
        string? best = null;
        var bestScore = double.MaxValue;
        foreach (var (k, loc) in locs)
        {
            if (k == fromKey || loc.Area != cur.Area) continue;
            int dc = loc.Col - cur.Col, dr = loc.Row - cur.Row;
            var ok = dir switch { NavDirection.Left => dc < 0, NavDirection.Right => dc > 0, NavDirection.Up => dr < 0, _ => dr > 0 };
            if (!ok) continue;
            var score = dir is NavDirection.Left or NavDirection.Right
                ? Math.Abs(dc) * 1000 + Math.Abs(dr)
                : Math.Abs(dr) * 1000 + Math.Abs(dc);
            if (score < bestScore) { bestScore = score; best = k; }
        }
        if (best == null) return;
        if (extend) { Selected.Add(best); AnchorKey = best; SelectionChanged?.Invoke(); }
        else SelectOnly(best);
    }

    // ------------------------------------------------------------ Shell 操作

    public void OpenSelected() => OpenItems(SelectedItems);

    public void OpenItems(IReadOnlyList<DesktopItem> items) => ShellContextMenu.InvokeDefault(SameParent(items), ActiveHwnd);

    public void DeleteSelected(bool permanent)
    {
        var items = SelectedItems;
        if (items.Count == 0) return;
        if (!permanent) ArmUndo("删除");
        foreach (var group in items.GroupBy(i => i.Container))
            ShellContextMenu.InvokeVerb(group.ToList(), "delete", ActiveHwnd, shift: permanent);
    }

    public void CopySelected() { var i = SameParent(SelectedItems); if (i.Count > 0) ShellContextMenu.InvokeVerb(i, "copy", ActiveHwnd); }
    public void CutSelected() { var i = SameParent(SelectedItems); if (i.Count > 0) ShellContextMenu.InvokeVerb(i, "cut", ActiveHwnd); }

    /// <summary>粘贴到当前目标：最近点击的是映射格子则粘贴到该目录，否则粘贴到桌面。</summary>
    public void Paste() => PasteVerb("paste", ClipboardWatcher.GetCutPaths().Count > 0 ? "移动" : "复制");

    public void PasteShortcut() => PasteVerb("pastelink", "创建快捷方式");

    private void PasteVerb(string verb, string undoLabel)
    {
        ArmUndo(undoLabel);
        _menuBox = ActiveBox is { Kind: BoxKind.Mapped } b ? b : null;
        try { ShellContextMenu.InvokeVerb(Array.Empty<DesktopItem>(), verb, ActiveHwnd); }
        finally { _menuBox = null; }
    }

    public void ArmUndo(string op)
    {
        _pendingOp = op;
        _pendingOpUntil = DateTime.UtcNow + TimeSpan.FromSeconds(20);
    }

    /// <summary>撤销：把 Explorer 的 FCIDM_SHVIEW_UNDO(0x701B) 发给系统里隐藏着的 DefView，由 Shell 的撤销栈执行。</summary>
    public void Undo()
    {
        var defView = DesktopShell.FindDesktop().DefView;
        if (defView == IntPtr.Zero) return;
        Log.Info($"撤销：{UndoLabel}");
        Win32.PostMessage(defView, 0x0111 /* WM_COMMAND */, (IntPtr)0x701B, IntPtr.Zero);
        UndoLabel = null;
    }
    public void ShowProperties() => ShellContextMenu.InvokeVerb(SameParent(SelectedItems), "properties", ActiveHwnd);

    public void ShowMenu(IntPtr hwnd, Win32.POINT screenPoint, string monitor, IReadOnlyList<DesktopItem> items)
    {
        ActiveHwnd = hwnd;
        var same = SameParent(items);
        var shift = (Win32.GetKeyState(Win32.VK_SHIFT) & 0x8000) != 0;
        var ctx = new MenuContext
        {
            Controller = this, Items = same, Hwnd = hwnd, ScreenPoint = screenPoint, Monitor = monitor, Shift = shift,
            Source = MenuSource.Desktop,
        };

        // 优先让 Explorer 进程内的代理弹出真菜单（夸克/百度网盘/NVIDIA 等扩展只认 explorer.exe）
        if (MenuProxy != null && !ExplorerMenuProxy.Disabled)
        {
            var req = new ProxyRequest
            {
                Kind = same.Count == 0 ? "background" : "item",
                Folder = same.Count > 0 && same[0].Container.Length > 0 ? (BoxOf(same[0].Container)?.MappedPath ?? "::desktop") : "::desktop",
                Items = same.Select(ProxyItemName).ToList(), X = screenPoint.X, Y = screenPoint.Y, Shift = shift,
            };
            if (MenuProxy.TryShow(ctx, req)) return;
        }

        MenuExtensions.Current = ctx;
        try { ShellContextMenu.Show(ctx); }
        finally { MenuExtensions.Current = null; }
    }

    /// <summary>发给代理的项名：桌面项为解析名（Key），映射格子里的项为文件系统路径。</summary>
    private static string ProxyItemName(DesktopItem i) =>
        i.Container.Length == 0 ? i.Key : (i.FilePath ?? i.Key[(i.Key.IndexOf(DesktopItemSource.KeySeparator) + 1)..]);

    /// <summary>格子空白处/标题栏右键：普通格子只有自定义格子菜单；映射格子再并入该目录的原生背景菜单。</summary>
    public void ShowBoxMenu(IntPtr hwnd, Win32.POINT screenPoint, string monitor, BoxState box)
    {
        ActiveHwnd = hwnd;
        var shift = (Win32.GetKeyState(Win32.VK_SHIFT) & 0x8000) != 0;
        var ctx = new MenuContext
        {
            Controller = this, Items = Array.Empty<DesktopItem>(), Hwnd = hwnd, ScreenPoint = screenPoint, Monitor = monitor,
            Shift = shift, Box = box, Source = box.Kind == BoxKind.Mapped ? MenuSource.MappedBox : MenuSource.Box,
        };
        MenuExtensions.Current = ctx;
        try
        {
            if (box.Kind == BoxKind.Mapped)
            {
                if (MenuProxy != null && !ExplorerMenuProxy.Disabled && !string.IsNullOrEmpty(box.MappedPath))
                {
                    var req = new ProxyRequest { Kind = "background", Folder = box.MappedPath, X = screenPoint.X, Y = screenPoint.Y, Shift = shift };
                    if (MenuProxy.TryShow(ctx, req)) return;
                }
                _menuBox = box;
                try { ShellContextMenu.Show(ctx); }
                finally { _menuBox = null; }
            }
            else BoxMenu.Show(ctx);
        }
        finally { MenuExtensions.Current = null; }
    }

    /// <summary>映射格子空白处的菜单对象：该目录的 IShellFolder.CreateViewObject(IContextMenu)。</summary>
    private IContextMenu? CreateMenuOverride(IReadOnlyList<DesktopItem> items, IntPtr hwnd)
    {
        if (items.Count != 0 || _menuBox is not { Kind: BoxKind.Mapped } box || string.IsNullOrEmpty(box.MappedPath)) return null;
        var folder = ShellApi.BindFolder(box.MappedPath, out var abs);
        if (folder == null) return null;
        try
        {
            var hr = folder.CreateViewObject(hwnd, ShellApi.IID_IContextMenu, out var ppv);
            if (hr < 0 || ppv == IntPtr.Zero) { Log.Info($"映射目录取背景菜单失败 hr=0x{hr:X}"); return null; }
            try { return Marshal.GetObjectForIUnknown(ppv) as IContextMenu; }
            finally { Marshal.Release(ppv); }
        }
        finally
        {
            Marshal.ReleaseComObject(folder);
            Win32.ILFree(abs);
        }
    }

    /// <summary>映射格子目录的 IDropTarget（拖入映射格子空白处时交给它）。</summary>
    public IDropTarget? CreateMappedDropTarget(BoxState box, IntPtr hwnd)
    {
        if (string.IsNullOrEmpty(box.MappedPath)) return null;
        var folder = ShellApi.BindFolder(box.MappedPath, out var abs);
        if (folder == null) return null;
        try
        {
            var hr = folder.CreateViewObject(hwnd, ShellApi.IID_IDropTarget, out var ppv);
            if (hr < 0 || ppv == IntPtr.Zero) return null;
            try { return Marshal.GetObjectForIUnknown(ppv) as IDropTarget; }
            finally { Marshal.Release(ppv); }
        }
        finally
        {
            Marshal.ReleaseComObject(folder);
            Win32.ILFree(abs);
        }
    }

    // ------------------------------------------------------------ 重命名

    public void BeginRename(string key)
    {
        if (ItemOf(key) == null) return;
        Log.Info($"请求原位重命名：{key}");
        RenameRequested?.Invoke(key);
    }

    /// <summary>提交重命名；返回 true 表示已改名（含内容未变）。</summary>
    public bool CommitRename(string key, string newName)
    {
        var item = ItemOf(key);
        if (item == null) return false;
        newName = newName.Trim();
        if (newName.Length == 0 || newName == item.EditName) return true;

        var res = ShellActions.Rename(item, newName, ActiveHwnd);
        if (res == null) return false;

        var mapped = item.Container.Length > 0;
        var newKey = mapped ? item.Container + DesktopItemSource.KeySeparator + res.Value.Key : res.Value.Key;
        UndoLabel = "重命名";
        if (mapped && _mapped.TryGetValue(item.Container, out var rt))
        {
            rt.Source.AddRenameHint(key, newKey);
            rt.Source.Refresh();
        }
        else
        {
            Source.AddRenameHint(key, newKey);
            LayoutReconciler.Rename(Layout, key, newKey);
            Source.Refresh();
        }
        Selected.Remove(key);
        Selected.Add(newKey);
        AnchorKey = newKey;
        ScheduleSave();
        ItemsChanged?.Invoke();
        SelectionChanged?.Invoke();
        return true;
    }

    // ------------------------------------------------------------ 位置 / 视图

    /// <summary>某显示器上已被占用的格子：自由图标 + 格子覆盖的区域。</summary>
    private HashSet<(int, int)> Occupied(string monitor, ICollection<string>? exclude = null)
    {
        var set = Source.Items
            .Where(i => (exclude == null || !exclude.Contains(i.Key)) &&
                        Layout.FreeIcons.TryGetValue(i.Key, out var s) && s.Monitor == monitor)
            .Select(i => (Layout.FreeIcons[i.Key].Col, Layout.FreeIcons[i.Key].Row))
            .ToHashSet();
        set.UnionWith(BoxGeometry.CoveredCells(Layout, Grids, monitor));
        return set;
    }

    /// <summary>把一组项依次放到目标格附近的空位（自由区）。</summary>
    private void PlaceNear(IReadOnlyList<string> keys, string monitor, int col, int row)
    {
        var grid = GridOf(monitor);
        if (grid == null) return;
        var occupied = Occupied(monitor, keys.ToList());
        foreach (var k in keys)
        {
            var (c, r) = GridLayout.NearestEmpty(grid.Size, occupied, col, row);
            LayoutReconciler.Move(Layout, k, monitor, c, r);
            occupied.Add((c, r));
        }
    }

    /// <summary>
    /// 把选中的桌面项移到自由区：锚点项落到 (col,row)，其余自由图标保持相对位置；
    /// 来自格子的项（没有自由位置）就近放在目标格附近。目标被占用或越界则就近找空位。
    /// </summary>
    public void MoveSelection(string anchorKey, string monitor, int col, int row)
    {
        var grid = GridOf(monitor);
        if (grid == null) return;
        var anchor = SlotOf(anchorKey);

        var all = SelectedItems.Where(i => i.Container.Length == 0).Select(i => i.Key).ToList();
        if (!all.Contains(anchorKey, StringComparer.OrdinalIgnoreCase)) all = new List<string> { anchorKey };
        var withSlot = anchor != null ? all.Where(k => Layout.FreeIcons.ContainsKey(k) && Source.Items.Any(i => i.Key == k)).ToList() : new List<string>();
        var withoutSlot = all.Where(k => !withSlot.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();

        var occupied = Occupied(monitor, all);
        if (anchor != null)
        {
            int dCol = col - anchor.Col, dRow = row - anchor.Row;
            var origin = withSlot.ToDictionary(k => k, k => (Layout.FreeIcons[k].Col, Layout.FreeIcons[k].Row), StringComparer.OrdinalIgnoreCase);
            foreach (var k in withSlot.OrderBy(k => origin[k].Col).ThenBy(k => origin[k].Row))
            {
                var (c, r) = (origin[k].Col + dCol, origin[k].Row + dRow);
                if (c < 0 || r < 0 || c >= grid.Cols || r >= grid.Rows || occupied.Contains((c, r)))
                    (c, r) = GridLayout.NearestEmpty(grid.Size, occupied, c, r);
                LayoutReconciler.Move(Layout, k, monitor, c, r);
                occupied.Add((c, r));
            }
        }
        foreach (var k in withoutSlot)
        {
            var (c, r) = GridLayout.NearestEmpty(grid.Size, occupied, col, row);
            LayoutReconciler.Move(Layout, k, monitor, c, r);
            occupied.Add((c, r));
        }
        Layout.View.SortKey = ""; // 手动摆放后不再视为“已排序”
        ScheduleSave();
        ItemsChanged?.Invoke();
    }

    public void SetIconSize(int size)
    {
        if (Layout.View.IconSize == size) return;
        Layout.View.IconSize = size;
        RebuildGrids();
        Reconcile();
        ScheduleSave();
        ItemsChanged?.Invoke();
    }

    /// <summary>按指定方式重新排列：每个显示器内的自由项按排序结果从左上角按列依次填充（避开格子占用的区域）。</summary>
    public void SortBy(string key)
    {
        Layout.View.SortKey = key;
        foreach (var grid in Grids)
        {
            var items = ItemsOn(grid.Name).ToList();
            var sorted = ItemSorter.Sort(items, key).ToList();
            var occupied = BoxGeometry.CoveredCells(Layout, Grids, grid.Name);
            foreach (var it in sorted)
            {
                var (c, r) = GridLayout.FirstEmpty(grid.Size, occupied);
                occupied.Add((c, r));
                LayoutReconciler.Move(Layout, it.Key, grid.Name, c, r);
            }
        }
        ScheduleSave();
        ItemsChanged?.Invoke();
    }

    // ------------------------------------------------------------ 格子：创建 / 修改 / 解散

    private string UniqueBoxName(string baseName)
    {
        if (!Layout.Boxes.Any(b => b.Name == baseName)) return baseName;
        for (var i = 2; ; i++)
            if (!Layout.Boxes.Any(b => b.Name == $"{baseName} {i}")) return $"{baseName} {i}";
    }

    /// <summary>在 screenPoint 附近找空地创建格子（cols × 内容 rows）。</summary>
    private BoxState CreateBox(string name, BoxKind kind, string? mappedPath, string monitor, Win32.POINT screenPoint,
        int cols, int rows, ICollection<string>? excludeKeys)
    {
        var grid = GridOf(monitor) ?? Grids.First();
        var wantCol = (int)Math.Floor((screenPoint.X - grid.WorkLeft) / grid.Scale / CellW);
        var wantRow = (int)Math.Floor((screenPoint.Y - grid.WorkTop) / grid.Scale / CellH);
        wantCol = Math.Clamp(wantCol, 0, Math.Max(0, grid.Cols - cols));
        wantRow = Math.Clamp(wantRow, 0, Math.Max(0, grid.Rows - 1));

        cols = Math.Min(cols, grid.Cols);
        var (wCells, hCells) = BoxGeometry.CellsFor(cols, rows, CellH);
        var blocked = Occupied(grid.Name, excludeKeys);
        var spot = BoxGeometry.FindSpot(grid.Size, blocked, wCells, hCells, wantCol, wantRow)
                   ?? (wantCol, Math.Clamp(wantRow, 0, Math.Max(0, grid.Rows - hCells)));

        var box = new BoxState
        {
            Id = BoxOps.NewId(),
            Name = name,
            Kind = kind,
            MappedPath = mappedPath,
            Monitor = grid.Name,
            Rect = new BoxRect(spot.Item1 * CellW, spot.Item2 * CellH, BoxGeometry.WidthFor(cols, CellW), BoxGeometry.HeightFor(rows, CellH)),
        };
        Layout.Boxes.Add(box);
        Log.Info($"新建格子 {kind} \"{name}\" id={box.Id} 位置=({box.Rect.X},{box.Rect.Y}) 尺寸=({box.Rect.W}x{box.Rect.H}) 显示器={grid.Name}");
        return box;
    }

    private void AfterBoxChange()
    {
        Reconcile();
        SyncMapped();
        ScheduleSave();
        ItemsChanged?.Invoke();
        SelectionChanged?.Invoke();
    }

    public BoxState NewBox(IntPtr hwnd, Win32.POINT screenPoint, string monitor)
    {
        var box = CreateBox(UniqueBoxName("新格子"), BoxKind.Normal, null, monitor, screenPoint,
            BoxGeometry.DefaultCols, BoxGeometry.DefaultRows, null);
        AfterBoxChange();
        return box;
    }

    /// <summary>托盘菜单用：在主显示器工作区中央新建空格子（图标隐藏时先显示）。</summary>
    public BoxState? NewBoxCentered()
    {
        if (_monitors.Count == 0) return null;
        var m = _monitors.FirstOrDefault(x => x.IsPrimary);
        if (m.DeviceName == null) m = _monitors[0];
        SetIconsVisible(true);
        var pt = new Win32.POINT { X = m.Work.Left + m.Work.Width / 2, Y = m.Work.Top + m.Work.Height / 2 };
        return NewBox(IntPtr.Zero, pt, m.DeviceName);
    }

    /// <summary>用选中的桌面项新建普通格子。</summary>
    public BoxState NewBoxFromItems(IntPtr hwnd, Win32.POINT screenPoint, string monitor, IReadOnlyList<DesktopItem> items)
    {
        var keys = items.Where(i => i.Container.Length == 0).Select(i => i.Key).ToList();
        var rows = Math.Max(BoxGeometry.DefaultRows, (keys.Count + BoxGeometry.DefaultCols - 1) / BoxGeometry.DefaultCols);
        var box = CreateBox(UniqueBoxName("新格子"), BoxKind.Normal, null, monitor, screenPoint, BoxGeometry.DefaultCols, rows, keys);
        BoxOps.MoveToBox(Layout, keys, box, int.MaxValue);
        AfterBoxChange();
        return box;
    }

    /// <summary>选择一个目录并新建映射格子；取消返回 null。</summary>
    public BoxState? NewMappedBox(IntPtr hwnd, Win32.POINT screenPoint, string monitor)
    {
        var path = PickFolder(hwnd);
        return path == null ? null : NewMappedBoxAt(path, screenPoint, monitor);
    }

    public BoxState NewMappedBoxAt(string path, Win32.POINT screenPoint, string monitor)
    {
        path = path.TrimEnd('\\', '/') is { Length: > 0 } p ? p : path;
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) name = path;
        var box = CreateBox(UniqueBoxName(name), BoxKind.Mapped, path, monitor, screenPoint,
            BoxGeometry.DefaultCols, BoxGeometry.DefaultRows, null);
        AfterBoxChange();
        return box;
    }

    /// <summary>IFileOpenDialog(FOS_PICKFOLDERS)：WPF 的 OpenFolderDialog 即其封装。</summary>
    private static string? PickFolder(IntPtr hwnd)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择要映射的文件夹" };
        var owner = hwnd != IntPtr.Zero ? System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.RootVisual as System.Windows.Window : null;
        var ok = owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
        return ok == true && !string.IsNullOrEmpty(dlg.FolderName) ? dlg.FolderName : null;
    }

    /// <summary>移动/缩放结束后提交矩形（展开状态的完整尺寸）。冲突的自由图标由对账就近挪开。</summary>
    public void SetBoxRect(BoxState box, string monitor, BoxRect rect)
    {
        box.Monitor = monitor;
        box.Rect = rect;
        Reconcile();
        ScheduleSave();
        ItemsChanged?.Invoke();
    }

    public void RenameBox(BoxState box, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name == box.Name) return;
        box.Name = name;
        ScheduleSave();
        ItemsChanged?.Invoke();
    }

    public void BeginRenameBox(string boxId) => BoxRenameRequested?.Invoke(boxId);

    public void ToggleBoxCollapsed(BoxState box)
    {
        box.Collapsed = !box.Collapsed;
        Reconcile(); // 展开后覆盖到的自由图标需要让开
        ScheduleSave();
        ItemsChanged?.Invoke();
    }

    public void ToggleBoxLocked(BoxState box)
    {
        box.Locked = !box.Locked;
        ScheduleSave();
        ItemsChanged?.Invoke();
    }

    /// <summary>设置排序方式（"" = 手动顺序，仅普通格子；切到手动时把当前显示顺序固化为成员顺序）。</summary>
    public void SetBoxSort(BoxState box, string mode)
    {
        if (box.SortMode == mode) return;
        if (mode == "") MaterializeOrder(box);
        box.SortMode = mode;
        ScheduleSave();
        ItemsChanged?.Invoke();
    }

    /// <summary>设置格子视图（"" = 跟随桌面 | large | medium | small | list）。</summary>
    public void SetBoxView(BoxState box, string mode)
    {
        mode = BoxGeometry.NormalizeViewMode(mode);
        if (box.ViewMode == mode) return;
        box.ViewMode = mode;
        ScheduleSave();
        ItemsChanged?.Invoke();
    }

    /// <summary>把按排序模式显示的顺序固化到 ItemKeys，并转为手动顺序（之后的插入位置才有意义）。</summary>
    private void MaterializeOrder(BoxState box)
    {
        if (box.Kind != BoxKind.Normal || box.SortMode == "") return;
        var shown = BoxItems(box).Select(i => i.Key).ToList();
        var rest = box.ItemKeys.Where(k => !shown.Contains(k, StringComparer.OrdinalIgnoreCase));
        box.ItemKeys = shown.Concat(rest).ToList();
        box.SortMode = "";
    }

    /// <summary>解散（删除）格子：普通格子的图标回到自由区原位置附近；映射格子直接消失，不动任何文件。</summary>
    public void DissolveBox(BoxState box)
    {
        var placed = BoxOps.Dissolve(Layout, box.Id, Source.Items.Select(i => i.Key).ToList(), Grids);
        Log.Info($"解散格子 \"{box.Name}\"（{box.Kind}），{placed.Count} 个图标回到桌面");
        if (ActiveBox?.Id == box.Id) ActiveBox = null;
        AfterBoxChange();
    }

    /// <summary>把桌面项移进普通格子（只改布局，不动文件）。index = 移动前的插入位置，int.MaxValue = 末尾。</summary>
    public void MoveKeysToBox(IReadOnlyList<string> keys, BoxState box, int index)
    {
        if (box.Kind != BoxKind.Normal) return;
        var desktopKeys = keys.Where(k => _byKey.ContainsKey(k)).ToList();
        if (desktopKeys.Count == 0) return;
        MaterializeOrder(box);
        BoxOps.MoveToBox(Layout, desktopKeys, box, index);
        Layout.View.SortKey = "";
        ScheduleSave();
        ItemsChanged?.Invoke();
    }

    public void MoveItemsToBox(IReadOnlyList<DesktopItem> items, BoxState box) =>
        MoveKeysToBox(items.Select(i => i.Key).ToList(), box, int.MaxValue);

    /// <summary>把选中项移出普通格子，回到格子附近的自由区空位。</summary>
    public void MoveItemsOutOfBoxes(IReadOnlyList<DesktopItem> items)
    {
        foreach (var group in items.Select(i => i.Key).Where(k => BoxOfKey(k) != null).GroupBy(k => BoxOfKey(k)!))
        {
            var box = group.Key;
            var eff = EffectiveRect(box, ignoreCollapsed: true);
            if (eff == null) continue;
            var col = (int)Math.Floor(eff.Value.Rect.X / CellW + 1e-6);
            var row = (int)Math.Floor(eff.Value.Rect.Y / CellH + 1e-6);
            var keys = group.ToList();
            BoxOps.RemoveFromBoxes(Layout, keys);
            PlaceNear(keys, eff.Value.Grid.Name, col, row);
        }
        AfterBoxChange();
    }

    // ------------------------------------------------------------ 整理至新文件夹

    /// <summary>在这些路径所在的目录新建文件夹，并用 IFileOperation 把它们移进去；renameAfter 时新文件夹出现后进入重命名。</summary>
    public void MoveToNewFolder(IReadOnlyList<string> paths, bool renameAfter)
    {
        if (!MenuExtensions.InSameFolder(paths, out var dir)) return;
        try
        {
            var name = "新建文件夹";
            for (var i = 2; Directory.Exists(Path.Combine(dir, name)) || File.Exists(Path.Combine(dir, name)); i++) name = $"新建文件夹 ({i})";
            var target = Path.Combine(dir, name);
            Directory.CreateDirectory(target);
            Log.Info($"整理至新文件夹：{target}（{paths.Count} 项）");
            if (renameAfter) ExpectNewItem();

            var iidItem = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            var op = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"))!)!;
            try
            {
                op.SetOperationFlags(0x40 | 0x200 | 0x10); // ALLOWUNDO | NOCONFIRMMKDIR | NOCONFIRMATION
                op.SetOwnerWindow(ActiveHwnd);
                Win32.SHCreateItemFromParsingName(target, IntPtr.Zero, iidItem, out var dest);
                foreach (var p in paths)
                {
                    if (Win32.SHCreateItemFromParsingName(p, IntPtr.Zero, iidItem, out var src) < 0) continue;
                    op.MoveItem(src, dest, null, IntPtr.Zero);
                    Marshal.ReleaseComObject(src);
                }
                var hr = op.PerformOperations();
                Log.Info($"整理至新文件夹：PerformOperations hr=0x{hr:X}");
                Marshal.ReleaseComObject(dest);
            }
            finally { Marshal.ReleaseComObject(op); }
        }
        catch (Exception ex)
        {
            Log.Error("整理至新文件夹失败", ex);
        }
    }

    // ------------------------------------------------------------ 一键整理 / 设置

    /// <summary>最近一次整理是否还可以撤销（程序重启后仍有效）。</summary>
    public bool CanUndoOrganize => _organizeUndo != null;

    /// <summary>一键整理：自由区的项按规则归入同名格子（没有则新建）。只改布局，不动文件。返回移动的项数。</summary>
    public int OrganizeAll()
    {
        if (Grids.Count == 0) return 0;
        var plan = AutoOrganizer.Plan(Layout, Source.Items, Settings.OrganizeRules, Grids);
        var undo = AutoOrganizer.Apply(Layout, plan, DateTime.UtcNow);
        if (undo == null) { Log.Info("一键整理：没有可整理的项"); return 0; }
        _organizeUndo = undo;
        _undoStore.Save(undo);
        Layout.View.SortKey = "";
        Log.Info($"一键整理：{plan.MovedCount} 项，{plan.Boxes.Count} 个格子（新建 {undo.CreatedBoxIds.Count}）");
        AfterBoxChange();
        Flush();
        return plan.MovedCount;
    }

    /// <summary>撤销最近一次整理（只回滚那次新建的格子与移入的项）。</summary>
    public void UndoOrganize()
    {
        if (_organizeUndo == null) return;
        var n = AutoOrganizer.ApplyUndo(Layout, _organizeUndo, Source.Items.Select(i => i.Key).ToList(), Grids);
        Log.Info($"撤销整理：{n} 项回到自由区");
        _organizeUndo = null;
        _undoStore.Clear();
        AfterBoxChange();
        Flush();
    }

    /// <summary>保存并启用新的设置（设置窗口调用）。</summary>
    public void ApplySettings(AppSettings settings)
    {
        settings.Normalize();
        Settings = settings;
        _settingsStore.Save(settings);
        Log.SetRetention(settings.LogRetentionDays);
        PreviewBoxOpacity(settings.BoxOpacity);
        if (AppSettings.IconSizeOf(settings.IconSizeMode) is { } size) SetIconSize(size);
        else SyncFromSystemView();
    }

    // ------------------------------------------------------------ 拖放

    /// <summary>开始拖出选中项（SHDoDragDrop，阻塞到结束）。拖到本程序自己的桌面由 DesktopDropTarget 处理为改位置。</summary>
    public void StartDrag(string anchorKey, IntPtr hwnd)
    {
        if (!Selected.Contains(anchorKey)) SelectOnly(anchorKey);
        var anchorItem = ItemOf(anchorKey);
        if (anchorItem == null) return;
        // Shell 数据对象要求同一父文件夹：只拖与锚点同来源的项
        var items = SelectedItems.Where(i => i.Container == anchorItem.Container).ToList();
        if (items.Count == 0) return;
        if (items.Count != Selected.Count) SetSelection(items.Select(i => i.Key), anchorKey);
        DragKeys = items.Select(i => i.Key).ToList();
        DragAnchorKey = anchorKey;
        DragContainer = anchorItem.Container;
        try { ShellActions.DoDragDrop(items, hwnd); }
        finally
        {
            DragKeys = null;
            DragAnchorKey = null;
            DragContainer = "";
        }
    }

    public string? DragAnchorKey { get; private set; }

    /// <summary>被拖项的来源：空 = 桌面（自由区/普通格子）；否则为映射格子 Id。</summary>
    public string DragContainer { get; private set; } = "";

    // ------------------------------------------------------------ 剪切状态

    private void UpdateCutState()
    {
        var cut = ClipboardWatcher.GetCutPaths();
        if (cut.SetEquals(CutPaths)) return;
        CutPaths = cut;
        CutStateChanged?.Invoke();
    }

    public bool IsCut(DesktopItem item) => item.FilePath != null && CutPaths.Contains(item.FilePath);

    public void Dispose()
    {
        Flush();
        if (ShellContextMenu.CreateOverride == (Func<IReadOnlyList<DesktopItem>, IntPtr, IContextMenu?>)CreateMenuOverride)
            ShellContextMenu.CreateOverride = null;
        _clipboard.Dispose();
        foreach (var rt in _mapped.Values) rt.Source.Dispose();
        _mapped.Clear();
        Source.Dispose();
        Icons.Dispose();
    }
}
