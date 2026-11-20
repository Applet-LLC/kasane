using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>
/// 設定画面の「文字入力の速さ」タブ（2026-11-20）。ヘルパー動作の Text と、Sequence の Text 手順の、1 文字ごとの間隔・揺らぎ・
/// IME 操作前のウェイト（<see cref="HelperConfig.TextTyping"/>）。対象は「このキーボード」か「全キーボード共通」。
/// 上段がその範囲の既定、下段がレイヤー番号ごとの上書き（一覧に「追加」で足していく）。
/// 数の欄の空欄と、揺らぎの選択を触っていない状態は、ひとつ外側の値を使う（<see cref="TextTyping.Resolve"/>）。
/// 編集は <see cref="HelperSettingsModel"/> のコピーに対して行い、書くのは設定画面の OK のとき。
/// </summary>
internal sealed class TextTypingTab : HelperTabBase
{
    private sealed class Fields
    {
        public ComboBox Jitter = null!; // 追加 / 無し
        public TextBox JitterMs = null!, CharMs = null!, ImeWaitMs = null!;
        // 空欄のときに、欄の中に薄く出す値（空欄で使われる値）。
        public TextBlock JitterMsHint = null!, CharMsHint = null!, ImeWaitMsHint = null!;
        /// <summary>この欄の組を、書かなかったときに使われる値（ひとつ外側の設定）。揺らぎの選択を「触っていない」かの判定にも使う。</summary>
        public TextTypingProfile Inherited = TextTypingProfile.Default;
        public IEnumerable<Control> All => new Control[] { Jitter, JitterMs, CharMs, ImeWaitMs };
    }

    private readonly KeymapProject? _project;
    private readonly Fields _base;
    private readonly Fields _layer;
    private readonly ListBox _layerList = ListBoxOf("TextTypingLayerList", 84);
    private readonly ComboBox _layerBox = new() { Name = "TextTypingLayerBox", Width = 200 };
    private readonly Button _addLayer = Btn("TextTypingLayerAdd", Loc.T("追加", "Add"), 80);
    private readonly Button _clearLayer = Btn("TextTypingLayerClear", Loc.T("このレイヤーの上書きを消す", "Clear this layer's override"), 190);
    private readonly TextBlock _layerSummary = new() { Name = "TextTypingLayerSummary", TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Palette.SubText, Margin = new Thickness(0, 4, 0, 4) };
    private readonly Dictionary<TextBox, string> _invalid = new();
    // 範囲ごとに、一覧に出しているレイヤー（値を書いたもの＋「追加」で足して、まだ値を書いていないもの）。
    private readonly Dictionary<HelperScope, SortedSet<int>> _listed = new();
    // 一覧の各項目のレイヤー番号。
    private readonly List<int> _entries = new();
    private bool _loading;

    public TextTypingTab(HelperSettingsModel model, Window owner, KeymapProject? project)
        : base(model, owner, "TextTyping")
    {
        _project = project;

        Stack.Children.Add(Heading(Loc.T("この範囲の既定", "Defaults for this scope")));
        _base = BuildFields("TextTyping", Stack,
            Loc.T("触っていない(空欄の)項目は、共通の設定(このキーボードのとき)か、kasane の既定を使います", "An item you have not touched (a blank box) uses the shared settings (for this keyboard) or the kasane default"));

        Stack.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 8) });
        Stack.Children.Add(Heading(Loc.T("レイヤーごとの上書き", "Per-layer overrides")));
        string layerTip = Loc.T("そのレイヤーが有効なときに使います。複数のレイヤーが有効なら、番号の大きいレイヤーの値が先です。共通の設定に書くと、どのキーボードでも同じ番号のレイヤーに効きます。アプリごとに変えるには、アプリ別レイヤーでレイヤーを割り当てます",
                                "Used while that layer is on. If several layers are on, the higher layer's values come first. In the shared settings, it applies to the same layer number on every keyboard. To change it per app, assign the layer with per-app layers");
        _layerList.ToolTip = layerTip;
        Stack.Children.Add(_layerList);

        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        addRow.Children.Add(new TextBlock { Text = Loc.T("レイヤー", "Layer"), Width = 190, VerticalAlignment = VerticalAlignment.Center, ToolTip = layerTip });
        _layerBox.ToolTip = Loc.T("上書きを足すレイヤーを選んで、「追加」を押します。何度でも足せます", "Choose a layer to add an override for, then press Add. You can add as many as you like");
        _addLayer.ToolTip = _layerBox.ToolTip;
        _addLayer.Margin = new Thickness(6, 0, 0, 0);
        _clearLayer.Margin = new Thickness(6, 0, 0, 0);
        addRow.Children.Add(_layerBox);
        addRow.Children.Add(_clearLayer);
        addRow.Children.Add(_addLayer);
        Stack.Children.Add(addRow);
        Stack.Children.Add(_layerSummary);
        _layer = BuildFields("TextTypingLayer", Stack, Loc.T("触っていない(空欄の)項目は、この範囲の既定を使います", "An item you have not touched (a blank box) uses the defaults for this scope"));

        _layerList.SelectionChanged += (_, _) => { if (!_loading) Refresh(); };
        _addLayer.Click += (_, _) =>
        {
            if (!Editable) return;
            int layer = Math.Max(0, _layerBox.SelectedIndex);
            Listed.Add(layer);
            _loading = true;
            RebuildList(layer);
            _loading = false;
            Refresh();
        };
        _clearLayer.Click += (_, _) =>
        {
            if (SelectedEntry is not int layer) return;
            Config.TextTyping?.Layers?.RemoveAll(l => l.Layer == layer);
            Listed.Remove(layer);
            Normalize();
            Refresh();
        };
        _loading = true;
        var names = new List<string>();
        for (int i = 0; i < TextTyping.MaxLayers; i++) names.Add(LayerLabel(i));
        _layerBox.ItemsSource = names;
        _layerBox.SelectedIndex = 0;
        _loading = false;
        Init();
    }

    /// <summary>数として読めない欄があれば、その説明（設定画面の OK が止める）。</summary>
    public string? InputError => _invalid.Count == 0 ? null : string.Join("\n", _invalid.Values);

    private SortedSet<int> Listed => _listed.TryGetValue(Scope, out var s) ? s : _listed[Scope] = new SortedSet<int>();

    private int? SelectedEntry => _layerList.SelectedIndex >= 0 && _layerList.SelectedIndex < _entries.Count ? _entries[_layerList.SelectedIndex] : null;

    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 4) };

    private string LayerLabel(int i)
    {
        string? name = _project != null ? ProjectService.LayerName(_project, i) : null;
        return string.IsNullOrWhiteSpace(name) ? i.ToString() : $"{i}: {name}";
    }

    private Fields BuildFields(string prefix, Panel parent, string blankTip)
    {
        var f = new Fields
        {
            Jitter = new ComboBox
            {
                Name = prefix + "Jitter", Width = 120,
                ItemsSource = new[] { Loc.T("追加", "Add"), Loc.T("無し", "None") },
            },
            JitterMs = NumberBox(prefix + "JitterMs"),
            CharMs = NumberBox(prefix + "CharMs"),
            ImeWaitMs = NumberBox(prefix + "ImeWaitMs"),
        };
        f.JitterMsHint = new TextBlock(); f.CharMsHint = new TextBlock(); f.ImeWaitMsHint = new TextBlock();
        var d = TextTypingProfile.Default;
        AddRow(parent, Loc.T("揺らぎ", "Jitter"), f.Jitter, null,
            Loc.T("1 文字ごとの間隔に、0 から揺らぎの範囲までの乱数を「追加」します。「無し」なら、間隔は一定です。触っていないときは、ひとつ外側の設定の選択が表示されます。", "\"Add\" adds a random 0 to jitter-range ms to each interval. \"None\" keeps the interval constant. While untouched, the choice of the next outer setting is shown. ") + blankTip);
        AddRow(parent, Loc.T("揺らぎの範囲", "Jitter range"), WithHint(f.JitterMs, f.JitterMsHint), Unit(TextTyping.MinJitterMs, TextTyping.MaxJitterMs, d.JitterMs),
            Loc.T("揺らぎで足す時間の上限です。", "The most the jitter adds. ") + blankTip);
        AddRow(parent, Loc.T("1 文字ごとの間隔", "Interval between characters"), WithHint(f.CharMs, f.CharMsHint), Unit(0, TextTyping.MaxCharMs, d.CharMs),
            Loc.T("短くすると速く入力できますが、アプリによっては文字が落ちます。", "Shorter is faster, but some apps drop characters. ") + blankTip);
        AddRow(parent, Loc.T("IME 操作前のウェイト", "Wait before IME operations"), WithHint(f.ImeWaitMs, f.ImeWaitMsHint), Unit(0, TextTyping.MaxImeWaitMs, d.ImeWaitMs),
            Loc.T("文字を入力したあと、IME を操作する(切り替える、元に戻す)前に待つ時間です。短いと、文字が IME に取られることがあります。IME を切り替えた直後のウェイトは、ここでは決めません(IME の状態が変わったことを確かめてから、入力を始めます)。", "Time to wait after typing before an IME operation (switching or restoring the IME). Too short, and the IME may take characters. The wait right after switching the IME is not set here (typing starts once the IME state is confirmed to have changed). ") + blankTip);

        f.Jitter.SelectionChanged += (_, _) => Apply(f);
        foreach (var box in new[] { f.JitterMs, f.CharMs, f.ImeWaitMs }) box.TextChanged += (_, _) => Apply(f);
        return f;
    }

    // 単位と、範囲と、既定（欄を空にしたときの値）。
    private static string Unit(int min, int max, int def) =>
        Loc.T($"ms ({min}～{max}: 既定 {def})", $"ms ({min}–{max}: default {def})");

    private static TextBox NumberBox(string name) => new() { Name = name, Width = 80 };

    // 空欄のときだけ、欄の中に薄く値を出す（クリックは、下の欄に通す）。
    private static UIElement WithHint(TextBox box, TextBlock hint)
    {
        hint.IsHitTestVisible = false;
        hint.Foreground = AppTheme.Palette.SubText;
        hint.Margin = new Thickness(5, 0, 0, 0);
        hint.VerticalAlignment = VerticalAlignment.Center;
        var grid = new Grid { Width = 80 };
        grid.Children.Add(box);
        grid.Children.Add(hint);
        box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        return grid;
    }

    private static void AddRow(Panel parent, string label, UIElement input, string? unit, string tip)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(new TextBlock { Text = label, Width = 190, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip });
        if (input is FrameworkElement fe) fe.ToolTip = tip;
        row.Children.Add(input);
        if (unit != null) row.Children.Add(new TextBlock { Text = unit, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Palette.SubText });
        parent.Children.Add(row);
    }

    // 一覧を作り直す（値を書いたレイヤー＋「追加」で足したレイヤー）。select があれば、それを選ぶ。呼ぶ側が _loading を立てておく。
    private void RebuildList(int? select)
    {
        foreach (var l in Config.TextTyping?.Layers ?? new()) if (l.HasAnyValue) Listed.Add(l.Layer);
        _entries.Clear();
        _entries.AddRange(Listed);
        _layerList.ItemsSource = _entries.Select(LayerLabel).ToList();
        _layerList.SelectedIndex = select is int s && _entries.IndexOf(s) is int i && i >= 0 ? i : (_entries.Count > 0 ? 0 : -1);
    }

    protected override void Refresh()
    {
        _loading = true;
        _invalid.Clear();
        int? keep = SelectedEntry;
        var tt = Config.TextTyping;
        RebuildList(keep);
        int? entry = SelectedEntry;
        Show(_base, tt);
        Show(_layer, entry is int e ? tt?.LayerOrNull(e) : null);
        ShowHints(entry);
        bool editable = Editable;
        foreach (var c in _base.All) c.IsEnabled = editable;
        foreach (var c in _layer.All) c.IsEnabled = editable && entry != null;
        _layerBox.IsEnabled = editable;
        _addLayer.IsEnabled = editable;
        _clearLayer.IsEnabled = editable && entry != null;
        UpdateSummary();
        _loading = false;
    }

    private void UpdateSummary() =>
        _layerSummary.Text = _entries.Count == 0
            ? Loc.T("上書きのあるレイヤーはありません。レイヤーを選んで「追加」を押すと、一覧に足せます。", "No layer has an override. Choose a layer and press Add to put it in the list.")
            : "";

    private void Show(Fields f, TextTypingValues? v)
    {
        f.JitterMs.Text = v?.JitterMs?.ToString() ?? "";
        f.CharMs.Text = v?.CharMs?.ToString() ?? "";
        f.ImeWaitMs.Text = v?.ImeWaitMs?.ToString() ?? "";
        foreach (var box in new[] { f.JitterMs, f.CharMs, f.ImeWaitMs }) box.ClearValue(Control.BorderBrushProperty);
    }

    // 画面の内容を、編集中の設定に写す。何も指定していない値は null（外側に従う）。
    // 揺らぎは、ひとつ外側の設定と同じ選択なら、書かない（触っていないのと同じ）。
    private void Apply(Fields f)
    {
        if (_loading || !Editable) return;
        var tt = Config.TextTyping ?? new TextTypingConfig();
        TextTypingValues target;
        int? entry = SelectedEntry;
        if (ReferenceEquals(f, _base)) target = tt;
        else
        {
            if (entry is not int layer) return;
            tt.Layers ??= new();
            target = tt.LayerOrNull(layer) ?? AddLayer(tt, layer);
        }
        bool add = f.Jitter.SelectedIndex <= 0;
        target.Jitter = add == f.Inherited.Jitter ? null : add;
        target.JitterMs = Read(f.JitterMs);
        target.CharMs = Read(f.CharMs);
        target.ImeWaitMs = Read(f.ImeWaitMs);
        Config.TextTyping = tt;
        Normalize();

        // 一覧と、使われる値の表示だけを直す（入力中の欄は作り直さない）。
        _loading = true;
        RebuildList(entry);
        ShowHints(SelectedEntry);
        UpdateSummary();
        _clearLayer.IsEnabled = Editable && SelectedEntry != null;
        _loading = false;
    }

    // 空欄のときに使われる値を、欄の中に薄く出す。上段は、ひとつ外側の設定（このキーボードなら共通、共通なら kasane の既定）、
    // 下段は、そのレイヤーに書かなかったときの値（この範囲の既定。共通のレイヤーの上書きがあれば、それも）。
    // 揺らぎの選択は、書いていなければ、その値（追加 / 無し）を表示する。
    private void ShowHints(int? entry)
    {
        var global = Model.Global.TextTyping;
        var device = Model.HasDevice ? Model.Device.TextTyping : null;
        _base.Inherited = Scope == HelperScope.Device
            ? TextTyping.Resolve(global, null, null)
            : TextTypingProfile.Default;
        ApplyHints(_base, Config.TextTyping);

        if (entry is int layer)
        {
            var g = Scope == HelperScope.Global ? WithoutLayer(global, layer) : global;
            var d = Scope == HelperScope.Device ? WithoutLayer(device, layer) : device;
            _layer.Inherited = TextTyping.Resolve(g, d, 1u << layer);
            ApplyHints(_layer, Config.TextTyping?.LayerOrNull(layer));
        }
        else
        {
            _layer.Inherited = _base.Inherited;
            ApplyHints(_layer, null);
        }
    }

    private static void ApplyHints(Fields f, TextTypingValues? target)
    {
        var p = f.Inherited;
        f.JitterMsHint.Text = p.JitterMs.ToString();
        f.CharMsHint.Text = p.CharMs.ToString();
        f.ImeWaitMsHint.Text = p.ImeWaitMs.ToString();
        f.Jitter.SelectedIndex = (target?.Jitter ?? p.Jitter) ? 0 : 1;
    }

    private static TextTypingConfig? WithoutLayer(TextTypingConfig? c, int layer)
    {
        if (c == null) return null;
        return new TextTypingConfig
        {
            Jitter = c.Jitter, JitterMs = c.JitterMs, CharMs = c.CharMs, ImeWaitMs = c.ImeWaitMs,
            Layers = c.Layers?.Where(l => l.Layer != layer).ToList(),
        };
    }

    private static TextTypingLayer AddLayer(TextTypingConfig tt, int layer)
    {
        var l = new TextTypingLayer { Layer = layer };
        tt.Layers!.Add(l);
        tt.Layers.Sort((a, b) => a.Layer.CompareTo(b.Layer));
        return l;
    }

    // 値の無いレイヤーを外し、何も無ければ設定ごと消す（空の設定を保存しない）。一覧（Listed）には残るので、追加した項目は消えない。
    private void Normalize()
    {
        var tt = Config.TextTyping;
        if (tt == null) return;
        tt.Layers?.RemoveAll(l => !l.HasAnyValue);
        if (tt.Layers is { Count: 0 }) tt.Layers = null;
        if (tt.IsEmpty) Config.TextTyping = null;
    }

    private int? Read(TextBox box)
    {
        string text = box.Text.Trim();
        if (text.Length == 0) { MarkValid(box); return null; }
        if (int.TryParse(text, out int v)) { MarkValid(box); return v; }
        string label = Scope == HelperScope.Device ? Loc.T("このキーボード", "this keyboard") : Loc.T("共通", "shared");
        _invalid[box] = Loc.T($"{label}の文字入力の速さ: 「{text}」は数ではありません(ms を整数で入力します)", $"Typing speed ({label}): \"{text}\" is not a number (enter whole milliseconds)");
        box.BorderBrush = Brushes.Red;
        return null;
    }

    private void MarkValid(TextBox box)
    {
        _invalid.Remove(box);
        box.ClearValue(Control.BorderBrushProperty);
    }
}
