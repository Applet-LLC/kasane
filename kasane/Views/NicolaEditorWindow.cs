using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>
/// NICOLA（親指シフト）の表の編集: 上に全体の設定（出力の形式・レイヤー・親指キー・判定時間）、
/// 左に文字キーの一覧、右に選んだ文字キーの「単独 / 親指左 / 親指右」で出すかな。
/// 編集は作業用コピーに対して行い、OKで検証が通ったときだけ Result に返す。
/// </summary>
public sealed class NicolaEditorWindow : Window
{
    private static string[] OutputLabels => new[]
    {
        Loc.T("ローマ字入力の IME", "IME with romaji input (romaji)"),
        Loc.T("JIS かな入力の IME", "IME with JIS kana input (jis-kana)"),
    };

    private readonly NicolaDef _def;
    private readonly ListBox _list = new() { Name = "KeyList", Width = 380, Margin = new Thickness(0, 0, 10, 0), FontFamily = new FontFamily("Consolas, Meiryo UI") };
    private readonly TextBlock _validation = new() { Name = "ValidationText", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    private readonly ComboBox _output = new() { Name = "OutputBox", Width = 260, ItemsSource = OutputLabels };
    private readonly CheckBox _enabled = new() { Name = "EnabledCheck", Content = Loc.T("判定する", "Enable detection"),
        ToolTip = Loc.T("OFF にすると、設定はキーボードに残したまま、親指シフトをしません", "When OFF, the settings stay on the keyboard but thumb shifting is not performed"), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _layer = Box("LayerBox", 60);
    private readonly TextBox _thumbLeft = Box("ThumbLeftBox", 110), _thumbRight = Box("ThumbRightBox", 110);
    private readonly TextBox _thumbLeftTap = Box("ThumbLeftTapBox", 110), _thumbRightTap = Box("ThumbRightTapBox", 110);
    private readonly TextBox _onEnter = Box("OnEnterBox", 110), _onExit = Box("OnExitBox", 110);
    private readonly TextBox _simulTerm = Box("SimulTermBox", 60), _overlapRatio = Box("OverlapRatioBox", 60), _minOverlap = Box("MinOverlapBox", 60);
    private readonly CheckBox _continuous = new() { Name = "ContinuousShiftCheck", Content = Loc.T("連続シフト", "Continuous shift"),
        ToolTip = Loc.T("親指キーを押している間の文字キーを、常にシフトします", "Always shifts character keys while a thumb key is held"), VerticalAlignment = VerticalAlignment.Center };

    private readonly TextBox _key = Box("KeyBox", 140);
    private readonly ComboBox _hand = new() { Name = "HandBox", Width = 140, ItemsSource = new[] { Loc.T("左手側", "Left hand"), Loc.T("右手側", "Right hand") } };
    private readonly TextBox _single = Box("SingleBox", 80), _kanaLeft = Box("ThumbLeftKanaBox", 80), _kanaRight = Box("ThumbRightKanaBox", 80);
    private bool _loading;

    public NicolaDef? Result { get; private set; }

    public NicolaEditorWindow(NicolaDef def)
    {
        _def = def.Clone();
        Title = Loc.T("NICOLAの表", "NICOLA table");
        Width = 1000;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        // --- 全体の設定 ---
        var header = new StackPanel();
        // 欄の意味は、ツールチップとマニュアル（10 章「表を編集する」）に置く（2026-11-08）。
        string thumbTip = Loc.T("例: KC_MHEN(無変換)、KC_HENK(変換)、KC_SPC", "For example KC_MHEN (Muhenkan), KC_HENK (Henkan), KC_SPC");
        string tapTip = Loc.T("空なら、親指キーだけを押しても何も送りません", "If empty, pressing the thumb key alone sends nothing");
        _thumbLeft.ToolTip = _thumbRight.ToolTip = thumbTip;
        _thumbLeftTap.ToolTip = _thumbRightTap.ToolTip = tapTip;
        _onEnter.ToolTip = Loc.T("判定が有効になったときに 1 回押すキー(IME のオンなど)", "Pressed once when detection becomes enabled (for example IME on)");
        _onExit.ToolTip = Loc.T("判定が無効になったときに 1 回押すキー(IME のオフなど)", "Pressed once when detection becomes disabled (for example IME off)");
        header.Children.Add(Line((Loc.T("出力の形式", "Output format"), _output), (Loc.T("レイヤー", "Layer"), _layer)));
        header.Children.Add(Line(("", _enabled)));
        header.Children.Add(Line((Loc.T("親指左", "Left thumb"), _thumbLeft), (Loc.T("単独打鍵", "Solo press"), _thumbLeftTap), (Loc.T("親指右", "Right thumb"), _thumbRight), (Loc.T("単独打鍵", "Solo press"), _thumbRightTap)));
        header.Children.Add(Line((Loc.T("有効のとき", "When enabled"), _onEnter), (Loc.T("無効のとき", "When disabled"), _onExit)));
        header.Children.Add(Line((Loc.T("判定時間(ms)", "Detection time (ms)"), _simulTerm), (Loc.T("重なりの比率", "Overlap ratio"), _overlapRatio), (Loc.T("最小の重なり(ms)", "Min overlap (ms)"), _minOverlap)));
        header.Children.Add(Line(("", _continuous)));
        var headerBox = new GroupBox { Header = Loc.T("全体", "Overall"), Content = header, Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 8) };

        // --- 文字キー ---
        var detail = new StackPanel();
        string kanaTip = Loc.T("かなを 1 文字。濁音・小書き・ー。、・，．゛゜ も使えます。空なら何も出しません",
                               "One kana. Voiced and small kana and ー。、・，．゛゜ can be used. Empty outputs nothing");
        _single.ToolTip = _kanaLeft.ToolTip = _kanaRight.ToolTip = kanaTip;
        _hand.ToolTip = Loc.T("nicola.json の \"left\" と \"right\" の、どちらに書くか", "Which of \"left\" / \"right\" in nicola.json it is written under");
        // 文字キーは、ほかの画面と同じ「押して入力」「選ぶ...」で入れられる（2026-11-06、GUI 計画 1-4。修飾キーは使わない）。
        detail.Children.Add(Line((Loc.T("文字キー", "Character key"), KeycodeField.Wrap(_key, allowMods: false)), ("", _hand)));
        detail.Children.Add(Line((Loc.T("単独", "Solo"), _single), (Loc.T("親指左", "Left thumb"), _kanaLeft), (Loc.T("親指右", "Right thumb"), _kanaRight)));
        var add = new Button { Name = "AddKeyButton", Content = Loc.T("文字キーを追加", "Add character key"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 8, 6, 0) };
        var remove = new Button { Name = "RemoveKeyButton", Content = Loc.T("選んだ文字キーを削除", "Delete selected key"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 8, 6, 0) };
        var duplicate = new Button { Name = "DuplicateKeyButton", Content = Loc.T("複製", "Duplicate"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 8, 6, 0),
                                     ToolTip = Loc.T("選んだ行を、文字キーを空にして下に写します", "Copies the selected row below it, with the key left empty") };
        var keyButtons = new StackPanel { Orientation = Orientation.Horizontal };
        keyButtons.Children.Add(add);
        keyButtons.Children.Add(duplicate);
        keyButtons.Children.Add(remove);
        detail.Children.Add(keyButtons);
        detail.Children.Add(_validation);

        var ok = new Button { Name = "OkButton", Content = "OK", IsDefault = true, Width = 90 };
        ok.Click += (_, _) =>
        {
            if (Validate()) { Result = _def; DialogResult = true; }
        };
        var okCancel = new StackPanel { Orientation = Orientation.Horizontal };
        okCancel.Children.Add(ok);
        okCancel.Children.Add(new Button { Name = "CancelButton", Content = Loc.T("キャンセル", "Cancel"), IsCancel = true, Width = 90 });
        var buttons = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = false };
        var help = HelpButton.Create(HelpTopics.NicolaTable);
        DockPanel.SetDock(help, Dock.Left);
        DockPanel.SetDock(okCancel, Dock.Right);
        buttons.Children.Add(help);
        buttons.Children.Add(okCancel);
        HelpButton.AttachF1(this, () => HelpTopics.NicolaTable);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(headerBox, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_list, Dock.Left);
        root.Children.Add(headerBox);
        root.Children.Add(buttons);
        root.Children.Add(_list);
        root.Children.Add(new ScrollViewer { Content = detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        LoadHeader();
        foreach (var k in _def.Keys) _list.Items.Add(new ListBoxItem { Content = ItemText(k) });

        foreach (var b in new[] { _layer, _thumbLeft, _thumbRight, _thumbLeftTap, _thumbRightTap, _onEnter, _onExit, _simulTerm, _overlapRatio, _minOverlap })
            b.TextChanged += (_, _) => Validate();
        _output.SelectionChanged += (_, _) => Validate();
        _enabled.Click += (_, _) => Validate();
        _continuous.Click += (_, _) => Validate();

        foreach (var b in new[] { _key, _single, _kanaLeft, _kanaRight }) b.TextChanged += (_, _) => KeyChanged();
        _hand.SelectionChanged += (_, _) => KeyChanged();
        _list.SelectionChanged += (_, _) => ShowKey();
        add.Click += (_, _) =>
        {
            _def.Keys.Add(new NicolaKeyDef());
            _list.Items.Add(new ListBoxItem { Content = ItemText(_def.Keys[^1]) });
            _list.SelectedIndex = _def.Keys.Count - 1;
            _key.Focus();
        };
        duplicate.Click += (_, _) =>
        {
            int i = _list.SelectedIndex;
            if (i < 0) return;
            var src = _def.Keys[i];
            var copy = new NicolaKeyDef { Key = "", RightHand = src.RightHand, Single = src.Single, ThumbLeft = src.ThumbLeft, ThumbRight = src.ThumbRight };
            _def.Keys.Insert(i + 1, copy);
            _list.Items.Insert(i + 1, new ListBoxItem { Content = ItemText(copy) });
            _list.SelectedIndex = i + 1;
            _key.Focus();
            Validate();
        };
        remove.Click += (_, _) =>
        {
            int i = _list.SelectedIndex;
            if (i < 0) return;
            _def.Keys.RemoveAt(i);
            _list.Items.RemoveAt(i);
            _list.SelectedIndex = Math.Min(i, _def.Keys.Count - 1);
            Validate();
        };

        Loaded += (_, _) =>
        {
            _list.SelectedIndex = _def.Keys.Count > 0 ? 0 : -1;
            ShowKey();
            Validate();
        };
    }

    private static TextBox Box(string name, double width) =>
        new() { Name = name, Width = width, FontFamily = new FontFamily("Consolas, Meiryo UI") };

    private static StackPanel Line(params (string Label, FrameworkElement Input)[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        foreach (var (label, input) in items)
        {
            if (label.Length > 0)
                row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), MinWidth = 70, ToolTip = input.ToolTip });
            input.Margin = new Thickness(0, 0, 16, 0);
            row.Children.Add(input);
        }
        return row;
    }

    private static string ItemText(NicolaKeyDef k)
    {
        static string S(string s) => string.IsNullOrEmpty(s) ? "－" : s;
        string key = k.Key.Length > 0 ? k.Key : Loc.T("(未指定)", "(not set)");
        return Loc.T($"{key,-9}{(k.RightHand ? "右" : "左")}  単独 {S(k.Single)}  左 {S(k.ThumbLeft)}  右 {S(k.ThumbRight)}",
                     $"{key,-9}{(k.RightHand ? "R" : "L")}  Solo {S(k.Single)}  L {S(k.ThumbLeft)}  R {S(k.ThumbRight)}");
    }

    private void LoadHeader()
    {
        _output.SelectedIndex = _def.Output == "jis-kana" ? 1 : 0;
        _enabled.IsChecked = _def.Enabled;
        _layer.Text = _def.Layer.ToString();
        _thumbLeft.Text = _def.ThumbLeft;
        _thumbRight.Text = _def.ThumbRight;
        _thumbLeftTap.Text = _def.ThumbLeftTap;
        _thumbRightTap.Text = _def.ThumbRightTap;
        _onEnter.Text = _def.OnEnter;
        _onExit.Text = _def.OnExit;
        _simulTerm.Text = _def.SimulTermMs.ToString();
        _overlapRatio.Text = _def.OverlapRatio.ToString("0.0##", CultureInfo.InvariantCulture);
        _minOverlap.Text = _def.MinOverlapMs.ToString();
        _continuous.IsChecked = _def.ContinuousShift;
    }

    private void ShowKey()
    {
        int i = _list.SelectedIndex;
        _loading = true;
        foreach (var b in new[] { _key, _single, _kanaLeft, _kanaRight }) b.IsEnabled = i >= 0;
        _hand.IsEnabled = i >= 0;
        var k = i >= 0 ? _def.Keys[i] : new NicolaKeyDef();
        _key.Text = k.Key;
        _hand.SelectedIndex = k.RightHand ? 1 : 0;
        _single.Text = k.Single;
        _kanaLeft.Text = k.ThumbLeft;
        _kanaRight.Text = k.ThumbRight;
        _loading = false;
    }

    private void KeyChanged()
    {
        int i = _list.SelectedIndex;
        if (_loading || i < 0) return;
        var k = _def.Keys[i];
        k.Key = _key.Text.Trim();
        k.RightHand = _hand.SelectedIndex == 1;
        k.Single = _single.Text.Trim();
        k.ThumbLeft = _kanaLeft.Text.Trim();
        k.ThumbRight = _kanaRight.Text.Trim();
        ((ListBoxItem)_list.Items[i]).Content = ItemText(k);
        Validate();
    }

    /// <summary>全体の設定を作業用コピーへ取り込み、blobを組み立てて検証する。エラーが無ければ true。</summary>
    private bool Validate()
    {
        var errors = new List<string>();
        _def.Output = _output.SelectedIndex == 1 ? "jis-kana" : "romaji";
        _def.Enabled = _enabled.IsChecked == true;
        _def.ContinuousShift = _continuous.IsChecked == true;
        _def.ThumbLeft = _thumbLeft.Text.Trim();
        _def.ThumbRight = _thumbRight.Text.Trim();
        _def.ThumbLeftTap = _thumbLeftTap.Text.Trim();
        _def.ThumbRightTap = _thumbRightTap.Text.Trim();
        _def.OnEnter = _onEnter.Text.Trim();
        _def.OnExit = _onExit.Text.Trim();
        if (int.TryParse(_layer.Text, out var layer)) _def.Layer = layer;
        else errors.Add(Loc.T("レイヤーは整数で入力してください", "Enter the layer as an integer"));
        if (uint.TryParse(_simulTerm.Text, out var simul)) _def.SimulTermMs = simul;
        else errors.Add(Loc.T("判定時間は整数(ms)で入力してください", "Enter the detection time as an integer (ms)"));
        if (double.TryParse(_overlapRatio.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio)) _def.OverlapRatio = ratio;
        else errors.Add(Loc.T("重なりの比率は数値(例: 1.0)で入力してください", "Enter the overlap ratio as a number (for example 1.0)"));
        if (uint.TryParse(_minOverlap.Text, out var minOverlap)) _def.MinOverlapMs = minOverlap;
        else errors.Add(Loc.T("最小の重なりは整数(ms)で入力してください", "Enter the minimum overlap as an integer (ms)"));

        var warnings = new List<string>();
        if (errors.Count == 0) NicolaService.BuildBlob(_def, warnings, errors);
        if (errors.Count > 0)
        {
            _validation.Foreground = AppTheme.Palette.ErrorText;
            _validation.Text = string.Join("\n", errors);
            return false;
        }
        _validation.Foreground = warnings.Count > 0 ? AppTheme.Palette.WarnText : AppTheme.Palette.OkText;
        _validation.Text = warnings.Count > 0 ? Loc.T("注意(OKは押せます):\n", "Note (you can still click OK):\n") + string.Join("\n", warnings) : "OK";
        return true;
    }
}
