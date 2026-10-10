using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>
/// 前面のアプリの規則1つの編集（2026-10-08、P4）。照合条件（指定した項目がすべて一致したら一致）と、そのときの動作
/// （立てるアプリ管理レイヤー、リマップのOFF）を決める。OKで検証が通ったときだけ <see cref="Result"/> に入る。
/// </summary>
public sealed class AppRuleEditorWindow : Window
{
    private readonly TextBox _name = Box("RuleName", 320);
    private readonly CheckBox _enabled = new() { Name = "RuleEnabled", Content = "有効", Margin = new Thickness(0, 3, 0, 3) };
    private readonly CheckBox _default = new() { Name = "RuleDefault", Content = "どの規則にも一致しなかったときに使う（既定の規則）", Margin = new Thickness(0, 3, 0, 3) };
    private readonly TextBox _exe = Box("RuleExe", 320);
    private readonly TextBox _pathRegex = Box("RulePathRegex", 320);
    private readonly TextBox _titleRegex = Box("RuleTitleRegex", 320);
    private readonly TextBox _class = Box("RuleClass", 320);
    private readonly TextBox _nodokaClass = Box("RuleNodokaClass", 320);
    private readonly TextBox _nodokaTitle = Box("RuleNodokaTitle", 320);
    private readonly CheckBox _nodokaOr = new() { Name = "RuleNodokaOr", Content = "のどかの条件は、どちらかが一致すればよい（OR。指定しなければ両方：AND）", Margin = new Thickness(0, 3, 0, 3) };
    private readonly CheckBox[] _layers = new CheckBox[KbdLayerLayers];
    private readonly CheckBox _mapOff = new() { Name = "RuleMapOff", Content = "このアプリが前面の間、このキーボードのリマップをOFFにする（ゲームなど）", Margin = new Thickness(0, 3, 0, 3) };
    private readonly TextBlock _status = new() { Name = "RuleStatus", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _matchPanel = new();
    private bool _loading = true;

    private const int KbdLayerLayers = 16;

    public AppLayerRule Result { get; private set; } = new();

    public AppRuleEditorWindow(AppLayerRule rule)
    {
        Title = "前面のアプリの規則";
        Width = 760;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 800;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var panel = new StackPanel();
        panel.Children.Add(Help(
            "前面のウィンドウが条件に一致したとき、アプリ管理レイヤーを立てる、またはリマップをOFFにします。規則は上から順に見て、" +
            "最初に一致したものを使います。条件は、指定した項目がすべて一致したときに一致です（空の項目は見ません）。" +
            "正規表現は、大文字小文字を区別しない部分一致です。"));

        panel.Children.Add(Row("名前", _name));
        panel.Children.Add(_enabled);
        panel.Children.Add(_default);

        _matchPanel.Children.Add(Row("Exe", _exe, "exe のファイル名（例 code.exe）。\\ を含めばフルパス"));
        _matchPanel.Children.Add(Row("PathRegex", _pathRegex, "exe のフルパスの正規表現"));
        _matchPanel.Children.Add(Row("TitleRegex", _titleRegex, "ウィンドウのタイトルの正規表現"));
        _matchPanel.Children.Add(Row("Class", _class, "ウィンドウのクラス名（完全一致）"));
        _matchPanel.Children.Add(Row("NodokaClass", _nodokaClass, "のどかの window と同じ文字列（exeのフルパス:クラス:…:フォーカスのクラス）に対する正規表現"));
        _matchPanel.Children.Add(Row("NodokaTitle", _nodokaTitle, "のどかの window と同じ文字列（exeのフルパス:タイトル:…）に対する正規表現"));
        _matchPanel.Children.Add(_nodokaOr);
        var pick = new Button { Name = "PickWindowButton", Content = "実行中のアプリから選ぶ...", Width = 190, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        pick.Click += (_, _) => PickWindow();
        _matchPanel.Children.Add(pick);
        panel.Children.Add(new GroupBox { Header = "条件", Content = _matchPanel, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8) });

        var actionPanel = new StackPanel();
        actionPanel.Children.Add(new TextBlock { Text = "立てるアプリ管理レイヤー（キーボード側のレイヤーとOR。複数選べる）:", Margin = new Thickness(0, 0, 0, 4) });
        var wrap = new WrapPanel();
        for (int i = 0; i < KbdLayerLayers; i++)
        {
            _layers[i] = new CheckBox { Name = $"RuleLayer{i}", Content = i.ToString(), Width = 54, Margin = new Thickness(0, 2, 0, 2) };
            _layers[i].Click += (_, _) => Revalidate();
            wrap.Children.Add(_layers[i]);
        }
        actionPanel.Children.Add(wrap);
        actionPanel.Children.Add(_mapOff);
        panel.Children.Add(new GroupBox { Header = "そのときの動作", Content = actionPanel, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8) });
        panel.Children.Add(_status);

        var ok = new Button { Name = "OkButton", Content = "OK", IsDefault = true, Width = 90 };
        ok.Click += (_, _) => { if (TryBuild(out var built, out var errors)) { Result = built; DialogResult = true; } else Show(errors); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Name = "CancelButton", Content = "キャンセル", IsCancel = true, Width = 90 });

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        Load(rule);
        foreach (var t in new[] { _name, _exe, _pathRegex, _titleRegex, _class, _nodokaClass, _nodokaTitle }) t.TextChanged += (_, _) => Revalidate();
        _default.Click += (_, _) => { _matchPanel.IsEnabled = _default.IsChecked != true; Revalidate(); };
        _loading = false;
        Revalidate();
    }

    private void Load(AppLayerRule r)
    {
        _name.Text = r.Name ?? "";
        _enabled.IsChecked = r.Enabled;
        _default.IsChecked = r.Default;
        _matchPanel.IsEnabled = !r.Default;
        _exe.Text = r.Match.Exe ?? "";
        _pathRegex.Text = r.Match.PathRegex ?? "";
        _titleRegex.Text = r.Match.TitleRegex ?? "";
        _class.Text = r.Match.Class ?? "";
        _nodokaClass.Text = r.Match.NodokaClass ?? "";
        _nodokaTitle.Text = r.Match.NodokaTitle ?? "";
        _nodokaOr.IsChecked = r.Match.NodokaOr;
        _mapOff.IsChecked = r.MapOff;
        foreach (int l in r.Layers)
            if (l >= 0 && l < KbdLayerLayers) _layers[l].IsChecked = true;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private bool TryBuild(out AppLayerRule rule, out List<string> errors)
    {
        rule = new AppLayerRule
        {
            Name = NullIfEmpty(_name.Text),
            Enabled = _enabled.IsChecked == true,
            Default = _default.IsChecked == true,
            MapOff = _mapOff.IsChecked == true,
            Layers = Enumerable.Range(0, KbdLayerLayers).Where(i => _layers[i].IsChecked == true).ToList(),
        };
        if (!rule.Default)
        {
            rule.Match = new AppMatch
            {
                Exe = NullIfEmpty(_exe.Text),
                PathRegex = NullIfEmpty(_pathRegex.Text),
                TitleRegex = NullIfEmpty(_titleRegex.Text),
                Class = NullIfEmpty(_class.Text),
                NodokaClass = NullIfEmpty(_nodokaClass.Text),
                NodokaTitle = NullIfEmpty(_nodokaTitle.Text),
                NodokaOr = _nodokaOr.IsChecked == true,
            };
        }
        errors = HelperSettingsModel.ValidateRule(rule, "この規則");
        return errors.Count == 0;
    }

    private void Revalidate()
    {
        if (_loading) return;
        TryBuild(out _, out var errors);
        Show(errors);
    }

    private void Show(List<string> errors)
    {
        _status.Foreground = errors.Count == 0 ? Brushes.DarkGreen : Brushes.Firebrick;
        _status.Text = errors.Count == 0 ? "OK" : string.Join("\n", errors);
    }

    private void PickWindow()
    {
        var picker = new WindowPickerWindow { Owner = this };
        if (picker.ShowDialog() != true || picker.Selected is not { } w) return;
        _exe.Text = w.ExeName;
        if (picker.IncludeClass) _class.Text = w.ClassName;
    }

    // ------------------------------------------------------------------

    private static TextBox Box(string name, double width) => new() { Name = name, Width = width, VerticalContentAlignment = VerticalAlignment.Center };

    private static TextBlock Help(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 10) };

    private static StackPanel Row(string label, UIElement input, string? help = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(input);
        if (help != null)
            row.Children.Add(new TextBlock { Text = help, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 });
        return row;
    }
}
