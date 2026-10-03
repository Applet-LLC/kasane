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
                bool layerRef = ActionType.ReferencesLayer(a.Type);
                if (layerRef && a.Param1 >= project.Layers.Count)
                    errors.Add($"レイヤー{L} {slot.Description}: {token} のレイヤー{a.Param1}がありません（レイヤーを追加してください）");
                else if (a.Type == ActionType.Macro && !macroIds.Contains(a.Param1))
                    warnings.Add($"{token} の中身が未設定です（押しても何も起きません）");
                else if (a.Type == ActionType.TapDance && !tapDanceIds.Contains(a.Param1))
                    warnings.Add($"{token} の中身が未設定です（押しても何も起きません）");
            }
        }
        // タップダンス（タップ側・長押し）とコンボの出力から参照しているマクロ。
        foreach (var td in project.TapDances)
        {
            foreach (var token in new[] { td.SingleTap, td.SingleHold, td.DoubleTap, td.TripleTap })
            {
                if (KeycodeTable.TryResolve(token, out var a) && a.Type == ActionType.Macro && !macroIds.Contains(a.Param1))
                    warnings.Add($"タップダンス{td.Id}: {token} の中身が未設定です（そこでは何も起きません）");
            }
        }
        foreach (var combo in project.Combos)
        {
            if (KeycodeTable.TryResolve(combo.Output, out var a) && a.Type == ActionType.Macro && !macroIds.Contains(a.Param1))
                warnings.Add($"コンボ{combo.Id}: {combo.Output} の中身が未設定です（成立しても何も起きません）");
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
        if (!KeycodeTable.TryResolve(combo.Output, out var output) ||
            output.Type is not (ActionType.Key or ActionType.ModdedKey or ActionType.Macro))
        {
            errors.Add($"コンボ{combo.Id}: 出力「{combo.Output}」はキー(KC_*)、修飾付きキー(LCTL(KC_Z)等)、MACRO(n)のいずれかにしてください");
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
                errors.Add($"タップダンス{td.Id}: {what}「{token}」はキー(KC_*)、修飾付きキー(LCTL(KC_C)等)、MACRO(n)、空欄（動作なし）のいずれかにしてください");
                return KeyAction.None;
            }
            return a;
        }
        int before = errors.Count;
        var single = Tap(td.SingleTap, "1回タップ");
        var dbl = Tap(td.DoubleTap, "2回タップ");
        var triple = Tap(td.TripleTap, "3回タップ");
        KeyAction hold = KeyAction.None; // 長押しの動作なし
        if (!IsNoAction(td.SingleHold) &&
            (!KeycodeTable.TryResolve(td.SingleHold, out hold) ||
             hold.Type is not (ActionType.Key or ActionType.ModTap or ActionType.LayerTap
                 or ActionType.ModdedKey or ActionType.LayerMod or ActionType.Macro)))
        {
            errors.Add($"タップダンス{td.Id}: ホールド「{td.SingleHold}」はKC_*、MT(mod,kc)、LT(layer,kc)、修飾付きキー(LCTL(KC_Z)等)、" +
                       "LM(layer,mod)、MACRO(n)、空欄（動作なし）のいずれかにしてください");
        }
        return errors.Count == before ? new TapDanceEntry(single, hold, dbl, triple) : null;
    }

    /// <summary>
    /// ドライバへ一括適用する: 検証 → OFF → レイヤーマップ → タイミング → マクロ/コンボ/タップダンス/キー置換
    /// → 機能のON/OFF → NICOLA → ON。
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
        var keyOverrides = project.KeyOverrides.ToDictionary(k => k.Id, k => KeyOverrideToEntry(k, errors));

        byte[]? nicolaBlob = null;
        if (project.Nicola != null)
        {
            nicolaBlob = NicolaService.BuildBlob(project.Nicola, warnings, errors);
            if (project.Nicola.Layer >= project.Layers.Count)
                warnings.Add($"NICOLAのレイヤー{project.Nicola.Layer}はキーマップにありません（そのレイヤーに切り替えるキーが無いと、親指シフトは働きません）");
        }
        if (project.EnabledFeatures is uint f && (f & ~KbdLayerClient.FeatureAll) != 0)
            errors.Add($"機能のON/OFFの値 0x{f:X} に未知のビットがあります");

        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));

        uint id = project.UniqId;
        client.SetEnabled(id, false);
        if (blob != null) client.SetLayerMap(id, blob);
        else client.ClearLayerMap(id);
        client.SetConfig(id, project.TapTermMs, project.ComboTermMs, project.TapDanceTermMs,
                         project.TapHoldMode, project.QuickTapTermMs, project.TappingToggle);

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
        if (project.EnabledFeatures is uint features)
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
        else if (project.ClearNicola && client.GetNicola(id).Length > 0) client.SetNicola(id, Array.Empty<byte>());

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
                    Key = KeycodeTable.NameForScanCode(ko.TriggerScanCode) ?? $"KC_UNKNOWN_{ko.TriggerScanCode:X4}",
                    Replacement = KeycodeTable.ToToken(ko.Replacement),
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
    /// 置き換え先（KC_* か LCTL(KC_Z) のような修飾付きキー）がそろっていなければエラー。
    /// </summary>
    public static KeyOverrideEntry? KeyOverrideToEntry(KeyOverrideDef ko, List<string> errors)
    {
        int before = errors.Count;
        if (!KeycodeTable.TryParseModMask(ko.Mods.Trim(), out var mods))
            errors.Add($"キー置換{ko.Id}: 修飾キー「{ko.Mods}」は MOD_LSFT や MOD_LCTL|MOD_LSFT のように書いてください");
        if (!KeycodeTable.TryResolve(ko.Key.Trim(), out var key) || key.Type != ActionType.Key || key.Param2 == 0)
            errors.Add($"キー置換{ko.Id}: キー「{ko.Key}」は単純なキー(KC_*)にしてください");
        if (!KeycodeTable.TryResolve(ko.Replacement.Trim(), out var replacement) ||
            replacement.Type is not (ActionType.Key or ActionType.ModdedKey) || replacement.Param2 == 0)
            errors.Add($"キー置換{ko.Id}: 置き換え先「{ko.Replacement}」はキー(KC_*)か修飾付きキー(LCTL(KC_Z)等)にしてください");
        return errors.Count == before ? new KeyOverrideEntry(mods, key.Param2, replacement) : null;
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

        if (project.Macros.Count + project.Combos.Count + project.TapDances.Count + project.KeyOverrides.Count > 0)
            warnings.Add("マクロ・コンボ・タップダンス・キー置換はkeymap.jsonに含まれません（CLIでは --set-macro / --set-combo / --set-tapdance / --set-key-override で別途登録します）");
        return warnings;
    }
}
