using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskNook.Model;

namespace DeskNook.Views;

/// <summary>桌面图标：图标 + 文字，支持悬停 / 选中 / 选中且失焦 / 剪切半透明。</summary>
public partial class IconItemControl : UserControl
{
    private static readonly Brush HoverFill = Frozen(new SolidColorBrush(Color.FromArgb(0x33, 0x9B, 0xD0, 0xF5)));
    private static readonly Brush HoverBorder = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0xC8, 0xE4, 0xFA)));
    private static readonly Brush SelectedFill = Frozen(new SolidColorBrush(Color.FromArgb(0x66, 0x4F, 0xA3, 0xE6)));
    private static readonly Brush SelectedBorder = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0x7D, 0xC0, 0xF0)));
    private static readonly Brush InactiveFill = Frozen(new SolidColorBrush(Color.FromArgb(0x4D, 0xC8, 0xC8, 0xC8)));
    private static readonly Brush InactiveBorder = Frozen(new SolidColorBrush(Color.FromArgb(0x66, 0xE0, 0xE0, 0xE0)));

    private bool _hover, _selected, _active = true, _cut;
    private double _lineHeight = 16;
    private bool _horizontal;
    private double _iconDip;

    public DesktopItem Item { get; private set; } = null!;

    public IconItemControl()
    {
        InitializeComponent();
        Label.FontFamily = SystemFonts.MessageFontFamily;
        Label.FontSize = SystemFonts.MessageFontSize;
        MouseEnter += (_, _) => { _hover = true; UpdateVisual(); };
        MouseLeave += (_, _) => { _hover = false; UpdateVisual(); };
    }

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }

    /// <summary>当前是否横排（图标在左、文字在右、单行）。</summary>
    public bool Horizontal => _horizontal;

    /// <summary>当前加载图标用的像素尺寸（0 = 尚未加载）。</summary>
    public int IconPx { get; set; }

    /// <summary>绑定项并按图标/格子尺寸（DIP）布局。horizontal：图标在左文字在右的单行布局（小图标/列表视图）。</summary>
    public void Bind(DesktopItem item, double iconDip, double cellW, double cellH, bool horizontal = false)
    {
        Item = item;
        _horizontal = horizontal;
        _iconDip = iconDip;
        Width = cellW;
        Height = cellH;
        Icon.Width = Icon.Height = iconDip;
        _lineHeight = Math.Ceiling(Label.FontFamily.LineSpacing * Label.FontSize);
        if (horizontal)
        {
            Stack.Orientation = Orientation.Horizontal;
            Frame.HorizontalAlignment = HorizontalAlignment.Left;
            Frame.VerticalAlignment = VerticalAlignment.Stretch;
            Frame.Width = double.NaN;
            Icon.HorizontalAlignment = HorizontalAlignment.Left;
            Icon.VerticalAlignment = VerticalAlignment.Center;
            Icon.Margin = new Thickness(4, 0, 6, 0);
            Label.Margin = new Thickness(0);
            Label.TextWrapping = TextWrapping.NoWrap;
            Label.TextAlignment = TextAlignment.Left;
            Label.HorizontalAlignment = HorizontalAlignment.Left;
            Label.VerticalAlignment = VerticalAlignment.Center;
            Label.MaxWidth = HorizontalLabelWidth(cellW);
            Label.Text = item.DisplayName;
        }
        else
        {
            Stack.Orientation = Orientation.Vertical;
            Frame.HorizontalAlignment = HorizontalAlignment.Center;
            Frame.VerticalAlignment = VerticalAlignment.Top;
            Frame.Width = cellW - 4; // 固定为文字最大宽度 + 边框，所有图标框等宽（与系统桌面一致）
            Icon.HorizontalAlignment = HorizontalAlignment.Center;
            Icon.VerticalAlignment = VerticalAlignment.Stretch;
            Icon.Margin = new Thickness(2, 6, 2, 3);
            Label.Margin = new Thickness(0, 0, 0, 2);
            Label.TextWrapping = TextWrapping.Wrap;
            Label.TextAlignment = TextAlignment.Center;
            Label.HorizontalAlignment = HorizontalAlignment.Center;
            Label.VerticalAlignment = VerticalAlignment.Stretch;
            Label.MaxWidth = cellW - 6;
            Label.Text = AllowCharWrap(item.DisplayName, Label.MaxWidth);
        }
        UpdateVisual();
    }

    /// <summary>横排时文字可用宽度：单元宽 − 图标 − 图标边距(4+6) − 边框(2) − 右侧留白(4)。</summary>
    private double HorizontalLabelWidth(double itemW) => Math.Max(10, itemW - _iconDip - 16);

    /// <summary>横排时按拉伸后的单元宽度设置控件宽度与文字最大宽度（竖排不用）。</summary>
    public void SetItemWidth(double w)
    {
        if (!_horizontal || Math.Abs(Width - w) < 0.01) return;
        Width = w;
        Label.MaxWidth = HorizontalLabelWidth(w);
    }

    /// <summary>尚未取到图标（需要加载/重试）。</summary>
    public bool NeedsIcon { get; set; } = true;

    /// <summary>超过一行宽度的“单词”（如 settings.json）允许在任意字符处换行，与系统桌面一致。</summary>
    private string AllowCharWrap(string text, double maxWidth)
    {
        var tf = new Typeface(Label.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double Measure(string t) => new FormattedText(t, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            tf, Label.FontSize, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;
        var parts = text.Split(' ');
        for (var i = 0; i < parts.Length; i++)
            if (parts[i].Length > 1 && Measure(parts[i]) > maxWidth)
                parts[i] = string.Join("\u200B", parts[i].Select(c => c.ToString()));
        return string.Join(" ", parts);
    }

    public void SetIcon(BitmapSource? bmp)
    {
        Icon.Source = bmp;
        NeedsIcon = bmp == null;
    }

    public bool IsSelected { get => _selected; set { _selected = value; UpdateVisual(); } }
    /// <summary>窗口是否是活动窗口（选中但失焦时显示灰色）。</summary>
    public bool WindowActive { get => _active; set { _active = value; UpdateVisual(); } }
    public bool IsCut { get => _cut; set { _cut = value; UpdateVisual(); } }

    /// <summary>图标+文字的命中区域（控件坐标）。</summary>
    public Rect HitBounds
    {
        get
        {
            var p = Frame.TranslatePoint(new Point(0, 0), this);
            return new Rect(p, new Size(Frame.ActualWidth, Frame.ActualHeight));
        }
    }

    /// <summary>文字区域（控件坐标），重命名框覆盖在这里。</summary>
    public Rect LabelBounds
    {
        get
        {
            var p = Label.TranslatePoint(new Point(0, 0), this);
            var w = _horizontal ? Label.ActualWidth : Math.Max(Label.ActualWidth, Width - 6);
            return new Rect(p, new Size(w, Math.Max(Label.ActualHeight, _lineHeight)));
        }
    }

    private void UpdateVisual()
    {
        var expanded = _selected || _hover;
        Label.MaxHeight = _horizontal ? _lineHeight : expanded ? double.PositiveInfinity : _lineHeight * 2;
        Panel.SetZIndex(this, expanded ? 10 : 0);

        if (_selected)
        {
            Frame.Background = _active ? SelectedFill : InactiveFill;
            Frame.BorderBrush = _active ? SelectedBorder : InactiveBorder;
        }
        else if (_hover)
        {
            Frame.Background = HoverFill;
            Frame.BorderBrush = HoverBorder;
        }
        else
        {
            Frame.Background = Brushes.Transparent;
            Frame.BorderBrush = Brushes.Transparent;
        }

        // 隐藏/灰显项与被剪切项半透明
        Opacity = _cut || Item is { IsHidden: true } or { IsGhosted: true } ? 0.5 : 1.0;
    }
}
