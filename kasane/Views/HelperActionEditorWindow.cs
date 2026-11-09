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
    private static (string Type, string Label)[] Types => new[]
    {
        (HelperActionBody.Launch, Loc.T("プログラムを起動する", "Launch a program (Launch)")),
        (HelperActionBody.Open, Loc.T("URL・ファイル・フォルダを開く", "Open a URL, file, or folder (Open)")),
        (HelperActionBody.Sequence, Loc.T("キー・文字列・マウスの操作", "Key, text, and mouse actions (Sequence)")),
        (HelperActionBody.AppLayer, Loc.T("アプリ管理レイヤーの操作", "Control app-managed layers (AppLayer)")),
        (HelperActionBody.PauseToggle, Loc.T("一時停止・再開", "Pause / resume (PauseToggle)")),
        (HelperActionBody.TextInput, Loc.T("文字列を入力する", "Type text (Text)")), // 末尾に足す（UI Automation 試験が種類を番号で選ぶため）
        (HelperActionBody.KillLine, Loc.T("行末まで切り取る", "Cut to end of line (KillLine)")),
    };

    private static readonly string[] LayerOps = { "On", "Off", "Toggle", "Hold" };
    private static string[] LayerOpLabels => new[]
    {
        // 日本語は動作の名前だけ（On / Toggle / Hold は英語の表示だけに出す。helper.json の LayerOp の値は変えない）。
        Loc.T("有効にする", "On: turn on"), Loc.T("無効にする", "Off: turn off"),
        Loc.T("押すたびに切り替える", "Toggle: switch each time you press"),
        Loc.T("押している間だけ有効にする", "Hold: on only while held"),
    };

    private static readonly string[] ImeMethods = { "Auto", "Imm", "Tsf", "Key", "AltKanji" };
    private static string[] ImeMethodLabels => new[]
    {
        Loc.T("自動", "Automatic"),
        Loc.T("IMM32 に固定", "IMM32 only"),
        Loc.T("TSF に固定(実験的)", "TSF only (experimental)"),
        Loc.T("IME キーに固定", "IME key only"),
        Loc.T("Alt+半角/全角に固定", "Alt+Hankaku/Zenkaku only"),
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
    private readonly TextBox _text = MultiLine("ActionText");
    private readonly StackPanel _textPanel = new();
    private readonly CheckBox _imeOff = new() { Name = "ActionImeOffDuring", Content = Loc.T("入力の間だけ IME を OFF にする", "Turn the IME off while typing"), ToolTip = Loc.T("終わったら元に戻します。すでに OFF なら何も変えません", "Restored afterwards; nothing changes if it is already off"), Margin = new Thickness(0, 8, 0, 0) };
    private readonly ComboBox _imeMethod = new() { Name = "ActionImeMethod", Width = 300 };
    private readonly CheckBox _imeAbort = new() { Name = "ActionImeAbort", Content = Loc.T("OFF にできなければ入力しない", "Don't type if it can't be turned off"), Margin = new Thickness(0, 4, 0, 0) };
    private readonly StackPanel _imeOptions = new();
    // 方式の欄。以前の設定で方式を固定しているときだけ出す（2026-11-05。ふだんは自動だけで、選ばせない）。
    private readonly StackPanel _imeMethodRow = new() { Visibility = Visibility.Collapsed };
    private readonly CheckBox[] _layers = new CheckBox[16];
    private readonly ComboBox _layerOp = new() { Name = "ActionLayerOp", Width = 300 };
    private readonly CheckBox _repeat = new() { Name = "ActionRepeat", Content = Loc.T("キーを押し続けたときも繰り返す", "Repeat while the key is held"), ToolTip = Loc.T("オートリピートのたびに繰り返します", "Repeats on every auto-repeat"), Margin = new Thickness(0, 8, 0, 0) };
    private readonly CheckBox _keepMods = new() { Name = "ActionKeepMods", Content = Loc.T("実行中も、押している修飾キーを送る", "Send held modifier keys while running"), ToolTip = Loc.T("チェックしないと、実行の間だけ Ctrl などを隠します", "If unchecked, Ctrl and the like are hidden during the run"), Margin = new Thickness(0, 6, 0, 0) };
    private readonly StackPanel _launchPanel = new();
    private readonly StackPanel _sequencePanel = new();
    private readonly StackPanel _layerPanel = new();
    private readonly StackPanel _pausePanel = new();
    private readonly TextBlock _pathLabel = new() { Text = "Path", Width = 100, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _argsRow;
    private readonly StackPanel _workDirRow;
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
            panel.Children.Add(Row("Id", _id, Loc.T($"キーには HLP(Id) で割り当てます。0〜{HelperSettingsModel.MaxActionId} で、ほかの動作と重ならない数", $"Assign it to a key as HLP(Id). 0-{HelperSettingsModel.MaxActionId}, not used by another action")));
            panel.Children.Add(Row(Loc.T("名前", "Name"), _name));
        }
        else
        {
            panel.Children.Add(Row(Loc.T("名前", "Name"), _name));
            panel.Children.Add(Row(Loc.T("キー", "Key"), KeycodeField.Wrap(_key, allowMods: false), Loc.T("F13〜F24 など、普段使わないキーが向いています", "Keys you don't normally use, such as F13-F24, work well")));
            var modPanel = new StackPanel { Orientation = Orientation.Horizontal };
            modPanel.Children.Add(_ctrl);
            modPanel.Children.Add(_shift);
            modPanel.Children.Add(_alt);
            modPanel.Children.Add(_win);
            panel.Children.Add(Row(Loc.T("修飾キー", "Modifiers"), modPanel));
        }

        foreach (var t in Types) _type.Items.Add(t.Label);
        panel.Children.Add(Row(Loc.T("種類", "Type"), _type));

        // Launch / Open
        var pathRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        pathRow.Children.Add(_pathLabel);
        pathRow.Children.Add(_path);
        var browse = new Button { Name = "BrowseButton", Content = Loc.T("参照...", "Browse..."), Width = 70, Margin = new Thickness(6, 0, 0, 0) };
        browse.Click += (_, _) => Browse();
        pathRow.Children.Add(browse);
        _launchPanel.Children.Add(pathRow);
        _argsRow = Row(Loc.T("引数", "Arguments"), _args);
        _workDirRow = Row(Loc.T("作業フォルダ", "Working folder"), _workDir);
        _launchPanel.Children.Add(_argsRow);
        _launchPanel.Children.Add(_workDirRow);

        // Sequence
        // 書き方の説明は、マニュアル（11 章「手順の書き方」）へ移した。「?」と F1 で開く（2026-11-08）。
        _sequencePanel.Children.Add(new TextBlock { Text = Loc.T("キーを押したときの手順:", "Steps when the key is pressed:"), Margin = new Thickness(0, 4, 0, 2) });
        _sequencePanel.Children.Add(StepInsertBar.Create(_steps));
        _sequencePanel.Children.Add(_steps);
        _sequencePanel.Children.Add(new TextBlock { Text = Loc.T("キーを離したときの手順(任意):", "Steps when the key is released (optional):"), Margin = new Thickness(0, 8, 0, 2),
            ToolTip = Loc.T("例: マウスのボタンを、キーを押している間だけ押す", "Example: hold a mouse button only while the key is held") });
        _sequencePanel.Children.Add(StepInsertBar.Create(_releaseSteps));
        _sequencePanel.Children.Add(_releaseSteps);

        // Text
        _text.ToolTip = Loc.T($"書いたとおりに送ります。改行は Enter、タブは Tab として送ります。{HelperActionBody.MaxTextLength} 文字まで",
                              $"Sent exactly as written. A line break is sent as Enter and a tab as Tab. Up to {HelperActionBody.MaxTextLength} characters");
        _textPanel.Children.Add(_text);
        _textPanel.Children.Add(_imeOff);
        foreach (var label in ImeMethodLabels) _imeMethod.Items.Add(label);
        _imeOptions.Margin = new Thickness(20, 0, 0, 0);
        _imeMethodRow.Children.Add(Row(Loc.T("IME の方式", "IME method"), _imeMethod));
        _imeMethodRow.Children.Add(Help(Loc.T(
            "以前の設定で、方式を固定しています。固定した方式は、失敗しても別の方式に切り替えません。「自動」にして保存すると、次からこの欄は出ません。",
            "An earlier setting fixes the method. A fixed method is not replaced by another one if it fails. If you choose \"Automatic\" and save, this field no longer appears.")));
        _imeOptions.Children.Add(_imeMethodRow);
        _imeOptions.Children.Add(_imeAbort);
        _textPanel.Children.Add(_imeOptions);

        // AppLayer
        var wrap = new WrapPanel { ToolTip = Loc.T("対象は、そのキーを押したキーボードです", "Applies to the keyboard on which the key was pressed") };
        for (int i = 0; i < _layers.Length; i++)
        {
            _layers[i] = new CheckBox { Name = $"ActionLayer{i}", Content = i.ToString(), Width = 54, Margin = new Thickness(0, 2, 0, 2) };
            _layers[i].Click += (_, _) => Revalidate();
            wrap.Children.Add(_layers[i]);
        }
        _layerPanel.Children.Add(wrap);
        foreach (var label in LayerOpLabels) _layerOp.Items.Add(label);
        _layerPanel.Children.Add(Row(Loc.T("操作", "Operation"), _layerOp));

        // HLP のキーは、一時停止中はリマップが止まって届かない。同じキーで再開できないことだけ、その場合に知らせる。
        if (mode == HelperEditMode.Action)
            _pausePanel.Children.Add(Help(Loc.T("HLP のキーでは再開できません。再開にも使うなら、トリガーにします", "A HLP key can't resume. To resume with a key, use a trigger.")));

        panel.Children.Add(_launchPanel);
        panel.Children.Add(_sequencePanel);
        panel.Children.Add(_textPanel);
        panel.Children.Add(_layerPanel);
        panel.Children.Add(_pausePanel);
        panel.Children.Add(_repeat);
        panel.Children.Add(_keepMods);
        panel.Children.Add(_status);

        var ok = new Button { Name = "OkButton", Content = "OK", IsDefault = true, Width = 90 };
        ok.Click += (_, _) => { if (Commit()) DialogResult = true; };
        var okCancel = new StackPanel { Orientation = Orientation.Horizontal };
        okCancel.Children.Add(ok);
        okCancel.Children.Add(new Button { Name = "CancelButton", Content = Loc.T("キャンセル", "Cancel"), IsCancel = true, Width = 90 });
        var buttons = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = false };
        var help = HelpButton.Create(CurrentHelpTopic);
        DockPanel.SetDock(help, Dock.Left);
        DockPanel.SetDock(okCancel, Dock.Right);
        buttons.Children.Add(help);
        buttons.Children.Add(okCancel);
        HelpButton.AttachF1(this, CurrentHelpTopic);

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
        _imeOff.Click += (_, _) => { _imeOptions.IsEnabled = _imeOff.IsChecked == true; Revalidate(); };
        _imeMethod.SelectionChanged += (_, _) => Revalidate();
        _imeAbort.Click += (_, _) => Revalidate();
        _loading = false;
        ShowPanels();
        Revalidate();
    }

    private string SelectedType => Types[Math.Max(0, _type.SelectedIndex)].Type;

    // 「?」と F1: 手順（Sequence）は書き方の節、トリガーは注意の節、ほかは種類の一覧。
    private string CurrentHelpTopic() =>
        SelectedType == HelperActionBody.Sequence ? HelpTopics.HelperSequence
        : _mode == HelperEditMode.Trigger ? HelpTopics.HelperTrigger
        : HelpTopics.HelperAction;

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
        _text.Text = body.Text ?? "";
        foreach (int l in body.Layers ?? new List<int>())
            if (l >= 0 && l < _layers.Length) _layers[l].IsChecked = true;
        int op = Array.FindIndex(LayerOps, o => string.Equals(o, body.LayerOp, StringComparison.OrdinalIgnoreCase));
        _layerOp.SelectedIndex = op < 0 ? 0 : op;
        _repeat.IsChecked = body.Repeat;
        _keepMods.IsChecked = body.KeepMods;
        _imeOff.IsChecked = body.ImeOffDuring;
        int method = Array.FindIndex(ImeMethods, o => string.Equals(o, body.ImeMethod, StringComparison.OrdinalIgnoreCase));
        _imeMethod.SelectedIndex = method < 0 ? 0 : method;
        _imeMethodRow.Visibility = method > 0 ? Visibility.Visible : Visibility.Collapsed;
        _imeAbort.IsChecked = string.Equals(body.ImeOnFail, "abort", StringComparison.OrdinalIgnoreCase);
        _imeOptions.IsEnabled = body.ImeOffDuring;
    }

    private void ShowPanels()
    {
        string type = SelectedType;
        bool launch = type is HelperActionBody.Launch or HelperActionBody.Open;
        _launchPanel.Visibility = launch ? Visibility.Visible : Visibility.Collapsed;
        _argsRow.Visibility = _workDirRow.Visibility = type == HelperActionBody.Launch ? Visibility.Visible : Visibility.Collapsed;
        string pathTip = type == HelperActionBody.Launch
            ? Loc.T("起動するプログラム。PATH にあれば名前だけでもよい。%USERPROFILE% のような環境変数も使えます", "The program to launch. A name alone is enough if it is on PATH. Environment variables such as %USERPROFILE% can be used")
            : Loc.T("関連付けられたアプリで開きます", "Opens in the associated app");
        _pathLabel.ToolTip = _path.ToolTip = pathTip;
        _sequencePanel.Visibility = type == HelperActionBody.Sequence ? Visibility.Visible : Visibility.Collapsed;
        _textPanel.Visibility = type == HelperActionBody.TextInput ? Visibility.Visible : Visibility.Collapsed;
        _layerPanel.Visibility = type == HelperActionBody.AppLayer ? Visibility.Visible : Visibility.Collapsed;
        _pausePanel.Visibility = type == HelperActionBody.PauseToggle ? Visibility.Visible : Visibility.Collapsed;
        _repeat.Visibility = type == HelperActionBody.PauseToggle ? Visibility.Collapsed : Visibility.Visible;
        // 修飾キーを隠すのは、トリガー（キー＋修飾キー）で動かす操作（Sequence・Text）だけ。
        _keepMods.Visibility = _mode == HelperEditMode.Trigger && type is HelperActionBody.Sequence or HelperActionBody.TextInput ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("プログラム・ファイルを選ぶ", "Choose a program or file"), CheckFileExists = true };
        if (SelectedType == HelperActionBody.Launch)
            dlg.Filter = Loc.T("プログラム (*.exe;*.bat;*.cmd;*.lnk)|*.exe;*.bat;*.cmd;*.lnk|すべてのファイル (*.*)|*.*", "Programs (*.exe;*.bat;*.cmd;*.lnk)|*.exe;*.bat;*.cmd;*.lnk|All files (*.*)|*.*");
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
        var body = new HelperActionBody
        {
            Type = SelectedType,
            Repeat = _repeat.IsChecked == true && SelectedType != HelperActionBody.PauseToggle,
            KeepMods = _mode == HelperEditMode.Trigger && SelectedType is HelperActionBody.Sequence or HelperActionBody.TextInput && _keepMods.IsChecked == true,
        };
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
                errors.AddRange(releaseErrors.Select(e => Loc.T("離したとき ", "On release: ") + e));
                body.Steps = steps.Count > 0 ? steps : null;
                body.ReleaseSteps = releaseSteps.Count > 0 ? releaseSteps : null;
                break;
            case HelperActionBody.TextInput:
                body.Text = _text.Text.Length > 0 ? _text.Text : null; // 前後の空白も含めて、そのまま
                if (_imeOff.IsChecked == true)
                {
                    body.ImeOffDuring = true;
                    string method = ImeMethods[Math.Max(0, _imeMethod.SelectedIndex)];
                    body.ImeMethod = method == "Auto" ? null : method;
                    body.ImeOnFail = _imeAbort.IsChecked == true ? "Abort" : null;
                }
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
                errors.Add(Loc.T($"Id は 0〜{HelperSettingsModel.MaxActionId} の整数で入力してください。", $"Enter an Id as an integer from 0 to {HelperSettingsModel.MaxActionId}."));
            else if (_idInUse(id))
                errors.Add(Loc.T($"Id {id} は、すでに別の動作で使われています。", $"Id {id} is already used by another action."));
        }
        else
        {
            bool keyOk = KeycodeTable.TryResolveBaseKey(_key.Text.Trim(), out ushort sc) && sc != 0;
            if (!keyOk) errors.Add(Loc.T($"キー '{_key.Text.Trim()}' を解決できません(KC_F13 や SC_E06A のような書き方)。", $"Cannot resolve the key '{_key.Text.Trim()}' (write it like KC_F13 or SC_E06A)."));
        }

        if (errors.Count == 0) HelperSettingsModel.ValidateBody(body, Loc.T("この動作", "This action"), errors);
        return errors;
    }

    private void Revalidate()
    {
        if (_loading) return;
        var errors = Check(out _);
        _status.Foreground = errors.Count == 0 ? AppTheme.Palette.OkText : AppTheme.Palette.ErrorText;
        _status.Text = errors.Count == 0 ? "OK" : string.Join("\n", errors);
    }

    private bool Commit()
    {
        var errors = Check(out var body);
        if (errors.Count > 0)
        {
            _status.Foreground = AppTheme.Palette.ErrorText;
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
            ResultName = NullIfEmpty(_name.Text);
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
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Palette.SubText, Margin = new Thickness(0, 0, 0, 8) };

    private static StackPanel Row(string label, UIElement input, string? help = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(new TextBlock { Text = label, Width = 100, VerticalAlignment = VerticalAlignment.Center, ToolTip = help });
        if (help != null && input is FrameworkElement fe) fe.ToolTip ??= help;
        row.Children.Add(input);
        return row;
    }
}
