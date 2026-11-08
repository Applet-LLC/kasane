using System.IO;
using System.Text;
using System.Text.Json;
using Kasane.Interop;
using Kasane.Keycodes;
using Kasane.Layout;

namespace Kasane.Model;

/// <summary>プロジェクトの作成・保存・検証・ドライバへの適用・ドライバからの読み込み・KeymapTool（--apply-simple）用 JSON へのエクスポート。</summary>
public static partial class ProjectService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <param name="baseDirectory">%LOCALAPPDATA%\applet\kasane の代わりの場所（単体テスト用。null なら既定）。</param>
    public static string ProjectPath(uint uniqId, string? baseDirectory = null) =>
        Path.Combine(baseDirectory ?? HelperConfig.BaseDirectory, uniqId.ToString("X8"), "project.json");

    /// <summary>名前を kasane に変える前（2026-10まで）の保存先。%LOCALAPPDATA%\LayerDriver\&lt;UniqId&gt;\project.json。</summary>
    public static string LegacyProjectPath(uint uniqId) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LayerDriver", uniqId.ToString("X8"), "project.json");

    public static KeymapProject? TryLoad(uint uniqId, string? baseDirectory = null)
    {
        var path = ProjectPath(uniqId, baseDirectory);
        if (baseDirectory == null) MigrateLegacyProject(uniqId, path);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<KeymapProject>(File.ReadAllText(path), JsonOptions);
    }

    /// <summary>
    /// 新しい保存先にまだ無く、旧保存先（LayerDriver）にあれば、新しい保存先へコピーする。旧ファイルは消さない
    /// （古い版のGUIに戻したときにも読めるように）。コピーに失敗したら、新規として扱われるだけ。
    /// </summary>
    private static void MigrateLegacyProject(uint uniqId, string path)
    {
        var legacy = LegacyProjectPath(uniqId);
        if (File.Exists(path) || !File.Exists(legacy)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(legacy, path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Save(KeymapProject project, string? baseDirectory = null)
    {
        var path = ProjectPath(project.UniqId, baseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(project, JsonOptions), new UTF8Encoding(false));
    }

    /// <summary>複製を作る（読み込みの試行など、元を変えたくないとき）。</summary>
    public static KeymapProject Clone(KeymapProject project) =>
        JsonSerializer.Deserialize<KeymapProject>(JsonSerializer.Serialize(project, JsonOptions), JsonOptions)!;

    /// <summary>
    /// スキャンコード → キー（スロット）の対応。実測値があればそれを使い、無ければレイアウトの既定のスキャンコード。
    /// 同じスキャンコードのキーが複数あれば、先のものを使う。レイアウトの違うキーボードの間で、設定を持ち運ぶのに使う。
    /// </summary>
    public static Dictionary<ushort, KeySlot> SlotsByScanCode(KeymapProject? project, KeyboardLayout layout)
    {
        var bySc = new Dictionary<ushort, KeySlot>();
        foreach (var s in layout.Slots)
        {
            ushort? sc = project != null ? EffectiveScanCode(project, s) : s.DefaultScanCode;
            if (sc is ushort v && !bySc.ContainsKey(v)) bySc[v] = s;
        }
        return bySc;
    }

    /// <summary>同梱レイアウト名 or 保存されたフルパスからレイアウトファイルを探す。</summary>
    public static string? FindLayoutFile(KeymapProject project)
    {
        if (project.LayoutPath != null && File.Exists(project.LayoutPath)) return project.LayoutPath;
        return KeyboardLayout.EnumerateLayoutFiles()
            .FirstOrDefault(f => KeyboardLayout.NameFromPath(f).Equals(project.LayoutName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>レイヤー0=各キー本来の動作（既定キーコード）の、1レイヤーだけの新規プロジェクト。</summary>
    public static KeymapProject CreateDefault(uint uniqId, string? hardwareId, KeyboardLayout layout, bool bundled)
    {
        var p = new KeymapProject
        {
            UniqId = uniqId,
            HardwareId = hardwareId,
            LayoutName = layout.Name,
            LayoutPath = bundled ? null : layout.FilePath,
        };
        p.Layers.Add(DefaultLayer(layout, 0));
        return p;
    }

    public static List<string> DefaultLayer(KeyboardLayout layout, int layerIndex) =>
        layout.Slots.Select(s => layerIndex == 0 ? s.DefaultKeycode ?? "KC_TRNS" : "KC_TRNS").ToList();

    /// <summary>レイアウトのキー数に合わせて各レイヤーの長さを揃える（レイアウトを変えた場合など）。</summary>
    public static void NormalizeShape(KeymapProject project, KeyboardLayout layout)
    {
        if (project.Layers.Count == 0) project.Layers.Add(DefaultLayer(layout, 0));
        for (int L = 0; L < project.Layers.Count; L++)
        {
            var layer = project.Layers[L];
            var fill = DefaultLayer(layout, L);
            while (layer.Count < layout.Slots.Count) layer.Add(fill[layer.Count]);
            if (layer.Count > layout.Slots.Count) layer.RemoveRange(layout.Slots.Count, layer.Count - layout.Slots.Count);
        }
        foreach (var k in project.MeasuredScanCodes.Keys.Where(k => k >= layout.Slots.Count).ToList())
            project.MeasuredScanCodes.Remove(k);
        NormalizeLayerNames(project);
        project.MacroNames = CleanNames(project.MacroNames, KbdLayerClient.MaxMacroCount);
        project.ComboNames = CleanNames(project.ComboNames, KbdLayerClient.MaxComboCount);
        project.TapDanceNames = CleanNames(project.TapDanceNames, KbdLayerClient.MaxTapDanceCount);
        project.KeyOverrideNames = CleanNames(project.KeyOverrideNames, KbdLayerClient.MaxKeyOverrideCount);
    }

    /// <summary>レイヤーの名前の並びを、レイヤーの枚数に合わせる（足りなければ null を足し、余りは捨てる）。名前が1つも無ければ null にする。</summary>
    public static void NormalizeLayerNames(KeymapProject project)
    {
        if (project.LayerNames == null) return;
        while (project.LayerNames.Count < project.Layers.Count) project.LayerNames.Add(null);
        if (project.LayerNames.Count > project.Layers.Count)
            project.LayerNames.RemoveRange(project.Layers.Count, project.LayerNames.Count - project.Layers.Count);
        for (int i = 0; i < project.LayerNames.Count; i++)
            if (string.IsNullOrWhiteSpace(project.LayerNames[i])) project.LayerNames[i] = null;
        if (project.LayerNames.All(n => n == null)) project.LayerNames = null;
    }

    /// <summary>名前の表を整える: 空白だけの名前・範囲外の番号を捨て、空なら null にする。</summary>
    public static Dictionary<int, string>? CleanNames(IEnumerable<KeyValuePair<int, string>>? names, int max)
    {
        if (names == null) return null;
        var clean = new Dictionary<int, string>();
        foreach (var (id, name) in names)
            if (id >= 0 && id < max && !string.IsNullOrWhiteSpace(name)) clean[id] = name.Trim();
        return clean.Count > 0 ? clean : null;
    }

    /// <summary>そのキーボードで NICOLA の IME 連動を使うか（キーボードごとの設定が先。無ければ tray.json の ImeLink）。</summary>
    public static bool NicolaImeLinkFor(uint uniqId)
    {
        bool global = TrayConfig.Load().ImeLink;
        if (uniqId == 0) return global;
        var device = HelperConfig.Load(HelperConfig.DevicePath(uniqId));
        return device?.NicolaImeLink ?? global;
    }

    /// <summary>レイヤーの名前（無ければ null）。</summary>
    public static string? LayerName(KeymapProject project, int layer) =>
        project.LayerNames != null && layer >= 0 && layer < project.LayerNames.Count && !string.IsNullOrWhiteSpace(project.LayerNames[layer])
            ? project.LayerNames[layer] : null;

    /// <summary>レイヤーの名前を付ける（空・null なら消す）。レイヤーの枚数を超える番号は無視する。</summary>
    public static void SetLayerName(KeymapProject project, int layer, string? name)
    {
        if (layer < 0 || layer >= project.Layers.Count) return;
        string? clean = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (clean == null && project.LayerNames == null) return;
        project.LayerNames ??= new List<string?>();
        while (project.LayerNames.Count < project.Layers.Count) project.LayerNames.Add(null);
        project.LayerNames[layer] = clean;
        NormalizeLayerNames(project);
    }

    /// <summary>
    /// キーに割り当てたもの（MO(1)・LT(2,…)・MACRO(3)・TAPDANCE(0) など）が指す、名前つきのレイヤー・マクロ・タップダンスの表示（ツールチップ用）。
    /// 名前を付けていない・名前のあるものを指していないときは null。
    /// </summary>
    public static string? TargetNameNote(KeymapProject project, KeyAction action)
    {
        if (ActionType.ReferencesLayer(action.Type))
            return LayerName(project, action.Param1) is { } ln ? Loc.T($"レイヤー{action.Param1}「{ln}」", $"Layer {action.Param1} \"{ln}\"") : null;
        string? name = action.Type switch
        {
            ActionType.Macro => project.MacroNames is { } m && m.TryGetValue(action.Param1, out var mn) ? mn : null,
            ActionType.TapDance => project.TapDanceNames is { } t && t.TryGetValue(action.Param1, out var tn) ? tn : null,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(name)) return null;
        return Loc.T((action.Type == ActionType.Macro ? "マクロ" : "タップダンス") + $"{action.Param1}「{name}」",
                     (action.Type == ActionType.Macro ? "Macro" : "Tap dance") + $" {action.Param1} \"{name}\"");
    }

    /// <summary>画面に出す、レイヤーの呼び名。名前があれば「レイヤー3「仕事」」、無ければ「レイヤー3」。</summary>
    public static string LayerLabel(KeymapProject project, int layer) =>
        LayerName(project, layer) is { } n
            ? Loc.T($"レイヤー{layer}「{n}」", $"Layer {layer} \"{n}\"")
            : Loc.T($"レイヤー{layer}", $"Layer {layer}");

    public static ushort? EffectiveScanCode(KeymapProject project, KeySlot slot) =>
        project.MeasuredScanCodes.TryGetValue(slot.Index, out var sc) ? sc : slot.DefaultScanCode;

    public static bool IsMeasuredDifferent(KeymapProject project, KeySlot slot) =>
        project.MeasuredScanCodes.TryGetValue(slot.Index, out var sc) && sc != slot.DefaultScanCode;

    /// <summary>実測結果を記録する。既定値と同じなら記録しない（既定値に任せる）。</summary>
    public static void SetMeasured(KeymapProject project, KeySlot slot, ushort scanCode)
    {
        if (scanCode == slot.DefaultScanCode) project.MeasuredScanCodes.Remove(slot.Index);
        else project.MeasuredScanCodes[slot.Index] = scanCode;
    }

    public sealed record SlotMapping(KeySlot Slot, ushort ScanCode);

    /// <summary>
    /// ドライバへ送るキー（=レイヤーマップのエントリ）を決める。
    /// - 全レイヤーKC_TRNSのキーは含めない（含めると「全レイヤー透過=入力無し」になってしまうため。
    ///   含めなければドライバはそのキーを素通しする）。
    /// - スキャンコードが決まらない（既定キーコードが無く未実測）キーは含められないので警告にする。
    /// - 同じスキャンコードが2つのキーに割り当たる場合はエラー。
    /// </summary>
    public static List<SlotMapping> ResolveMappedSlots(KeymapProject project, KeyboardLayout layout,
        List<string> warnings, List<string> errors)
    {
        var result = new List<SlotMapping>();
        var seen = new Dictionary<ushort, KeySlot>();
        foreach (var slot in layout.Slots)
        {
            bool allTransparent = project.Layers.All(l => l[slot.Index] == "KC_TRNS");
            if (allTransparent) continue;
            var sc = EffectiveScanCode(project, slot);
            if (sc == null)
            {
                warnings.Add(Loc.T($"{slot.Description}: スキャンコードが不明なため適用されません(キャリブレーションで実測してください)", $"{slot.Description}: Not applied because its scan code is unknown (measure it with Calibration)"));
                continue;
            }
            if (seen.TryGetValue(sc.Value, out var other))
            {
                errors.Add(Loc.T($"{slot.Description} と {other.Description} が同じスキャンコード 0x{sc:X4} です(キャリブレーションをやり直してください)", $"{slot.Description} and {other.Description} have the same scan code 0x{sc:X4} (redo Calibration)"));
                continue;
            }
            seen[sc.Value] = slot;
            result.Add(new SlotMapping(slot, sc.Value));
        }
        return result;
    }

    /// <summary>
    /// ドライバへ送るレイヤーマップのキーとレイヤー数。<see cref="ResolveMappedSlots"/> の結果に、NICOLAを使うときは、
    /// NICOLAのレイヤーへ親指キーの割り当てを足す（<see cref="NicolaService.ThumbLayerEntries"/> の理由）。
    ///  - キーマップにそのキーがあれば、NICOLAのレイヤーが透過（KC_TRNS）のときだけ入れる（利用者が割り当てていれば触らない）。
    ///  - キーマップに無ければ、そのキーだけのエントリを足す。
    /// NICOLAのレイヤーが、キーマップのレイヤー数以上でもよい（IME連動ではトレイがそのレイヤーを立てる）ので、レイヤー数を
    /// その分まで広げる（足りない分は透過）。
    /// </summary>
    public static (List<BlobBuilder.KeyInput> Inputs, int LayerCount) BuildMapInputs(KeymapProject project, IReadOnlyList<SlotMapping> mapped)
    {
        var nicola = project.Nicola is { Enabled: true } n ? n : null;
        var thumbs = nicola != null ? NicolaService.ThumbLayerEntries(nicola) : new List<(ushort ScanCode, string Token)>();
        int layerCount = project.Layers.Count;
        if (nicola != null && thumbs.Count > 0 && nicola.Layer >= 0 && nicola.Layer < KbdLayerClient.MaxLayers)
            layerCount = Math.Max(layerCount, nicola.Layer + 1);

        List<string> Tokens(IEnumerable<string> own) => own.Concat(Enumerable.Repeat("KC_TRNS", Math.Max(0, layerCount - project.Layers.Count))).ToList();

        var inputs = new List<BlobBuilder.KeyInput>();
        var present = new HashSet<ushort>();
        foreach (var m in mapped)
        {
            var tokens = Tokens(project.Layers.Select(l => l[m.Slot.Index]));
            if (nicola != null && thumbs.FirstOrDefault(t => t.ScanCode == m.ScanCode) is { ScanCode: not 0 } thumb
                && nicola.Layer < tokens.Count && tokens[nicola.Layer] == "KC_TRNS")
                tokens[nicola.Layer] = thumb.Token;
            inputs.Add(new BlobBuilder.KeyInput(m.ScanCode, tokens));
            present.Add(m.ScanCode);
        }
        foreach (var (scanCode, token) in thumbs)
        {
            if (present.Contains(scanCode) || nicola == null || nicola.Layer >= layerCount) continue;
            var tokens = Enumerable.Repeat("KC_TRNS", layerCount).ToList();
            tokens[nicola.Layer] = token;
            inputs.Add(new BlobBuilder.KeyInput(scanCode, tokens));
        }
        return (inputs, layerCount);
    }

    /// <summary>存在しないレイヤーへの切り替え（エラー）と、中身の無いマクロ/タップダンスの参照（警告）を調べる。</summary>
    private static void CheckReferences(KeymapProject project, KeyboardLayout layout, List<string> warnings, List<string> errors)
    {
        var macroIds = project.Macros.Where(m => m.Keys.Count > 0 && !m.Disabled).Select(m => m.Id).ToHashSet();
        var tapDanceIds = project.TapDances.Where(t => !t.Disabled).Select(t => t.Id).ToHashSet();
        // 無効にした定義（あるが、適用しない）。参照している側には、「未設定」と別の警告を出す。
        var disabledMacroIds = project.Macros.Where(m => m.Keys.Count > 0 && m.Disabled).Select(m => m.Id).ToHashSet();
        var disabledTapDanceIds = project.TapDances.Where(t => t.Disabled).Select(t => t.Id).ToHashSet();
        string NothingSet(string token, int id, HashSet<int> disabled, string prefix, string jaTail, string enTail) =>
            disabled.Contains(id)
                ? Loc.T($"{prefix}{token} は無効にしてあります({jaTail})", $"{prefix}{token} is disabled ({enTail})")
                : Loc.T($"{prefix}{token} の中身が未設定です({jaTail})", $"{prefix}{token} has nothing set ({enTail})");
        for (int L = 0; L < project.Layers.Count; L++)
        {
            foreach (var slot in layout.Slots)
            {
                string token = project.Layers[L][slot.Index];
                if (!KeycodeTable.TryResolve(token, out var a, out var err))
                {
                    errors.Add(Loc.T($"レイヤー{L} {slot.Description}: {err}", $"Layer {L} {slot.Description}: {err}"));
                    continue;
                }
                bool layerRef = ActionType.ReferencesLayer(a.Type);
                if (layerRef && a.Param1 >= project.Layers.Count)
                    errors.Add(Loc.T($"レイヤー{L} {slot.Description}: {token} のレイヤー{a.Param1}がありません(レイヤーを追加してください)", $"Layer {L} {slot.Description}: {token} refers to layer {a.Param1}, which does not exist (add the layer)"));
                else if (a.Type == ActionType.Macro && !macroIds.Contains(a.Param1))
                    warnings.Add(NothingSet(token, a.Param1, disabledMacroIds, "", "押しても何も起きません", "pressing it does nothing"));
                else if (a.Type == ActionType.TapDance && !tapDanceIds.Contains(a.Param1))
                    warnings.Add(NothingSet(token, a.Param1, disabledTapDanceIds, "", "押しても何も起きません", "pressing it does nothing"));
            }
        }
        // タップダンス（タップ側・長押し）とコンボの出力から参照しているマクロ。
        foreach (var td in project.TapDances.Where(t => !t.Disabled))
        {
            foreach (var token in new[] { td.SingleTap, td.SingleHold, td.DoubleTap, td.TripleTap })
            {
                if (KeycodeTable.TryResolve(token, out var a) && a.Type == ActionType.Macro && !macroIds.Contains(a.Param1))
                    warnings.Add(NothingSet(token, a.Param1, disabledMacroIds, Loc.T($"タップダンス{td.Id}: ", $"Tap dance {td.Id}: "), "そこでは何も起きません", "nothing happens there"));
            }
        }
        foreach (var combo in project.Combos.Where(c => !c.Disabled))
        {
            if (KeycodeTable.TryResolve(combo.Output, out var a) && a.Type == ActionType.Macro && !macroIds.Contains(a.Param1))
                warnings.Add(NothingSet(combo.Output, a.Param1, disabledMacroIds, Loc.T($"コンボ{combo.Id}: ", $"Combo {combo.Id}: "), "成立しても何も起きません", "nothing happens when the combo fires"));
        }
        // キー置換の置き換え先が、無効にしたマクロのとき（中身が未設定のマクロは、今までどおり警告しない）。
        foreach (var ko in project.KeyOverrides.Where(k => !k.Disabled))
        {
            if (!KeycodeTable.TryResolve(ko.Replacement, out var a)) continue;
            if (a.Type == ActionType.Macro && disabledMacroIds.Contains(a.Param1))
                warnings.Add(NothingSet(ko.Replacement, a.Param1, disabledMacroIds, Loc.T($"キー置換{ko.Id}: ", $"Key override {ko.Id}: "), "そこでは何も起きません", "nothing happens there"));
            // 置き換え先のレイヤー操作（OSL・TG・TO・DF）が、存在しないレイヤーを指しているとき。
            if (ActionType.ReferencesLayer(a.Type) && a.Param1 >= project.Layers.Count)
                errors.Add(Loc.T($"キー置換{ko.Id}: {ko.Replacement} のレイヤー{a.Param1}がありません(レイヤーを追加してください)", $"Key override {ko.Id}: {ko.Replacement} refers to layer {a.Param1}, which does not exist (add the layer)"));
        }
    }

    public static List<MacroStep> MacroSteps(MacroDef macro, List<string> errors)
    {
        var steps = new List<MacroStep>();
        foreach (var token in macro.Keys)
        {
            if (!KeycodeTable.TryResolve(token, out var a) || (a.Type != ActionType.Key && a.Type != ActionType.ModdedKey))
            {
                errors.Add(Loc.T($"マクロ{macro.Id}: 「{token}」はキー(KC_*)または修飾付きキー(LCTL(KC_C)等)ではありません", $"Macro {macro.Id}: \"{token}\" is not a key (KC_*) or a key with modifiers (such as LCTL(KC_C))"));
                continue;
            }
            // LCTL(KC_C) は「Ctrl押す, C押す, C離す, Ctrl離す」に展開する（修飾キーは逆順で離す）。
            byte mods = a.Type == ActionType.ModdedKey ? a.Param1 : (byte)0;
            for (int bit = 0; bit < 8; bit++)
                if ((mods & (1 << bit)) != 0) steps.Add(new MacroStep(KeycodeTable.ModifierScanCodes[bit], false));
            steps.Add(new MacroStep(a.Param2, false));
            steps.Add(new MacroStep(a.Param2, true));
            for (int bit = 7; bit >= 0; bit--)
                if ((mods & (1 << bit)) != 0) steps.Add(new MacroStep(KeycodeTable.ModifierScanCodes[bit], true));
        }
        if (steps.Count > KbdLayerClient.MaxMacroSteps)
            errors.Add(Loc.T(
                ($"マクロ{macro.Id}: 長すぎます({steps.Count}ステップ。上限は{KbdLayerClient.MaxMacroSteps}で、" +
                 "キー1つが2、修飾キー1つにつき2を使います)"),
                ($"Macro {macro.Id}: too long ({steps.Count} steps; the limit is {KbdLayerClient.MaxMacroSteps}. " +
                 "Each key uses 2, and each modifier key uses 2)")));
        return steps;
    }

    /// <summary>
    /// ドライバから読んだマクロのステップ列を、キーコードの並びに戻す（MacroStepsの逆）。
    /// 「修飾キーを押す → キーを押して離す → 修飾キーを離す」の並びは LCTL(KC_C) の形にする。
    /// </summary>
    public static List<string> MacroTokensFromSteps(IReadOnlyList<MacroStep> steps)
    {
        string Name(ushort sc) => KeycodeTable.KeyToken(sc);
        int ModBit(ushort sc) => Array.IndexOf(KeycodeTable.ModifierScanCodes, sc);

        var tokens = new List<string>();
        int i = 0;
        while (i < steps.Count)
        {
            // 修飾キーの押下（直後が自分自身の解放でないもの）を集める。
            byte mask = 0;
            while (i + 1 < steps.Count && !steps[i].IsBreak && ModBit(steps[i].ScanCode) >= 0 &&
                   !(steps[i + 1].IsBreak && steps[i + 1].ScanCode == steps[i].ScanCode))
            {
                mask |= (byte)(1 << ModBit(steps[i].ScanCode));
                i++;
            }
            if (i >= steps.Count) break;
            if (steps[i].IsBreak)
            {
                i++; // 対応する押下の無い解放は読み飛ばす
                continue;
            }
            ushort key = steps[i].ScanCode;
            i++;
            if (i < steps.Count && steps[i].IsBreak && steps[i].ScanCode == key) i++;
            while (i < steps.Count && steps[i].IsBreak && ModBit(steps[i].ScanCode) >= 0 &&
                   (mask & (1 << ModBit(steps[i].ScanCode))) != 0)
            {
                i++;
            }
            tokens.Add(mask == 0 ? Name(key) : KeycodeTable.WrapWithMods(mask, Name(key)));
        }
        return tokens;
    }

    public static ComboEntry? ComboToEntry(KeymapProject project, KeyboardLayout layout, ComboDef combo, List<string> errors)
    {
        if (combo.Slot1 < 0 || combo.Slot1 >= layout.Slots.Count || combo.Slot2 < 0 || combo.Slot2 >= layout.Slots.Count)
        {
            errors.Add(Loc.T($"コンボ{combo.Id}: メンバーのキーがレイアウトにありません", $"Combo {combo.Id}: a member key is not in the layout"));
            return null;
        }
        var s1 = EffectiveScanCode(project, layout.Slots[combo.Slot1]);
        var s2 = EffectiveScanCode(project, layout.Slots[combo.Slot2]);
        if (s1 == null || s2 == null) { errors.Add(Loc.T($"コンボ{combo.Id}: メンバーのスキャンコードが不明です", $"Combo {combo.Id}: a member's scan code is unknown")); return null; }
        if (s1 == s2) { errors.Add(Loc.T($"コンボ{combo.Id}: メンバーの2キーは別のキーにしてください", $"Combo {combo.Id}: the 2 member keys must be different keys")); return null; }
        if (!KeycodeTable.TryResolve(combo.Output, out var output) ||
            output.Type is not (ActionType.Key or ActionType.ModdedKey or ActionType.Macro))
        {
            errors.Add(Loc.T($"コンボ{combo.Id}: 出力「{combo.Output}」はキー(KC_*)、修飾付きキー(LCTL(KC_Z)等)、MACRO(n)のいずれかにしてください", $"Combo {combo.Id}: the output \"{combo.Output}\" must be a key (KC_*), a key with modifiers (such as LCTL(KC_Z)), or MACRO(n)"));
            return null;
        }
        return new ComboEntry(s1.Value, s2.Value, output);
    }

    public static TapDanceEntry? TapDanceToEntry(TapDanceDef td, List<string> errors)
    {
        // 空欄と KC_NO は「動作なし」（そのタップ回数・長押しでは何も出さない）。
        // 4つとも空なら未使用（TapDanceDef自体を作らない。TapDanceEditorWindow参照）。
        static bool IsNoAction(string token) => token.Trim().Length == 0 || token.Trim() is "KC_NO" or "XXXXXXX";

        // タップ側は、キー(KC_*)・修飾付きキー(LCTL(KC_C)等)・MACRO(n)を置ける（ドライバが確定時に
        // 押して離す。修飾付きキーは修飾キーごと、マクロは中身を順に送る）。動作なしは、KEY・
        // スキャンコード0として送る（1回タップのNONEはドライバ側で「クリア」の意味になるため）。
        KeyAction Tap(string token, string what)
        {
            if (IsNoAction(token)) return new KeyAction(ActionType.Key, 0, 0);
            if (!KeycodeTable.TryResolve(token, out var a) ||
                a.Type is not (ActionType.Key or ActionType.ModdedKey or ActionType.Macro))
            {
                errors.Add(Loc.T($"タップダンス{td.Id}: {what}「{token}」はキー(KC_*)、修飾付きキー(LCTL(KC_C)等)、MACRO(n)、空欄(動作なし)のいずれかにしてください", $"Tap dance {td.Id}: {what} \"{token}\" must be a key (KC_*), a key with modifiers (such as LCTL(KC_C)), MACRO(n), or empty (no action)"));
                return KeyAction.None;
            }
            return a;
        }
        int before = errors.Count;
        var single = Tap(td.SingleTap, Loc.T("1回タップ", "Single tap"));
        var dbl = Tap(td.DoubleTap, Loc.T("2回タップ", "Double tap"));
        var triple = Tap(td.TripleTap, Loc.T("3回タップ", "Triple tap"));
        KeyAction hold = KeyAction.None; // 長押しの動作なし
        if (!IsNoAction(td.SingleHold) &&
            (!KeycodeTable.TryResolve(td.SingleHold, out hold) ||
             hold.Type is not (ActionType.Key or ActionType.ModTap or ActionType.LayerTap
                 or ActionType.ModdedKey or ActionType.LayerMod or ActionType.Macro)))
        {
            errors.Add(Loc.T(
                ($"タップダンス{td.Id}: ホールド「{td.SingleHold}」はKC_*、MT(mod,kc)、LT(layer,kc)、修飾付きキー(LCTL(KC_Z)等)、" +
                 "LM(layer,mod)、MACRO(n)、空欄(動作なし)のいずれかにしてください"),
                ($"Tap dance {td.Id}: the hold \"{td.SingleHold}\" must be KC_*, MT(mod,kc), LT(layer,kc), a key with modifiers (such as LCTL(KC_Z)), " +
                 "LM(layer,mod), MACRO(n), or empty (no action)")));
        }
        return errors.Count == before ? new TapDanceEntry(single, hold, dbl, triple) : null;
    }

    /// <summary>
    /// プロジェクトを検証して、ドライバへ送る（またはレジストリへ書く）内容に組み立てる。
    /// 検証エラーがあれば例外を投げる。警告は <see cref="DriverSettings.Warnings"/>。
    /// </summary>
    public static DriverSettings Compile(KeymapProject project, KeyboardLayout layout)
    {
        var warnings = new List<string>();
        var errors = new List<string>();

        var mapped = ResolveMappedSlots(project, layout, warnings, errors);
        byte[]? blob = null;
        var (mapInputs, mapLayerCount) = BuildMapInputs(project, mapped);
        if (mapInputs.Count > 0)
        {
            try { blob = BlobBuilder.Build(mapLayerCount, mapInputs); }
            catch (InvalidOperationException ex) { errors.Add(ex.Message); }
        }

        CheckReferences(project, layout, warnings, errors);

        // 無効にした定義は、検証も適用もしない（ドライバ側に残っていれば、Apply が消す）。
        var macroSteps = project.Macros.Where(m => !m.Disabled).ToDictionary(m => m.Id, m => MacroSteps(m, errors));
        var combos = project.Combos.Where(c => !c.Disabled).ToDictionary(c => c.Id, c => ComboToEntry(project, layout, c, errors));
        var tapDances = project.TapDances.Where(t => !t.Disabled).ToDictionary(t => t.Id, t => TapDanceToEntry(t, errors));
        var keyOverrides = project.KeyOverrides.Where(k => !k.Disabled).ToDictionary(k => k.Id, k => KeyOverrideToEntry(k, errors));

        byte[]? nicolaBlob = null;
        if (project.Nicola != null)
        {
            nicolaBlob = NicolaService.BuildBlob(project.Nicola, warnings, errors);
            // IME連動（トレイ）が、IMEが日本語入力モードのときにそのレイヤーを立てる。使わない場合は、そのレイヤーに切り替えるキーが要る。
            if (project.Nicola.Layer >= project.Layers.Count && !NicolaImeLinkFor(project.UniqId))
                warnings.Add(Loc.T($"NICOLAのレイヤー{project.Nicola.Layer}はキーマップにありません(IME 連動を使わない場合は、そのレイヤーに切り替えるキー(TG({project.Nicola.Layer})等)が無いと、親指シフトは働きません)", $"NICOLA layer {project.Nicola.Layer} is not in the keymap (if you don't use IME link, thumb shift won't work without a key that switches to that layer, such as TG({project.Nicola.Layer}))"));
        }
        if (project.EnabledFeatures is uint f && (f & ~KbdLayerClient.FeatureAll) != 0)
            errors.Add(Loc.T($"機能のON/OFFの値 0x{f:X} に未知のビットがあります", $"The feature ON/OFF value 0x{f:X} contains an unknown bit"));

        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));

        if (blob == null) warnings.Add(Loc.T("割り当てのあるキーが無いため、レイヤーマップは消去されOFFのままです", "No keys have assignments, so the layer map is cleared and stays OFF"));
        return new DriverSettings(blob, project.TapTermMs, project.ComboTermMs, project.TapDanceTermMs,
                                  project.TapHoldMode, project.QuickTapTermMs, project.TappingToggle,
                                  macroSteps, combos, tapDances, keyOverrides, project.EnabledFeatures,
                                  nicolaBlob, project.ClearNicola, warnings);
    }

    /// <summary>
    /// ドライバへ一括適用する: 検証 → OFF → レイヤーマップ → タイミング → マクロ/コンボ/タップダンス/キー置換
    /// → 機能のON/OFF → NICOLA → ON。
    /// 検証エラーがあれば何も送らずに例外を投げる。戻り値は警告の一覧。
    /// </summary>
    public static List<string> Apply(KbdLayerClient client, KeymapProject project, KeyboardLayout layout)
    {
        var settings = Compile(project, layout);
        var blob = settings.LayerMap;
        var macroSteps = settings.Macros;
        var combos = settings.Combos;
        var tapDances = settings.TapDances;
        var keyOverrides = settings.KeyOverrides;
        var nicolaBlob = settings.Nicola;

        uint id = project.UniqId;
        client.SetEnabled(id, false);
        if (blob != null) client.SetLayerMap(id, blob);
        else client.ClearLayerMap(id);
        client.SetConfig(id, settings.TapTermMs, settings.ComboTermMs, settings.TapDanceTermMs,
                         settings.TapHoldMode, settings.QuickTapTermMs, settings.TappingToggle);

        // SET_MACROは1回ごとにマクロ全体（128個分、約33KB）をレジストリへ書くので、全IDには送らない。
        // 定義のあるものと、ドライバ側で空でないもの（消す必要があるもの）だけを送る。
        for (int m = 0; m < KbdLayerClient.MaxMacroCount; m++)
        {
            if (macroSteps.TryGetValue(m, out var s)) client.SetMacro(id, m, s);
            else if (client.GetMacro(id, m).Count > 0) client.SetMacro(id, m, new List<MacroStep>());
        }

        // コンボは「同じキーが複数のコンボに属さない」ことをドライバが登録時に検証するため、
        // 先に全部消してから登録する（IDを入れ替えた場合に古いコンボと衝突しないように）。
        for (int c = 0; c < KbdLayerClient.MaxComboCount; c++) client.SetCombo(id, c, null);
        foreach (var (c, entry) in combos) if (entry != null) client.SetCombo(id, c, entry);

        for (int t = 0; t < KbdLayerClient.MaxTapDanceCount; t++)
            client.SetTapDance(id, t, tapDances.TryGetValue(t, out var e) ? e : null);

        // Key Override（2026-10-01）。SET_KEY_OVERRIDEは1回ごとに表全体をレジストリへ書くので、定義の
        // あるものと、ドライバ側で使われているもの（消す必要があるもの）だけを送る。
        // 古いドライバ（このIOCTLが無い）では、定義が無ければ何もしない。定義があればエラーになる。
        if (keyOverrides.Count > 0 || SupportsKeyOverride(client, id))
        {
            for (int k = 0; k < KbdLayerClient.MaxKeyOverrideCount; k++)
            {
                if (keyOverrides.TryGetValue(k, out var ko) && ko != null) client.SetKeyOverride(id, k, ko);
                else if (client.GetKeyOverride(id, k) != null) client.SetKeyOverride(id, k, null);
            }
        }

        // 機能別ON/OFF。キー置換より前のドライバはFeatureKeyOverrideのビットを知らないので、
        // 対応していなければそのビットを外して送る。
        if (settings.Features is uint features)
        {
            if (!SupportsKeyOverride(client, id)) features &= ~KbdLayerClient.FeatureKeyOverride;
            try { client.SetFeatures(id, features); }
            catch (KbdLayerException) when ((features | KbdLayerClient.FeatureKeyOverride) == KbdLayerClient.FeatureAll)
            {
                // 機能別ON/OFFより前のドライバ。全機能を有効にするだけなら、送れなくても結果は同じ。
            }
        }

        // NICOLA。null（GUIでは管理しない）ならドライバの設定はそのまま。
        if (nicolaBlob != null) client.SetNicola(id, nicolaBlob);
        else if (settings.ClearNicola && client.GetNicola(id).Length > 0) client.SetNicola(id, Array.Empty<byte>());

        if (blob != null) client.SetEnabled(id, true);
        return settings.Warnings;
    }

    /// <summary>
    /// ドライバに現在設定されている内容から、レイアウトの既定スキャンコードを手がかりにプロジェクトを組み立てる
    /// （プロジェクトファイルが無いキーボードを開いたとき用）。設定が何も無ければ null。
    /// </summary>
    public static KeymapProject? ImportFromDriver(KbdLayerClient client, DeviceInfo device, KeyboardLayout layout,
        bool bundled, List<string> warnings)
    {
        var (layerCount, entries) = KbdLayerClient.ParseLayerMapBlob(client.GetLayerMap(device.UniqId));
        if (entries.Count == 0) return null;

        var project = CreateDefault(device.UniqId, device.HardwareId, layout, bundled);
        for (int L = 1; L < layerCount; L++) project.Layers.Add(DefaultLayer(layout, L));

        var bySc = SlotsByScanCode(null, layout);

        foreach (var e in entries)
        {
            if (!bySc.TryGetValue(e.SourceScanCode, out var slot))
            {
                warnings.Add(Loc.T($"スキャンコード 0x{e.SourceScanCode:X4} のキーはレイアウト上に見つからないため読み込めませんでした", $"The key with scan code 0x{e.SourceScanCode:X4} was not found in the layout, so it could not be loaded"));
                continue;
            }
            for (int L = 0; L < layerCount; L++) project.Layers[L][slot.Index] = KeycodeTable.ToToken(e.Actions[L]);
        }

        var cfg = client.GetConfig(device.UniqId);
        project.TapTermMs = cfg.TapTermMs;
        project.ComboTermMs = cfg.ComboTermMs;
        project.TapDanceTermMs = cfg.TapDanceTermMs;
        project.TapHoldMode = cfg.TapHoldMode;
        project.QuickTapTermMs = cfg.QuickTapTermMs;
        // ドライバは実効値（既定なら5）を返す。既定値と同じなら0（既定）として持つ。
        project.TappingToggle = cfg.TappingToggle == KbdLayerClient.DefaultTappingToggle ? 0 : cfg.TappingToggle;

        for (int m = 0; m < KbdLayerClient.MaxMacroCount; m++)
        {
            var steps = client.GetMacro(device.UniqId, m);
            if (steps.Count == 0) continue;
            project.Macros.Add(new MacroDef
            {
                Id = m,
                Keys = MacroTokensFromSteps(steps),
            });
        }
        for (int c = 0; c < KbdLayerClient.MaxComboCount; c++)
        {
            var combo = client.GetCombo(device.UniqId, c);
            if (combo == null) continue;
            if (bySc.TryGetValue(combo.ScanCode1, out var s1) && bySc.TryGetValue(combo.ScanCode2, out var s2))
                project.Combos.Add(new ComboDef { Id = c, Slot1 = s1.Index, Slot2 = s2.Index, Output = KeycodeTable.ToToken(combo.Output) });
            else
                warnings.Add(Loc.T($"コンボ{c}のメンバーのキーがレイアウト上に見つからないため読み込めませんでした", $"A member key of combo {c} was not found in the layout, so it could not be loaded"));
        }
        for (int t = 0; t < KbdLayerClient.MaxTapDanceCount; t++)
        {
            var td = client.GetTapDance(device.UniqId, t);
            if (td == null) continue;
            project.TapDances.Add(new TapDanceDef
            {
                Id = t,
                SingleTap = KeycodeTable.ToToken(td.SingleTap),
                SingleHold = KeycodeTable.ToToken(td.SingleHold),
                DoubleTap = KeycodeTable.ToToken(td.DoubleTap),
                TripleTap = KeycodeTable.ToToken(td.TripleTap),
            });
        }
        if (SupportsKeyOverride(client, device.UniqId))
        {
            for (int k = 0; k < KbdLayerClient.MaxKeyOverrideCount; k++)
            {
                var ko = client.GetKeyOverride(device.UniqId, k);
                if (ko == null) continue;
                project.KeyOverrides.Add(new KeyOverrideDef
                {
                    Id = k,
                    Mods = KeycodeTable.ModMaskToNames(ko.TriggerMods),
                    Key = KeycodeTable.KeyToken(ko.TriggerScanCode),
                    Replacement = KeycodeTable.ToToken(ko.Replacement),
                    RepeatMacro = (ko.Flags & KeyOverrideEntry.FlagRepeat) != 0,
                });
            }
        }
        return project;
    }

    /// <summary>ドライバがKey Override（GET_KEY_OVERRIDE）に対応しているか（古いドライバはIOCTLが無い）。</summary>
    private static bool SupportsKeyOverride(KbdLayerClient client, uint uniqId)
    {
        try { client.GetKeyOverride(uniqId, 0); return true; }
        catch (KbdLayerException) { return false; }
    }

    /// <summary>
    /// キー置換1件をドライバへ送る形にする。条件の修飾キー（MOD_*を|でつなぐ）・条件のキー（KC_*）・
    /// 置き換え先（KC_*、LCTL(KC_Z) のような修飾付きキー、MACRO(n)、レイヤー操作 OSL・TG・TO・DF、ヘルパー動作 HLP）が
    /// そろっていなければエラー。レイヤー操作とヘルパー動作は、キーを出力せず、レイヤーのキーに割り当てたのと同じ動作をする。
    /// </summary>
    public static KeyOverrideEntry? KeyOverrideToEntry(KeyOverrideDef ko, List<string> errors)
    {
        int before = errors.Count;
        if (!KeycodeTable.TryParseModMask(ko.Mods.Trim(), out var mods))
            errors.Add(Loc.T($"キー置換{ko.Id}: 修飾キー「{ko.Mods}」は MOD_LSFT や MOD_LCTL|MOD_LSFT のように書いてください", $"Key override {ko.Id}: write the modifier \"{ko.Mods}\" like MOD_LSFT or MOD_LCTL|MOD_LSFT"));
        if (!KeycodeTable.TryResolve(ko.Key.Trim(), out var key) || key.Type != ActionType.Key || key.Param2 == 0)
            errors.Add(Loc.T($"キー置換{ko.Id}: キー「{ko.Key}」は単純なキー(KC_*)にしてください", $"Key override {ko.Id}: the key \"{ko.Key}\" must be a simple key (KC_*)"));
        if (!KeycodeTable.TryResolve(ko.Replacement.Trim(), out var replacement) || !IsValidOverrideReplacement(replacement))
            errors.Add(Loc.T($"キー置換{ko.Id}: 置き換え先「{ko.Replacement}」はキー(KC_*)、修飾付きキー(LCTL(KC_Z)等)、マクロ(MACRO(n))、レイヤー操作(OSL(n)・TG(n)・TO(n)・DF(n))、ヘルパー動作(HLP(n))、マークモード(MARK(on)・MARK(off)・MARK(toggle))のどれかにしてください", $"Key override {ko.Id}: the replacement \"{ko.Replacement}\" must be a key (KC_*), a key with modifiers (such as LCTL(KC_Z)), a macro (MACRO(n)), a layer operation (OSL(n), TG(n), TO(n), DF(n)) a helper action (HLP(n)) or mark mode (MARK(on), MARK(off), MARK(toggle))"));
        // オートリピートの印は、置き換え先がマクロのときだけ有効（ドライバも、マクロ以外では受け付けない）。
        byte flags = ko.RepeatMacro && replacement.Type == ActionType.Macro ? KeyOverrideEntry.FlagRepeat : (byte)0;
        return errors.Count == before ? new KeyOverrideEntry(mods, key.Param2, replacement, flags) : null;
    }

    /// <summary>キー置換の置き換え先に使える動作か（ドライバの IsValidKeyOverrideEntry と同じ範囲）。</summary>
    public static bool IsValidOverrideReplacement(KeyAction a) => a.Type switch
    {
        ActionType.Key or ActionType.ModdedKey => a.Param2 != 0,
        ActionType.Macro => true,
        // レイヤー操作: レイヤー番号は範囲内、Param2は0（ドライバと同じ）。
        ActionType.OneShotLayer or ActionType.Toggle or ActionType.LayerTo or ActionType.Default =>
            a.Param1 < KbdLayerClient.MaxLayers && a.Param2 == 0,
        // ヘルパー動作 HLP(id): Param1は予約で0、Param2がID。
        ActionType.Custom => a.Param1 == 0,
        // マークモード MARK(on|off|toggle): Param1はモード、Param2は0。
        ActionType.Mark => a.Param1 <= ActionType.MarkToggle && a.Param2 == 0,
        _ => false,
    };

    /// <summary>
    /// CLI（KeymapTool --apply-simple）でそのまま使える keymap.json と profile.json を書き出す。
    /// 並びはGUIが実際にドライバへ送るキー（ResolveMappedSlots）と同じ。
    /// </summary>
    public static List<string> Export(KeymapProject project, KeyboardLayout layout, string folder)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        var mapped = ResolveMappedSlots(project, layout, warnings, errors);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
        if (mapped.Count == 0) throw new InvalidOperationException(Loc.T("割り当てのあるキーがありません", "There are no keys with assignments"));

        string keymapPath = Path.Combine(folder, "keymap.json");
        string profilePath = Path.Combine(folder, "profile.json");

        using (var fs = File.Create(keymapPath))
        using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("notes", $"Exported by kasane. Layout {layout.Name}, {mapped.Count} keys " +
                "(keys that are transparent on every layer are omitted and pass through unchanged). " +
                "Apply with: KeymapTool.exe --apply-simple <UniqIdHex> keymap.json profile.json");
            w.WriteStartArray("layout");
            foreach (var layer in project.Layers)
            {
                w.WriteStartArray();
                foreach (var m in mapped) w.WriteStringValue(layer[m.Slot.Index]);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        // driver/KeymapTool/CalibrationProfile.cpp と同じ形式（--calibrate-simpleの出力: row=0, col=番号）。
        var sb = new StringBuilder("{\n  \"positions\": [\n");
        for (int i = 0; i < mapped.Count; i++)
        {
            sb.Append($"    {{\"row\": 0, \"col\": {i}, \"scanCode\": {mapped[i].ScanCode}}}");
            sb.Append(i + 1 < mapped.Count ? ",\n" : "\n");
        }
        sb.Append("  ]\n}\n");
        File.WriteAllText(profilePath, sb.ToString(), new UTF8Encoding(false));

        if (project.Macros.Count + project.Combos.Count + project.TapDances.Count + project.KeyOverrides.Count > 0)
            warnings.Add(Loc.T("マクロ・コンボ・タップダンス・キー置換はkeymap.jsonに含まれません(CLIでは --set-macro / --set-combo / --set-tapdance / --set-key-override で別途登録します)", "Macros, combos, tap dances, and key overrides are not included in keymap.json (with the CLI, register them separately using --set-macro / --set-combo / --set-tapdance / --set-key-override)"));
        return warnings;
    }
}
