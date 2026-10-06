using System.Windows.Threading;
using XkDesk.Model;
using XkDesk.Native;
using XkDesk.Services;

namespace XkDesk.Desktop;

internal enum NavDirection { Left, Right, Up, Down }

/// <summary>
/// 桌面图标区的中枢：桌面项集合、布局、选择、剪切状态，以及所有操作（打开/删除/重命名/移动…）。
/// 各显示器的 DesktopSurface 只负责画和转发输入。全部在 UI 线程。
/// </summary>
internal sealed class DesktopController : IDisposable
{
    private const double DefaultCellExtra = 27; // 系统间距 75 - 图标 48
    private static readonly TimeSpan NewItemWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DropWindow = TimeSpan.FromSeconds(10);

    private readonly Dispatcher _dispatcher;
    private readonly LayoutStore _store = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly ClipboardWatcher _clipboard;
    private Dictionary<string, DesktopItem> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private List<MonitorInfo> _monitors = new();
    private double _cellExtraDip = DefaultCellExtra;
    private double _cellExtraYDip = 52; // 系统间距 100 - 图标 48

    private DateTime _expectNewUntil = DateTime.MinValue;
    private (string Monitor, int Col, int Row, DateTime Until)? _pendingDrop;

    /// <summary>可撤销的最近一次操作名（“删除”“复制”“移动”“重命名”）；没有则为 null，此时菜单不显示“撤消”。</summary>
    public string? UndoLabel { get; private set; }
    private string? _pendingOp;
    private DateTime _pendingOpUntil;

    public LayoutState Layout { get; private set; } = new();
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

    /// <summary>本程序发起的拖拽中：被拖的 key；否则为 null。</summary>
    public IReadOnlyList<string>? DragKeys { get; private set; }

    /// <summary>项集合/位置/图标大小变化，需要整体刷新视图。</summary>
    public event Action? ItemsChanged;
    public event Action? SelectionChanged;
    /// <summary>某项（null=全部）图标失效，需要重新取图。</summary>
    public event Action<string?>? IconInvalidated;
    public event Action? CutStateChanged;
    public event Action<string>? RenameRequested;

    public DesktopController(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Layout = _store.Load();

        Source = new DesktopItemSource();
        Icons = new ShellIconCache(dispatcher);
        Source.Changed += OnSourceChanged;
        Source.IconInvalidated += key =>
        {
            Icons.Invalidate(key);
            IconInvalidated?.Invoke(key);
        };

        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            _saveTimer!.Stop();
            _store.Save(Layout);
        }, dispatcher);
        _saveTimer.Stop();

        _clipboard = new ClipboardWatcher();
        _clipboard.Changed += UpdateCutState;
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
        }
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
        UpdateCutState();
        ScheduleSave();
    }

    /// <summary>显示器布局变化后调用：重新计算网格并让越界项就近落位。</summary>
    public void UpdateMonitors()
    {
        _monitors = DesktopShell.GetMonitors();
        RebuildGrids();
        Reconcile();
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

    public MonitorGrid? GridOf(string monitor) => Grids.FirstOrDefault(g => g.Name == monitor);

    public IconSlot? SlotOf(string key) => Layout.FreeIcons.GetValueOrDefault(key);

    public DesktopItem? ItemOf(string key) => _byKey.GetValueOrDefault(key);

    public IEnumerable<DesktopItem> ItemsOn(string monitor) =>
        Source.Items.Where(i => Layout.FreeIcons.TryGetValue(i.Key, out var s) && s.Monitor == monitor);

    public IReadOnlyList<DesktopItem> SelectedItems =>
        Source.Items.Where(i => Selected.Contains(i.Key)).ToList();

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

        // 拖入：新项落在鼠标释放的格子
        if (_pendingDrop is { } drop && DateTime.UtcNow < drop.Until && diff.Added.Count > 0)
        {
            PlaceNear(diff.Added.Select(a => a.Key).ToList(), drop.Monitor, drop.Col, drop.Row);
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
            _dispatcher.BeginInvoke(DispatcherPriority.Background, () => RenameRequested?.Invoke(renameKey));
    }

    /// <summary>用户在“新建”菜单里点了命令：数秒内出现的新项自动选中并重命名。</summary>
    public void ExpectNewItem() => _expectNewUntil = DateTime.UtcNow + NewItemWindow;

    /// <summary>拖入（外部）释放位置：随后出现的新项落在这里。</summary>
    public void SetPendingDrop(string monitor, int col, int row) =>
        _pendingDrop = (monitor, col, row, DateTime.UtcNow + DropWindow);

    public void Refresh()
    {
        Icons.Invalidate(null);
        IconInvalidated?.Invoke(null);
        Source.Refresh();
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

    /// <summary>Shift+点击：选中锚点与目标之间矩形区域内的项。</summary>
    public void SelectRange(string key)
    {
        var anchor = AnchorKey != null ? SlotOf(AnchorKey) : null;
        var target = SlotOf(key);
        if (anchor == null || target == null || anchor.Monitor != target.Monitor)
        {
            SelectOnly(key);
            return;
        }
        int c1 = Math.Min(anchor.Col, target.Col), c2 = Math.Max(anchor.Col, target.Col);
        int r1 = Math.Min(anchor.Row, target.Row), r2 = Math.Max(anchor.Row, target.Row);
        var keys = Source.Items
            .Where(i => Layout.FreeIcons.TryGetValue(i.Key, out var s) && s.Monitor == target.Monitor &&
                        s.Col >= c1 && s.Col <= c2 && s.Row >= r1 && s.Row <= r2)
            .Select(i => i.Key);
        SetSelection(keys); // 保持锚点
    }

    /// <summary>方向键：移动选择到该方向最近的项。</summary>
    public void Navigate(NavDirection dir, bool extend)
    {
        var withSlot = Source.Items.Where(i => Layout.FreeIcons.ContainsKey(i.Key)).ToList();
        if (withSlot.Count == 0) return;
        var from = (AnchorKey != null ? ItemOf(AnchorKey) : null) ?? SelectedItems.FirstOrDefault();
        if (from == null || !Layout.FreeIcons.TryGetValue(from.Key, out var cur))
        {
            var first = withSlot.OrderBy(i => Layout.FreeIcons[i.Key].Monitor != Grids.FirstOrDefault()?.Name)
                .ThenBy(i => Layout.FreeIcons[i.Key].Col).ThenBy(i => Layout.FreeIcons[i.Key].Row).First();
            SelectOnly(first.Key);
            return;
        }

        string? best = null;
        var bestScore = double.MaxValue;
        foreach (var it in withSlot)
        {
            if (it.Key == from.Key) continue;
            var s = Layout.FreeIcons[it.Key];
            if (s.Monitor != cur.Monitor) continue;
            int dc = s.Col - cur.Col, dr = s.Row - cur.Row;
            var ok = dir switch { NavDirection.Left => dc < 0, NavDirection.Right => dc > 0, NavDirection.Up => dr < 0, _ => dr > 0 };
            if (!ok) continue;
            var score = dir is NavDirection.Left or NavDirection.Right
                ? Math.Abs(dc) * 1000 + Math.Abs(dr)
                : Math.Abs(dr) * 1000 + Math.Abs(dc);
            if (score < bestScore) { bestScore = score; best = it.Key; }
        }
        if (best == null) return;
        if (extend) { Selected.Add(best); AnchorKey = best; SelectionChanged?.Invoke(); }
        else SelectOnly(best);
    }

    // ------------------------------------------------------------ Shell 操作

    public void OpenSelected() => OpenItems(SelectedItems);

    public void OpenItems(IReadOnlyList<DesktopItem> items) => ShellContextMenu.InvokeDefault(items, ActiveHwnd);

    public void DeleteSelected(bool permanent)
    {
        var items = SelectedItems;
        if (items.Count == 0) return;
        if (!permanent) ArmUndo("删除");
        ShellContextMenu.InvokeVerb(items, "delete", ActiveHwnd, shift: permanent);
    }

    public void CopySelected() { var i = SelectedItems; if (i.Count > 0) ShellContextMenu.InvokeVerb(i, "copy", ActiveHwnd); }
    public void CutSelected() { var i = SelectedItems; if (i.Count > 0) ShellContextMenu.InvokeVerb(i, "cut", ActiveHwnd); }
    public void Paste()
    {
        ArmUndo(ClipboardWatcher.GetCutPaths().Count > 0 ? "移动" : "复制");
        ShellContextMenu.InvokeVerb(Array.Empty<DesktopItem>(), "paste", ActiveHwnd);
    }

    public void PasteShortcut()
    {
        ArmUndo("创建快捷方式");
        ShellContextMenu.InvokeVerb(Array.Empty<DesktopItem>(), "pastelink", ActiveHwnd);
    }

    private void ArmUndo(string op)
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
    public void ShowProperties() => ShellContextMenu.InvokeVerb(SelectedItems, "properties", ActiveHwnd);

    public void ShowMenu(IntPtr hwnd, Win32.POINT screenPoint, string monitor, IReadOnlyList<DesktopItem> items)
    {
        ActiveHwnd = hwnd;
        var ctx = new MenuContext
        {
            Controller = this, Items = items, Hwnd = hwnd, ScreenPoint = screenPoint, Monitor = monitor,
            Shift = (Win32.GetKeyState(Win32.VK_SHIFT) & 0x8000) != 0,
        };
        ShellContextMenu.Show(ctx);
    }

    // ------------------------------------------------------------ 重命名

    public void BeginRename(string key)
    {
        if (!_byKey.ContainsKey(key)) return;
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

        var newKey = res.Value.Key;
        UndoLabel = "重命名";
        Source.AddRenameHint(key, newKey);
        LayoutReconciler.Rename(Layout, key, newKey);
        Selected.Remove(key);
        Selected.Add(newKey);
        AnchorKey = newKey;
        Source.Refresh();
        ScheduleSave();
        ItemsChanged?.Invoke();
        SelectionChanged?.Invoke();
        return true;
    }

    // ------------------------------------------------------------ 位置 / 视图

    private HashSet<(int, int)> Occupied(string monitor, ICollection<string>? exclude = null) =>
        Source.Items
            .Where(i => (exclude == null || !exclude.Contains(i.Key)) &&
                        Layout.FreeIcons.TryGetValue(i.Key, out var s) && s.Monitor == monitor)
            .Select(i => (Layout.FreeIcons[i.Key].Col, Layout.FreeIcons[i.Key].Row))
            .ToHashSet();

    /// <summary>把一组项依次放到目标格附近的空位。</summary>
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
    /// 把选中项一起移动：锚点项落到 (col,row)，其余项保持相对位置；目标被占用或越界则就近找空位。
    /// </summary>
    public void MoveSelection(string anchorKey, string monitor, int col, int row)
    {
        var grid = GridOf(monitor);
        var anchor = SlotOf(anchorKey);
        if (grid == null || anchor == null) return;

        var moving = SelectedItems.Where(i => Layout.FreeIcons.ContainsKey(i.Key)).Select(i => i.Key).ToList();
        if (!moving.Contains(anchorKey)) moving = new List<string> { anchorKey };
        int dCol = col - anchor.Col, dRow = row - anchor.Row;
        var origin = moving.ToDictionary(k => k, k => (Layout.FreeIcons[k].Col, Layout.FreeIcons[k].Row), StringComparer.OrdinalIgnoreCase);

        var occupied = Occupied(monitor, moving);
        foreach (var k in moving.OrderBy(k => origin[k].Col).ThenBy(k => origin[k].Row))
        {
            var (c, r) = (origin[k].Col + dCol, origin[k].Row + dRow);
            if (c < 0 || r < 0 || c >= grid.Cols || r >= grid.Rows || occupied.Contains((c, r)))
                (c, r) = GridLayout.NearestEmpty(grid.Size, occupied, c, r);
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

    /// <summary>按指定方式重新排列：每个显示器内的项按排序结果从左上角按列依次填充。</summary>
    public void SortBy(string key)
    {
        Layout.View.SortKey = key;
        foreach (var grid in Grids)
        {
            var items = ItemsOn(grid.Name).ToList();
            var sorted = Sort(items, key).ToList();
            var occupied = new HashSet<(int, int)>();
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

    private static IEnumerable<DesktopItem> Sort(List<DesktopItem> items, string key)
    {
        // 虚拟项（此电脑、回收站…）始终在最前，保持枚举顺序
        var virtuals = items.Where(i => i.IsVirtual);
        var real = items.Where(i => !i.IsVirtual);
        IEnumerable<DesktopItem> sorted = key switch
        {
            "size" => real.OrderBy(i => i.IsFolder ? 0 : 1).ThenBy(i => i.Size).ThenBy(i => i.DisplayName, Comparer<string>.Create(Win32.StrCmpLogicalW)),
            "type" => real.OrderBy(i => i.IsFolder ? "" : i.Extension).ThenBy(i => i.DisplayName, Comparer<string>.Create(Win32.StrCmpLogicalW)),
            "date" => real.OrderByDescending(i => i.Modified).ThenBy(i => i.DisplayName, Comparer<string>.Create(Win32.StrCmpLogicalW)),
            _ => real.OrderBy(i => i.IsFolder ? 0 : 1).ThenBy(i => i.DisplayName, Comparer<string>.Create(Win32.StrCmpLogicalW)),
        };
        return virtuals.Concat(sorted);
    }

    // ------------------------------------------------------------ 拖放

    /// <summary>开始拖出选中项（SHDoDragDrop，阻塞到结束）。拖到本程序自己的桌面由 DesktopDropTarget 处理为改位置。</summary>
    public void StartDrag(string anchorKey, IntPtr hwnd)
    {
        if (!Selected.Contains(anchorKey)) SelectOnly(anchorKey);
        var items = SelectedItems;
        if (items.Count == 0) return;
        DragKeys = items.Select(i => i.Key).ToList();
        DragAnchorKey = anchorKey;
        try { ShellActions.DoDragDrop(items, hwnd); }
        finally
        {
            DragKeys = null;
            DragAnchorKey = null;
        }
    }

    public string? DragAnchorKey { get; private set; }

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
        _clipboard.Dispose();
        Source.Dispose();
        Icons.Dispose();
    }
}
