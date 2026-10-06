using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DeskNext.Desktop;
using DeskNext.Model;
using DeskNext.Services;

namespace DeskNext.Views;

/// <summary>设置窗口里一条规则的可编辑副本（扩展名以文本形式编辑）。</summary>
internal sealed class RuleVm : INotifyPropertyChanged
{
    private string _name = "";
    private string _extText = "";

    public string Name { get => _name; set { _name = value; Raise(nameof(Name)); } }
    public string ExtText { get => _extText; set { _extText = value; Raise(nameof(ExtText)); } }

    public static RuleVm From(OrganizeRule r) => new() { Name = r.Name, ExtText = string.Join(" ", r.Extensions) };

    public OrganizeRule ToRule() => new(Name.Trim(),
        ExtText.Split(new[] { ' ', ',', ';', '，', '；', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>规则设置窗口：普通顶层窗口，不参与桌面层 Z 序逻辑。</summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _instance;

    private readonly DesktopController _controller;
    private readonly ObservableCollection<RuleVm> _rules = new();

    internal static void ShowSingleton(DesktopController controller)
    {
        if (_instance != null)
        {
            _instance.Activate();
            return;
        }
        _instance = new SettingsWindow(controller);
        _instance.Closed += (_, _) => _instance = null;
        _instance.Show();
        _instance.Activate();
    }

    private SettingsWindow(DesktopController controller)
    {
        InitializeComponent();
        _controller = controller;
        LoadGeneral(controller.Settings);
        ShowTab(general: true);
        Load(controller.Settings.OrganizeRules);
        RuleList.ItemsSource = _rules;
        if (_rules.Count > 0) RuleList.SelectedIndex = 0;
        UpdateButtons();
    }

    private bool _loading;

    private void LoadGeneral(AppSettings s)
    {
        _loading = true;
        DblChk.IsChecked = s.DoubleClickToggle;
        AutoChk.IsChecked = AutoStart.Default.IsEnabled; // 以注册表为准
        SizeSystem.IsChecked = s.IconSizeMode == "system";
        SizeSmall.IsChecked = s.IconSizeMode == "small";
        SizeMedium.IsChecked = s.IconSizeMode == "medium";
        SizeLarge.IsChecked = s.IconSizeMode == "large";
        OpacitySlider.Value = s.BoxOpacity;
        OpacityText.Text = $"{(int)Math.Round(s.BoxOpacity * 100)}%";
        _loading = false;
    }

    private string SelectedSizeMode() =>
        SizeSmall.IsChecked == true ? "small" : SizeMedium.IsChecked == true ? "medium" : SizeLarge.IsChecked == true ? "large" : "system";

    private void ShowTab(bool general)
    {
        GeneralPage.Visibility = general ? Visibility.Visible : Visibility.Collapsed;
        RulesPage.Visibility = general ? Visibility.Collapsed : Visibility.Visible;
        BtnReset.Visibility = general ? Visibility.Collapsed : Visibility.Visible;
        var on = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3D, 0x8B, 0xFD));
        var off = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0x3A, 0x41));
        TabGeneral.Background = general ? on : off;
        TabRules.Background = general ? off : on;
    }

    private void OnTabGeneral(object sender, RoutedEventArgs e) => ShowTab(general: true);
    private void OnTabRules(object sender, RoutedEventArgs e) => ShowTab(general: false);

    private void OnAutoStartClick(object sender, RoutedEventArgs e)
    {
        AutoStart.Default.SetEnabled(AutoChk.IsChecked == true);
        AutoChk.IsChecked = AutoStart.Default.IsEnabled; // 写入失败时回显真实状态
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsLoaded) return;
        OpacityText.Text = $"{(int)Math.Round(e.NewValue * 100)}%";
        _controller.PreviewBoxOpacity(e.NewValue);
    }

    protected override void OnClosed(EventArgs e)
    {
        // 未保存的透明度预览还原为已保存的值
        _controller.PreviewBoxOpacity(_controller.Settings.BoxOpacity);
        base.OnClosed(e);
    }

    private void Load(IEnumerable<OrganizeRule> rules)
    {
        _rules.Clear();
        foreach (var r in rules) _rules.Add(RuleVm.From(r));
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Editor.DataContext = RuleList.SelectedItem;
        Editor.IsEnabled = RuleList.SelectedItem != null;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var i = RuleList.SelectedIndex;
        BtnUp.IsEnabled = i > 0;
        BtnDown.IsEnabled = i >= 0 && i < _rules.Count - 1;
        BtnDelete.IsEnabled = i >= 0;
    }

    private void OnUp(object sender, RoutedEventArgs e) => Move(-1);
    private void OnDown(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int d)
    {
        var i = RuleList.SelectedIndex;
        var j = i + d;
        if (i < 0 || j < 0 || j >= _rules.Count) return;
        _rules.Move(i, j);
        RuleList.SelectedIndex = j;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var at = RuleList.SelectedIndex >= 0 ? RuleList.SelectedIndex + 1 : _rules.Count;
        _rules.Insert(at, new RuleVm { Name = "新分类", ExtText = "" });
        RuleList.SelectedIndex = at;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var i = RuleList.SelectedIndex;
        if (i < 0) return;
        _rules.RemoveAt(i);
        RuleList.SelectedIndex = Math.Min(i, _rules.Count - 1);
        UpdateButtons();
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        Load(AppSettings.DefaultRules());
        RuleList.SelectedIndex = _rules.Count > 0 ? 0 : -1;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var rules = _rules.Select(r => r.ToRule()).Where(r => r.Name.Length > 0).ToList();
        _controller.ApplySettings(new AppSettings
        {
            OrganizeRules = rules,
            DoubleClickToggle = DblChk.IsChecked == true,
            IconSizeMode = SelectedSizeMode(),
            BoxOpacity = OpacitySlider.Value,
        });
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
