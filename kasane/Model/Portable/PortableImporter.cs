using System.Text.Json;
using Kasane.Interop;
using Kasane.Keycodes;
using Kasane.Layout;

namespace Kasane.Model.Portable;

public enum ImportMode { Replace, Append }

public sealed class ImportOptions
{
    public bool Layers { get; set; } = true;
    public bool Macros { get; set; } = true;
    public bool Combos { get; set; } = true;
    public bool TapDances { get; set; } = true;
    public bool KeyOverrides { get; set; } = true;
    public bool Timing { get; set; } = true;
    public bool Features { get; set; } = true;
    public bool Nicola { get; set; } = true;
    public bool Helper { get; set; } = true;
    /// <summary>実測したスキャンコードも読み込む（同じレイアウトのときだけ）。</summary>
    public bool MeasuredScanCodes { get; set; }

    /// <summary>
    /// 置き換え: 選んだ項目を、ファイルの内容で置き換える。追加: 今の設定を残し、ファイルの内容を足す
    /// （レイヤーは同じ番号に重ねる。マクロなどは番号が重なれば付け直す。タイミング・機能・NICOLA は触らない）。
    /// </summary>
    public ImportMode Mode { get; set; } = ImportMode.Replace;

    /// <summary>レイヤー単位の読み込みの行き先。null = 新しいレイヤーとして末尾に足す。数字 = そのレイヤーを上書きする。</summary>
    public int? TargetLayer { get; set; }
}

public sealed class ImportResult
{
    public PortableReport Report { get; } = new();
    /// <summary>書き込むべき、このキーボードの helper.json の新しい内容（変えないなら null）。</summary>
    public HelperConfig? Helper { get; set; }
    public bool ProjectChanged { get; set; }
}

public static class PortableImporter
{
    private static readonly JsonSerializerOptions CompareOptions = new();

    /// <summary>
    /// ファイルの1台分を、プロジェクトに取り込む（プロジェクトを直接書き換える。呼び出し側が複製を渡して試せる）。
    /// ヘルパー設定は書き込まず、新しい内容を <see cref="ImportResult.Helper"/> に返す。
    /// </summary>
    /// <param name="layerScope">レイヤー単位のファイル（1枚だけ。<see cref="ImportOptions.TargetLayer"/> が行き先）。</param>
    public static ImportResult Import(PortableKeyboard src, KeymapProject target, KeyboardLayout layout,
                                      HelperConfig? deviceHelper, HelperConfig? globalHelper, ImportOptions o, bool layerScope)
    {
        var result = new ImportResult();
        var r = result.Report;
        var mode = layerScope ? ImportMode.Append : o.Mode;

        ProjectService.NormalizeShape(target, layout);

        // キーボードの名前は、取り込み先に名前がまだ無いときだけ引き継ぐ（利用者がつけた名前を上書きしない）。レイヤー1枚の取り込みでは触らない。
        if (!layerScope && !string.IsNullOrWhiteSpace(src.Name) && string.IsNullOrWhiteSpace(target.KeyboardName))
        {
            target.KeyboardName = src.Name;
            r.Info($"キーボードの名前「{src.Name}」を引き継ぎました");
        }

        if (o.MeasuredScanCodes && src.MeasuredScanCodes is { Count: > 0 } measured)
        {
            if (string.Equals(src.LayoutName, target.LayoutName, StringComparison.OrdinalIgnoreCase)) ApplyMeasured(target, layout, measured, r);
            else r.Warn($"実測値は、同じレイアウトのときだけ読み込みます（ファイル: {src.LayoutName}、今: {target.LayoutName}）");
        }

        // ヘルパー動作を先に決める（キーの HLP(n) の番号を付け替えるため）。
        Dictionary<int, int>? helperMap = null;
        if (o.Helper && src.Helper != null)
        {
            var merged = HelperMerge.Merge(deviceHelper, globalHelper, src.Helper, mode, out helperMap, r);
            result.Helper = merged;
        }
        else if (!o.Helper && src.Helper != null && src.Helper.Actions.Count > 0)
            r.Info("ヘルパー設定は読み込みません（HLP(n) のキーは、番号のまま取り込みます）");

        Dictionary<int, int>? macroMap = null, tdMap = null;
        if (o.Macros && src.Macros != null) macroMap = ImportMacros(src.Macros, target, mode, r);
        if (o.TapDances && src.TapDances != null) tdMap = ImportTapDances(src.TapDances, target, mode, macroMap, helperMap, r);

        string Fix(string token) => TokenIds.Rewrite(token, macroMap, tdMap, helperMap);

        var bySc = ProjectService.SlotsByScanCode(target, layout);

        if (o.Layers && src.Layers is { Count: > 0 })
        {
            if (layerScope) ImportOneLayer(src.Layers[0], target, layout, bySc, o.TargetLayer, Fix, r);
            else if (mode == ImportMode.Replace) ReplaceLayers(src.Layers, target, layout, bySc, Fix, r);
            else OverlayLayers(src.Layers, target, layout, bySc, Fix, r);
            result.ProjectChanged = true;
        }

        if (o.Combos && src.Combos != null) ImportCombos(src.Combos, target, bySc, mode, Fix, r);
        if (o.KeyOverrides && src.KeyOverrides != null) ImportKeyOverrides(src.KeyOverrides, target, mode, r);

        if (mode == ImportMode.Replace)
        {
            if (o.Timing && src.Timing is { } t)
            {
                target.TapTermMs = t.TapTermMs;
                target.ComboTermMs = t.ComboTermMs;
                target.TapDanceTermMs = t.TapDanceTermMs;
                target.TapHoldMode = t.TapHoldMode;
                target.QuickTapTermMs = t.QuickTapTermMs;
                target.TappingToggle = t.TappingToggle;
                r.Info("タイミングを読み込みました");
            }
            if (o.Features && src.EnabledFeatures is uint f)
            {
                target.EnabledFeatures = f & KbdLayerClient.FeatureAll;
                r.Info("機能のON/OFFを読み込みました");
            }
            if (o.Nicola && src.Nicola != null)
            {
                target.Nicola = src.Nicola.Clone();
                target.ClearNicola = false;
                r.Info("NICOLAの設定を読み込みました");
            }
        }
        else if ((o.Timing && src.Timing != null) || (o.Features && src.EnabledFeatures != null) || (o.Nicola && src.Nicola != null))
            r.Info("タイミング・機能のON/OFF・NICOLAは、「置き換え」のときだけ読み込みます");

        CheckLayerReferences(target, r);
        return result;
    }

    // ------------------------------------------------------------------
    // レイヤー
    // ------------------------------------------------------------------

    private static void ReplaceLayers(List<PortableLayer> layers, KeymapProject target, KeyboardLayout layout,
                                      Dictionary<ushort, KeySlot> bySc, Func<string, string> fix, PortableReport r)
    {
        int count = Math.Min(layers.Max(l => l.Index) + 1, KbdLayerClient.MaxLayers);
        if (layers.Any(l => l.Index >= KbdLayerClient.MaxLayers))
            r.Warn($"レイヤーは{KbdLayerClient.MaxLayers}枚までなので、レイヤー{KbdLayerClient.MaxLayers}以降は読み込みません");
        count = Math.Max(count, 1);
        target.Layers = Enumerable.Range(0, count).Select(i => ProjectService.DefaultLayer(layout, i)).ToList();
        var missing = new List<string>();
        int placed = 0;
        foreach (var l in layers)
            if (l.Index >= 0 && l.Index < count) placed += PlaceKeys(l, target.Layers[l.Index], bySc, fix, missing, r, l.Index);
        r.Info($"レイヤーを{count}枚、キーを{placed}個読み込みました");
        ReportMissing(missing, r);
    }

    private static void OverlayLayers(List<PortableLayer> layers, KeymapProject target, KeyboardLayout layout,
                                      Dictionary<ushort, KeySlot> bySc, Func<string, string> fix, PortableReport r)
    {
        var missing = new List<string>();
        int placed = 0;
        foreach (var l in layers.OrderBy(l => l.Index))
        {
            if (l.Index < 0 || l.Index >= KbdLayerClient.MaxLayers)
            {
                r.Warn($"レイヤー{l.Index}は読み込めません（0〜{KbdLayerClient.MaxLayers - 1}）");
                continue;
            }
            while (target.Layers.Count <= l.Index) target.Layers.Add(ProjectService.DefaultLayer(layout, target.Layers.Count));
            placed += PlaceKeys(l, target.Layers[l.Index], bySc, fix, missing, r, l.Index);
        }
        r.Info($"キーを{placed}個、今のレイヤーに重ねました");
        ReportMissing(missing, r);
    }

    private static void ImportOneLayer(PortableLayer src, KeymapProject target, KeyboardLayout layout,
                                       Dictionary<ushort, KeySlot> bySc, int? targetLayer, Func<string, string> fix, PortableReport r)
    {
        int index;
        if (targetLayer is int t)
        {
            if (t < 0 || t >= target.Layers.Count) { r.Error($"レイヤー{t}はありません"); return; }
            index = t;
            target.Layers[index] = ProjectService.DefaultLayer(layout, index);
        }
        else
        {
            if (target.Layers.Count >= KbdLayerClient.MaxLayers)
            {
                r.Error($"レイヤーは{KbdLayerClient.MaxLayers}枚までです。上書きするレイヤーを選んでください");
                return;
            }
            index = target.Layers.Count;
            target.Layers.Add(ProjectService.DefaultLayer(layout, index));
        }
        var missing = new List<string>();
        int placed = PlaceKeys(src, target.Layers[index], bySc, fix, missing, r, index);
        r.Info($"レイヤー{index}に、キーを{placed}個読み込みました" + (targetLayer == null ? "（新しいレイヤー）" : "（上書き）"));
        ReportMissing(missing, r);
        if (src.Keys.Values.Any(v => KeycodeTable.TryResolve(v, out var a) && ActionType.ReferencesLayer(a.Type)))
            r.Warn("レイヤーを切り替えるキー（MO(1) など）は、元の番号のまま取り込みました。番号が合っているか確かめてください");
    }

    private static int PlaceKeys(PortableLayer l, List<string> tokens, Dictionary<ushort, KeySlot> bySc, Func<string, string> fix,
                                 List<string> missing, PortableReport r, int index)
    {
        int placed = 0;
        foreach (var (name, raw) in l.Keys)
        {
            if (!KeycodeTable.TryParseRawScanCode(name, out ushort sc) && !KeycodeTable.TryResolveBaseKey(name, out sc))
            {
                r.Warn($"レイヤー{index}: キー「{name}」はスキャンコード（SC_XXXX）として読めないため、読み込みません");
                continue;
            }
            string token = fix(raw.Trim());
            if (!KeycodeTable.TryResolve(token, out _, out string? err))
            {
                r.Warn($"レイヤー{index} {name}: {err}");
                continue;
            }
            if (!bySc.TryGetValue(sc, out var slot))
            {
                missing.Add(PortableExporter.ScName(sc));
                continue;
            }
            tokens[slot.Index] = token;
            placed++;
        }
        return placed;
    }

    private static void ReportMissing(List<string> missing, PortableReport r)
    {
        if (missing.Count == 0) return;
        var distinct = missing.Distinct().ToList();
        string list = string.Join(", ", distinct.Take(12)) + (distinct.Count > 12 ? $" ほか{distinct.Count - 12}個" : "");
        r.Warn($"今のレイアウトに無いキーがありました（読み込んでいません）: {list}");
    }

    private static void CheckLayerReferences(KeymapProject p, PortableReport r)
    {
        int bad = 0;
        foreach (var layer in p.Layers)
            foreach (var t in layer)
                if (KeycodeTable.TryResolve(t, out var a) && ActionType.ReferencesLayer(a.Type) && a.Param1 >= p.Layers.Count) bad++;
        if (bad > 0) r.Warn($"存在しないレイヤーへ切り替えるキーが{bad}個あります（レイヤーを足すか、キーを直してください）");
    }

    private static void ApplyMeasured(KeymapProject target, KeyboardLayout layout, Dictionary<string, string> measured, PortableReport r)
    {
        var map = new Dictionary<int, ushort>();
        foreach (var (slotText, scText) in measured)
        {
            if (int.TryParse(slotText, out int slot) && slot >= 0 && slot < layout.Slots.Count && KeycodeTable.TryParseRawScanCode(scText, out ushort sc))
                map[slot] = sc;
        }
        target.MeasuredScanCodes = map;
        r.Info($"実測したスキャンコードを{map.Count}個読み込みました");
    }

    // ------------------------------------------------------------------
    // マクロ・タップダンス・コンボ・キー置換
    // ------------------------------------------------------------------

    private static int FreeId(IEnumerable<int> used, int max)
    {
        var set = used.ToHashSet();
        for (int i = 0; i < max; i++) if (!set.Contains(i)) return i;
        return -1;
    }

    private static Dictionary<int, int> ImportMacros(List<MacroDef> src, KeymapProject target, ImportMode mode, PortableReport r)
    {
        var map = new Dictionary<int, int>();
        if (mode == ImportMode.Replace)
        {
            target.Macros = new List<MacroDef>();
            foreach (var m in src)
            {
                if (m.Id < 0 || m.Id >= KbdLayerClient.MaxMacroCount) { r.Warn($"マクロ{m.Id}は読み込めません（0〜{KbdLayerClient.MaxMacroCount - 1}）"); continue; }
                target.Macros.Add(new MacroDef { Id = m.Id, Keys = new List<string>(m.Keys) });
                map[m.Id] = m.Id;
            }
            r.Info($"マクロを{target.Macros.Count}個読み込みました（置き換え）");
            return map;
        }
        int added = 0, reused = 0;
        foreach (var m in src)
        {
            var same = target.Macros.FirstOrDefault(x => x.Keys.SequenceEqual(m.Keys));
            if (same != null) { map[m.Id] = same.Id; reused++; continue; }
            int id = target.Macros.All(x => x.Id != m.Id) && m.Id >= 0 && m.Id < KbdLayerClient.MaxMacroCount
                ? m.Id : FreeId(target.Macros.Select(x => x.Id), KbdLayerClient.MaxMacroCount);
            if (id < 0) { r.Warn($"マクロの空きが無いため、マクロ{m.Id}は読み込めませんでした（上限{KbdLayerClient.MaxMacroCount}）"); continue; }
            target.Macros.Add(new MacroDef { Id = id, Keys = new List<string>(m.Keys) });
            map[m.Id] = id;
            added++;
            if (id != m.Id) r.Info($"マクロ{m.Id} → マクロ{id}（番号を付け直しました）");
        }
        r.Info($"マクロを{added}個足しました" + (reused > 0 ? $"（同じ内容の{reused}個は今のものを使います）" : ""));
        return map;
    }

    private static Dictionary<int, int> ImportTapDances(List<TapDanceDef> src, KeymapProject target, ImportMode mode,
                                                         Dictionary<int, int>? macroMap, Dictionary<int, int>? helperMap, PortableReport r)
    {
        var map = new Dictionary<int, int>();
        string Fix(string t) => TokenIds.Rewrite(t, macroMap, null, helperMap);
        TapDanceDef Copy(TapDanceDef t, int id) => new()
        {
            Id = id, SingleTap = Fix(t.SingleTap), SingleHold = Fix(t.SingleHold), DoubleTap = Fix(t.DoubleTap), TripleTap = Fix(t.TripleTap),
        };
        bool Same(TapDanceDef a, TapDanceDef b) => a.SingleTap == b.SingleTap && a.SingleHold == b.SingleHold && a.DoubleTap == b.DoubleTap && a.TripleTap == b.TripleTap;

        if (mode == ImportMode.Replace)
        {
            target.TapDances = new List<TapDanceDef>();
            foreach (var t in src)
            {
                if (t.Id < 0 || t.Id >= KbdLayerClient.MaxTapDanceCount) { r.Warn($"タップダンス{t.Id}は読み込めません（0〜{KbdLayerClient.MaxTapDanceCount - 1}）"); continue; }
                target.TapDances.Add(Copy(t, t.Id));
                map[t.Id] = t.Id;
            }
            r.Info($"タップダンスを{target.TapDances.Count}個読み込みました（置き換え）");
            return map;
        }
        int added = 0, reused = 0;
        foreach (var t in src)
        {
            var copy = Copy(t, t.Id);
            var same = target.TapDances.FirstOrDefault(x => Same(x, copy));
            if (same != null) { map[t.Id] = same.Id; reused++; continue; }
            int id = target.TapDances.All(x => x.Id != t.Id) && t.Id >= 0 && t.Id < KbdLayerClient.MaxTapDanceCount
                ? t.Id : FreeId(target.TapDances.Select(x => x.Id), KbdLayerClient.MaxTapDanceCount);
            if (id < 0) { r.Warn($"タップダンスの空きが無いため、タップダンス{t.Id}は読み込めませんでした（上限{KbdLayerClient.MaxTapDanceCount}）"); continue; }
            copy.Id = id;
            target.TapDances.Add(copy);
            map[t.Id] = id;
            added++;
            if (id != t.Id) r.Info($"タップダンス{t.Id} → タップダンス{id}（番号を付け直しました）");
        }
        r.Info($"タップダンスを{added}個足しました" + (reused > 0 ? $"（同じ内容の{reused}個は今のものを使います）" : ""));
        return map;
    }

    private static void ImportCombos(List<PortableCombo> src, KeymapProject target, Dictionary<ushort, KeySlot> bySc, ImportMode mode,
                                     Func<string, string> fix, PortableReport r)
    {
        if (mode == ImportMode.Replace) target.Combos = new List<ComboDef>();
        int added = 0;
        foreach (var c in src)
        {
            if (!KeycodeTable.TryParseRawScanCode(c.Key1, out ushort a) || !KeycodeTable.TryParseRawScanCode(c.Key2, out ushort b)
                || !bySc.TryGetValue(a, out var s1) || !bySc.TryGetValue(b, out var s2))
            {
                r.Warn($"コンボ{c.Id}: メンバーのキー（{c.Key1}, {c.Key2}）が今のレイアウトに無いため、読み込みません");
                continue;
            }
            if (mode == ImportMode.Append)
            {
                if (target.Combos.Any(x => (x.Slot1 == s1.Index && x.Slot2 == s2.Index) || (x.Slot1 == s2.Index && x.Slot2 == s1.Index)))
                    continue; // 同じ組み合わせは、すでにある
                if (target.Combos.Any(x => x.Slot1 == s1.Index || x.Slot2 == s1.Index || x.Slot1 == s2.Index || x.Slot2 == s2.Index))
                {
                    r.Warn($"コンボ{c.Id}: メンバーのキーが、今のコンボですでに使われているため読み込みません（1つのキーは1つのコンボにだけ入れられます）");
                    continue;
                }
            }
            int id = c.Id;
            if (id < 0 || id >= KbdLayerClient.MaxComboCount || target.Combos.Any(x => x.Id == id))
                id = FreeId(target.Combos.Select(x => x.Id), KbdLayerClient.MaxComboCount);
            if (id < 0) { r.Warn($"コンボの空きが無いため、コンボ{c.Id}は読み込めませんでした（上限{KbdLayerClient.MaxComboCount}）"); continue; }
            target.Combos.Add(new ComboDef { Id = id, Slot1 = s1.Index, Slot2 = s2.Index, Output = fix(c.Output) });
            added++;
        }
        r.Info($"コンボを{added}個読み込みました" + (mode == ImportMode.Replace ? "（置き換え）" : ""));
    }

    private static void ImportKeyOverrides(List<KeyOverrideDef> src, KeymapProject target, ImportMode mode, PortableReport r)
    {
        if (mode == ImportMode.Replace) target.KeyOverrides = new List<KeyOverrideDef>();
        int added = 0;
        foreach (var k in src)
        {
            if (mode == ImportMode.Append &&
                target.KeyOverrides.Any(x => x.Mods == k.Mods && x.Key == k.Key && x.Replacement == k.Replacement)) continue;
            int id = k.Id;
            if (id < 0 || id >= KbdLayerClient.MaxKeyOverrideCount || target.KeyOverrides.Any(x => x.Id == id))
                id = FreeId(target.KeyOverrides.Select(x => x.Id), KbdLayerClient.MaxKeyOverrideCount);
            if (id < 0) { r.Warn($"キー置換の空きが無いため、キー置換{k.Id}は読み込めませんでした（上限{KbdLayerClient.MaxKeyOverrideCount}）"); continue; }
            target.KeyOverrides.Add(new KeyOverrideDef { Id = id, Mods = k.Mods, Key = k.Key, Replacement = k.Replacement });
            added++;
        }
        r.Info($"キー置換を{added}個読み込みました" + (mode == ImportMode.Replace ? "（置き換え）" : ""));
    }

    internal static string Json(object o) => JsonSerializer.Serialize(o, CompareOptions);
}

/// <summary>ヘルパー設定（helper.json）の取り込み。</summary>
public static class HelperMerge
{
    /// <summary>
    /// 置き換え: 取り込む設定そのものにする。追加: 今の設定に足す。動作（Actions）は、同じ内容のものがあればそれを使い、
    /// 番号が重なれば空き番号に付け直す（キーボードごとの設定と共通の設定の、両方の番号を避ける）。
    /// トリガーと規則は、同じものがなければ足す。<paramref name="idMap"/> は、取り込む側の番号 → 結果の番号。
    /// </summary>
    public static HelperConfig Merge(HelperConfig? existing, HelperConfig? other, HelperConfig incoming, ImportMode mode,
                                     out Dictionary<int, int> idMap, PortableReport r)
    {
        idMap = new Dictionary<int, int>();
        if (mode == ImportMode.Replace)
        {
            var copy = PortableExporter.CloneHelper(incoming);
            foreach (var a in copy.Actions) idMap[a.Id] = a.Id;
            r.Info($"ヘルパー設定を読み込みました（動作{copy.Actions.Count}、トリガー{copy.Triggers.Count}、アプリ別レイヤーの規則{copy.AppLayerRules.Count}。置き換え）");
            return copy;
        }

        var result = PortableExporter.CloneHelper(existing ?? new HelperConfig());
        var used = new HashSet<int>(result.Actions.Select(a => a.Id));
        if (other != null) foreach (var a in other.Actions) used.Add(a.Id);
        var allExisting = result.Actions.Concat(other?.Actions ?? new()).ToList();

        int addedActions = 0;
        foreach (var a in incoming.Actions)
        {
            string body = PortableImporter.Json(a.Action);
            var same = allExisting.FirstOrDefault(x => PortableImporter.Json(x.Action) == body);
            if (same != null) { idMap[a.Id] = same.Id; continue; }
            int id = a.Id;
            if (id < 1 || id > HelperSettingsModel.MaxActionId || used.Contains(id))
            {
                id = 1;
                while (id <= HelperSettingsModel.MaxActionId && used.Contains(id)) id++;
                if (id > HelperSettingsModel.MaxActionId) { r.Warn($"ヘルパー動作の空き番号が無いため、動作{a.Id}は読み込めませんでした"); continue; }
                r.Info($"ヘルパー動作{a.Id} → 動作{id}（番号を付け直しました）");
            }
            used.Add(id);
            var copy = PortableExporter.CloneHelper(new HelperConfig { Actions = { a } }).Actions[0];
            copy.Id = id;
            result.Actions.Add(copy);
            allExisting.Add(copy);
            idMap[a.Id] = id;
            addedActions++;
        }

        int addedTriggers = 0, addedRules = 0;
        foreach (var t in incoming.Triggers)
        {
            string j = PortableImporter.Json(t);
            if (result.Triggers.Any(x => PortableImporter.Json(x) == j)) continue;
            result.Triggers.Add(PortableExporter.CloneHelper(new HelperConfig { Triggers = { t } }).Triggers[0]);
            addedTriggers++;
        }
        foreach (var rule in incoming.AppLayerRules)
        {
            string j = PortableImporter.Json(rule);
            if (result.AppLayerRules.Any(x => PortableImporter.Json(x) == j)) continue;
            result.AppLayerRules.Add(PortableExporter.CloneHelper(new HelperConfig { AppLayerRules = { rule } }).AppLayerRules[0]);
            addedRules++;
        }
        r.Info($"ヘルパー設定を足しました（動作{addedActions}、トリガー{addedTriggers}、アプリ別レイヤーの規則{addedRules}）");
        return result;
    }
}
