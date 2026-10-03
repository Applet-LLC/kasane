using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KbdLayerKeymapGui.Model;

namespace KbdLayerKeymapGui.Views;

/// <summary>
/// NICOLA（親指シフト）の表の編集: 上に全体の設定（出力の形式・レイヤー・親指キー・判定時間）、
/// 左に文字キーの一覧、右に選んだ文字キーの「単独 / 親指左 / 親指右」で出すかな。
/// 編集は作業用コピーに対して行い、OKで検証が通ったときだけ Result に返す。
/// </summary>
public sealed class NicolaEditorWindow : Window
{
    private static readonly string[] OutputLabels = { "ローマ字入力のIME（romaji）", "JISかな入力のIME（jis-kana）" };

    private readonly NicolaDef _def;
    private readonly ListBox _list = new() { Name = "KeyList", Width = 380, Margin = new Thickness(0, 0, 10, 0), FontFamily = new FontFamily("Consolas, Meiryo UI") };
    private readonly TextBlock _validation = new() { Name = "ValidationText", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    private readonly ComboBox _output = new() { Name = "OutputBox", Width = 260, ItemsSource = OutputLabels };
    private readonly CheckBox _enabled = new() { Name = "EnabledCheck", Content = "判定する（OFFにすると、設定はキーボードに置いたまま親指シフトをしない）", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _layer = Box("LayerBox", 60);
    private readonly TextBox _thumbLeft = Box("ThumbLeftBox", 110), _thumbRight = Box("ThumbRightBox", 110);
    private readonly TextBox _thumbLeftTap = Box("ThumbLeftTapBox", 110), _thumbRightTap = Box("ThumbRightTapBox", 110);
    private readonly TextBox _onEnter = Box("OnEnterBox", 110), _onExit = Box("OnExitBox", 110);
    private readonly TextBox _simulTerm = Box("SimulTermBox", 60), _overlapRatio = Box("OverlapRatioBox", 60), _minOverlap = Box("MinOverlapBox", 60);
    private readonly CheckBox _continuous = new() { Name = "ContinuousShiftCheck", Content = "連続シフト（親指キーを押している間の文字キーを常にシフトする）", VerticalAlignment = VerticalAlignment.Center };

    private readonly TextBox _key = Box("KeyBox", 140);
    private readonly ComboBox _hand = new() { Name = "HandBox", Width = 140, ItemsSource = new[] { "左手側", "右手側" } };
    private readonly TextBox _single = Box("SingleBox", 80), _kanaLeft = Box("ThumbLeftKanaBox", 80), _kanaRight = Box("ThumbRightKanaBox", 80);
    private bool _loading;

    public NicolaDef? Result { get; private set; }

    public NicolaEditorWindow(NicolaDef def)
    {
        _def = def.Clone();
        Title = "NICOLAの表";
        Width = 1000;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        // --- 全体の設定 ---
        var header = new StackPanel();
        header.Children.Add(new TextBlock
        {
            Text = "キーは KC_* で指定します（親指キーの例: KC_MHEN 無変換 / KC_HENK 変換 / KC_SPC）。単独打鍵の欄を空にすると、親指キーを単独で押しても何も送りません。" +
                   "有効・無効のときのキーは、判定が有効・無効になったときに1回押すキーです（IMEのオン・オフ等。空 = なし）。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 6),
        });
        header.Children.Add(Line(("出力の形式", _output), ("レイヤー", _layer)));
        header.Children.Add(Line(("", _enabled)));
        header.Children.Add(Line(("親指左", _thumbLeft), ("単独打鍵", _thumbLeftTap), ("親指右", _thumbRight), ("単独打鍵", _thumbRightTap)));
        header.Children.Add(Line(("有効のとき", _onEnter), ("無効のとき", _onExit)));
        header.Children.Add(Line(("判定時間(ms)", _simulTerm), ("重なりの比率", _overlapRatio), ("最小の重なり(ms)", _minOverlap)));
        header.Children.Add(Line(("", _continuous)));
        var headerBox = new GroupBox { Header = "全体", Content = header, Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 8) };

        // --- 文字キー ---
        var detail = new StackPanel();
        detail.Children.Add(new TextBlock
        {
            Text = "選んだ文字キーを、単独で・親指左と同時に・親指右と同時に押したときに出す「かな」を1文字ずつ入れます" +
                   "（空 = 何も出さない）。濁音（が等）・小書き（ぁ等）・ー。、・，．゛゜ も使えます。" +
                   "左手側・右手側は、nicola.json の \"left\" / \"right\" のどちらに書くかです。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 6),
        });
        detail.Children.Add(Line(("文字キー", _key), ("", _hand)));
        detail.Children.Add(Line(("単独", _single), ("親指左", _kanaLeft), ("親指右", _kanaRight)));
        var add = new Button { Name = "AddKeyButton", Content = "文字キーを追加", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 8, 6, 0) };
        var remove = new Button { Name = "RemoveKeyButton", Content = "選んだ文字キーを削除", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 8, 6, 0) };
        var keyButtons = new StackPanel { Orientation = Orientation.Horizontal };
        keyButtons.Children.Add(add);
        keyButtons.Children.Add(remove);
        detail.Children.Add(keyButtons);
        detail.Children.Add(_validation);

        var ok = new Button { Name = "OkButton", Content = "OK", IsDefault = true, Width = 90 };
        ok.Click += (_, _) =>
        {
            if (Validate()) { Result = _def; DialogResult = true; }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Name = "CancelButton", Content = "キャンセル", IsCancel = true, Width = 90 });

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
                row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), MinWidth = 70 });
            input.Margin = new Thickness(0, 0, 16, 0);
            row.Children.Add(input);
        }
        return row;
    }

    private static string ItemText(NicolaKeyDef k)
    {
        static string S(string s) => string.IsNullOrEmpty(s) ? "－" : s;
        string key = k.Key.Length > 0 ? k.Key : "（未指定）";
        return $"{key,-9}{(k.RightHand ? "右" : "左")}  単独 {S(k.Single)}  左 {S(k.ThumbLeft)}  右 {S(k.ThumbRight)}";
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
        else errors.Add("レイヤーは整数で入力してください");
        if (uint.TryParse(_simulTerm.Text, out var simul)) _def.SimulTermMs = simul;
        else errors.Add("判定時間は整数(ms)で入力してください");
        if (double.TryParse(_overlapRatio.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio)) _def.OverlapRatio = ratio;
        else errors.Add("重なりの比率は数値（例: 1.0）で入力してください");
        if (uint.TryParse(_minOverlap.Text, out var minOverlap)) _def.MinOverlapMs = minOverlap;
        else errors.Add("最小の重なりは整数(ms)で入力してください");

        var warnings = new List<string>();
        if (errors.Count == 0) NicolaService.BuildBlob(_def, warnings, errors);
        if (errors.Count > 0)
        {
            _validation.Foreground = Brushes.Firebrick;
            _validation.Text = string.Join("\n", errors);
            return false;
        }
        _validation.Foreground = warnings.Count > 0 ? Brushes.DarkOrange : Brushes.DarkGreen;
        _validation.Text = warnings.Count > 0 ? "注意（OKは押せます）:\n" + string.Join("\n", warnings) : "OK";
        return true;
    }
}
