using System.Windows;
using System.Windows.Controls;
using Kasane.Interop;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>キー置換（Key Override。修飾キー＋キーを、別のキーやマクロに置き換える）の編集。</summary>
public sealed class KeyOverrideEditorWindow : IdListEditorWindow
{
    private readonly KeyOverrideDef?[] _defs = new KeyOverrideDef?[KbdLayerClient.MaxKeyOverrideCount];
    // 「効くレイヤー」のチェックを並べるレイヤーの数と名前（プロジェクトのレイヤー）。
    private readonly KeymapProject? _project;

    public List<KeyOverrideDef> Result { get; private set; } = new();

    protected override string HelpTopic => HelpTopics.KeyOverride;

    public KeyOverrideEditorWindow(IEnumerable<KeyOverrideDef> keyOverrides, IReadOnlyDictionary<int, string>? names = null, KeymapProject? project = null)
        : base(Loc.T("キー置換", "Key Override"), KbdLayerClient.MaxKeyOverrideCount)
    {
        _project = project;
        foreach (var k in keyOverrides)
            if (k.Id >= 0 && k.Id < _defs.Length)
            {
                _defs[k.Id] = new KeyOverrideDef { Id = k.Id, Mods = k.Mods, Key = k.Key, Replacement = k.Replacement, RepeatMacro = k.RepeatMacro, Layers = k.Layers == null ? null : new List<int>(k.Layers) };
                LoadDisabled(k.Id, k.Disabled);
            }
        LoadNames(names);
    }

    /// <summary>
    /// 最初の空き番号に、条件のキーを <paramref name="key"/> にしたキー置換を作って選ぶ（修飾キーと置き換え先は利用者が書く）。
    /// プロジェクトは、適用・OK まで変わらない。空き番号が無ければ -1。
    /// </summary>
    public int StartNew(string key)
    {
        int id = FreeId();
        if (id < 0) return -1;
        _defs[id] = new KeyOverrideDef { Id = id, Mods = "", Key = key, Replacement = "" };
        RefreshItem(id);
        ShowId(id);
        return id;
    }

    protected override bool CanReorder => true;
    protected override void ClearItem(int id) => _defs[id] = null;
    protected override void CopyItem(int from, int to) =>
        _defs[to] = _defs[from] is { } k ? new KeyOverrideDef { Id = to, Mods = k.Mods, Key = k.Key, Replacement = k.Replacement, RepeatMacro = k.RepeatMacro, Layers = k.Layers == null ? null : new List<int>(k.Layers) } : null;
    protected override void SwapItems(int a, int b)
    {
        (_defs[a], _defs[b]) = (_defs[b], _defs[a]);
        if (_defs[a] != null) _defs[a]!.Id = a;
        if (_defs[b] != null) _defs[b]!.Id = b;
    }

    // ほかの画面でマクロ・ヘルパー動作を削除して参照を外したとき: 置き換え先がそれのキー置換を無効にする（プロジェクトと同じ規則）。
    public override void ReferencesRemoved(RefKind kind, int id)
    {
        for (int i = 0; i < _defs.Length; i++)
            if (_defs[i] is { } k && !Disabled[i] && ProjectService.RefersTo(k.Replacement, kind, id)) SetDisabled(i, true);
        ShowId(Math.Max(0, CurrentId));
    }

    protected override string Summary(int id) =>
        _defs[id] is { } k ? $"{k.Mods} + {k.Key} → {k.Replacement}" + (k.RepeatMacro ? Loc.T("(繰り返す)", " (repeat)") : "") + LayersSuffix(k) : "";

    /// <summary>一覧の要約に付ける、効くレイヤー（全レイヤーなら何も付けない）。</summary>
    private static string LayersSuffix(KeyOverrideDef k) =>
        k.Layers is { Count: > 0 } l ? Loc.T($"(レイヤー {string.Join("・", l)})", $" (layer {string.Join(", ", l)})") : "";

    private static bool IsMacro(string replacement) => replacement.Trim().StartsWith("MACRO(", StringComparison.OrdinalIgnoreCase);

    protected override void ShowDetail(int id)
    {
        var d = _defs[id];
        var mods = KeycodeBox(d?.Mods ?? "", "ModsBox");
        var key = KeycodeBox(d?.Key ?? "", "KeyBox");
        var replacement = KeycodeBox(d?.Replacement ?? "", "ReplacementBox");
        var repeat = new CheckBox
        {
            Name = "RepeatMacroCheck",
            Content = Loc.T("キーを押し続けたときも、マクロを繰り返す", "Repeat the macro while the key is held"),
            ToolTip = Loc.T("オートリピートのたびに繰り返します", "Repeats on every auto-repeat"),
            IsChecked = d?.RepeatMacro ?? false,
            Margin = new Thickness(0, 6, 0, 0),
        };

        // 効くレイヤー: プロジェクトのレイヤーごとのチェック。どれも選ばなければ、すべてのレイヤーで効く。
        // 範囲外の番号が書いてあるときも、外せるように並べる（検証がエラーにする）。
        var layerChecks = new List<CheckBox>();
        var layerPanel = new WrapPanel { MaxWidth = 560 };
        int shown = Math.Max(Math.Max(_project?.Layers.Count ?? 1, 1), (d?.Layers?.Max() ?? -1) + 1);
        for (int n = 0; n < shown; n++)
        {
            string? layerName = _project == null ? null : ProjectService.LayerName(_project, n);
            var c = new CheckBox
            {
                Name = $"OverrideLayerCheck{n}",
                Content = string.IsNullOrWhiteSpace(layerName) ? n.ToString() : $"{n}: {layerName}",
                IsChecked = d?.Layers?.Contains(n) ?? false,
                Margin = new Thickness(0, 0, 10, 0),
                Tag = n,
            };
            layerChecks.Add(c);
            layerPanel.Children.Add(c);
        }
        var layerHint = new TextBlock
        {
            Text = Loc.T("どれも選ばないと、すべてのレイヤーで効きます", "If none is checked, it works on all layers"),
            Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(110, 0, 0, 0),
        };

        void Changed()
        {
            repeat.Visibility = IsMacro(replacement.Text) ? Visibility.Visible : Visibility.Collapsed;
            var boxes = new[] { mods, key, replacement };
            if (boxes.All(b => b.Text.Trim().Length == 0)) _defs[id] = null;
            else _defs[id] = new KeyOverrideDef
            {
                Id = id,
                Mods = mods.Text.Trim(),
                Key = key.Text.Trim(),
                Replacement = replacement.Text.Trim(),
                RepeatMacro = repeat.IsChecked == true && IsMacro(replacement.Text),
                Layers = ProjectService.NormalizeOverrideLayers(layerChecks.Where(c => c.IsChecked == true).Select(c => (int)c.Tag)),
            };
            RefreshItem(id);
            var errors = new List<string>();
            if (_defs[id] is { } k) ProjectService.KeyOverrideToEntry(k, errors);
            ShowValidation(errors.Count == 0 ? "OK" : string.Join("\n", errors), errors.Count > 0);
        }
        foreach (var b in new[] { mods, key, replacement }) b.TextChanged += (_, _) => Changed();
        // Click ではなく Checked/Unchecked で受ける（UI オートメーションの Toggle など、Click が起きない操作でも定義に反映するため）。
        repeat.Checked += (_, _) => Changed();
        repeat.Unchecked += (_, _) => Changed();
        foreach (var c in layerChecks)
        {
            c.Checked += (_, _) => Changed();
            c.Unchecked += (_, _) => Changed();
        }

        Detail.Children.Add(Row(Loc.T("修飾キー", "Modifiers"), ModsWithChecks(mods)));
        Detail.Children.Add(Row(Loc.T("キー", "Key"), KeycodeField.Wrap(key, allowMods: false)));
        Detail.Children.Add(Row(Loc.T("置き換え先", "Replace with"), KeycodeField.Wrap(replacement, acceptsMacro: true)));
        Detail.Children.Add(Row(Loc.T("効くレイヤー", "Active layers"), layerPanel));
        Detail.Children.Add(layerHint);
        Detail.Children.Add(repeat);
        repeat.Visibility = IsMacro(replacement.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    // 修飾キーの欄（MOD_LCTL|MOD_LSFT のような文字）と、その右の Shift・Alt・Ctrl・Win のチェック（2026-11-09）。
    // チェックを付ける・外すと、欄の文字が変わる。欄を書き換えると、チェックが変わる。左右は区別しない
    // （どちらかが書いてあればチェックが付き、外すと左右とも消える。付けるときは左側）。
    private static readonly (string Label, string Id, byte Left, byte Right)[] ModGroups =
    {
        ("Shift", "ModShiftCheck", 0x02, 0x20), ("Alt", "ModAltCheck", 0x04, 0x40),
        ("Ctrl", "ModCtrlCheck", 0x01, 0x10), ("Win", "ModWinCheck", 0x08, 0x80),
    };

    private static UIElement ModsWithChecks(TextBox box)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(box);
        var checks = new List<CheckBox>();
        bool syncing = false;
        byte CurrentMask() => Kasane.Keycodes.KeycodeTable.TryParseModMask(box.Text.Trim(), out var m) ? m : (byte)0;
        void FromText()
        {
            byte m = CurrentMask();
            syncing = true;
            for (int i = 0; i < ModGroups.Length; i++) checks[i].IsChecked = (m & (ModGroups[i].Left | ModGroups[i].Right)) != 0;
            syncing = false;
        }
        for (int i = 0; i < ModGroups.Length; i++)
        {
            var g = ModGroups[i];
            var c = new CheckBox
            {
                Name = g.Id, Content = g.Label, Margin = new Thickness(i == 0 ? 12 : 8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                ToolTip = Loc.T("チェックすると、修飾キーの欄に書き足します(左右は区別しません)", "Check to add it to the modifiers field (left and right are not distinguished)"),
            };
            // Click ではなく Checked/Unchecked で受ける（UI オートメーションの Toggle など、Click が起きない操作でも欄に反映するため）。
            // 欄から入れ直すときの IsChecked の変更は、syncing で読み飛ばす。
            void ToChecked()
            {
                if (syncing) return;
                byte m = CurrentMask();
                if (c.IsChecked == true) m |= g.Left; else m = (byte)(m & ~(g.Left | g.Right));
                box.Text = m == 0 ? "" : Kasane.Keycodes.KeycodeTable.ModMaskToNames(m);
            }
            c.Checked += (_, _) => ToChecked();
            c.Unchecked += (_, _) => ToChecked();
            checks.Add(c);
            panel.Children.Add(c);
        }
        box.TextChanged += (_, _) => FromText();
        FromText();
        return panel;
    }

    protected override void OnItemEnabled(int id)
    {
        var errors = new List<string>();
        if (_defs[id] is { } k) ProjectService.KeyOverrideToEntry(k, errors);
        ShowValidation(errors.Count == 0 ? "OK" : string.Join("\n", errors), errors.Count > 0);
    }

    protected override bool ValidateAll(out string error)
    {
        var errors = new List<string>();
        // 無効のものは、検証しない。
        foreach (var k in _defs.Where(k => k != null && !Disabled[k.Id])) ProjectService.KeyOverrideToEntry(k!, errors);
        error = string.Join("\n", errors);
        if (errors.Count > 0) return false;
        Result = _defs.Where(k => k != null).Select(k => { k!.Disabled = Disabled[k.Id]; return k; }).ToList();
        return true;
    }
}
