using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using XkDesk.Desktop;
using XkDesk.Model;

namespace XkDesk.Views;

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
        Load(controller.Settings.OrganizeRules);
        RuleList.ItemsSource = _rules;
        if (_rules.Count > 0) RuleList.SelectedIndex = 0;
        UpdateButtons();
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
        _controller.ApplySettings(new AppSettings { OrganizeRules = rules });
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
