using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kasane.Keycodes;
using Kasane.Model;

namespace Kasane.Views;

public enum HelperEditMode
{
    /// <summary>HLP(id) のキーから呼ぶ動作（Id と名前がある）。</summary>
    Action,
    /// <summary>F13 などのキー（＋修飾キー）で動かす動作（キーと修飾キーがある）。</summary>
    Trigger,
}

/// <summary>
/// ヘルパー動作1つの編集（2026-10-08、P4）。動作の種類（Launch / Open / Sequence / AppLayer / PauseToggle）で入力欄が変わる。
/// OKで検証が通ったときだけ結果（<see cref="ResultBody"/> など）に入る。
/// </summary>
public sealed class HelperActionEditorWindow : Window
{
    private static readonly (string Type, string Label)[] Types =
    {
        (HelperActionBody.Launch, "プログラムを起動する (Launch)"),
        (HelperActionBody.Open, "URL・ファイル・フォルダを開く (Open)"),
        (HelperActionBody.Sequence, "キー・文字列・マウスの操作 (Sequence)"),
        (HelperActionBody.AppLayer, "アプリ管理レイヤーの操作 (AppLayer)"),
        (HelperActionBody.PauseToggle, "一時停止・再開 (PauseToggle)"),
    };

    private static readonly string[] LayerOps = { "On", "Off", "Toggle", "Hold" };
    private static readonly string[] LayerOpLabels =
    {
        "On: 立てる", "Off: 下ろす", "Toggle: 押すたびに切り替える", "Hold: 押している間だけ立てる",
    };

    private readonly HelperEditMode _mode;
    private readonly Func<int, bool> _idInUse;
    private readonly TextBox _id = Box("ActionId", 90);
    private readonly TextBox _name = Box("ActionName", 320);
    private readonly TextBox _key = Box("TriggerKey", 200, mono: true);
    private readonly CheckBox _ctrl = new() { Name = "TriggerCtrl", Content = "Ctrl", Margin = new Thickness(0, 0, 12, 0) };
    private readonly CheckBox _shift = new() { Name = "TriggerShift", Content = "Shift", Margin = new Thickness(0, 0, 12, 0) };
    private readonly CheckBox _alt = new() { Name = "TriggerAlt", Content = "Alt", Margin = new Thickness(0, 0, 12, 0) };
    private readonly CheckBox _win = new() { Name = "TriggerWin", Content = "Win" };
    private readonly ComboBox _type = new() { Name = "ActionType", Width = 380 };
    private readonly TextBox _path = Box("ActionPath", 380, mono: true);
    private readonly TextBox _args = Box("ActionArgs", 380, mono: true);
    private readonly TextBox _workDir = Box("ActionWorkDir", 380, mono: true);
    private readonly TextBox _steps = MultiLine("ActionSteps");
    private readonly TextBox _releaseSteps = MultiLine("ActionReleaseSteps");
    private readonly CheckBox[] _layers = new CheckBox[16];
    private readonly ComboBox _layerOp = new() { Name = "ActionLayerOp", Width = 300 };
    private readonly CheckBox _repeat = new() { Name = "ActionRepeat", Content = "キーを押し続けたとき（オートリピート）も繰り返す", Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _launchPanel = new();
    private readonly StackPanel _sequencePanel = new();
    private readonly StackPanel _layerPanel = new();
    private readonly StackPanel _pausePanel = new();
    private readonly StackPanel _argsRow;
    private readonly StackPanel _workDirRow;
    private readonly TextBlock _pathHelp = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 4) };
    private readonly TextBlock _status = new() { Name = "ActionStatus", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private bool _loading = true;

    public int ResultId { get; private set; }
    public string? ResultName { get; private set; }
    public string ResultKey { get; private set; } = "";
    public string? ResultMods { get; private set; }
    public HelperActionBody ResultBody { get; private set; } = new();

    /// <param name="idInUse">その Id がすでに他の動作で使われているか（編集中の動作自身は含めない）。Trigger のときは使わない。</param>
    public HelperActionEditorWindow(string title, HelperEditMode mode, int id, string? name, string? key, string? mods,
                                    HelperActionBody body, Func<int, bool>? idInUse = null)
    {
        _mode = mode;
        _idInUse = idInUse ?? (_ => false);
        Title = title;
        Width = 820;
        SizeToContent = SizeToContent.Height; // 入力欄の数（種類で変わる）に合わせる。高すぎれば、中でスクロールする
        MaxHeight = 800;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var panel = new StackPanel();
        if (mode == HelperEditMode.Action)
        {
            panel.Children.Add(Help("この動作は、キーに割り当てた HLP(Id) を押したときに動きます（キー割り当て画面の「ヘルパー動作」から選びます）。" +
                                    "Id は、このキーボードの分と共通の分で重ならない数（1〜65535）にします。"));
            panel.Children.Add(Row("Id", _id, "HLP(Id)"));
            panel.Children.Add(Row("名前", _name));
        }
        else
        {
            panel.Children.Add(Help("このトリガーは、トレイのキーボードフックで受けます（トリガーが1つでもあると、フックを張ります）。" +
                                    "押したキーは飲み込まれ、アプリには届きません。F13〜F24 など、普段使わないキーが向いています。" +
                                    "修飾キーは左右を区別せず、押している分と過不足なく一致したときだけ動きます。Alt・Win の組み合わせは、" +
                                    "単独で離したときにメニューやスタートが開くことがあるので、避けてください。"));
            panel.Children.Add(Row("キー", _key, "KC_F13、SC_E06A のような書き方"));
            var modPanel = new StackPanel { Orientation = Orientation.Horizontal };
            modPanel.Children.Add(_ctrl);
            modPanel.Children.Add(_shift);
            modPanel.Children.Add(_alt);
            modPanel.Children.Add(_win);
            panel.Children.Add(Row("修飾キー", modPanel));
        }

        foreach (var t in Types) _type.Items.Add(t.Label);
        panel.Children.Add(Row("種類", _type));

        // Launch / Open
        _launchPanel.Children.Add(_pathHelp);
        var pathRow = Row("Path", _path);
        var browse = new Button { Name = "BrowseButton", Content = "参照...", Width = 70, Margin = new Thickness(6, 0, 0, 0) };
        browse.Click += (_, _) => Browse();
        pathRow.Children.Add(browse);
        _launchPanel.Children.Add(pathRow);
        _argsRow = Row("引数", _args);
        _workDirRow = Row("作業フォルダ", _workDir);
        _launchPanel.Children.Add(_argsRow);
        _launchPanel.Children.Add(_workDirRow);
        _launchPanel.Children.Add(new TextBlock { Text = "%USERPROFILE% のような環境変数が使えます。昇格（管理者として実行）はしません。", Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap });

        // Sequence
        _sequencePanel.Children.Add(Help(
            "1行が1手順です。書き方:\n" +
            "  key LCTL(KC_C)   キーを押して離す（KC_A、LCTL(KC_C)、SC_E06A など）\n" +
            "  down KC_A / up KC_A   押す / 離す\n" +
            "  text こんにちは\\n   文字列を打つ（\\n = Enter、\\t = Tab、\\\\ = バックスラッシュ。日本語のフォントでは \\ が ¥ に見えますが、入力するのは半角のバックスラッシュです）\n" +
            "  delay 50   待つ（ミリ秒、0〜10000）\n" +
            "  mouse left / mouse right down / mouse x1 up   ボタン（left right middle x1 x2、click down up）\n" +
            "  move 10 -5 / moveto 100 200   マウスを動かす（今の位置から / 画面の座標へ）\n" +
            "  wheel -120 / hwheel 120   ホイール（120 で1ノッチ）\n" +
            "# で始まる行は無視します。管理者として動くウィンドウには入力が届きません。"));
        _sequencePanel.Children.Add(new TextBlock { Text = "キーを押したときの手順:", Margin = new Thickness(0, 4, 0, 2) });
        _sequencePanel.Children.Add(_steps);
        _sequencePanel.Children.Add(new TextBlock { Text = "キーを離したときの手順（任意。マウスのボタンを押している間だけ押す、など）:", Margin = new Thickness(0, 8, 0, 2) });
        _sequencePanel.Children.Add(_releaseSteps);

        // AppLayer
        _layerPanel.Children.Add(Help("アプリ管理レイヤーを操作します（IME連動・前面のアプリの規則と合わせて、ORで効きます）。対象は、そのキーを押したキーボードです。"));
        var wrap = new WrapPanel();
        for (int i = 0; i < _layers.Length; i++)
        {
            _layers[i] = new CheckBox { Name = $"ActionLayer{i}", Content = i.ToString(), Width = 54, Margin = new Thickness(0, 2, 0, 2) };
            _layers[i].Click += (_, _) => Revalidate();
            wrap.Children.Add(_layers[i]);
        }
        _layerPanel.Children.Add(wrap);
        foreach (var label in LayerOpLabels) _layerOp.Items.Add(label);
        _layerPanel.Children.Add(Row("操作", _layerOp));

        _pausePanel.Children.Add(Help("トレイの一時停止・再開を切り替えます。一時停止中は、キーフックのトリガーだけが動きます（HLP のキーはリマップが止まるので届きません）。"));

        panel.Children.Add(_launchPanel);
        panel.Children.Add(_sequencePanel);
        panel.Children.Add(_layerPanel);
        panel.Children.Add(_pausePanel);
        panel.Children.Add(_repeat);
        panel.Children.Add(_status);

        var ok = new Button { Name = "OkButton", Content = "OK", IsDefault = true, Width = 90 };
        ok.Click += (_, _) => { if (Commit()) DialogResult = true; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Name = "CancelButton", Content = "キャンセル", IsCancel = true, Width = 90 });

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        Load(id, name, key, mods, body);
        _type.SelectionChanged += (_, _) => { ShowPanels(); Revalidate(); };
        foreach (var t in new[] { _id, _name, _key, _path, _args, _workDir, _steps, _releaseSteps }) t.TextChanged += (_, _) => Revalidate();
        foreach (var c in new[] { _ctrl, _shift, _alt, _win }) c.Click += (_, _) => Revalidate();
        _layerOp.SelectionChanged += (_, _) => Revalidate();
        _loading = false;
        ShowPanels();
        Revalidate();
    }

    private string SelectedType => Types[Math.Max(0, _type.SelectedIndex)].Type;

    private void Load(int id, string? name, string? key, string? mods, HelperActionBody body)
    {
        _id.Text = id.ToString();
        _name.Text = name ?? "";
        _key.Text = key ?? "";
        if (TriggerTable.TryParseMods(mods, out byte m))
        {
            _ctrl.IsChecked = (m & TriggerTable.ModCtrl) != 0;
            _shift.IsChecked = (m & TriggerTable.ModShift) != 0;
            _alt.IsChecked = (m & TriggerTable.ModAlt) != 0;
            _win.IsChecked = (m & TriggerTable.ModWin) != 0;
        }
        int typeIndex = Array.FindIndex(Types, t => t.Type == body.Type);
        _type.SelectedIndex = typeIndex < 0 ? 0 : typeIndex;
        _path.Text = body.Path ?? "";
        _args.Text = body.Args ?? "";
        _workDir.Text = body.WorkDir ?? "";
        _steps.Text = StepText.Format(body.Steps);
        _releaseSteps.Text = StepText.Format(body.ReleaseSteps);
        foreach (int l in body.Layers ?? new List<int>())
            if (l >= 0 && l < _layers.Length) _layers[l].IsChecked = true;
        int op = Array.FindIndex(LayerOps, o => string.Equals(o, body.LayerOp, StringComparison.OrdinalIgnoreCase));
        _layerOp.SelectedIndex = op < 0 ? 0 : op;
        _repeat.IsChecked = body.Repeat;
    }

    private void ShowPanels()
    {
        string type = SelectedType;
        bool launch = type is HelperActionBody.Launch or HelperActionBody.Open;
        _launchPanel.Visibility = launch ? Visibility.Visible : Visibility.Collapsed;
        _argsRow.Visibility = _workDirRow.Visibility = type == HelperActionBody.Launch ? Visibility.Visible : Visibility.Collapsed;
        _pathHelp.Text = type == HelperActionBody.Launch
            ? "起動するプログラムのパス（notepad.exe のように、PATH にあるものは名前だけでもよい）。"
            : "開く URL・ファイル・フォルダ。関連付けられたアプリで開きます。";
        _sequencePanel.Visibility = type == HelperActionBody.Sequence ? Visibility.Visible : Visibility.Collapsed;
        _layerPanel.Visibility = type == HelperActionBody.AppLayer ? Visibility.Visible : Visibility.Collapsed;
        _pausePanel.Visibility = type == HelperActionBody.PauseToggle ? Visibility.Visible : Visibility.Collapsed;
        _repeat.Visibility = type == HelperActionBody.PauseToggle ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "プログラム・ファイルを選ぶ", CheckFileExists = true };
        if (SelectedType == HelperActionBody.Launch)
            dlg.Filter = "プログラム (*.exe;*.bat;*.cmd;*.lnk)|*.exe;*.bat;*.cmd;*.lnk|すべてのファイル (*.*)|*.*";
        if (dlg.ShowDialog(this) == true) _path.Text = dlg.FileName;
    }

    private string TriggerMods()
    {
        var names = new List<string>();
        if (_ctrl.IsChecked == true) names.Add("MOD_LCTL");
        if (_shift.IsChecked == true) names.Add("MOD_LSFT");
        if (_alt.IsChecked == true) names.Add("MOD_LALT");
        if (_win.IsChecked == true) names.Add("MOD_LGUI");
        return string.Join("|", names);
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // 入力欄から動作を作る。書き方の誤りは errors に入れる。
    private HelperActionBody BuildBody(List<string> errors)
    {
        var body = new HelperActionBody { Type = SelectedType, Repeat = _repeat.IsChecked == true && SelectedType != HelperActionBody.PauseToggle };
        switch (body.Type)
        {
            case HelperActionBody.Launch:
                body.Path = NullIfEmpty(_path.Text);
                body.Args = NullIfEmpty(_args.Text);
                body.WorkDir = NullIfEmpty(_workDir.Text);
                break;
            case HelperActionBody.Open:
                body.Path = NullIfEmpty(_path.Text);
                break;
            case HelperActionBody.Sequence:
                StepText.TryParse(_steps.Text, out var steps, out var stepErrors);
                StepText.TryParse(_releaseSteps.Text, out var releaseSteps, out var releaseErrors);
                errors.AddRange(stepErrors);
                errors.AddRange(releaseErrors.Select(e => "離したとき " + e));
                body.Steps = steps.Count > 0 ? steps : null;
                body.ReleaseSteps = releaseSteps.Count > 0 ? releaseSteps : null;
                break;
            case HelperActionBody.AppLayer:
                body.Layers = Enumerable.Range(0, _layers.Length).Where(i => _layers[i].IsChecked == true).ToList();
                body.LayerOp = LayerOps[Math.Max(0, _layerOp.SelectedIndex)];
                break;
        }
        return body;
    }

    // 画面全体の誤り。空なら確定してよい。
    private List<string> Check(out HelperActionBody body)
    {
        var errors = new List<string>();
        body = BuildBody(errors);

        if (_mode == HelperEditMode.Action)
        {
            if (!int.TryParse(_id.Text.Trim(), out int id) || id < 0 || id > HelperSettingsModel.MaxActionId)
                errors.Add($"Id は 0〜{HelperSettingsModel.MaxActionId} の整数で入力してください。");
            else if (_idInUse(id))
                errors.Add($"Id {id} は、すでに別の動作で使われています。");
        }
        else
        {
            bool keyOk = KeycodeTable.TryResolveBaseKey(_key.Text.Trim(), out ushort sc) && sc != 0;
            if (!keyOk) errors.Add($"キー '{_key.Text.Trim()}' を解決できません（KC_F13 や SC_E06A のような書き方）。");
        }

        if (errors.Count == 0) HelperSettingsModel.ValidateBody(body, "この動作", errors);
        return errors;
    }

    private void Revalidate()
    {
        if (_loading) return;
        var errors = Check(out _);
        _status.Foreground = errors.Count == 0 ? Brushes.DarkGreen : Brushes.Firebrick;
        _status.Text = errors.Count == 0 ? "OK" : string.Join("\n", errors);
    }

    private bool Commit()
    {
        var errors = Check(out var body);
        if (errors.Count > 0)
        {
            _status.Foreground = Brushes.Firebrick;
            _status.Text = string.Join("\n", errors);
            return false;
        }
        ResultBody = body;
        if (_mode == HelperEditMode.Action)
        {
            ResultId = int.Parse(_id.Text.Trim());
            ResultName = NullIfEmpty(_name.Text);
        }
        else
        {
            ResultKey = _key.Text.Trim();
            ResultMods = NullIfEmpty(TriggerMods());
        }
        return true;
    }

    // ------------------------------------------------------------------

    private static TextBox Box(string name, double width, bool mono = false)
    {
        var box = new TextBox { Name = name, Width = width, VerticalContentAlignment = VerticalAlignment.Center };
        if (mono) box.FontFamily = new FontFamily("Consolas");
        return box;
    }

    private static TextBox MultiLine(string name) => new()
    {
        Name = name,
        AcceptsReturn = true,
        AcceptsTab = false,
        TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Consolas"),
        Height = 130,
    };

    private static TextBlock Help(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 8) };

    private static StackPanel Row(string label, UIElement input, string? help = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(new TextBlock { Text = label, Width = 100, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(input);
        if (help != null)
            row.Children.Add(new TextBlock { Text = help, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray });
        return row;
    }
}
