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

    /// <summary>绑定项并按图标/格子尺寸（DIP）布局。</summary>
    public void Bind(DesktopItem item, double iconDip, double cellW, double cellH)
    {
        Item = item;
        Width = cellW;
        Height = cellH;
        Icon.Width = Icon.Height = iconDip;
        Icon.Margin = new Thickness(2, 6, 2, 3);
        Label.Margin = new Thickness(0, 0, 0, 2);
        Label.MaxWidth = cellW - 6;
        Label.Text = AllowCharWrap(item.DisplayName, Label.MaxWidth);
        _lineHeight = Math.Ceiling(Label.FontFamily.LineSpacing * Label.FontSize);
        UpdateVisual();
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
            return new Rect(p, new Size(Math.Max(Label.ActualWidth, Width - 6), Math.Max(Label.ActualHeight, _lineHeight)));
        }
    }

    private void UpdateVisual()
    {
        var expanded = _selected || _hover;
        Label.MaxHeight = expanded ? double.PositiveInfinity : _lineHeight * 2;
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
