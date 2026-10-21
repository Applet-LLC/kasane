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
        (HelperActionBody.Launch, Loc.T("プログラムを起動する (Launch)", "Launch a program (Launch)")),
        (HelperActionBody.Open, Loc.T("URL・ファイル・フォルダを開く (Open)", "Open a URL, file, or folder (Open)")),
        (HelperActionBody.Sequence, Loc.T("キー・文字列・マウスの操作 (Sequence)", "Key, text, and mouse actions (Sequence)")),
        (HelperActionBody.AppLayer, Loc.T("アプリ管理レイヤーの操作 (AppLayer)", "Control app-managed layers (AppLayer)")),
        (HelperActionBody.PauseToggle, Loc.T("一時停止・再開 (PauseToggle)", "Pause / resume (PauseToggle)")),
        (HelperActionBody.TextInput, Loc.T("文字列を入力する (Text)", "Type text (Text)")), // 末尾に足す（UI Automation 試験が種類を番号で選ぶため）
    };

    private static readonly string[] LayerOps = { "On", "Off", "Toggle", "Hold" };
    private static string[] LayerOpLabels => new[]
    {
        Loc.T("On: 立てる", "On: turn on"), Loc.T("Off: 下ろす", "Off: turn off"),
        Loc.T("Toggle: 押すたびに切り替える", "Toggle: switch each time you press"),
        Loc.T("Hold: 押している間だけ立てる", "Hold: on only while held"),
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
    private readonly CheckBox[] _layers = new CheckBox[16];
    private readonly ComboBox _layerOp = new() { Name = "ActionLayerOp", Width = 300 };
    private readonly CheckBox _repeat = new() { Name = "ActionRepeat", Content = Loc.T("キーを押し続けたとき(オートリピート)も繰り返す", "Repeat while the key is held down (auto-repeat)"), Margin = new Thickness(0, 8, 0, 0) };
    private readonly CheckBox _keepMods = new() { Name = "ActionKeepMods", Content = Loc.T("実行中も、押している修飾キー(Ctrl など)を送る(チェックしないと、実行の間だけ隠す)", "Send held modifier keys (such as Ctrl) while running (if unchecked, they are hidden during the run)"), Margin = new Thickness(0, 6, 0, 0) };
    private readonly StackPanel _launchPanel = new();
    private readonly StackPanel _sequencePanel = new();
    private readonly StackPanel _layerPanel = new();
    private readonly StackPanel _pausePanel = new();
    private readonly StackPanel _argsRow;
    private readonly StackPanel _workDirRow;
    private readonly TextBlock _pathHelp = new() { TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Palette.SubText, Margin = new Thickness(0, 0, 0, 4) };
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
            panel.Children.Add(Help(Loc.T(
                ("この動作は、キーに割り当てた HLP(Id) を押したときに動きます(キー割り当て画面の「ヘルパー動作」から選びます)。" +
                 "Id は、このキーボードの分と共通の分で重ならない数(0〜65535)にします。"),
                ("This action runs when you press a key assigned HLP(Id) (choose it from \"Helper actions\" on the key assignment screen). " +
                 "Choose an Id (0-65535) that is not used by another action, either for this keyboard or shared."))));
            panel.Children.Add(Row("Id", _id, "HLP(Id)"));
            panel.Children.Add(Row(Loc.T("名前", "Name"), _name));
        }
        else
        {
            panel.Children.Add(Help(Loc.T(
                ("このトリガーは、トレイのキーボードフックで受けます(トリガーが1つでもあると、フックを張ります)。" +
                 "押したキーはアプリに届きません。F13〜F24 など、普段使わないキーが向いています。" +
                 "修飾キーは左右を区別せず、押している分と過不足なく一致したときだけ動きます。Alt・Win の組み合わせは、" +
                 "単独で離したときにメニューやスタートが開くことがあるので、避けてください。"),
                ("This trigger is received by the tray's keyboard hook (the hook is installed when there is at least one trigger). " +
                 "The pressed key does not reach the app. Keys you don't normally use, such as F13-F24, work well. " +
                 "Modifier keys are matched without distinguishing left and right, and the trigger fires only when exactly the held modifiers match. " +
                 "Avoid combinations of Alt and Win, because releasing them alone may open a menu or the Start menu."))));
            panel.Children.Add(Row(Loc.T("キー", "Key"), KeycodeField.Wrap(_key, allowMods: false), Loc.T("KC_F13、SC_E06A のような書き方", "Written like KC_F13 or SC_E06A")));
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
        _launchPanel.Children.Add(_pathHelp);
        var pathRow = Row("Path", _path);
        var browse = new Button { Name = "BrowseButton", Content = Loc.T("参照...", "Browse..."), Width = 70, Margin = new Thickness(6, 0, 0, 0) };
        browse.Click += (_, _) => Browse();
        pathRow.Children.Add(browse);
        _launchPanel.Children.Add(pathRow);
        _argsRow = Row(Loc.T("引数", "Arguments"), _args);
        _workDirRow = Row(Loc.T("作業フォルダ", "Working folder"), _workDir);
        _launchPanel.Children.Add(_argsRow);
        _launchPanel.Children.Add(_workDirRow);
        _launchPanel.Children.Add(new TextBlock { Text = Loc.T("%USERPROFILE% のような環境変数が使えます。昇格(管理者として実行)はしません。", "Environment variables such as %USERPROFILE% can be used. The program is not elevated (not run as administrator)."), Foreground = AppTheme.Palette.SubText, TextWrapping = TextWrapping.Wrap });

        // Sequence
        _sequencePanel.Children.Add(Help(Loc.T(
            ("1行が1手順です。書き方:\n" +
             "  key LCTL(KC_C)   キーを押して離す(KC_A、LCTL(KC_C)、SC_E06A など)\n" +
             "  down KC_A / up KC_A   押す / 離す\n" +
             "  text こんにちは\\n   文字列を打つ(\\n = Enter、\\t = Tab、\\\\ = バックスラッシュ。バックスラッシュは日本語のフォントでは円記号(¥)の形で表示されます。¥ キーで入力できます)\n" +
             "  delay 50   待つ(ミリ秒、0〜10000)\n" +
             "  mouse left / mouse right down / mouse x1 up   ボタン(left right middle x1 x2、click down up)\n" +
             "  move 10 -5 / moveto 100 200   マウスを動かす(今の位置から / 画面の座標へ)\n" +
             "  wheel -120 / hwheel 120   ホイール(120 で1ノッチ)\n" +
             "# で始まる行は無視します。\n" +
             "欄の上のボタンで、キー名(キーを押して挿入／選んで挿入)やマウス操作を、カーソルのある行の次に1行挿入できます。"),
            ("One step per line. Syntax:\n" +
             "  key LCTL(KC_C)   Press and release a key (KC_A, LCTL(KC_C), SC_E06A, etc.)\n" +
             "  down KC_A / up KC_A   Press / release\n" +
             "  text Hello\\n   Type a string (\\n = Enter, \\t = Tab, \\\\ = backslash)\n" +
             "  delay 50   Wait (milliseconds, 0-10000)\n" +
             "  mouse left / mouse right down / mouse x1 up   Buttons (left right middle x1 x2, click down up)\n" +
             "  move 10 -5 / moveto 100 200   Move the mouse (from the current position / to screen coordinates)\n" +
             "  wheel -120 / hwheel 120   Wheel (120 = one notch)\n" +
             "Lines starting with # are ignored.\n" +
             "The buttons above the box insert one line after the cursor line: a key name (press a key to insert / pick one to insert) or a mouse action."))));
        _sequencePanel.Children.Add(new TextBlock { Text = Loc.T("キーを押したときの手順:", "Steps when the key is pressed:"), Margin = new Thickness(0, 4, 0, 2) });
        _sequencePanel.Children.Add(StepInsertBar.Create(_steps));
        _sequencePanel.Children.Add(_steps);
        _sequencePanel.Children.Add(new TextBlock { Text = Loc.T("キーを離したときの手順(任意。マウスのボタンを押している間だけ押す、など):", "Steps when the key is released (optional; for example, hold a mouse button only while the key is held):"), Margin = new Thickness(0, 8, 0, 2) });
        _sequencePanel.Children.Add(StepInsertBar.Create(_releaseSteps));
        _sequencePanel.Children.Add(_releaseSteps);

        // Text
        _textPanel.Children.Add(Help(Loc.T(
            ("入力した文字列を、そのまま送ります(例: Apple 漢字)。キーボードで打ったように、いま入力できる場所(エディタなど)の現在の位置に追記されます。" +
             "日本語や記号もそのまま送れます。改行は Enter、タブは Tab として送ります。バックスラッシュなどに特別な意味はありません(そのまま送ります)。" +
             $"{HelperActionBody.MaxTextLength} 文字までです。"),
            ("Sends the text exactly as entered (for example: Apple 漢字). As if typed on the keyboard, it is inserted at the current position wherever you can type now (such as an editor). " +
             "Japanese characters and symbols are sent as they are. A line break is sent as Enter and a tab as Tab. Backslashes and similar characters have no special meaning (they are sent as they are). " +
             $"Up to {HelperActionBody.MaxTextLength} characters."))));
        _textPanel.Children.Add(_text);

        // AppLayer
        _layerPanel.Children.Add(Help(Loc.T("アプリ管理レイヤーを操作します(IME連動・前面のアプリの規則と合わせて、ORで効きます)。対象は、そのキーを押したキーボードです。", "Controls app-managed layers (combined with IME link and per-app rules by OR). It applies to the keyboard on which the key was pressed.")));
        var wrap = new WrapPanel();
        for (int i = 0; i < _layers.Length; i++)
        {
            _layers[i] = new CheckBox { Name = $"ActionLayer{i}", Content = i.ToString(), Width = 54, Margin = new Thickness(0, 2, 0, 2) };
            _layers[i].Click += (_, _) => Revalidate();
            wrap.Children.Add(_layers[i]);
        }
        _layerPanel.Children.Add(wrap);
        foreach (var label in LayerOpLabels) _layerOp.Items.Add(label);
        _layerPanel.Children.Add(Row(Loc.T("操作", "Operation"), _layerOp));

        _pausePanel.Children.Add(Help(Loc.T("トレイの一時停止・再開を切り替えます。一時停止中は、キーフックのトリガーだけが動きます(HLP のキーはリマップが止まるので届きません)。同じキーで再開したいときは、F13〜F24 などのトリガーに結び付けてください。", "Toggles pause / resume in the tray. While paused, only key hook triggers work (keys assigned HLP do not arrive because remapping is stopped). To resume with the same key, bind it to a trigger such as F13-F24.")));

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
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Name = "CancelButton", Content = Loc.T("キャンセル", "Cancel"), IsCancel = true, Width = 90 });

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
        _text.Text = body.Text ?? "";
        foreach (int l in body.Layers ?? new List<int>())
            if (l >= 0 && l < _layers.Length) _layers[l].IsChecked = true;
        int op = Array.FindIndex(LayerOps, o => string.Equals(o, body.LayerOp, StringComparison.OrdinalIgnoreCase));
        _layerOp.SelectedIndex = op < 0 ? 0 : op;
        _repeat.IsChecked = body.Repeat;
        _keepMods.IsChecked = body.KeepMods;
    }

    private void ShowPanels()
    {
        string type = SelectedType;
        bool launch = type is HelperActionBody.Launch or HelperActionBody.Open;
        _launchPanel.Visibility = launch ? Visibility.Visible : Visibility.Collapsed;
        _argsRow.Visibility = _workDirRow.Visibility = type == HelperActionBody.Launch ? Visibility.Visible : Visibility.Collapsed;
        _pathHelp.Text = type == HelperActionBody.Launch
            ? Loc.T("起動するプログラムのパス(notepad.exe のように、PATH にあるものは名前だけでもよい)。", "Path of the program to launch (for a program on PATH, such as notepad.exe, the name alone is enough).")
            : Loc.T("開く URL・ファイル・フォルダ。関連付けられたアプリで開きます。", "The URL, file, or folder to open. It opens in its associated app.");
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
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Palette.SubText, Margin = new Thickness(0, 0, 0, 8) };

    private static StackPanel Row(string label, UIElement input, string? help = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(new TextBlock { Text = label, Width = 100, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(input);
        if (help != null)
            row.Children.Add(new TextBlock { Text = help, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Palette.SubText });
        return row;
    }
}
