using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>
/// 設定画面の「文字入力の速さ」タブ（2026-11-20）。ヘルパー動作の Text と、Sequence の Text 手順の、1 文字ごとの間隔・揺らぎ・
/// IME を切り替える前の待ち（<see cref="HelperConfig.TextTyping"/>）。対象は「このキーボード」か「全キーボード共通」。
/// 上段がその範囲の既定、下段がレイヤー番号ごとの上書き。空欄（指定しない）は、ひとつ外側の値を使う（<see cref="TextTyping.Resolve"/>）。
/// 編集は <see cref="HelperSettingsModel"/> のコピーに対して行い、書くのは設定画面の OK のとき。
/// </summary>
internal sealed class TextTypingTab : HelperTabBase
{
    private sealed class Fields
    {
        public ComboBox Jitter = null!;
        public TextBox JitterMs = null!, CharMs = null!, ImeWaitMs = null!;
        public IEnumerable<Control> All => new Control[] { Jitter, JitterMs, CharMs, ImeWaitMs };
    }

    private readonly KeymapProject? _project;
    private readonly Fields _base;
    private readonly Fields _layer;
    private readonly ComboBox _layerBox = new() { Name = "TextTypingLayerBox", Width = 220 };
    private readonly Button _clearLayer = Btn("TextTypingLayerClear", Loc.T("このレイヤーの上書きを消す", "Clear this layer's override"), 190);
    private readonly TextBlock _layerSummary = new() { Name = "TextTypingLayerSummary", TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Palette.SubText, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Dictionary<TextBox, string> _invalid = new();
    private bool _loading;

    public TextTypingTab(HelperSettingsModel model, Window owner, KeymapProject? project)
        : base(model, owner, "TextTyping")
    {
        _project = project;
        var d = TextTypingProfile.Default;

        Stack.Children.Add(Heading(Loc.T("この範囲の既定", "Defaults for this scope")));
        _base = BuildFields("TextTyping", Stack,
            Loc.T($"空欄は、共通の設定(このキーボードのとき)か、kasane の既定を使います。既定は、揺らぎ {(d.Jitter ? "あり" : "なし")}、範囲 {d.JitterMs} ms、間隔 {d.CharMs} ms、待ち {d.ImeWaitMs} ms です",
                  $"Blank uses the shared settings (for this keyboard) or the kasane default: jitter {(d.Jitter ? "on" : "off")}, range {d.JitterMs} ms, interval {d.CharMs} ms, wait {d.ImeWaitMs} ms"));

        Stack.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 8) });
        Stack.Children.Add(Heading(Loc.T("レイヤーごとの上書き", "Per-layer overrides")));
        var layerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        string layerTip = Loc.T("このレイヤーが有効なときに使います。複数のレイヤーが有効なら、番号の大きいレイヤーの値が先です。共通の設定に書くと、どのキーボードでも同じ番号のレイヤーに効きます。アプリごとに変えるには、アプリ別レイヤーでレイヤーを割り当てます",
                                "Used while this layer is on. If several layers are on, the higher layer's values come first. In the shared settings, it applies to the same layer number on every keyboard. To change it per app, assign the layer with per-app layers");
        layerRow.Children.Add(new TextBlock { Text = Loc.T("レイヤー", "Layer"), Width = 190, VerticalAlignment = VerticalAlignment.Center, ToolTip = layerTip });
        _layerBox.ToolTip = layerTip;
        layerRow.Children.Add(_layerBox);
        Stack.Children.Add(layerRow);
        _layer = BuildFields("TextTypingLayer", Stack, Loc.T("空欄は、この範囲の既定を使います", "Blank uses the defaults for this scope"));
        Stack.Children.Add(Buttons(_clearLayer));
        Stack.Children.Add(_layerSummary);

        _layerBox.SelectionChanged += (_, _) => { if (!_loading) Refresh(); };
        _clearLayer.Click += (_, _) =>
        {
            var tt = Config.TextTyping;
            if (tt?.Layers == null) return;
            tt.Layers.RemoveAll(l => l.Layer == SelectedLayer);
            Normalize();
            Refresh();
        };
        _loading = true;
        RebuildLayerItems(0);
        _loading = false;
        Init();
    }

    /// <summary>数として読めない欄があれば、その説明（設定画面の OK が止める）。</summary>
    public string? InputError => _invalid.Count == 0 ? null : string.Join("\n", _invalid.Values);

    private int SelectedLayer => Math.Max(0, _layerBox.SelectedIndex);

    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 4) };

    private Fields BuildFields(string prefix, Panel parent, string blankTip)
    {
        var f = new Fields
        {
            Jitter = new ComboBox
            {
                Name = prefix + "Jitter", Width = 120,
                ItemsSource = new[] { Loc.T("指定しない", "Not set"), Loc.T("入れる", "On"), Loc.T("入れない", "Off") },
            },
            JitterMs = NumberBox(prefix + "JitterMs"),
            CharMs = NumberBox(prefix + "CharMs"),
            ImeWaitMs = NumberBox(prefix + "ImeWaitMs"),
        };
        AddRow(parent, Loc.T("揺らぎ", "Jitter"), f.Jitter, null,
            Loc.T("1 文字ごとの間隔に、0 から範囲までの乱数を足します。", "Adds a random 0 to range ms to each interval. ") + blankTip);
        AddRow(parent, Loc.T("揺らぎの範囲", "Jitter range"), f.JitterMs, $"ms ({TextTyping.MinJitterMs}〜{TextTyping.MaxJitterMs})",
            Loc.T("揺らぎで足す時間の上限です。", "The most the jitter adds. ") + blankTip);
        AddRow(parent, Loc.T("1 文字ごとの間隔", "Interval between characters"), f.CharMs, $"ms (0〜{TextTyping.MaxCharMs})",
            Loc.T("短くすると速く入力できますが、アプリによっては文字が落ちます。", "Shorter is faster, but some apps drop characters. ") + blankTip);
        AddRow(parent, Loc.T("IME を切り替える前の待ち", "Wait before switching the IME"), f.ImeWaitMs, $"ms (0〜{TextTyping.MaxImeWaitMs})",
            Loc.T("文字を入力したあと、IME を切り替える(元に戻す)前に待つ時間です。短いと、文字が IME に取られることがあります。", "Time to wait after typing before the IME is switched (restored). Too short, and the IME may take characters. ") + blankTip);

        f.Jitter.SelectionChanged += (_, _) => Apply(f);
        foreach (var box in new[] { f.JitterMs, f.CharMs, f.ImeWaitMs }) box.TextChanged += (_, _) => Apply(f);
        return f;
    }

    private static TextBox NumberBox(string name) => new() { Name = name, Width = 80 };

    private static void AddRow(Panel parent, string label, Control input, string? unit, string tip)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(new TextBlock { Text = label, Width = 190, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip });
        input.ToolTip = tip;
        row.Children.Add(input);
        if (unit != null) row.Children.Add(new TextBlock { Text = unit, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Palette.SubText });
        parent.Children.Add(row);
    }

    private void RebuildLayerItems(int select)
    {
        var tt = Config.TextTyping;
        var items = new List<string>();
        for (int i = 0; i < TextTyping.MaxLayers; i++)
        {
            string? name = _project != null ? ProjectService.LayerName(_project, i) : null;
            string mark = tt?.LayerOrNull(i) is { HasAnyValue: true } ? " *" : "";
            items.Add(string.IsNullOrWhiteSpace(name) ? $"{i}{mark}" : $"{i}: {name}{mark}");
        }
        _layerBox.ItemsSource = items;
        _layerBox.SelectedIndex = select;
    }

    protected override void Refresh()
    {
        _loading = true;
        _invalid.Clear();
        var tt = Config.TextTyping;
        RebuildLayerItems(SelectedLayer);
        Show(_base, tt);
        Show(_layer, tt?.LayerOrNull(SelectedLayer));
        bool editable = Editable;
        foreach (var c in _base.All.Concat(_layer.All)) c.IsEnabled = editable;
        _layerBox.IsEnabled = true;
        _clearLayer.IsEnabled = editable && tt?.LayerOrNull(SelectedLayer) != null;
        var layers = (tt?.Layers ?? new()).Where(l => l.HasAnyValue).Select(l => l.Layer).OrderBy(l => l).ToList();
        _layerSummary.Text = layers.Count == 0
            ? Loc.T("上書きのあるレイヤーはありません。", "No layer has an override.")
            : Loc.T("上書きのあるレイヤー: ", "Layers with an override: ") + string.Join(", ", layers);
        _loading = false;
    }

    private void Show(Fields f, TextTypingValues? v)
    {
        f.Jitter.SelectedIndex = v?.Jitter switch { true => 1, false => 2, _ => 0 };
        f.JitterMs.Text = v?.JitterMs?.ToString() ?? "";
        f.CharMs.Text = v?.CharMs?.ToString() ?? "";
        f.ImeWaitMs.Text = v?.ImeWaitMs?.ToString() ?? "";
        foreach (var box in new[] { f.JitterMs, f.CharMs, f.ImeWaitMs }) box.ClearValue(Control.BorderBrushProperty);
    }

    // 画面の内容を、編集中の設定に写す。何も指定していない値は null（外側に従う）。
    private void Apply(Fields f)
    {
        if (_loading || !Editable) return;
        var tt = Config.TextTyping ?? new TextTypingConfig();
        TextTypingValues target;
        if (ReferenceEquals(f, _base)) target = tt;
        else
        {
            tt.Layers ??= new();
            target = tt.LayerOrNull(SelectedLayer) ?? AddLayer(tt, SelectedLayer);
        }
        target.Jitter = f.Jitter.SelectedIndex switch { 1 => true, 2 => false, _ => null };
        target.JitterMs = Read(f.JitterMs);
        target.CharMs = Read(f.CharMs);
        target.ImeWaitMs = Read(f.ImeWaitMs);
        Config.TextTyping = tt;
        Normalize();

        // 一覧の印と、上書きのあるレイヤーの表示だけを直す（入力中の欄は作り直さない）。
        _loading = true;
        RebuildLayerItems(SelectedLayer);
        _loading = false;
        var current = Config.TextTyping;
        _clearLayer.IsEnabled = Editable && current?.LayerOrNull(SelectedLayer) != null;
        var layers = (current?.Layers ?? new()).Where(l => l.HasAnyValue).Select(l => l.Layer).OrderBy(l => l).ToList();
        _layerSummary.Text = layers.Count == 0
            ? Loc.T("上書きのあるレイヤーはありません。", "No layer has an override.")
            : Loc.T("上書きのあるレイヤー: ", "Layers with an override: ") + string.Join(", ", layers);
    }

    private static TextTypingLayer AddLayer(TextTypingConfig tt, int layer)
    {
        var l = new TextTypingLayer { Layer = layer };
        tt.Layers!.Add(l);
        tt.Layers.Sort((a, b) => a.Layer.CompareTo(b.Layer));
        return l;
    }

    // 値の無いレイヤーを外し、何も無ければ設定ごと消す（空の設定を保存しない）。
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
