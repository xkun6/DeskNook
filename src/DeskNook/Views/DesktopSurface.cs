using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskNook.Desktop;
using DeskNook.Model;
using DeskNook.Native;
using DeskNook.Services;

namespace DeskNook.Views;

/// <summary>
/// 某个显示器宿主窗口里的图标画布：只画属于本显示器的项，处理选择/框选/拖动/右键/键盘/重命名。
/// 状态都在 DesktopController，这里只负责渲染和转发输入。
/// </summary>
internal sealed class DesktopSurface : Canvas
{
    private readonly DesktopController _c;
    private readonly MonitorInfo _monitor;
    private readonly Dictionary<string, IconItemControl> _controls = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BoxControl> _boxes = new();
    private readonly List<Border> _guideLines = new();
    private Border? _ghost;
    private bool _ghostShown;
    private readonly Border _band;
    private IntPtr _hwnd;
    private bool _windowActive = true;

    // 鼠标状态
    private string? _pressKey;
    private Point _pressPoint;
    private bool _pressWasSelectedNoMod;
    private bool _banding;
    private Point _bandStart;
    private HashSet<string> _bandBase = new();
    private BoxControl? _bandBox;

    // 重命名
    private TextBox? _renameBox;
    private Canvas? _renameParent;
    private string? _renameKey;
    private bool _renameDone;

    public DesktopSurface(DesktopController controller, MonitorInfo monitor)
    {
        _c = controller;
        _monitor = monitor;
        Background = Brushes.Transparent; // 整块区域可命中（空白处框选 / 右键）
        Focusable = true;
        FocusVisualStyle = null;
        SnapsToDevicePixels = true;

        _band = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x55, 0x33, 0x99, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0x33, 0x99, 0xFF)),
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        SetZIndex(_band, 100);
        Children.Add(_band);

        _c.ItemsChanged += Rebuild;
        _c.SelectionChanged += UpdateSelection;
        _c.IconInvalidated += OnIconInvalidated;
        _c.Icons.Upgraded += OnIconUpgraded;
        _c.CutStateChanged += UpdateCut;
        _c.RenameRequested += OnRenameRequested;
        _c.BoxRenameRequested += OnBoxRenameRequested;
        _c.IconsVisibleChanged += OnIconsVisibleChanged;
        _c.AppearanceChanged += ApplyAppearance;
        _c.BoxGhostChanged += OnBoxGhost;
        Loaded += (_, _) => Rebuild();
        Unloaded += OnUnloaded;
    }

    public string MonitorName => _monitor.DeviceName;
    public MonitorInfo Monitor => _monitor;

    public void AttachWindow(IntPtr hwnd) => _hwnd = hwnd;

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    /// <summary>取消对控制器事件的订阅（宿主窗口关闭时调用）。</summary>
    public void Detach()
    {
        _c.ItemsChanged -= Rebuild;
        _c.SelectionChanged -= UpdateSelection;
        _c.IconInvalidated -= OnIconInvalidated;
        _c.Icons.Upgraded -= OnIconUpgraded;
        _c.CutStateChanged -= UpdateCut;
        _c.RenameRequested -= OnRenameRequested;
        _c.BoxRenameRequested -= OnBoxRenameRequested;
        _c.IconsVisibleChanged -= OnIconsVisibleChanged;
        _c.AppearanceChanged -= ApplyAppearance;
        _c.BoxGhostChanged -= OnBoxGhost;
    }

    /// <summary>“显示桌面图标”开关：只隐藏本程序画的图标和格子，画布本身仍可右键（弹桌面背景菜单）。</summary>
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(200));
    private int _fadeVersion;

    private void OnIconsVisibleChanged() => ApplyIconsVisible(animate: IsLoaded);

    private void ApplyAppearance()
    {
        foreach (var box in _boxes.Values) box.ApplyOpacity(_c.BoxOpacity);
    }

    /// <summary>应用显示状态：animate 时 200ms 淡入/淡出（对整个画布做不透明度动画），结束后再切 Visibility。</summary>
    private void ApplyIconsVisible(bool animate = false)
    {
        var show = _c.IconsVisible;
        var v = show ? Visibility.Visible : Visibility.Hidden;
        var version = ++_fadeVersion;
        if (!show) _c.ClearSelection();
        BeginAnimation(OpacityProperty, null);
        if (!animate)
        {
            Opacity = 1;
            SetChildrenVisibility(v);
            return;
        }
        if (show)
        {
            Opacity = 0;
            SetChildrenVisibility(v);
            BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, FadeDuration));
        }
        else
        {
            var anim = new System.Windows.Media.Animation.DoubleAnimation(1, 0, FadeDuration);
            anim.Completed += (_, _) =>
            {
                if (version != _fadeVersion) return; // 期间又切换了状态
                BeginAnimation(OpacityProperty, null);
                Opacity = 1;
                SetChildrenVisibility(Visibility.Hidden);
            };
            BeginAnimation(OpacityProperty, anim);
        }
    }

    private void SetChildrenVisibility(Visibility v)
    {
        foreach (var ctl in _controls.Values) ctl.Visibility = v;
        foreach (var box in _boxes.Values) box.Visibility = v;
    }

    /// <summary>窗口激活状态变化：选中项在失焦时变灰。</summary>
    public void SetWindowActive(bool active)
    {
        _windowActive = active;
        foreach (var c in _controls.Values) c.WindowActive = active;
    }

    // ------------------------------------------------------------ 布局与渲染

    private MonitorGrid? Grid => _c.GridOf(_monitor.DeviceName);

    /// <summary>格子(0,0) 左上角在本窗口中的 DIP 坐标（工作区原点）。</summary>
    private Point Origin => new((_monitor.Work.Left - _monitor.Bounds.Left) / _monitor.Scale,
                                (_monitor.Work.Top - _monitor.Bounds.Top) / _monitor.Scale);

    private Point CellToPoint(int col, int row) => new(Origin.X + col * _c.CellW, Origin.Y + row * _c.CellH);

    public (int Col, int Row) PointToCell(Point p)
    {
        var g = Grid;
        var col = (int)Math.Floor((p.X - Origin.X) / _c.CellW);
        var row = (int)Math.Floor((p.Y - Origin.Y) / _c.CellH);
        if (g == null) return (Math.Max(0, col), Math.Max(0, row));
        return (Math.Clamp(col, 0, g.Cols - 1), Math.Clamp(row, 0, g.Rows - 1));
    }

    private int _rebuildCount;

    private void Rebuild()
    {
        Log.Info($"桌面重建 #{++_rebuildCount}（{_monitor.DeviceName}）");
        var freeItems = _c.ItemsOn(_monitor.DeviceName).ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);
        var boxes = _c.BoxesOn(_monitor.DeviceName).ToList();
        var boxItems = boxes.ToDictionary(b => b.Id, b => _c.BoxItems(b));
        var wanted = new HashSet<string>(freeItems.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var list in boxItems.Values) foreach (var it in list) wanted.Add(it.Key);

        foreach (var key in _controls.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            Detach(_controls[key]);
            _controls.Remove(key);
        }
        foreach (var id in _boxes.Keys.Where(id => boxes.All(b => b.Id != id)).ToList())
        {
            Children.Remove(_boxes[id]);
            _boxes.Remove(id);
        }

        // 格子：位置/大小/标题 + 内部图标
        foreach (var box in boxes)
        {
            var eff = _c.EffectiveRect(box, ignoreCollapsed: true)!.Value;
            if (!_boxes.TryGetValue(box.Id, out var bc))
            {
                bc = new BoxControl(box, _c, this);
                _boxes[box.Id] = bc;
                Children.Add(bc);
            }
            bc.Bind(box, eff.Rect, Origin);
            var ctls = new List<IconItemControl>();
            var view = _c.ViewOf(box);
            foreach (var item in boxItems[box.Id])
                ctls.Add(BindItem(item, bc.Content, view));
            bc.SetItems(ctls);
        }

        // 自由区图标
        var freeView = new BoxView(_c.IconSize, _c.CellW, _c.CellH, false, false);
        foreach (var (key, item) in freeItems)
        {
            var slot = _c.SlotOf(key)!;
            var ctl = BindItem(item, this, freeView);
            var pt = CellToPoint(slot.Col, slot.Row);
            SetLeft(ctl, pt.X);
            SetTop(ctl, pt.Y);
        }
        if (!_c.IconsVisible) ApplyIconsVisible(animate: false);
    }

    private void Detach(IconItemControl ctl)
    {
        if (ctl.Parent is Canvas parent) parent.Children.Remove(ctl);
    }

    /// <summary>创建/复用图标控件并放进 parent（自由区为 Surface，格子内为其 Content）。</summary>
    private IconItemControl BindItem(DesktopItem item, Canvas parent, BoxView view)
    {
        if (!_controls.TryGetValue(item.Key, out var ctl))
        {
            ctl = new IconItemControl();
            _controls[item.Key] = ctl;
        }
        if (!ReferenceEquals(ctl.Parent, parent))
        {
            Detach(ctl);
            parent.Children.Add(ctl);
        }
        var px = (int)Math.Round(view.IconSize * _monitor.Scale);
        var changed = ctl.Item != item || ctl.IconPx != px || ctl.Horizontal != view.Horizontal || (!view.Horizontal && ctl.Width != view.CellW);
        ctl.Bind(item, view.IconSize, view.CellW, view.CellH, view.Horizontal);
        ctl.WindowActive = _windowActive;
        ctl.IsSelected = _c.Selected.Contains(item.Key);
        ctl.IsCut = _c.IsCut(item);
        if (changed || ctl.NeedsIcon) LoadIcon(ctl, item, px);
        return ctl;
    }

    private void LoadIcon(IconItemControl ctl, DesktopItem item, int px)
    {
        ctl.NeedsIcon = false;
        ctl.IconPx = px;
        var requested = Stopwatch.GetTimestamp();
        _c.Icons.Get(item, px, bmp =>
        {
            var iconMs = (long)Stopwatch.GetElapsedTime(requested).TotalMilliseconds;
            if (iconMs > 500) Log.Info($"图标加载耗时 {iconMs} ms：{item.Key}");
            if (bmp == null) { ctl.NeedsIcon = true; return; }
            if (_controls.TryGetValue(item.Key, out var cur) && cur == ctl) ctl.SetIcon(bmp);
        });
    }

    private void OnIconInvalidated(string? key)
    {
        var px = (int)Math.Round(_c.IconSize * _monitor.Scale);
        foreach (var (k, ctl) in _controls)
            if (key == null || string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                LoadIcon(ctl, ctl.Item, ctl.IconPx > 0 ? ctl.IconPx : px);
    }

    /// <summary>阶段 2 缩略图就绪：该 key 的控件仍是同一像素尺寸时换上新图。</summary>
    private void OnIconUpgraded(string key, int px)
    {
        if (_controls.TryGetValue(key, out var ctl) && ctl.IconPx == px && _c.Icons.TryGet(key, px) is { } bmp)
            ctl.SetIcon(bmp);
    }

    private void UpdateSelection()
    {
        foreach (var (k, ctl) in _controls) ctl.IsSelected = _c.Selected.Contains(k);
    }

    private void UpdateCut()
    {
        foreach (var ctl in _controls.Values) ctl.IsCut = _c.IsCut(ctl.Item);
    }

    // ------------------------------------------------------------ 命中

    private static IconItemControl? ItemAt(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is IconItemControl ic) return ic;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    public bool IsIconSource(DependencyObject? source) => ItemAt(source) != null;

    private static BoxControl? BoxAtSource(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is BoxControl bc) return bc;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    /// <summary>图标所在的格子控件；自由区图标为 null。</summary>
    private static BoxControl? BoxOfControl(IconItemControl ctl) => (ctl.Parent as Canvas)?.Parent as BoxControl;

    /// <summary>图标命中区在 Surface 坐标中的矩形。</summary>
    private Rect BoundsOf(IconItemControl ctl)
    {
        try { return ctl.TransformToAncestor(this).TransformBounds(ctl.HitBounds); }
        catch (InvalidOperationException) { return Rect.Empty; }
    }

    private Point ToLocal(Win32.POINT screen) =>
        new((screen.X - _monitor.Bounds.Left) / _monitor.Scale, (screen.Y - _monitor.Bounds.Top) / _monitor.Scale);

    private BoxControl? BoxAtLocal(Point local)
    {
        foreach (var bc in _boxes.Values.OrderByDescending(b => b.IsHoverExpanded))
            if (bc.OuterRect.Contains(local)) return bc;
        return null;
    }

    /// <summary>屏幕物理坐标下的格子（含标题栏）；没有返回 null。</summary>
    public BoxState? BoxAtScreenPoint(Win32.POINT screen) => BoxAtLocal(ToLocal(screen))?.Box;

    /// <summary>屏幕物理坐标下的图标 key（拖放用）：落在格子里只找该格子内的图标，否则只找自由区图标。</summary>
    public string? KeyAtScreenPoint(Win32.POINT screen)
    {
        var local = ToLocal(screen);
        var box = BoxAtLocal(local);
        if (box != null && !box.ViewportRect.Contains(local)) return null;
        foreach (var (k, ctl) in _controls)
        {
            if (!ctl.IsVisible || !ReferenceEquals(BoxOfControl(ctl), box)) continue;
            if (BoundsOf(ctl).Contains(local)) return k;
        }
        return null;
    }

    public (int Col, int Row) CellAtScreenPoint(Win32.POINT screen) => PointToCell(ToLocal(screen));

    /// <summary>拖到格子上时的插入位置（0..当前图标数）。</summary>
    public int InsertIndexAtScreenPoint(BoxState box, Win32.POINT screen) =>
        _boxes.TryGetValue(box.Id, out var bc) ? bc.InsertIndexAt(ToLocal(screen)) : int.MaxValue;

    /// <summary>拖放反馈：高亮某个格子，和/或在某个格子里显示插入位置指示线。</summary>
    public void SetDropFeedback(string? highlightBoxId, string? insertBoxId, int insertIndex)
    {
        foreach (var (id, bc) in _boxes)
            bc.SetDropFeedback(id == highlightBoxId, id == insertBoxId ? insertIndex : null);
    }

    // ------------------------------------------------------------ 供 BoxControl 使用

    /// <summary>本显示器工作区尺寸（DIP）。</summary>
    public Size WorkSize
    {
        get
        {
            var g = Grid;
            return g == null ? new Size(_monitor.Work.Width / _monitor.Scale, _monitor.Work.Height / _monitor.Scale)
                             : new Size(g.WorkWidth / g.Scale, g.WorkHeight / g.Scale);
        }
    }

    /// <summary>本显示器 DPI 缩放（DIP→物理像素）。</summary>
    public double Scale => _monitor.Scale;

    /// <summary>本显示器上其他格子的当前矩形（相对工作区）。</summary>
    public IReadOnlyList<BoxRect> OtherRects(string boxId) =>
        _c.BoxesOn(_monitor.DeviceName).Where(b => b.Id != boxId).Select(b => _c.EffectiveRect(b)!.Value.Rect).ToList();

    /// <summary>显示/更新/清除对齐辅助线。</summary>
    public void ShowGuides(IReadOnlyList<Guide> guides)
    {
        while (_guideLines.Count < guides.Count)
        {
            var line = new Border { Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x5A, 0xB0, 0xFF)), IsHitTestVisible = false };
            SetZIndex(line, 150);
            Children.Add(line);
            _guideLines.Add(line);
        }
        for (var i = 0; i < _guideLines.Count; i++)
        {
            var line = _guideLines[i];
            if (i >= guides.Count) { line.Visibility = Visibility.Collapsed; continue; }
            var g = guides[i];
            line.Visibility = Visibility.Visible;
            if (g.Vertical)
            {
                line.Width = 1; line.Height = Math.Max(1, g.To - g.From);
                SetLeft(line, Origin.X + g.Pos); SetTop(line, Origin.Y + g.From);
            }
            else
            {
                line.Height = 1; line.Width = Math.Max(1, g.To - g.From);
                SetLeft(line, Origin.X + g.From); SetTop(line, Origin.Y + g.Pos);
            }
        }
    }

    /// <summary>跨显示器拖动格子：目标是本屏时画预览框与辅助线，否则（或 null）清除本屏上的预览。</summary>
    private void OnBoxGhost(BoxGhost? g)
    {
        if (g == null || !string.Equals(g.Monitor, _monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            if (!_ghostShown) return;
            _ghostShown = false;
            _ghost!.Visibility = Visibility.Collapsed;
            ShowGuides(Array.Empty<Guide>());
            return;
        }
        if (_ghost == null)
        {
            _ghost = new Border
            {
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2), IsHitTestVisible = false,
                Background = new SolidColorBrush(Color.FromArgb(0x99, 0x1B, 0x20, 0x2A)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xE0, 0x5A, 0xB0, 0xFF)),
                Child = new TextBlock
                {
                    Foreground = Brushes.White, FontFamily = SystemFonts.MessageFontFamily, FontSize = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(11, 6, 11, 0), VerticalAlignment = VerticalAlignment.Top,
                },
            };
            SetZIndex(_ghost, 140);
            Children.Add(_ghost);
        }
        ((TextBlock)_ghost.Child).Text = g.Title;
        _ghost.Width = g.Rect.W;
        _ghost.Height = g.Rect.H;
        SetLeft(_ghost, Origin.X + g.Rect.X);
        SetTop(_ghost, Origin.Y + g.Rect.Y);
        _ghost.Visibility = Visibility.Visible;
        _ghostShown = true;
        ShowGuides(g.Guides);
    }

    public void RequestBoxMenu(BoxState box)
    {
        Win32.GetCursorPos(out var pt);
        _c.ActiveBox = box;
        _c.ShowBoxMenu(_hwnd, pt, _monitor.DeviceName, box);
    }

    public void FocusSurface() => BringToForeground();

    private void OnBoxRenameRequested(string boxId)
    {
        if (_boxes.TryGetValue(boxId, out var bc)) bc.BeginTitleEdit();
    }

    // ------------------------------------------------------------ 鼠标

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        BringToForeground();
        if (_renameBox != null) { Focus(); return; }

        var ctl = ItemAt(e.OriginalSource as DependencyObject);
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var pos = e.GetPosition(this);

        if (ctl != null)
        {
            var key = ctl.Item.Key;
            if (e.ClickCount == 2)
            {
                _pressKey = null;
                _c.SelectOnly(key);
                _c.OpenItems(new[] { ctl.Item });
                e.Handled = true;
                return;
            }
            _pressKey = key;
            _pressPoint = pos;
            _c.ActiveBox = BoxOfControl(ctl)?.Box;
            _pressWasSelectedNoMod = false;
            if (ctrl) _c.ToggleSelection(key);
            else if (shift) _c.SelectRange(key);
            else if (_c.Selected.Contains(key)) _pressWasSelectedNoMod = _c.Selected.Count > 1;
            else _c.SelectOnly(key);
            CaptureMouse();
            e.Handled = true;
            return;
        }

        // 自由区空白处双击：隐藏/显示全部图标与格子（格子内的空白不算）
        if (e.ClickCount == 2 && BoxAtSource(e.OriginalSource as DependencyObject) == null && _c.Settings.DoubleClickToggle)
        {
            _pressKey = null;
            _banding = false;
            _band.Visibility = Visibility.Collapsed;
            if (IsMouseCaptured) ReleaseMouseCapture();
            _c.SetIconsVisible(!_c.IconsVisible);
            e.Handled = true;
            return;
        }

        // 空白处（桌面或格子内）：开始框选（格子内只选该格子的图标）
        _pressKey = null;
        _bandBox = BoxAtSource(e.OriginalSource as DependencyObject);
        _c.ActiveBox = _bandBox?.Box;
        _banding = true;
        _bandStart = pos;
        _bandBase = ctrl ? new HashSet<string>(_c.Selected, StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!ctrl) _c.ClearSelection();
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);

        if (_banding)
        {
            var rect = new Rect(_bandStart, pos);
            SetLeft(_band, rect.X);
            SetTop(_band, rect.Y);
            _band.Width = rect.Width;
            _band.Height = rect.Height;
            _band.Visibility = Visibility.Visible;

            var hits = _controls.Values.Where(c =>
            {
                if (!c.IsVisible || !ReferenceEquals(BoxOfControl(c), _bandBox)) return false;
                var hb = BoundsOf(c);
                if (hb.IsEmpty || !hb.IntersectsWith(rect)) return false;
                return _bandBox == null || hb.IntersectsWith(_bandBox.ViewportRect);
            }).Select(c => c.Item.Key);
            _c.SetSelection(_bandBase.Concat(hits));
            return;
        }

        if (_pressKey != null &&
            (Math.Abs(pos.X - _pressPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
             Math.Abs(pos.Y - _pressPoint.Y) > SystemParameters.MinimumVerticalDragDistance))
        {
            var key = _pressKey;
            _pressKey = null;
            ReleaseMouseCapture();
            _c.ActiveHwnd = _hwnd;
            _c.StartDrag(key, _hwnd); // 阻塞到拖放结束
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_pressKey != null && _pressWasSelectedNoMod) _c.SelectOnly(_pressKey);
        _pressKey = null;
        if (_banding)
        {
            _banding = false;
            _band.Visibility = Visibility.Collapsed;
        }
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        BringToForeground();
        Focus();
        var ctl = ItemAt(e.OriginalSource as DependencyObject);
        if (ctl != null)
        {
            if (!_c.Selected.Contains(ctl.Item.Key)) _c.SelectOnly(ctl.Item.Key); // 右键未选中项：先只选中它
        }
        else
        {
            _c.ActiveBox = BoxAtSource(e.OriginalSource as DependencyObject)?.Box;
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) _c.ClearSelection();
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (_renameBox != null) return;
        var ctl = ItemAt(e.OriginalSource as DependencyObject);
        Win32.GetCursorPos(out var pt);
        if (ctl == null && BoxAtSource(e.OriginalSource as DependencyObject) is { } bc)
        {
            _c.ShowBoxMenu(_hwnd, pt, _monitor.DeviceName, bc.Box);
            e.Handled = true;
            return;
        }
        var items = ctl != null ? _c.SelectedItems : Array.Empty<DesktopItem>();
        _c.ShowMenu(_hwnd, pt, _monitor.DeviceName, items);
        e.Handled = true;
    }

    /// <summary>点击桌面时抢到前台（宿主窗口是 NOACTIVATE，需要显式获取键盘焦点）。</summary>
    private void BringToForeground()
    {
        _c.ActiveHwnd = _hwnd;
        if (Win32.GetForegroundWindow() != _hwnd)
        {
            var t0 = Stopwatch.GetTimestamp();
            var ok = Win32.SetForegroundWindow(_hwnd);
            var ms = (long)Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (ms > 100) Log.Info($"SetForegroundWindow 耗时 {ms} ms，返回 {ok}");
        }
        if (!IsKeyboardFocusWithin) Focus();
    }

    /// <summary>
    /// 同 <see cref="BringToForeground"/>，但 SetForegroundWindow 放到后台线程调用，完成后在 UI 线程执行 <paramref name="then"/>。
    /// 前台窗口属于别的线程且该线程正忙时（如新建后 Explorer 桌面线程在执行 DefView 新建并进入自身重命名），
    /// SetForegroundWindow 会阻塞到对方处理完失活，实测新建后 1.4~6 秒，同步调用会冻结 UI 线程。
    /// </summary>
    private void BringToForegroundThen(Action then)
    {
        _c.ActiveHwnd = _hwnd;
        if (Win32.GetForegroundWindow() == _hwnd) { then(); return; }
        var hwnd = _hwnd;
        var t0 = Stopwatch.GetTimestamp();
        Task.Run(() =>
        {
            try
            {
                var ok = Win32.SetForegroundWindow(hwnd);
                var ms = (long)Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                if (ms > 100) Log.Info($"SetForegroundWindow（后台线程）耗时 {ms} ms，返回 {ok}");
                Dispatcher.BeginInvoke(then);
            }
            catch (Exception ex)
            {
                Log.Info($"后台抢前台失败：{ex.Message}");
            }
        });
    }

    // ------------------------------------------------------------ 键盘

    /// <summary>由宿主窗口 PreviewKeyDown 转发。返回 true 表示已处理。</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (_renameBox != null || _boxes.Values.Any(b => b.IsEditingTitle)) return false;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        _c.ActiveHwnd = _hwnd;

        switch (key)
        {
            case Key.F2:
                if (_c.Selected.Count > 0) _c.BeginRename(_c.SelectedItems.First().Key);
                return true;
            case Key.Delete:
                _c.DeleteSelected(shift);
                return true;
            case Key.A when ctrl:
                _c.SelectAll();
                return true;
            case Key.C when ctrl:
                _c.CopySelected();
                return true;
            case Key.X when ctrl:
                _c.CutSelected();
                return true;
            case Key.Z when ctrl:
                _c.Undo();
                return true;
            case Key.V when ctrl:
                _c.Paste();
                return true;
            case Key.Enter when alt:
                _c.ShowProperties();
                return true;
            case Key.Enter:
                _c.OpenSelected();
                return true;
            case Key.F5:
                _c.Refresh();
                return true;
            case Key.Escape:
                _c.ClearSelection();
                return true;
            case Key.Left: _c.Navigate(NavDirection.Left, shift); return true;
            case Key.Right: _c.Navigate(NavDirection.Right, shift); return true;
            case Key.Up: _c.Navigate(NavDirection.Up, shift); return true;
            case Key.Down: _c.Navigate(NavDirection.Down, shift); return true;
        }
        return false;
    }

    // ------------------------------------------------------------ 重命名

    private void OnRenameRequested(string key)
    {
        var tStart = Stopwatch.GetTimestamp();
        if (!_controls.TryGetValue(key, out var ctl)) return;
        var posted = _c.RenamePostedAt;
        _c.RenamePostedAt = 0;
        var queueMs = posted == 0 ? -1 : (long)Stopwatch.GetElapsedTime(posted, tStart).TotalMilliseconds;
        EndRename(commit: false);

        var item = ctl.Item;
        var lb = ctl.LabelBounds;
        var horizontal = ctl.Horizontal;
        var left = horizontal ? GetLeft(ctl) + lb.X : GetLeft(ctl) + 1;
        var top = GetTop(ctl) + lb.Y - 1;
        var box = new TextBox
        {
            Text = item.EditName,
            FontFamily = ctl.FontFamily,
            FontSize = SystemFonts.MessageFontSize,
            Width = horizontal ? Math.Max(20, ctl.Width - lb.X - 2) : _c.CellW - 2,
            MinHeight = lb.Height + 2,
            TextWrapping = horizontal ? TextWrapping.NoWrap : TextWrapping.Wrap,
            TextAlignment = horizontal ? TextAlignment.Left : TextAlignment.Center,
            AcceptsReturn = false,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
        };
        SetLeft(box, left);
        SetTop(box, top);
        SetZIndex(box, 200);
        _renameBox = box;
        _renameParent = ctl.Parent as Canvas ?? this;
        _renameKey = key;
        _renameDone = false;
        _renameParent.Children.Add(box);

        box.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter) { EndRename(commit: true); ke.Handled = true; }
            else if (ke.Key == Key.Escape) { EndRename(commit: false); ke.Handled = true; }
        };

        var fg = Win32.GetForegroundWindow();
        Win32.GetWindowThreadProcessId(fg, out var fgPid);
        var queueText = queueMs < 0 ? "排队 -（非新建路径）ms" : $"排队 {queueMs} ms";
        Log.Info($"原位重命名框已显示：{key}（{queueText}，调用前前台=0x{fg.ToInt64():X}(进程Id {fgPid})）");

        // 拿到前台后才聚焦并挂失焦提交：抢前台过程中的焦点抖动不能触发提交
        BringToForegroundThen(() =>
        {
            if (_renameBox != box || _renameDone) return;
            box.LostKeyboardFocus += (_, _) => EndRename(commit: true);
            box.Focus();
            Keyboard.Focus(box);
            var name = item.EditName;
            var dot = name.LastIndexOf('.');
            if (!item.IsFolder && item.FilePath != null && dot > 0) box.Select(0, dot);
            else box.SelectAll();
            Log.Info($"原位重命名框获得焦点：{key} 键盘焦点={box.IsKeyboardFocused}（距显示 {(long)Stopwatch.GetElapsedTime(tStart).TotalMilliseconds} ms）");
        });
    }

    private void EndRename(bool commit)
    {
        var box = _renameBox;
        var key = _renameKey;
        if (box == null || key == null || _renameDone) return;
        _renameDone = true;
        _renameBox = null;
        _renameKey = null;
        var text = box.Text;
        (_renameParent ?? this).Children.Remove(box);
        _renameParent = null;
        Focus();
        if (commit) _c.CommitRename(key, text);
    }
}
