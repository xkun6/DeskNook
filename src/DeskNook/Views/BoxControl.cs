using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskNook.Desktop;
using DeskNook.Model;
using DeskNook.Native;
using DeskNook.Services;

namespace DeskNook.Views;

/// <summary>
/// 桌面格子的外观与交互：半透明圆角面板 + 标题栏 + 内容区（图标网格、细滚动条）。
/// 标题栏拖动移动、边缘/角缩放（吸附逻辑在 BoxGeometry）、双击标题重命名、折叠按钮、悬停展开。
/// 图标控件本身由 DesktopSurface 创建并交给本控件排布。
/// </summary>
internal sealed class BoxControl : Canvas
{
    private Brush _bgNormal = Brushes.Transparent;
    private Brush _bgHover = Brushes.Transparent;
    private bool _chromeReady;
    private Brush BgNormal => _bgNormal;
    private Brush BgHover => _bgHover;

    /// <summary>按设置的格子透明度（背景不透明度 0.2~1.0）重建背景画刷；悬停时略深。</summary>
    public void ApplyOpacity(double opacity)
    {
        var a = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        var h = (byte)Math.Min(255, a + 0x18);
        _bgNormal = Frozen(new SolidColorBrush(Color.FromArgb(a, 0x1B, 0x20, 0x2A)));
        _bgHover = Frozen(new SolidColorBrush(Color.FromArgb(h, 0x26, 0x2C, 0x3A)));
        if (_chromeReady) UpdateChrome();
    }
    private static readonly Brush BorderNormal = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush BorderHover = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush BorderDrop = Frozen(new SolidColorBrush(Color.FromArgb(0xE0, 0x5A, 0xB0, 0xFF)));
    private static readonly Brush TitleLine = Frozen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush ButtonHover = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush ThumbBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush InsertBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xFF, 0x5A, 0xB0, 0xFF)));
    private static readonly FontFamily IconFont = new("Segoe MDL2 Assets");

    private const double EdgeSize = 6;
    private const double ButtonSize = 24;

    private readonly DesktopController _c;
    private readonly DesktopSurface _surface;
    private readonly Border _bg;
    private readonly Border _titleLine;
    private readonly TextBlock _title;
    private readonly TextBlock _lock;
    private readonly Border _collapseBtn;
    private readonly TextBlock _collapseGlyph;
    private readonly Border _menuBtn;
    private readonly Canvas _content;
    private readonly Border _thumb;
    private readonly Border _insert;
    private readonly DispatcherTimer _leaveTimer;

    private IReadOnlyList<IconItemControl> _controls = Array.Empty<IconItemControl>();
    private BoxRect _rect = new();          // 展开状态下的有效矩形（相对工作区，DIP）
    private BoxRect? _preview;              // 拖动/缩放过程中的实时矩形
    private bool _hover, _hoverExpanded, _dropHighlight;
    private double _scroll;

    private enum Mode { None, Move, Resize, Thumb }
    private Mode _mode;
    private Point _startMouse;
    private BoxRect _startRect = new();
    private ResizeEdge _edge;
    private double _thumbGrab;
    private IReadOnlyList<BoxRect> _dragOthers = Array.Empty<BoxRect>();
    private Size _dragWork;
    private Point? _pendingPos;              // 合帧：每帧最多处理一次最新鼠标位置
    private bool _renderHooked;

    private TextBox? _titleEdit;
    private bool _titleEditDone;

    public BoxState Box { get; private set; }

    /// <summary>图标控件的容器（图标坐标相对内容区左上角）。</summary>
    public Canvas Content => _content;

    public BoxControl(BoxState box, DesktopController controller, DesktopSurface surface)
    {
        Box = box;
        _c = controller;
        _surface = surface;
        SnapsToDevicePixels = true;
        Background = Brushes.Transparent; // 整个外接矩形可命中（圆角之外的角落也要能拖拽缩放）

        ApplyOpacity(controller.BoxOpacity);
        _bg = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Background = BgNormal, BorderBrush = BorderNormal };
        Children.Add(_bg);
        _chromeReady = true;

        _titleLine = new Border { Height = 1, Background = TitleLine, IsHitTestVisible = false };
        SetTop(_titleLine, BoxGeometry.TitleH - 1);
        Children.Add(_titleLine);

        _content = new Canvas { ClipToBounds = true, Background = null };
        SetTop(_content, BoxGeometry.TitleH);
        Children.Add(_content);

        _title = new TextBlock
        {
            Foreground = Brushes.White, FontFamily = SystemFonts.MessageFontFamily, FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false,
        };
        SetLeft(_title, 12);
        SetTop(_title, 7);
        Children.Add(_title);

        _lock = new TextBlock
        {
            Text = "", FontFamily = IconFont, FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
            IsHitTestVisible = false, Visibility = Visibility.Collapsed,
        };
        SetTop(_lock, 10);
        Children.Add(_lock);

        (_collapseBtn, _collapseGlyph) = MakeButton("");
        _collapseBtn.MouseLeftButtonUp += (_, e) => { e.Handled = true; _c.ToggleBoxCollapsed(Box); };
        Children.Add(_collapseBtn);
        (_menuBtn, _) = MakeButton("");
        _menuBtn.MouseLeftButtonUp += (_, e) => { e.Handled = true; _surface.RequestBoxMenu(Box); };
        Children.Add(_menuBtn);

        _thumb = new Border { Width = 4, CornerRadius = new CornerRadius(2), Background = ThumbBrush, Visibility = Visibility.Collapsed, Cursor = Cursors.Arrow };
        _thumb.MouseLeftButtonDown += OnThumbDown;
        Children.Add(_thumb);

        _insert = new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Background = InsertBrush, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        Children.Add(_insert);

        _leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _leaveTimer.Tick += (_, _) =>
        {
            _leaveTimer.Stop();
            if (!IsMouseOver && _mode == Mode.None && _hoverExpanded) { _hoverExpanded = false; Relayout(); }
        };

        MouseEnter += (_, _) => { _hover = true; _leaveTimer.Stop(); if (Box.Collapsed && !_hoverExpanded) { _hoverExpanded = true; Relayout(); } UpdateChrome(); };
        MouseLeave += (_, _) => { _hover = false; UpdateChrome(); if (_hoverExpanded && _mode == Mode.None) _leaveTimer.Start(); };
    }

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    private (Border, TextBlock) MakeButton(string glyph)
    {
        var g = new TextBlock
        {
            Text = glyph, FontFamily = IconFont, FontSize = 12, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
        };
        var b = new Border { Width = ButtonSize, Height = ButtonSize, CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, Child = g };
        SetTop(b, 4);
        b.MouseEnter += (_, _) => b.Background = ButtonHover;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        return (b, g);
    }

    // ------------------------------------------------------------ 布局

    /// <summary>当前显示的矩形（相对工作区）：拖动中取预览；折叠且未悬停展开时只有标题栏高度。</summary>
    public BoxRect DisplayRect
    {
        get
        {
            var r = _preview ?? _rect;
            var h = IsCollapsedNow ? BoxGeometry.TitleH : r.H;
            return new BoxRect(r.X, r.Y, r.W, h);
        }
    }

    private bool IsCollapsedNow => Box.Collapsed && !_hoverExpanded && _mode == Mode.None;

    public bool IsHoverExpanded => _hoverExpanded;
    public bool IsEditingTitle => _titleEdit != null;
    public bool IsLayoutOnlyDragging => _mode != Mode.None;

    /// <summary>刷新绑定：rect 为展开状态下的有效矩形，origin 为工作区原点在窗口中的 DIP 位置。</summary>
    public void Bind(BoxState box, BoxRect rect, Point origin)
    {
        Box = box;
        if (_mode == Mode.None) { _rect = rect.Clone(); _preview = null; }
        if (!Box.Collapsed) _hoverExpanded = false;
        _surfaceOrigin = origin;
        Relayout();
    }

    private Point _surfaceOrigin;

    private void Relayout()
    {
        var r = DisplayRect;
        SetLeft(this, _surfaceOrigin.X + r.X);
        SetTop(this, _surfaceOrigin.Y + r.Y);
        Width = r.W;
        Height = r.H;
        _bg.Width = r.W;
        _bg.Height = r.H;
        _titleLine.Width = Math.Max(0, r.W - 16);
        SetLeft(_titleLine, 8);
        var collapsedNow = IsCollapsedNow;
        _titleLine.Visibility = collapsedNow ? Visibility.Collapsed : Visibility.Visible;

        _title.Text = Box.Name;
        var btnRight = r.W - 6;
        SetLeft(_menuBtn, btnRight - ButtonSize);
        SetLeft(_collapseBtn, btnRight - ButtonSize * 2 - 2);
        _collapseGlyph.Text = Box.Collapsed ? "" : "";
        _title.MaxWidth = Math.Max(20, r.W - 12 - ButtonSize * 2 - 22);
        _lock.Visibility = Box.Locked ? Visibility.Visible : Visibility.Collapsed;
        if (_mode != Mode.Move)   // 移动时标题宽度不变
        {
            _title.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            SetLeft(_lock, 12 + Math.Min(_title.DesiredSize.Width, _title.MaxWidth) + 6);
        }

        _content.Visibility = collapsedNow ? Visibility.Collapsed : Visibility.Visible;
        var vh = Math.Max(0, r.H - BoxGeometry.ChromeH);
        _content.Width = r.W;
        _content.Height = vh;
        LayoutItems();
        if (_titleEdit != null) _titleEdit.Width = Math.Max(40, r.W - 12 - ButtonSize * 2 - 20);
        UpdateChrome();
        Panel.SetZIndex(this, _hoverExpanded || _mode != Mode.None ? 50 : 20);
    }

    private void UpdateChrome()
    {
        _bg.Background = _hover || _mode != Mode.None ? BgHover : BgNormal;
        _bg.BorderBrush = _dropHighlight ? BorderDrop : _hover || _mode != Mode.None ? BorderHover : BorderNormal;
        _bg.BorderThickness = new Thickness(_dropHighlight ? 2 : 1);
    }

    /// <summary>设置格子内按顺序排列的图标控件（控件已由 Surface 放进 Content）。</summary>
    public void SetItems(IReadOnlyList<IconItemControl> controls)
    {
        _controls = controls;
        LayoutItems();
    }

    private int Cols => BoxGeometry.Cols(DisplayRect.W, _c.CellW);
    private double OffsetX => (DisplayRect.W - Cols * _c.CellW) / 2;

    private double ViewportH => Math.Max(0, DisplayRect.H - BoxGeometry.ChromeH);

    private double MaxScroll => Math.Max(0, BoxGeometry.ContentRows(_controls.Count, Cols) * _c.CellH - ViewportH);

    private void LayoutItems()
    {
        var cols = Cols;
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        var offX = OffsetX;
        if (_mode == Mode.Move) { UpdateThumb(); return; }   // 移动时图标相对格子不变
        for (var i = 0; i < _controls.Count; i++)
        {
            var (c, r) = BoxGeometry.CellOfIndex(i, cols);
            SetLeft(_controls[i], offX + c * _c.CellW);
            SetTop(_controls[i], r * _c.CellH - _scroll);
        }
        UpdateThumb();
    }

    private void UpdateThumb()
    {
        var max = MaxScroll;
        if (max <= 0 || IsCollapsedNow) { _thumb.Visibility = Visibility.Collapsed; return; }
        var vh = ViewportH;
        var total = vh + max;
        var h = Math.Max(24, vh * vh / total);
        _thumb.Height = h;
        SetLeft(_thumb, DisplayRect.W - 7);
        SetTop(_thumb, BoxGeometry.TitleH + (vh - h) * (_scroll / max));
        _thumb.Visibility = Visibility.Visible;
    }

    public void ScrollBy(double delta)
    {
        var old = _scroll;
        _scroll = Math.Clamp(_scroll + delta, 0, MaxScroll);
        if (_scroll != old) LayoutItems();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (IsCollapsedNow || MaxScroll <= 0) return;
        ScrollBy(-e.Delta / 120.0 * 60);
        e.Handled = true;
    }

    // ------------------------------------------------------------ 供 Surface/拖放使用

    /// <summary>内容区在窗口（Surface）坐标中的可视矩形。</summary>
    public Rect ViewportRect
    {
        get
        {
            var r = DisplayRect;
            return new Rect(_surfaceOrigin.X + r.X, _surfaceOrigin.Y + r.Y + BoxGeometry.TitleH, r.W, ViewportH);
        }
    }

    /// <summary>整个格子在窗口坐标中的矩形（含标题栏）。</summary>
    public Rect OuterRect
    {
        get
        {
            var r = DisplayRect;
            return new Rect(_surfaceOrigin.X + r.X, _surfaceOrigin.Y + r.Y, r.W, r.H);
        }
    }

    /// <summary>窗口坐标 → 插入位置（0..count）。</summary>
    public int InsertIndexAt(Point surfacePoint)
    {
        var vp = ViewportRect;
        var x = surfacePoint.X - vp.X - OffsetX;
        var y = surfacePoint.Y - vp.Y + _scroll;
        return BoxGeometry.InsertIndexAt(x, y, Cols, _c.CellW, _c.CellH, _controls.Count);
    }

    public void ShowInsert(int index)
    {
        var (c, r) = BoxGeometry.CellOfIndex(index, Cols);
        var x = OffsetX + c * _c.CellW - 2;
        var y = r * _c.CellH - _scroll + 6;
        if (y + _c.CellH - 12 < 0 || y > ViewportH) { _insert.Visibility = Visibility.Collapsed; return; }
        _insert.Height = _c.CellH - 12;
        SetLeft(_insert, x);
        SetTop(_insert, BoxGeometry.TitleH + y);
        Panel.SetZIndex(_insert, 30);
        _insert.Visibility = Visibility.Visible;
    }

    public void SetDropFeedback(bool highlight, int? insertIndex)
    {
        if (_dropHighlight != highlight) { _dropHighlight = highlight; UpdateChrome(); }
        if (insertIndex is { } i) ShowInsert(i);
        else _insert.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------ 鼠标

    private ResizeEdge HitEdge(Point p)
    {
        if (Box.Locked || Box.Collapsed) return ResizeEdge.None;
        var r = DisplayRect;
        var e = ResizeEdge.None;
        if (p.X <= EdgeSize) e |= ResizeEdge.Left;
        else if (p.X >= r.W - EdgeSize) e |= ResizeEdge.Right;
        if (p.Y <= EdgeSize - 2) e |= ResizeEdge.Top;
        else if (p.Y >= r.H - EdgeSize) e |= ResizeEdge.Bottom;
        return e;
    }

    private static Cursor CursorFor(ResizeEdge e) => e switch
    {
        ResizeEdge.Left or ResizeEdge.Right => Cursors.SizeWE,
        ResizeEdge.Top or ResizeEdge.Bottom => Cursors.SizeNS,
        ResizeEdge.Left | ResizeEdge.Top or ResizeEdge.Right | ResizeEdge.Bottom => Cursors.SizeNWSE,
        ResizeEdge.Right | ResizeEdge.Top or ResizeEdge.Left | ResizeEdge.Bottom => Cursors.SizeNESW,
        _ => Cursors.Arrow,
    };

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_titleEdit != null) return;
        if (_surface.IsIconSource(e.OriginalSource as DependencyObject)) return; // 图标点击交给 Surface
        if (IsOnButton(e.OriginalSource as DependencyObject)) { _c.ActiveBox = Box; _surface.FocusSurface(); e.Handled = true; return; } // 标题栏按钮：抬起时才触发
        _c.ActiveBox = Box;
        var p = e.GetPosition(this);

        var edge = HitEdge(p);
        if (edge != ResizeEdge.None)
        {
            BeginDrag(Mode.Resize, e);
            _edge = edge;
            e.Handled = true;
            return;
        }

        if (p.Y < BoxGeometry.TitleH)
        {
            _surface.FocusSurface();
            if (e.ClickCount == 2) { BeginTitleEdit(); e.Handled = true; return; }
            if (!Box.Locked && !Box.Collapsed) BeginDrag(Mode.Move, e);
            e.Handled = true;
        }
        // 内容区空白处：不处理，交给 Surface（框选/清空选择）
    }

    private bool IsOnButton(DependencyObject? source)
    {
        while (source != null && !ReferenceEquals(source, this))
        {
            if (ReferenceEquals(source, _collapseBtn) || ReferenceEquals(source, _menuBtn)) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void BeginDrag(Mode mode, MouseButtonEventArgs e)
    {
        _mode = mode;
        _startMouse = e.GetPosition(_surface);
        _startRect = _rect.Clone();
        _preview = _rect.Clone();
        _dragWork = _surface.WorkSize;
        _dragOthers = _surface.OtherRects(Box.Id);
        _pendingPos = null;
        if (!_renderHooked) { CompositionTarget.Rendering += OnRendering; _renderHooked = true; }
        _surface.FocusSurface();
        CaptureMouse();
        UpdateChrome();
        Panel.SetZIndex(this, 50);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_mode == Mode.None)
        {
            if (!IsMouseCaptured && _titleEdit == null) Cursor = CursorFor(HitEdge(e.GetPosition(this)));
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(commit: false); return; }

        if (_mode == Mode.Thumb) { DragThumb(e.GetPosition(this)); return; }
        _pendingPos = e.GetPosition(_surface);   // 下一帧渲染时统一处理最新位置
    }

    private void OnRendering(object? sender, EventArgs e) => ProcessPending();

    private void ProcessPending()
    {
        if (_pendingPos is not { } pos || _mode is not (Mode.Move or Mode.Resize)) return;
        _pendingPos = null;
        double dx = pos.X - _startMouse.X, dy = pos.Y - _startMouse.Y;
        var scale = _surface.Scale;
        SnapResult res = _mode == Mode.Move
            ? BoxGeometry.SnapMove(new BoxRect(_startRect.X + dx, _startRect.Y + dy, _startRect.W, _startRect.H), _dragOthers, _dragWork.Width, _dragWork.Height, scale)
            : BoxGeometry.SnapResize(_startRect, _edge, dx, dy, _dragOthers, _dragWork.Width, _dragWork.Height, _c.CellW, _c.CellH, scale);
        _preview = res.Rect;
        Relayout();
        _surface.ShowGuides(res.Guides);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_mode != Mode.None) { EndDrag(commit: true); e.Handled = true; }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_mode != Mode.None) EndDrag(commit: false);
    }

    private void EndDrag(bool commit)
    {
        if (commit) ProcessPending();
        _pendingPos = null;
        if (_renderHooked) { CompositionTarget.Rendering -= OnRendering; _renderHooked = false; }
        var mode = _mode;
        var preview = _preview;
        _mode = Mode.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
        _surface.ShowGuides(Array.Empty<Guide>());
        _preview = null;
        Cursor = Cursors.Arrow;
        if (commit && preview != null && mode is Mode.Move or Mode.Resize)
        {
            var changed = Math.Abs(preview.X - _startRect.X) > 0.01 || Math.Abs(preview.Y - _startRect.Y) > 0.01 ||
                          Math.Abs(preview.W - _startRect.W) > 0.01 || Math.Abs(preview.H - _startRect.H) > 0.01;
            if (changed) { _c.SetBoxRect(Box, _surface.MonitorName, preview); return; }
        }
        Relayout();
    }

    // ------------------------------------------------------------ 滚动条拖动

    private void OnThumbDown(object sender, MouseButtonEventArgs e)
    {
        _mode = Mode.Thumb;
        _thumbGrab = e.GetPosition(_thumb).Y;
        _surface.FocusSurface();
        CaptureMouse();
        e.Handled = true;
    }

    private void DragThumb(Point p)
    {
        var max = MaxScroll;
        var vh = ViewportH;
        var track = vh - _thumb.Height;
        if (max <= 0 || track <= 0) return;
        var top = p.Y - BoxGeometry.TitleH - _thumbGrab;
        _scroll = Math.Clamp(top / track, 0, 1) * max;
        LayoutItems();
    }

    // ------------------------------------------------------------ 标题重命名

    public void BeginTitleEdit()
    {
        if (_titleEdit != null) return;
        var box = new TextBox
        {
            Text = Box.Name, FontFamily = SystemFonts.MessageFontFamily, FontSize = 13,
            Width = Math.Max(40, DisplayRect.W - 12 - ButtonSize * 2 - 20), Height = 22,
            Padding = new Thickness(2, 0, 2, 0), VerticalContentAlignment = VerticalAlignment.Center,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xFF)), BorderThickness = new Thickness(1),
        };
        SetLeft(box, 10);
        SetTop(box, 5);
        Panel.SetZIndex(box, 40);
        _titleEdit = box;
        _titleEditDone = false;
        Children.Add(box);
        box.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter) { EndTitleEdit(true); ke.Handled = true; }
            else if (ke.Key == Key.Escape) { EndTitleEdit(false); ke.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => EndTitleEdit(true);
        _surface.FocusSurface();
        box.Focus();
        Keyboard.Focus(box);
        box.SelectAll();
    }

    private void EndTitleEdit(bool commit)
    {
        var box = _titleEdit;
        if (box == null || _titleEditDone) return;
        _titleEditDone = true;
        _titleEdit = null;
        var text = box.Text;
        Children.Remove(box);
        _surface.FocusSurface();
        if (commit) _c.RenameBox(Box, text);
    }
}
