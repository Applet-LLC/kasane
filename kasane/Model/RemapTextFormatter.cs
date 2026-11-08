using System.Text;
using Kasane.Interop;
using Kasane.Keycodes;
using Kasane.Layout;

namespace Kasane.Model;

/// <summary>
/// 割り当ての一覧を、1行1設定の文字で書く（2026-11-08、GUI 計画 2-2）。メイン画面の「割り当ての一覧」と、<c>kasane.exe --dump-text</c> が使う。
/// 問い合わせのときに貼り付けてもらうことも考えて、読める名前のあとに、丸括弧で元のキーコードを添える。
///  - レイヤー0 は、刻印のキー（KLE の既定キーコード）から変えたキーだけ。レイヤー1 以降は、▽（透過）でないキーだけ。
///  - コンボ・タップダンス・キー置換・マクロ・NICOLA・ヘルパー動作は、定義があるものだけ。無効にしたものは、行の頭に「# 」を付けて残す。
/// 形は、のどかの <c>key</c> 文に寄せて <c>key A -> B</c> にしたが、構文の互換はない（読み込みはしない）。
/// </summary>
public static class RemapTextFormatter
{
    /// <param name="device">このキーボードの helper.json（無ければ null）。</param>
    /// <param name="global">全キーボード共通のヘルパー設定（無ければ null）。</param>
    /// <param name="keyboardLabel">見出しに出すキーボードの名前（無ければ UniqId）。</param>
    /// <param name="at">見出しに出す日時（null なら出さない。単体テストで結果を固定するため）。</param>
    public static string Format(KeymapProject project, KeyboardLayout layout, HelperConfig? device = null, HelperConfig? global = null,
                                string? keyboardLabel = null, DateTime? at = null)
    {
        var sb = new StringBuilder();
        void Line(string s = "") => sb.Append(s).Append("\r\n");

        Line(Loc.T("# かさね 割り当ての一覧", "# kasane assignment list") + (at is { } t ? $" ({t:yyyy-MM-dd HH:mm})" : ""));
        Line(Loc.T($"# キーボード: {keyboardLabel ?? $"{project.UniqId:X8}"} / レイアウト: {project.LayoutName}",
                   $"# Keyboard: {keyboardLabel ?? $"{project.UniqId:X8}"} / Layout: {project.LayoutName}"));
        // 説明は、既定の画面の幅で横にはみ出さないよう、2 行に分ける（2026-11-08 の実機確認）。
        Line(Loc.T("# レイヤー0 は刻印のキーから変えたキー、レイヤー1 以降は ▽(透過)でないキーだけを出します。",
                   "# Layer 0 lists keys changed from their printed key; layers 1 and up list keys that are not ▽ (transparent)."));
        Line(Loc.T("# 行の頭の「# 」は、無効にした設定です。", "# Lines starting with \"# \" are disabled settings."));

        var slots = layout.Slots.OrderBy(s => s.Index).ToList();
        for (int layer = 0; layer < project.Layers.Count; layer++)
        {
            Line();
            Line("## " + ProjectService.LayerLabel(project, layer));
            var row = project.Layers[layer];
            var rows = new List<Row>();
            foreach (var slot in slots)
            {
                if (slot.Index >= row.Count) continue;
                string token = row[slot.Index]?.Trim() ?? "";
                if (!IsShown(token, slot, layer)) continue;
                rows.Add(new Row($"key {KeyName(slot)}", Describe(project, token, device, global), token));
            }
            if (rows.Count == 0) rows.Add(new Row(layer == 0 ? Loc.T("(刻印のまま)", "(as printed)") : Loc.T("(すべて ▽)", "(all ▽)")));
            foreach (var l in Align(rows)) Line(l);
        }

        Section(Line, Loc.T("コンボ", "Combos"), project, KbdLayerClient.FeatureCombo, project.Combos.OrderBy(c => c.Id).Select(c =>
            new Row(Disabled(c.Disabled) + $"combo {Numbered(c.Id, project.ComboNames)}: {SlotName(layout, c.Slot1)} + {SlotName(layout, c.Slot2)}",
                    Describe(project, c.Output, device, global), c.Output)));

        Section(Line, Loc.T("タップダンス", "Tap dances"), project, KbdLayerClient.FeatureTapDance, project.TapDances.OrderBy(t => t.Id).Select(t =>
            new Row(Disabled(t.Disabled) + $"tapdance {Numbered(t.Id, project.TapDanceNames)}: " +
            Loc.T($"1回 {Field(project, t.SingleTap, device, global)} / 長押し {Field(project, t.SingleHold, device, global)} / 2回 {Field(project, t.DoubleTap, device, global)} / 3回 {Field(project, t.TripleTap, device, global)}",
                  $"tap {Field(project, t.SingleTap, device, global)} / hold {Field(project, t.SingleHold, device, global)} / 2 taps {Field(project, t.DoubleTap, device, global)} / 3 taps {Field(project, t.TripleTap, device, global)}"))));

        Section(Line, Loc.T("キー置換", "Key overrides"), project, KbdLayerClient.FeatureKeyOverride, project.KeyOverrides.OrderBy(k => k.Id).Select(k =>
            new Row(Disabled(k.Disabled) + $"override {Numbered(k.Id, project.KeyOverrideNames)}: {ModsText(k.Mods)}{KeyText(k.Key)}",
                    Describe(project, k.Replacement, device, global) + (k.RepeatMacro ? Loc.T("(繰り返す)", " (repeat)") : ""),
                    $"{k.Mods} + {k.Key} -> {k.Replacement}")));

        Section(Line, Loc.T("マクロ", "Macros"), project, KbdLayerClient.FeatureMacro, project.Macros.OrderBy(m => m.Id).Select(m =>
            new Row(Disabled(m.Disabled) + $"macro {Numbered(m.Id, project.MacroNames)}: {string.Join(" ", m.Keys.Select(KeyText))}")));

        if (project.Nicola is { } n)
        {
            Line();
            Line("## NICOLA");
            Line(Disabled(!n.Enabled) + Loc.T(
                $"nicola レイヤー{n.Layer} / 親指左 {KeyText(n.ThumbLeft)} / 親指右 {KeyText(n.ThumbRight)} / 文字キー {n.Keys.Count} 個 / {(n.Output == "jis-kana" ? "JISかな入力" : "ローマ字入力")}",
                $"nicola layer {n.Layer} / left thumb {KeyText(n.ThumbLeft)} / right thumb {KeyText(n.ThumbRight)} / {n.Keys.Count} character keys / {(n.Output == "jis-kana" ? "JIS kana input" : "romaji input")}"));
        }
        else if (project.ClearNicola)
        {
            Line();
            Line("## NICOLA");
            Line(Loc.T("nicola (適用すると、ドライバの設定を消します)", "nicola (applying clears the driver's settings)"));
        }

        var helperLines = new List<string>();
        void Helpers(HelperConfig? config, string scope)
        {
            if (config == null) return;
            foreach (var a in config.Actions.OrderBy(a => a.Id)) helperLines.Add($"helper {scope} {HelperSummary.Action(a)}");
            foreach (var tr in config.Triggers) helperLines.Add($"helper trigger {scope} {HelperSummary.Trigger(tr)}");
        }
        Helpers(device, Loc.T("[このキーボード]", "[this keyboard]"));
        Helpers(global, Loc.T("[共通]", "[shared]"));
        if (helperLines.Count > 0)
        {
            Line();
            Line(Loc.T("## ヘルパー動作(トレイが動いているときだけ働きます)", "## Helper actions (work only while the tray is running)"));
            foreach (var l in helperLines) Line(l);
        }
        return sb.ToString();
    }

    private static void Section(Action<string> line, string title, KeymapProject project, uint feature, IEnumerable<Row> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return;
        line("");
        line("## " + title + (KeyMarks.FeatureOn(project, feature) ? "" : Loc.T("(機能が OFF。適用しても動きません)", " (feature OFF; not applied)")));
        foreach (var r in Align(list)) line(r);
    }

    /// <summary>
    /// 1行。<see cref="Right"/> があれば「Left -> Right : Raw」の形で、節の中で「->」と「:」の位置を揃える（2026-11-08、利用者の指摘）。
    /// <see cref="Right"/> が無い行は、Left をそのまま出す。<see cref="Raw"/> は元のキーコード（読める説明と並べて、問い合わせのときに設定が伝わるように）。
    /// </summary>
    private sealed record Row(string Left, string? Right = null, string? Raw = null);

    // 節の中の行を揃える。幅は、等幅の字体での見た目の幅（日本語は半角2文字分。DisplayWidth）。
    private static IEnumerable<string> Align(IReadOnlyList<Row> rows)
    {
        int leftWidth = rows.Where(r => r.Right != null).Select(r => DisplayWidth(r.Left)).DefaultIfEmpty(0).Max();
        int rightWidth = rows.Where(r => r.Right != null && r.Raw != null).Select(r => DisplayWidth(r.Right!)).DefaultIfEmpty(0).Max();
        foreach (var r in rows)
        {
            if (r.Right == null) { yield return r.Left; continue; }
            yield return PadRight(r.Left, leftWidth) + " -> " + (r.Raw == null ? r.Right : PadRight(r.Right, rightWidth) + " : " + r.Raw);
        }
    }

    private static string PadRight(string s, int width) => s + new string(' ', Math.Max(0, width - DisplayWidth(s)));

    /// <summary>
    /// 等幅の字体（MS ゴシック、BIZ UDゴシックなど）で表示したときの幅（半角 = 1）。日本語の文字（かな・漢字・全角の記号）と、
    /// 日本語の字体で全角になる記号（▽、—、…、矢印、⇧ など。U+2010〜U+2BFF）は 2 と数える。近似なので、字体によっては 1 文字ずれることがある。
    /// </summary>
    public static int DisplayWidth(string s)
    {
        int w = 0;
        foreach (char c in s)
            w += c >= 0x1100 && (c <= 0x115F || (c >= 0x2010 && c <= 0x2BFF) || (c >= 0x2E80 && c <= 0xA4CF) || (c >= 0xAC00 && c <= 0xD7A3) ||
                                 (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFE30 && c <= 0xFE4F) || (c >= 0xFF00 && c <= 0xFF60) || (c >= 0xFFE0 && c <= 0xFFE6)) ? 2 : 1;
        return w;
    }

    // レイヤー0: 刻印のキーと同じ（別名も同じとみなす）・透過・空は出さない。レイヤー1以降: 透過・空は出さない。
    private static bool IsShown(string token, KeySlot slot, int layer)
    {
        if (token.Length == 0 || token is "KC_TRNS" or "_______" or "KC_TRANSPARENT") return false;
        if (layer > 0) return true;
        if (slot.DefaultKeycode is not { } def) return true;
        if (string.Equals(token, def, StringComparison.Ordinal)) return false;
        return !(KeycodeTable.TryResolve(token, out var a) && KeycodeTable.TryResolve(def, out var b) && a == b);
    }

    private static string Disabled(bool disabled) => disabled ? "# " : "";

    // 名前は、日本語では「」、英語では "" で囲む（英語の表示に「」を残さない。ほかの画面の英語表示と同じ形）。
    private static string Quoted(string name) => Loc.T($"「{name}」", $" \"{name}\"");

    private static string Numbered(int id, Dictionary<int, string>? names) =>
        names != null && names.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n) ? id + Quoted(n) : id.ToString();

    /// <summary>見出しに出すキーボードの名前（UniqId と、付けた名前）。画面と --dump-text で共通。</summary>
    public static string KeyboardLabel(KeymapProject project) =>
        string.IsNullOrWhiteSpace(project.KeyboardName) ? $"{project.UniqId:X8}" : $"{project.UniqId:X8}" + Quoted(project.KeyboardName);

    /// <summary>キーの名前: 刻印のキーコードの表示名（Caps、変換 など）。無ければ刻印、それも無ければ番号。</summary>
    public static string KeyName(KeySlot slot)
    {
        if (!string.IsNullOrWhiteSpace(slot.DefaultKeycode)) return KeyText(slot.DefaultKeycode);
        string legend = slot.MainLegend.Trim();
        return legend.Length > 0 ? legend : $"#{slot.Index + 1}";
    }

    private static string SlotName(KeyboardLayout layout, int slot) =>
        layout.Slots.FirstOrDefault(s => s.Index == slot) is { } s ? KeyName(s) : Loc.T($"キー{slot}", $"Key {slot}");

    // 空の欄（動作なし）。英語の表示では半角の「-」。
    private static string Empty => Loc.T("－", "-");

    // 基本キー・修飾付きキーの読める名前（「Ctrl+Shift+T」）。空は Empty。
    private static string KeyText(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return Empty;
        string t = token.Trim();
        // 内側のキーは、書いたままの名前で引く（スキャンコードから引き直すと、LALT(KC_ZKHK) が同じスキャンコードの KC_GRAVE になる）。
        if (KeycodeTable.TryResolve(t, out var a) && a.Type == ActionType.ModdedKey)
            return ModMaskText(a.Param1) + KeyText(KeyMarks.OverrideKeyFor(t) ?? KeycodeTable.KeyToken(a.Param2));
        return KeycodeCatalog.ShortName(t).TrimStart('⚠');
    }

    // KBDLAYER_MOD_* のマスク → 「Ctrl+Shift+」（右は「右Ctrl+」）。
    private static string ModMaskText(byte mask)
    {
        string[] names = { "Ctrl", "Shift", "Alt", "Win" };
        var sb = new StringBuilder();
        for (int bit = 0; bit < 8; bit++)
        {
            if ((mask & (1 << bit)) == 0) continue;
            sb.Append(bit >= 4 ? Loc.T("右", "R") : "").Append(names[bit % 4]).Append('+');
        }
        return sb.ToString();
    }

    // キー置換の条件の修飾キー（MOD_LCTL|MOD_LSFT → 「Ctrl+Shift+」。左右は区別しないので、左右とも同じ名前）。
    private static string ModsText(string mods)
    {
        if (!KeycodeTable.TryParseModMask(mods.Replace(" ", ""), out var mask)) return mods + "+";
        byte merged = (byte)((mask | (mask >> 4)) & 0x0F);
        return ModMaskText(merged);
    }

    private static string Field(KeymapProject project, string? token, HelperConfig? device, HelperConfig? global) =>
        string.IsNullOrWhiteSpace(token) || token.Trim() == "KC_NO" ? Empty : Describe(project, token, device, global);

    /// <summary>割り当て1つの読める説明。</summary>
    public static string Describe(KeymapProject project, string token, HelperConfig? device = null, HelperConfig? global = null)
    {
        string t = token.Trim();
        if (!KeycodeTable.TryResolve(t, out var a)) return Loc.T($"(読めないキーコード {t})", $"(unreadable keycode {t})");
        string Layer(int n) => ProjectService.LayerLabel(project, n);
        return a.Type switch
        {
            ActionType.None => Loc.T("何もしない", "nothing"),
            ActionType.Transparent => "▽",
            ActionType.Key or ActionType.ModdedKey => KeyText(t),
            ActionType.ModTap => Loc.T($"タップ: {KeyText(KeycodeTable.KeyToken(a.Param2))} / 長押し: {ModMaskText((byte)(1 << a.Param1)).TrimEnd('+')} (Mod-Tap)",
                                       $"tap: {KeyText(KeycodeTable.KeyToken(a.Param2))} / hold: {ModMaskText((byte)(1 << a.Param1)).TrimEnd('+')} (Mod-Tap)"),
            ActionType.LayerTap => Loc.T($"タップ: {KeyText(KeycodeTable.KeyToken(a.Param2))} / 長押し: {Layer(a.Param1)} (Layer-Tap)",
                                         $"tap: {KeyText(KeycodeTable.KeyToken(a.Param2))} / hold: {Layer(a.Param1)} (Layer-Tap)"),
            ActionType.Momentary => Loc.T($"押している間 {Layer(a.Param1)}", $"{Layer(a.Param1)} while held"),
            ActionType.Toggle => Loc.T($"{Layer(a.Param1)} の切り替え", $"toggle {Layer(a.Param1)}"),
            ActionType.Default => Loc.T($"基本のレイヤーを {a.Param1} に", $"base layer to {a.Param1}"),
            ActionType.OneShotLayer => Loc.T($"次の1キーだけ {Layer(a.Param1)}", $"{Layer(a.Param1)} for the next key"),
            ActionType.LayerTo => Loc.T($"{Layer(a.Param1)} だけにする", $"only {Layer(a.Param1)}"),
            ActionType.LayerTapToggle => Loc.T($"押している間 {Layer(a.Param1)}(続けてタップで固定)", $"{Layer(a.Param1)} while held (tap repeatedly to lock)"),
            ActionType.LayerMod => Loc.T($"押している間 {Layer(a.Param1)} + {ModMaskText((byte)(a.Param2 & 0xFF)).TrimEnd('+')}", $"{Layer(a.Param1)} + {ModMaskText((byte)(a.Param2 & 0xFF)).TrimEnd('+')} while held"),
            ActionType.OneShotMod => Loc.T($"次の1キーだけ {ModMaskText(a.Param1).TrimEnd('+')}", $"{ModMaskText(a.Param1).TrimEnd('+')} for the next key"),
            ActionType.Macro => KeyMarks.Describe(Loc.T("マクロ", "Macro"), a.Param1, project.MacroNames),
            ActionType.TapDance => KeyMarks.Describe(Loc.T("タップダンス", "Tap dance"), a.Param1, project.TapDanceNames),
            ActionType.Custom => HelperText(a.Param2, device, global),
            ActionType.Mark => Loc.T($"マークモード({t})", $"mark mode ({t})"),
            _ => t,
        };
    }

    // HLP(n): このキーボード用 → 共通の順に引いて、名前（無ければ中身）を添える。
    private static string HelperText(int id, HelperConfig? device, HelperConfig? global)
    {
        var a = device?.Actions.FirstOrDefault(x => x.Id == id) ?? global?.Actions.FirstOrDefault(x => x.Id == id);
        if (a == null) return Loc.T($"ヘルパー動作 HLP({id})(動作がありません)", $"helper action HLP({id}) (no such action)");
        string what = string.IsNullOrWhiteSpace(a.Name) ? HelperSummary.Body(a.Action) : Quoted(a.Name).TrimStart();
        return Loc.T($"ヘルパー動作 HLP({id}) {what}", $"helper action HLP({id}) {what}");
    }
}
