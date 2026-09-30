using System.IO;
using System.Text;
using System.Text.Json;
using KbdLayerKeymapGui.Interop;
using KbdLayerKeymapGui.Keycodes;
using KbdLayerKeymapGui.Layout;

namespace KbdLayerKeymapGui.Model;

/// <summary>プロジェクトの作成・保存・検証・ドライバへの適用・ドライバからの読み込み・CLI形式へのエクスポート。</summary>
public static class ProjectService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string ProjectPath(uint uniqId) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LayerDriver", uniqId.ToString("X8"), "project.json");

    public static KeymapProject? TryLoad(uint uniqId)
    {
        var path = ProjectPath(uniqId);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<KeymapProject>(File.ReadAllText(path), JsonOptions);
    }

    public static void Save(KeymapProject project)
    {
        var path = ProjectPath(project.UniqId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(project, JsonOptions), new UTF8Encoding(false));
    }

    /// <summary>同梱レイアウト名 or 保存されたフルパスからレイアウトファイルを探す。</summary>
    public static string? FindLayoutFile(KeymapProject project)
    {
        if (project.LayoutPath != null && File.Exists(project.LayoutPath)) return project.LayoutPath;
        return KeyboardLayout.EnumerateBundledLayoutFiles()
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
    }

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
                warnings.Add($"{slot.Description}: スキャンコードが不明なため適用されません（キャリブレーションで実測してください）");
                continue;
            }
            if (seen.TryGetValue(sc.Value, out var other))
            {
                errors.Add($"{slot.Description} と {other.Description} が同じスキャンコード 0x{sc:X4} です（キャリブレーションをやり直してください）");
                continue;
            }
            seen[sc.Value] = slot;
            result.Add(new SlotMapping(slot, sc.Value));
        }
        return result;
    }

    /// <summary>存在しないレイヤーへの切り替え（エラー）と、中身の無いマクロ/タップダンスの参照（警告）を調べる。</summary>
    private static void CheckReferences(KeymapProject project, KeyboardLayout layout, List<string> warnings, List<string> errors)
    {
        var macroIds = project.Macros.Where(m => m.Keys.Count > 0).Select(m => m.Id).ToHashSet();
        var tapDanceIds = project.TapDances.Select(t => t.Id).ToHashSet();
        for (int L = 0; L < project.Layers.Count; L++)
        {
            foreach (var slot in layout.Slots)
            {
                string token = project.Layers[L][slot.Index];
                if (!KeycodeTable.TryResolve(token, out var a, out var err))
                {
                    errors.Add($"レイヤー{L} {slot.Description}: {err}");
                    continue;
                }
                bool layerRef = a.Type is ActionType.Momentary or ActionType.Toggle or ActionType.Default
                    or ActionType.OneShotLayer or ActionType.LayerTap or ActionType.LayerMod;
                if (layerRef && a.Param1 >= project.Layers.Count)
                    errors.Add($"レイヤー{L} {slot.Description}: {token} のレイヤー{a.Param1}がありません（レイヤーを追加してください）");
                else if (a.Type == ActionType.Macro && !macroIds.Contains(a.Param1))
                    warnings.Add($"{token} の中身が未設定です（押しても何も起きません）");
                else if (a.Type == ActionType.TapDance && !tapDanceIds.Contains(a.Param1))
                    warnings.Add($"{token} の中身が未設定です（押しても何も起きません）");
            }
        }
    }

    public static List<MacroStep> MacroSteps(MacroDef macro, List<string> errors)
    {
        var steps = new List<MacroStep>();
        foreach (var token in macro.Keys)
        {
            if (!KeycodeTable.TryResolve(token, out var a) || (a.Type != ActionType.Key && a.Type != ActionType.ModdedKey))
            {
                errors.Add($"マクロ{macro.Id}: 「{token}」はキー(KC_*)または修飾付きキー(LCTL(KC_C)等)ではありません");
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
            errors.Add($"マクロ{macro.Id}: 長すぎます（{steps.Count}ステップ。上限は{KbdLayerClient.MaxMacroSteps}で、" +
                       "キー1つが2、修飾キー1つにつき2を使います）");
        return steps;
    }

    /// <summary>
    /// ドライバから読んだマクロのステップ列を、キーコードの並びに戻す（MacroStepsの逆）。
    /// 「修飾キーを押す → キーを押して離す → 修飾キーを離す」の並びは LCTL(KC_C) の形にする。
    /// </summary>
    public static List<string> MacroTokensFromSteps(IReadOnlyList<MacroStep> steps)
    {
        string Name(ushort sc) => KeycodeTable.NameForScanCode(sc) ?? $"KC_UNKNOWN_{sc:X4}";
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
            errors.Add($"コンボ{combo.Id}: メンバーのキーがレイアウトにありません");
            return null;
        }
        var s1 = EffectiveScanCode(project, layout.Slots[combo.Slot1]);
        var s2 = EffectiveScanCode(project, layout.Slots[combo.Slot2]);
        if (s1 == null || s2 == null) { errors.Add($"コンボ{combo.Id}: メンバーのスキャンコードが不明です"); return null; }
        if (s1 == s2) { errors.Add($"コンボ{combo.Id}: メンバーの2キーは別のキーにしてください"); return null; }
        if (!KeycodeTable.TryResolve(combo.Output, out var output) || output.Type != ActionType.Key)
        {
            errors.Add($"コンボ{combo.Id}: 出力「{combo.Output}」は単純なキー(KC_*)にしてください");
            return null;
        }
        return new ComboEntry(s1.Value, s2.Value, output);
    }

    public static TapDanceEntry? TapDanceToEntry(TapDanceDef td, List<string> errors)
    {
        KeyAction Plain(string token, string what)
        {
            if (!KeycodeTable.TryResolve(token, out var a) || a.Type != ActionType.Key)
            {
                errors.Add($"タップダンス{td.Id}: {what}「{token}」は単純なキー(KC_*)にしてください");
                return KeyAction.None;
            }
            return a;
        }
        int before = errors.Count;
        var single = Plain(td.SingleTap, "1回タップ");
        var dbl = Plain(td.DoubleTap, "2回タップ");
        var triple = Plain(td.TripleTap, "3回タップ");
        if (!KeycodeTable.TryResolve(td.SingleHold, out var hold) ||
            (hold.Type != ActionType.Key && hold.Type != ActionType.ModTap && hold.Type != ActionType.LayerTap))
        {
            errors.Add($"タップダンス{td.Id}: ホールド「{td.SingleHold}」はKC_*、MT(mod,kc)、LT(layer,kc)のいずれかにしてください");
        }
        return errors.Count == before ? new TapDanceEntry(single, hold, dbl, triple) : null;
    }

    /// <summary>
    /// ドライバへ一括適用する: 検証 → OFF → レイヤーマップ → タイミング → マクロ/コンボ/タップダンス → ON。
    /// 検証エラーがあれば何も送らずに例外を投げる。戻り値は警告の一覧。
    /// </summary>
    public static List<string> Apply(KbdLayerClient client, KeymapProject project, KeyboardLayout layout)
    {
        var warnings = new List<string>();
        var errors = new List<string>();

        var mapped = ResolveMappedSlots(project, layout, warnings, errors);
        byte[]? blob = null;
        if (mapped.Count > 0)
        {
            try
            {
                blob = BlobBuilder.Build(project.Layers.Count,
                    mapped.Select(m => new BlobBuilder.KeyInput(m.ScanCode, project.Layers.Select(l => l[m.Slot.Index]).ToList())).ToList());
            }
            catch (InvalidOperationException ex) { errors.Add(ex.Message); }
        }

        CheckReferences(project, layout, warnings, errors);

        var macroSteps = project.Macros.ToDictionary(m => m.Id, m => MacroSteps(m, errors));
        var combos = project.Combos.ToDictionary(c => c.Id, c => ComboToEntry(project, layout, c, errors));
        var tapDances = project.TapDances.ToDictionary(t => t.Id, t => TapDanceToEntry(t, errors));

        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));

        uint id = project.UniqId;
        client.SetEnabled(id, false);
        if (blob != null) client.SetLayerMap(id, blob);
        else client.ClearLayerMap(id);
        client.SetConfig(id, project.TapTermMs, project.ComboTermMs, project.TapDanceTermMs);

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

        if (blob != null) client.SetEnabled(id, true);
        else warnings.Add("割り当てのあるキーが無いため、レイヤーマップは消去されOFFのままです");
        return warnings;
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

        var bySc = new Dictionary<ushort, KeySlot>();
        foreach (var s in layout.Slots)
            if (s.DefaultScanCode is ushort sc && !bySc.ContainsKey(sc)) bySc[sc] = s;

        foreach (var e in entries)
        {
            if (!bySc.TryGetValue(e.SourceScanCode, out var slot))
            {
                warnings.Add($"スキャンコード 0x{e.SourceScanCode:X4} のキーはレイアウト上に見つからないため読み込めませんでした");
                continue;
            }
            for (int L = 0; L < layerCount; L++) project.Layers[L][slot.Index] = KeycodeTable.ToToken(e.Actions[L]);
        }

        var cfg = client.GetConfig(device.UniqId);
        project.TapTermMs = cfg.TapTermMs;
        project.ComboTermMs = cfg.ComboTermMs;
        project.TapDanceTermMs = cfg.TapDanceTermMs;

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
                warnings.Add($"コンボ{c}のメンバーのキーがレイアウト上に見つからないため読み込めませんでした");
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
        return project;
    }

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
        if (mapped.Count == 0) throw new InvalidOperationException("割り当てのあるキーがありません");

        string keymapPath = Path.Combine(folder, "keymap.json");
        string profilePath = Path.Combine(folder, "profile.json");

        using (var fs = File.Create(keymapPath))
        using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("notes", $"Exported by LayerDriver Keymap Editor. Layout {layout.Name}, {mapped.Count} keys " +
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

        if (project.Macros.Count + project.Combos.Count + project.TapDances.Count > 0)
            warnings.Add("マクロ・コンボ・タップダンスはkeymap.jsonに含まれません（CLIでは --set-macro / --set-combo / --set-tapdance で別途登録します）");
        return warnings;
    }
}
