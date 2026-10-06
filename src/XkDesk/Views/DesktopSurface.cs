using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XkDesk.Desktop;
using XkDesk.Model;
using XkDesk.Native;
using XkDesk.Services;

namespace XkDesk.Views;

/// <summary>
/// 某个显示器宿主窗口里的图标画布：只画属于本显示器的项，处理选择/框选/拖动/右键/键盘/重命名。
/// 状态都在 DesktopController，这里只负责渲染和转发输入。
/// </summary>
internal sealed class DesktopSurface : Canvas
{
    private readonly DesktopController _c;
    private readonly MonitorInfo _monitor;
    private readonly Dictionary<string, IconItemControl> _controls = new(StringComparer.OrdinalIgnoreCase);
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

    // 重命名
    private TextBox? _renameBox;
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
        _c.CutStateChanged += UpdateCut;
        _c.RenameRequested += OnRenameRequested;
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
        _c.CutStateChanged -= UpdateCut;
        _c.RenameRequested -= OnRenameRequested;
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

    private void Rebuild()
    {
        var items = _c.ItemsOn(_monitor.DeviceName).ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var key in _controls.Keys.Where(k => !items.ContainsKey(k)).ToList())
        {
            Children.Remove(_controls[key]);
            _controls.Remove(key);
        }

        var px = (int)Math.Round(_c.IconSize * _monitor.Scale);
        foreach (var (key, item) in items)
        {
            var slot = _c.SlotOf(key)!;
            if (!_controls.TryGetValue(key, out var ctl))
            {
                ctl = new IconItemControl();
                _controls[key] = ctl;
                Children.Add(ctl);
            }
            var changed = ctl.Item != item || ctl.Width != _c.CellW;
            ctl.Bind(item, _c.IconSize, _c.CellW, _c.CellH);
            var pt = CellToPoint(slot.Col, slot.Row);
            SetLeft(ctl, pt.X);
            SetTop(ctl, pt.Y);
            ctl.WindowActive = _windowActive;
            ctl.IsSelected = _c.Selected.Contains(key);
            ctl.IsCut = _c.IsCut(item);
            if (changed || ctl.NeedsIcon) LoadIcon(ctl, item, px);
        }
    }

    private void LoadIcon(IconItemControl ctl, DesktopItem item, int px)
    {
        ctl.NeedsIcon = false;
        _c.Icons.Get(item, px, bmp =>
        {
            if (bmp == null) { ctl.NeedsIcon = true; return; }
            if (_controls.TryGetValue(item.Key, out var cur) && cur == ctl) ctl.SetIcon(bmp);
        });
    }

    private void OnIconInvalidated(string? key)
    {
        var px = (int)Math.Round(_c.IconSize * _monitor.Scale);
        foreach (var (k, ctl) in _controls)
            if (key == null || string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                LoadIcon(ctl, ctl.Item, px);
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

    private IconItemControl? ItemAt(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is IconItemControl ic) return ic;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    /// <summary>屏幕物理坐标下的图标 key（拖放用）。</summary>
    public string? KeyAtScreenPoint(Win32.POINT screen)
    {
        var local = new Point((screen.X - _monitor.Bounds.Left) / _monitor.Scale, (screen.Y - _monitor.Bounds.Top) / _monitor.Scale);
        foreach (var (k, ctl) in _controls)
        {
            var left = GetLeft(ctl);
            var top = GetTop(ctl);
            var hb = ctl.HitBounds;
            hb.Offset(left, top);
            if (hb.Contains(local)) return k;
        }
        return null;
    }

    public (int Col, int Row) CellAtScreenPoint(Win32.POINT screen) =>
        PointToCell(new Point((screen.X - _monitor.Bounds.Left) / _monitor.Scale, (screen.Y - _monitor.Bounds.Top) / _monitor.Scale));

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
            _pressWasSelectedNoMod = false;
            if (ctrl) _c.ToggleSelection(key);
            else if (shift) _c.SelectRange(key);
            else if (_c.Selected.Contains(key)) _pressWasSelectedNoMod = _c.Selected.Count > 1;
            else _c.SelectOnly(key);
            CaptureMouse();
            e.Handled = true;
            return;
        }

        // 空白处：开始框选
        _pressKey = null;
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
                var hb = c.HitBounds;
                hb.Offset(GetLeft(c), GetTop(c));
                return hb.IntersectsWith(rect);
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
        else if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _c.ClearSelection();
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (_renameBox != null) return;
        var ctl = ItemAt(e.OriginalSource as DependencyObject);
        Win32.GetCursorPos(out var pt);
        var items = ctl != null ? _c.SelectedItems : Array.Empty<DesktopItem>();
        _c.ShowMenu(_hwnd, pt, _monitor.DeviceName, items);
        e.Handled = true;
    }

    /// <summary>点击桌面时抢到前台（宿主窗口是 NOACTIVATE，需要显式获取键盘焦点）。</summary>
    private void BringToForeground()
    {
        _c.ActiveHwnd = _hwnd;
        if (Win32.GetForegroundWindow() != _hwnd) Win32.SetForegroundWindow(_hwnd);
        if (!IsKeyboardFocusWithin) Focus();
    }

    // ------------------------------------------------------------ 键盘

    /// <summary>由宿主窗口 PreviewKeyDown 转发。返回 true 表示已处理。</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (_renameBox != null) return false;
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
        if (!_controls.TryGetValue(key, out var ctl)) return;
        EndRename(commit: false);

        var item = ctl.Item;
        var lb = ctl.LabelBounds;
        var left = GetLeft(ctl) + 1;
        var top = GetTop(ctl) + lb.Y - 1;
        var box = new TextBox
        {
            Text = item.EditName,
            FontFamily = ctl.FontFamily,
            FontSize = SystemFonts.MessageFontSize,
            Width = _c.CellW - 2,
            MinHeight = lb.Height + 2,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            AcceptsReturn = false,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
        };
        SetLeft(box, left);
        SetTop(box, top);
        SetZIndex(box, 200);
        _renameBox = box;
        _renameKey = key;
        _renameDone = false;
        Children.Add(box);

        box.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter) { EndRename(commit: true); ke.Handled = true; }
            else if (ke.Key == Key.Escape) { EndRename(commit: false); ke.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => EndRename(commit: true);

        BringToForeground();
        box.Focus();
        Keyboard.Focus(box);
        var name = item.EditName;
        var dot = name.LastIndexOf('.');
        if (!item.IsFolder && item.FilePath != null && dot > 0) box.Select(0, dot);
        else box.SelectAll();
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
        Children.Remove(box);
        Focus();
        if (commit) _c.CommitRename(key, text);
    }
}
