using System.Text.RegularExpressions;
using Kasane.Keycodes;
using Kasane.Layout;

namespace Kasane.Model.Portable;

public sealed class ExportOptions
{
    /// <summary>実測したスキャンコードを含める（同じ機器へのバックアップ用。別のキーボードへ持っていくときは含めない）。</summary>
    public bool IncludeMeasured { get; set; }
    /// <summary>ヘルパー設定（helper.json）を含める。</summary>
    public bool IncludeHelper { get; set; } = true;
}

/// <summary>キーコード文字列の中の MACRO(n) / TAPDANCE(n) / HLP(n) の番号を調べる・付け替える。</summary>
public static class TokenIds
{
    private static readonly Regex Ref = new(@"\b(MACRO|TAPDANCE|HLP)\(\s*([^)\s]+)\s*\)", RegexOptions.Compiled);

    private static bool TryParseId(string s, out int id)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.AllowHexSpecifier,
                                System.Globalization.CultureInfo.InvariantCulture, out id);
        return int.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out id);
    }

    /// <summary>トークンが参照している (種類, 番号) を列挙する。</summary>
    public static IEnumerable<(string Kind, int Id)> References(string token)
    {
        foreach (Match m in Ref.Matches(token))
            if (TryParseId(m.Groups[2].Value, out int id)) yield return (m.Groups[1].Value, id);
    }

    /// <summary>番号を付け替える。表に無い番号はそのまま（HLP は10進に直す）。</summary>
    public static string Rewrite(string token, IReadOnlyDictionary<int, int>? macro, IReadOnlyDictionary<int, int>? tapDance,
                                 IReadOnlyDictionary<int, int>? helper) =>
        Ref.Replace(token, m =>
        {
            if (!TryParseId(m.Groups[2].Value, out int id)) return m.Value;
            var map = m.Groups[1].Value switch { "MACRO" => macro, "TAPDANCE" => tapDance, _ => helper };
            if (map != null && map.TryGetValue(id, out int to)) id = to;
            return $"{m.Groups[1].Value}({id})";
        });
}

public static class PortableExporter
{
    public static string ScName(ushort sc) => $"SC_{sc:X4}";

    public static PortableFile NewFile(PortableScope scope) => new()
    {
        Scope = scope.ToString(),
        Exported = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
        App = "kasane",
    };

    /// <summary>
    /// 1台分の全部。レイヤー0は全キーを書く（透過も含めて、元のとおりに戻せるように）。それ以外のレイヤーは透過を省く。
    /// </summary>
    public static PortableKeyboard ExportKeyboard(KeymapProject p, KeyboardLayout layout, HelperConfig? helper,
                                                  ExportOptions options, PortableReport report)
    {
        var kb = Header(p);
        kb.Layers = new List<PortableLayer>();
        for (int L = 0; L < p.Layers.Count; L++) kb.Layers.Add(LayerToPortable(p, layout, L, includeTransparent: L == 0, report));

        kb.Macros = p.Macros.Select(m => new MacroDef { Id = m.Id, Keys = new List<string>(m.Keys) }).ToList();
        kb.TapDances = p.TapDances.Select(CloneTapDance).ToList();
        kb.KeyOverrides = p.KeyOverrides.Select(k => new KeyOverrideDef { Id = k.Id, Mods = k.Mods, Key = k.Key, Replacement = k.Replacement }).ToList();
        kb.Combos = new List<PortableCombo>();
        foreach (var c in p.Combos)
        {
            if (!TryComboKeys(p, layout, c, out var k1, out var k2))
            {
                report.Warn($"コンボ{c.Id}: メンバーのスキャンコードが分からないため書き出せませんでした");
                continue;
            }
            kb.Combos.Add(new PortableCombo { Id = c.Id, Key1 = ScName(k1), Key2 = ScName(k2), Output = c.Output });
        }
        kb.Timing = new PortableTiming
        {
            TapTermMs = p.TapTermMs, ComboTermMs = p.ComboTermMs, TapDanceTermMs = p.TapDanceTermMs,
            TapHoldMode = p.TapHoldMode, QuickTapTermMs = p.QuickTapTermMs, TappingToggle = p.TappingToggle,
        };
        kb.EnabledFeatures = p.EnabledFeatures;
        kb.Nicola = p.Nicola?.Clone();
        if (options.IncludeMeasured && p.MeasuredScanCodes.Count > 0)
            kb.MeasuredScanCodes = p.MeasuredScanCodes.ToDictionary(kv => kv.Key.ToString(), kv => ScName(kv.Value));
        if (options.IncludeHelper && helper != null && HasContent(helper)) kb.Helper = CloneHelper(helper);
        return kb;
    }

    /// <summary>
    /// レイヤー1枚。そのレイヤーが参照するマクロ・タップダンス（とそこから参照するマクロ）・ヘルパー動作も一緒に書く
    /// （別のキーボードに持っていっても、番号の参照が切れないように）。ヘルパー動作は、キーボードごとの設定、
    /// 無ければ共通の設定から探す。
    /// </summary>
    public static PortableKeyboard ExportLayer(KeymapProject p, KeyboardLayout layout, int layer, HelperConfig? deviceHelper,
                                               HelperConfig? globalHelper, PortableReport report)
    {
        var kb = Header(p);
        var pl = LayerToPortable(p, layout, layer, includeTransparent: false, report);
        kb.Layers = new List<PortableLayer> { pl };

        var tokens = pl.Keys.Values.ToList();
        var tdIds = new SortedSet<int>();
        var macroIds = new SortedSet<int>();
        var helperIds = new SortedSet<int>();
        void Collect(string token)
        {
            foreach (var (kind, id) in TokenIds.References(token))
            {
                if (kind == "MACRO") macroIds.Add(id);
                else if (kind == "TAPDANCE") tdIds.Add(id);
                else helperIds.Add(id);
            }
        }
        tokens.ForEach(Collect);
        kb.TapDances = p.TapDances.Where(t => tdIds.Contains(t.Id)).Select(CloneTapDance).ToList();
        foreach (var td in kb.TapDances) foreach (var t in new[] { td.SingleTap, td.SingleHold, td.DoubleTap, td.TripleTap }) Collect(t);
        kb.Macros = p.Macros.Where(m => macroIds.Contains(m.Id)).Select(m => new MacroDef { Id = m.Id, Keys = new List<string>(m.Keys) }).ToList();

        foreach (int id in tdIds.Where(i => kb.TapDances.All(t => t.Id != i)))
            report.Warn($"TAPDANCE({id}) の中身が未設定のため、書き出していません");
        foreach (int id in macroIds.Where(i => kb.Macros.All(m => m.Id != i)))
            report.Warn($"MACRO({id}) の中身が未設定のため、書き出していません");

        if (helperIds.Count > 0)
        {
            var h = new HelperConfig();
            foreach (int id in helperIds)
            {
                var a = deviceHelper?.Actions.FirstOrDefault(x => x.Id == id) ?? globalHelper?.Actions.FirstOrDefault(x => x.Id == id);
                if (a == null) { report.Warn($"HLP({id}) のヘルパー動作が見つからないため、書き出していません"); continue; }
                h.Actions.Add(CloneHelper(new HelperConfig { Actions = { a } }).Actions[0]);
            }
            if (h.Actions.Count > 0) kb.Helper = h;
        }
        return kb;
    }

    private static PortableKeyboard Header(KeymapProject p) => new()
    {
        UniqId = PortableKeyboard.FormatUniqId(p.UniqId),
        HardwareId = p.HardwareId,
        Name = string.IsNullOrWhiteSpace(p.KeyboardName) ? null : p.KeyboardName,
        LayoutName = p.LayoutName,
    };

    private static PortableLayer LayerToPortable(KeymapProject p, KeyboardLayout layout, int layer, bool includeTransparent, PortableReport report)
    {
        var pl = new PortableLayer { Index = layer };
        var seen = new HashSet<ushort>();
        foreach (var slot in layout.Slots)
        {
            string token = p.Layers[layer][slot.Index];
            bool transparent = token == "KC_TRNS";
            if (transparent && !includeTransparent) continue;
            var sc = ProjectService.EffectiveScanCode(p, slot);
            if (sc is not ushort v)
            {
                if (!transparent) report.Warn($"レイヤー{layer} {slot.Description}: スキャンコードが分からないため書き出せませんでした");
                continue;
            }
            if (!seen.Add(v))
            {
                report.Warn($"レイヤー{layer} {slot.Description}: 同じスキャンコード {ScName(v)} のキーが他にあるため、書き出しません");
                continue;
            }
            pl.Keys[ScName(v)] = token;
        }
        return pl;
    }

    private static bool TryComboKeys(KeymapProject p, KeyboardLayout layout, ComboDef c, out ushort k1, out ushort k2)
    {
        k1 = k2 = 0;
        if (c.Slot1 < 0 || c.Slot1 >= layout.Slots.Count || c.Slot2 < 0 || c.Slot2 >= layout.Slots.Count) return false;
        var a = ProjectService.EffectiveScanCode(p, layout.Slots[c.Slot1]);
        var b = ProjectService.EffectiveScanCode(p, layout.Slots[c.Slot2]);
        if (a == null || b == null) return false;
        k1 = a.Value;
        k2 = b.Value;
        return true;
    }

    private static TapDanceDef CloneTapDance(TapDanceDef t) => new()
    {
        Id = t.Id, SingleTap = t.SingleTap, SingleHold = t.SingleHold, DoubleTap = t.DoubleTap, TripleTap = t.TripleTap,
    };

    public static bool HasContent(HelperConfig h) => h.Actions.Count + h.Triggers.Count + h.AppLayerRules.Count > 0;

    public static HelperConfig CloneHelper(HelperConfig h) => HelperConfig.FromJson(h.ToJson()) ?? new HelperConfig();
}
