using Kasane.Interop;
using Kasane.Keycodes;
using Kasane.Layout;

namespace Kasane.Model;

/// <summary>
/// メイン画面のキー図に出す、キー置換・コンボ・マクロ・タップダンスの印（2026-10-21）。
/// 割り当てが普通のキーに見えても、別の機能が関わるキーが分かるようにする。印は右下に1文字ずつ:
/// M = マクロ（キーの割り当てが MACRO(n)）、D = タップダンス（TAPDANCE(n)）、C = コンボ（同時押しの対象のキー）、
/// R = キー置換（Replace。修飾キーと押したときに、別のキーに置き換わるキー）。
/// コンボは物理のキー（スロット）で決まるので、すべてのレイヤーに出す。キー置換は「レイヤーを通したあとの出力キー」が条件なので、
/// そのキーを出すレイヤーに出す。透過（KC_TRNS）のキーは、下のレイヤーの割り当てを引き継ぐものとして数える。
/// </summary>
public static class KeyMarks
{
    /// <param name="Marks">右下に出す文字（「MCR」のように並べる。無ければ空）。</param>
    /// <param name="ToolTip">ツールチップに足す説明（印ごとに1行。無ければ空）。</param>
    public sealed record Info(string Marks, string ToolTip)
    {
        public static readonly Info None = new("", "");
    }

    /// <summary>
    /// そのキーに関わる定義（<see cref="Find"/> の結果）。無効にした定義も含む（<c>Disabled</c> で見分ける）。
    /// </summary>
    /// <param name="Token">レイヤーを通した、そのキーの割り当て（<see cref="EffectiveToken"/>）。</param>
    /// <param name="Combos">そのキーをメンバーに持つコンボ（番号の順）。</param>
    /// <param name="KeyOverrides">そのキーの出力を条件にするキー置換（番号の順）。</param>
    /// <param name="MacroId">割り当てが MACRO(n) なら n。</param>
    /// <param name="TapDanceId">割り当てが TAPDANCE(n) なら n。</param>
    public sealed record Related(string Token, IReadOnlyList<ComboDef> Combos, IReadOnlyList<KeyOverrideDef> KeyOverrides, int? MacroId, int? TapDanceId);

    /// <summary>
    /// レイヤー <paramref name="layer"/> の、そのキーに関わるコンボ・キー置換・マクロ・タップダンス（2026-11-05、GUI 計画 1-2）。
    /// 印（<see cref="For"/>）と、割り当て画面・右クリックの「開く」が、この判定を共用する。印と違い、無効にした定義も返す。
    /// </summary>
    public static Related Find(KeymapProject project, int layer, KeySlot slot)
    {
        string token = EffectiveToken(project, layer, slot);
        int? macro = null, tapDance = null;
        if (KeycodeTable.TryResolve(token, out var action))
        {
            if (action.Type == ActionType.Macro) macro = action.Param1;
            else if (action.Type == ActionType.TapDance) tapDance = action.Param1;
        }
        var combos = project.Combos.Where(c => c.Slot1 == slot.Index || c.Slot2 == slot.Index).OrderBy(c => c.Id).ToList();
        var overrides = project.KeyOverrides.Where(k => TriggersOverride(k, token)).OrderBy(k => k.Id).ToList();
        return new Related(token, combos, overrides, macro, tapDance);
    }

    /// <summary>
    /// ヘルパー動作とトリガー（helper.json）の、印に使う分（2026-11-09）。動作は Id → 説明、トリガーは (キー, 説明)。
    /// このキーボード用が先、共通が後（同じ Id は、先のものが勝つ。トレイも同じ順に引く）。
    /// </summary>
    public sealed class HelperLookup
    {
        public Dictionary<int, string> Actions { get; } = new();
        public List<(ushort Scan, string Text)> Triggers { get; } = new();

        public static HelperLookup From(HelperConfig? device, HelperConfig? global)
        {
            var l = new HelperLookup();
            foreach (var c in new[] { device, global })
            {
                if (c == null) continue;
                foreach (var a in c.Actions) l.Actions.TryAdd(a.Id, HelperSummary.Action(a));
                foreach (var t in c.Triggers)
                    if (KeycodeTable.TryResolveBaseKey(t.Key ?? "", out var sc) && sc != 0) l.Triggers.Add((sc, HelperSummary.Trigger(t)));
            }
            return l;
        }
    }

    /// <summary>レイヤー <paramref name="layer"/> の、そのキーの印。<paramref name="helper"/> が null のときは、ヘルパー動作・トリガーの印は H だけ（トリガーは調べない）。</summary>
    public static Info For(KeymapProject project, KeyboardLayout layout, int layer, KeySlot slot, HelperLookup? helper = null)
    {
        var r = Find(project, layer, slot);
        var marks = new System.Text.StringBuilder();
        var tip = new List<string>();

        if (r.MacroId is int macro)
        {
            marks.Append('M');
            tip.Add(Describe(Loc.T("マクロ", "Macro"), macro, project.MacroNames) +
                    (project.Macros.Any(m => m.Id == macro && m.Disabled) ? Loc.T("(無効)", " (disabled)") : ""));
        }
        else if (r.TapDanceId is int td)
        {
            marks.Append('D');
            tip.Add(Describe(Loc.T("タップダンス", "Tap dance"), td, project.TapDanceNames) +
                    (project.TapDances.Any(t => t.Id == td && t.Disabled) ? Loc.T("(無効)", " (disabled)") : ""));
        }

        // 無効にしたコンボ・キー置換は、適用されないので、印を付けない。
        var combos = r.Combos.Where(c => !c.Disabled).ToList();
        foreach (var c in combos) tip.Add(ComboText(project, layout, slot, c));
        if (combos.Count > 0) marks.Append('C');

        var overrides = r.KeyOverrides.Where(k => !k.Disabled).ToList();
        foreach (var k in overrides) tip.Add(KeyOverrideText(project, k));
        if (overrides.Count > 0) marks.Append('R');

        // ヘルパー動作 HLP(Id) のキー（H）。トレイが動作を実行する。
        if (KeycodeTable.TryResolve(r.Token, out var act) && act.Type == ActionType.Custom)
        {
            marks.Append('H');
            string? detail = null;
            helper?.Actions.TryGetValue(act.Param2, out detail);
            tip.Add(detail != null ? Loc.T("ヘルパー動作 ", "Helper action ") + detail
                                   : Loc.T($"ヘルパー動作 HLP({act.Param2})", $"Helper action HLP({act.Param2})") +
                                     (helper != null ? Loc.T("(動作がありません)", " (no such action)") : ""));
        }

        // トリガーのキー（T）。このキーが出すキーを、トレイが見て、動作を実行する（押したキーはアプリに届かない）。
        if (helper != null && OverrideKeyFor(r.Token) is { } outKey && KeycodeTable.TryResolveBaseKey(outKey, out var outScan) && outScan != 0)
        {
            var hits = helper.Triggers.Where(t => t.Scan == outScan).Select(t => t.Text).ToList();
            if (hits.Count > 0)
            {
                marks.Append('T');
                foreach (var h in hits) tip.Add(Loc.T("トリガー: ", "Trigger: ") + h);
            }
        }

        // 並びは「M D C R H T」の順に固定する（キーごとに同じ順で読めるように）。
        string ordered = new string("MDCRHT".Where(ch => marks.ToString().Contains(ch)).ToArray());
        return ordered.Length == 0 ? Info.None : new Info(ordered, string.Join("\n", tip));
    }

    /// <summary>コンボの説明（例: 「コンボ3「Esc」: #12 J と同時に押す → KC_ESC」）。ツールチップと割り当て画面の一覧で使う。</summary>
    public static string ComboText(KeymapProject project, KeyboardLayout layout, KeySlot slot, ComboDef c)
    {
        int otherIndex = c.Slot1 == slot.Index ? c.Slot2 : c.Slot1;
        string other = layout.Slots.FirstOrDefault(s => s.Index == otherIndex)?.Description ?? Loc.T($"キー{otherIndex}", $"Key {otherIndex}");
        string combo = Describe(Loc.T("コンボ", "Combo"), c.Id, project.ComboNames);
        return Loc.T($"{combo}: {other} と同時に押す → {c.Output}", $"{combo}: press together with {other} → {c.Output}");
    }

    /// <summary>キー置換の説明（例: 「キー置換0: MOD_LSFT を押しながら → KC_DELETE」）。</summary>
    public static string KeyOverrideText(KeymapProject project, KeyOverrideDef k)
    {
        string ko = Describe(Loc.T("キー置換", "Key override"), k.Id, project.KeyOverrideNames);
        return Loc.T($"{ko}: {k.Mods.Replace(" ", "")} を押しながら → {k.Replacement}", $"{ko}: while holding {k.Mods.Replace(" ", "")} → {k.Replacement}");
    }

    /// <summary>
    /// キー置換の条件のキーに使えるトークン。割り当てが基本キーならそのまま、修飾付きのキー（LALT(KC_ZKHK) など）なら出力キー（KC_ZKHK）。
    /// キーを出さない割り当て（MO(1)、MACRO(n)、KC_NO など）は null（キー置換の条件にならない）。
    /// </summary>
    public static string? OverrideKeyFor(string token)
    {
        if (!KeycodeTable.TryResolve(token, out var a) || a.Param2 == 0) return null;
        if (a.Type == ActionType.Key) return token.Trim();
        if (a.Type != ActionType.ModdedKey) return null;
        // 書いたままの内側の名前（LALT(KC_ZKHK) → KC_ZKHK）。同じスキャンコードの別名（KC_GRAVE）に変えない。
        string inner = token.Trim();
        while (inner.IndexOf('(') is int open && open > 0 && inner.EndsWith(')')) inner = inner[(open + 1)..^1].Trim();
        return KeycodeTable.TryResolveBaseKey(inner, out var sc) && sc == a.Param2 ? inner : KeycodeTable.KeyToken(a.Param2);
    }

    /// <summary>キーを出す割り当て（基本キー・修飾付きキー。KC_NO を除く）ならそのトークン、それ以外は null。タップダンスの1回タップの初期値。</summary>
    public static string? KeyLikeToken(string token) =>
        KeycodeTable.TryResolve(token, out var a) && a.Param2 != 0 && a.Type is ActionType.Key or ActionType.ModdedKey ? token.Trim() : null;


    /// <summary>
    /// 割り当て（token）が、キー置換の条件のキーに当たるか。同じトークンなら当たる。割り当てが修飾付きのキー
    /// （<c>LALT(KC_ZKHK)</c> など）のときは、出力キー（<c>KC_ZKHK</c>）で照合する（ドライバも、出力キーで照合する）。
    /// </summary>
    private static bool TriggersOverride(KeyOverrideDef k, string token)
    {
        string key = k.Key?.Trim() ?? "";
        if (string.Equals(key, token, StringComparison.OrdinalIgnoreCase)) return true;
        return KeycodeTable.TryResolve(token, out var assigned) && assigned.Type == ActionType.ModdedKey &&
               KeycodeTable.TryResolve(key, out var trigger) && trigger.Type == ActionType.Key &&
               trigger.Param2 == assigned.Param2;
    }

    /// <summary>レイヤーを通した、そのキーの割り当て。透過なら下のレイヤーへ、レイヤー0でも透過なら既定のキーコード。</summary>
    public static string EffectiveToken(KeymapProject project, int layer, KeySlot slot)
    {
        for (int l = Math.Min(layer, project.Layers.Count - 1); l >= 0; l--)
        {
            var row = project.Layers[l];
            if (slot.Index >= row.Count) continue;
            string t = row[slot.Index];
            if (!string.IsNullOrEmpty(t) && t != "KC_TRNS") return t;
        }
        return slot.DefaultKeycode ?? "";
    }

    /// <summary>「コンボ3「名前」」のような、番号と名前の表示（名前が無ければ「コンボ3」）。</summary>
    public static string Describe(string kind, int id, Dictionary<int, string>? names) =>
        names != null && names.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n)
            ? Loc.T($"{kind}{id}「{n}」", $"{kind} {id} \"{n}\"")
            : Loc.T($"{kind}{id}", $"{kind} {id}");

    /// <summary>
    /// ボタンの表示。定義が無ければ「マクロ...」、あれば「マクロ(2)...」。無効にした定義があれば「マクロ(2・無効1)...」
    /// （すべて無効なら「マクロ(無効1)...」）、機能がOFFなら「マクロ(2・OFF)...」。<paramref name="count"/> は無効を含む定義の数。
    /// </summary>
    public static string ButtonText(string name, int count, bool featureOn, int disabled = 0)
    {
        if (count == 0) return $"{name}...";
        var parts = new List<string>();
        if (count - disabled > 0 || disabled == 0) parts.Add((count - disabled).ToString());
        if (disabled > 0) parts.Add(Loc.T($"無効{disabled}", $"{disabled} disabled"));
        if (!featureOn) parts.Add("OFF");
        return Loc.T($"{name}({string.Join("・", parts)})...", $"{name} ({string.Join(", ", parts)})...");
    }

    /// <summary>機能のON/OFF（<see cref="KeymapProject.EnabledFeatures"/> が null なら、GUIでは管理しない＝すべて有効とみなす）。</summary>
    public static bool FeatureOn(KeymapProject project, uint feature) =>
        project.EnabledFeatures is not uint f || (f & feature) != 0;
}
