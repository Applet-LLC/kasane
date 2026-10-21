using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Kasane.Interop;
using Kasane.Keycodes;

namespace Kasane.Model;

/// <summary>
/// NICOLAの設定の読み書き（nicola.json）と、ドライバへ送るblob（KBDLAYER_NICOLA_HEADER +
/// KBDLAYER_NICOLA_KEY_ENTRY[]）の組み立て。driver/KeymapTool/NicolaTables.cpp の BuildBlob の移植で、
/// 検証の内容と blob の中身（キーの並びを含む）は CLI の --set-nicola と同じにする。
/// </summary>
public static class NicolaService
{
    private const uint NicolaVersion = 1;                 // KBDLAYER_NICOLA_VERSION
    private const uint FlagContinuousShift = 0x00000001;  // KBDLAYER_NICOLA_FLAG_CONTINUOUS_SHIFT
    private const int PlaneNone = 0, PlaneThumbLeft = 1, PlaneThumbRight = 2;

    /// <summary>NICOLA規格書の配列（samples/nicola.json と同じ）。[単独, 同じ側の親指, 反対側の親指]。</summary>
    private static readonly (string Key, string Single, string Same, string Cross)[] StandardLeft =
    {
        ("KC_Q", "。", "ぁ", ""), ("KC_W", "か", "え", "が"), ("KC_E", "た", "り", "だ"), ("KC_R", "こ", "ゃ", "ご"), ("KC_T", "さ", "れ", "ざ"),
        ("KC_A", "う", "を", "ヴ"), ("KC_S", "し", "あ", "じ"), ("KC_D", "て", "な", "で"), ("KC_F", "け", "ゅ", "げ"), ("KC_G", "せ", "も", "ぜ"),
        ("KC_Z", "．", "ぅ", "゜"), ("KC_X", "ひ", "ー", "び"), ("KC_C", "す", "ろ", "ず"), ("KC_V", "ふ", "や", "ぶ"), ("KC_B", "へ", "ぃ", "べ"),
    };
    private static readonly (string Key, string Single, string Same, string Cross)[] StandardRight =
    {
        ("KC_Y", "ら", "よ", "ぱ"), ("KC_U", "ち", "に", "ぢ"), ("KC_I", "く", "る", "ぐ"), ("KC_O", "つ", "ま", "づ"), ("KC_P", "，", "ぇ", "ぴ"),
        ("KC_LBRC", "、", "", ""), ("KC_RBRC", "゛", "゜", ""),
        ("KC_H", "は", "み", "ば"), ("KC_J", "と", "お", "ど"), ("KC_K", "き", "の", "ぎ"), ("KC_L", "い", "ょ", "ぽ"), ("KC_SCLN", "ん", "っ", ""),
        ("KC_N", "め", "ぬ", "ぷ"), ("KC_M", "そ", "ゆ", "ぞ"), ("KC_COMM", "ね", "む", "ぺ"), ("KC_DOT", "ほ", "わ", "ぼ"), ("KC_SLSH", "・", "ぉ", "゛"),
    };

    /// <summary>規格書どおりの配列（ローマ字出力、レイヤー1、親指キーは無変換・変換）。</summary>
    public static NicolaDef CreateStandard()
    {
        var def = new NicolaDef();
        foreach (var k in StandardLeft)
            def.Keys.Add(new NicolaKeyDef { Key = k.Key, RightHand = false, Single = k.Single, ThumbLeft = k.Same, ThumbRight = k.Cross });
        foreach (var k in StandardRight)
            def.Keys.Add(new NicolaKeyDef { Key = k.Key, RightHand = true, Single = k.Single, ThumbLeft = k.Cross, ThumbRight = k.Same });
        return def;
    }

    // ------------------------------------------------------------------
    // nicola.json の読み書き
    // ------------------------------------------------------------------

    public static NicolaDef Load(string path) => FromJson(File.ReadAllText(path));

    /// <summary>nicola.json の内容を読む。形が違えば InvalidDataException（中身の検証は BuildBlob で行う）。</summary>
    public static NicolaDef FromJson(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex) { throw new InvalidDataException($"JSONとして読めません: {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("nicola.json の最上位がオブジェクトではありません");

            string Str(string name, string def) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : def;
            double Num(string name, double def) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : def;
            bool Bool(string name, bool def) =>
                root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : def;

            var def = new NicolaDef
            {
                Output = Str("output", "romaji"),
                Enabled = Bool("enabled", true),
                Layer = (int)Num("layer", 0),
                ThumbLeft = Str("thumbLeft", ""),
                ThumbRight = Str("thumbRight", ""),
                ThumbLeftTap = Str("thumbLeftTap", ""),
                ThumbRightTap = Str("thumbRightTap", ""),
                OnEnter = Str("onEnter", ""),
                OnExit = Str("onExit", ""),
                SimulTermMs = (uint)Math.Max(0, Num("simulTermMs", 100)),
                OverlapRatio = Num("overlapRatio", 1.0),
                MinOverlapMs = (uint)Math.Max(0, Num("minOverlapMs", 0)),
                ContinuousShift = Bool("continuousShift", false),
            };

            foreach (var (region, right) in new[] { ("left", false), ("right", true) })
            {
                if (!root.TryGetProperty(region, out var keys)) continue;
                if (keys.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"\"{region}\" は \"KC_x\": [単独, 同じ側の親指, 反対側の親指] のオブジェクトにしてください");
                foreach (var kv in keys.EnumerateObject())
                {
                    if (kv.Value.ValueKind != JsonValueKind.Array || kv.Value.GetArrayLength() is 0 or > 3)
                        throw new InvalidDataException($"\"{kv.Name}\": 1〜3個の文字列の配列 [単独, 同じ側の親指, 反対側の親指] にしてください");
                    var planes = new string[3] { "", "", "" };
                    int i = 0;
                    foreach (var p in kv.Value.EnumerateArray())
                    {
                        if (p.ValueKind != JsonValueKind.String) throw new InvalidDataException($"\"{kv.Name}\": 配列の中身は文字列にしてください");
                        planes[i++] = p.GetString()!;
                    }
                    def.Keys.Add(new NicolaKeyDef
                    {
                        Key = kv.Name,
                        RightHand = right,
                        Single = planes[0],
                        ThumbLeft = right ? planes[2] : planes[1],
                        ThumbRight = right ? planes[1] : planes[2],
                    });
                }
            }
            return def;
        }
    }

    public static void Save(NicolaDef def, string path) =>
        File.WriteAllText(path, ToJson(def), new UTF8Encoding(false));

    /// <summary>samples/nicola.json と同じ形（文字キーは1行に1つ）で書く。</summary>
    public static string ToJson(NicolaDef def)
    {
        var opt = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        string S(string s) => JsonSerializer.Serialize(s, opt);
        string N(double d) => d.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture);

        var sb = new StringBuilder("{\n");
        sb.Append($"  \"output\": {S(def.Output)},\n");
        if (!def.Enabled) sb.Append("  \"enabled\": false,\n");
        sb.Append($"  \"layer\": {def.Layer},\n");
        sb.Append($"  \"thumbLeft\": {S(def.ThumbLeft)},\n");
        sb.Append($"  \"thumbRight\": {S(def.ThumbRight)},\n");
        sb.Append($"  \"thumbLeftTap\": {S(def.ThumbLeftTap)},\n");
        sb.Append($"  \"thumbRightTap\": {S(def.ThumbRightTap)},\n");
        sb.Append($"  \"onEnter\": {S(def.OnEnter)},\n");
        sb.Append($"  \"onExit\": {S(def.OnExit)},\n");
        sb.Append($"  \"simulTermMs\": {def.SimulTermMs},\n");
        sb.Append($"  \"overlapRatio\": {N(def.OverlapRatio)},\n");
        sb.Append($"  \"minOverlapMs\": {def.MinOverlapMs},\n");
        sb.Append($"  \"continuousShift\": {(def.ContinuousShift ? "true" : "false")},\n");
        foreach (var (region, right) in new[] { ("left", false), ("right", true) })
        {
            var keys = def.Keys.Where(k => k.RightHand == right).ToList();
            sb.Append($"  {S(region)}: {{\n");
            for (int i = 0; i < keys.Count; i++)
            {
                var k = keys[i];
                string same = right ? k.ThumbRight : k.ThumbLeft;
                string cross = right ? k.ThumbLeft : k.ThumbRight;
                sb.Append($"    {S(k.Key)}: [{S(k.Single)}, {S(same)}, {S(cross)}]{(i + 1 < keys.Count ? "," : "")}\n");
            }
            sb.Append(right ? "  }\n" : "  },\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // 親指キーのレイヤーマップ上の割り当て
    // ------------------------------------------------------------------

    /// <summary>
    /// NICOLAのレイヤーのレイヤーマップに入れる、親指キーの割り当て（スキャンコード、割り当てるキーコード）。
    /// ドライバは、NICOLAが判定するキー（親指・文字キー）を、レイヤーマップより先に取る。そのため、この割り当てが
    /// 使われるのは、NICOLAが判定しないとき（Ctrl・Shift・Alt・Winを押している間）だけで、次の2つに効く。
    ///  - Ctrl+無変換、Shift+変換などが、無変換・変換のままではなく、単独打鍵のキー（Enter・Space等）になる。
    ///  - NICOLAはレイヤーマップがONのときだけ動くので、キー割り当てが1つも無い人でも、マップが空にならない。
    /// 割り当てるのは、親指キーの単独打鍵のキー（nicola.jsonの thumbLeftTap / thumbRightTap）。空なら親指キー自身
    /// （＝そのまま通す）。
    /// </summary>
    public static List<(ushort ScanCode, string Token)> ThumbLayerEntries(NicolaDef def)
    {
        var entries = new List<(ushort, string)>();
        foreach (var (key, tap) in new[] { (def.ThumbLeft, def.ThumbLeftTap), (def.ThumbRight, def.ThumbRightTap) })
        {
            string k = key.Trim(), t = tap.Trim();
            if (!KeycodeTable.TryResolve(k, out var thumb) || thumb.Type != ActionType.Key || thumb.Param2 == 0) continue;
            string token = t.Length > 0 && t != "KC_NO" && KeycodeTable.TryResolve(t, out var a) && a.Type == ActionType.Key ? t : k;
            if (entries.All(e => e.Item1 != thumb.Param2)) entries.Add((thumb.Param2, token));
        }
        return entries;
    }

    // ------------------------------------------------------------------
    // blob の組み立て
    // ------------------------------------------------------------------

    /// <summary>
    /// ドライバへ送る blob を組み立てる。エラーがあれば errors に足して null を返す。
    /// 変換できない「かな」はその欄を空にして warnings に足す（CLIと同じ）。
    /// </summary>
    public static byte[]? BuildBlob(NicolaDef def, List<string> warnings, List<string> errors)
    {
        int before = errors.Count;
        bool jisKana;
        if (def.Output == "romaji") jisKana = false;
        else if (def.Output == "jis-kana") jisKana = true;
        else { errors.Add($"NICOLA: 出力の形式は romaji か jis-kana にしてください({def.Output})"); return null; }

        uint overlapPct = (uint)Math.Max(0, Math.Round(def.OverlapRatio * 100.0, MidpointRounding.AwayFromZero));
        if (def.Layer < 0 || def.Layer >= KbdLayerClient.MaxLayers)
            errors.Add($"NICOLA: レイヤーは0〜{KbdLayerClient.MaxLayers - 1}にしてください");
        if (def.SimulTermMs == 0 || def.SimulTermMs > 1000 || overlapPct == 0 || overlapPct > 1000 || def.MinOverlapMs > 1000)
            errors.Add("NICOLA: 同時打鍵の判定時間は1〜1000ms、重なりの比率は0.01〜10、最小の重なりは0〜1000msにしてください");

        ushort Key(string label, string token, bool allowEmpty)
        {
            token = token.Trim();
            if (token.Length == 0 || token == "KC_NO")
            {
                if (!allowEmpty) errors.Add($"NICOLA: {label}のキーを指定してください");
                return 0;
            }
            if (!KeycodeTable.TryResolve(token, out var a) || a.Type != ActionType.Key)
            {
                errors.Add($"NICOLA: {label}「{token}」は単純なキー(KC_*)にしてください");
                return 0;
            }
            return a.Param2;
        }
        ushort thumbL = Key("親指左", def.ThumbLeft, false);
        ushort thumbR = Key("親指右", def.ThumbRight, false);
        ushort thumbLTap = Key("親指左の単独打鍵", def.ThumbLeftTap, true);
        ushort thumbRTap = Key("親指右の単独打鍵", def.ThumbRightTap, true);
        ushort onEnter = Key("有効になったときのキー", def.OnEnter, true);
        ushort onExit = Key("無効になったときのキー", def.OnExit, true);
        if (thumbL != 0 && thumbL == thumbR) errors.Add("NICOLA: 親指左と親指右は別のキーにしてください");

        // CLIと同じく、左手側のキー → 右手側のキーの順に並べる（ドライバは並びに依存しないが、blobを揃える）。
        var ordered = def.Keys.Where(k => !k.RightHand).Concat(def.Keys.Where(k => k.RightHand)).ToList();
        var entries = new List<(ushort Source, List<(ushort, bool)>[] Planes)>();
        foreach (var k in ordered)
        {
            ushort source = Key("文字キー", k.Key, false);
            if (source == 0) continue;
            if (source == thumbL || source == thumbR) { errors.Add($"NICOLA: 「{k.Key}」は親指キーにも使われています"); continue; }
            if (entries.Any(e => e.Source == source)) { errors.Add($"NICOLA: 「{k.Key}」が2回以上定義されています"); continue; }

            var planes = new List<(ushort, bool)>[3] { new(), new(), new() };
            foreach (var (plane, text, name) in new[] { (PlaneNone, k.Single, "単独"), (PlaneThumbLeft, k.ThumbLeft, "親指左"), (PlaneThumbRight, k.ThumbRight, "親指右") })
            {
                if (string.IsNullOrEmpty(text)) continue;
                var steps = new List<(ushort, bool)>();
                if (!NicolaKana.TextToSteps(text, jisKana, steps, out var bad))
                {
                    warnings.Add($"NICOLA: {k.Key}の{name}「{bad}」は{(jisKana ? "JISかな" : "ローマ字")}入力では打てないため、空にしました");
                    continue;
                }
                if (steps.Count > KbdLayerClient.NicolaMaxSteps)
                {
                    errors.Add($"NICOLA: {k.Key}の{name}「{text}」は送出に{steps.Count}ステップ必要です(最大{KbdLayerClient.NicolaMaxSteps})");
                    continue;
                }
                planes[plane] = steps;
            }
            entries.Add((source, planes));
        }
        if (entries.Count == 0 && errors.Count == before) errors.Add("NICOLA: 文字キーがありません");
        if (entries.Count > KbdLayerClient.NicolaMaxKeys) errors.Add($"NICOLA: 文字キーが多すぎます(最大{KbdLayerClient.NicolaMaxKeys})");
        if (errors.Count > before) return null;

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            w.Write(NicolaVersion);
            w.Write(def.Enabled ? 1u : 0u);
            w.Write((uint)def.Layer);
            w.Write(thumbL);
            w.Write(thumbR);
            w.Write(thumbLTap);
            w.Write(thumbRTap);
            w.Write(onEnter);
            w.Write(onExit);
            w.Write(def.SimulTermMs);
            w.Write(overlapPct);
            w.Write(def.MinOverlapMs);
            w.Write(def.ContinuousShift ? FlagContinuousShift : 0u);
            w.Write((uint)entries.Count);
            foreach (var (source, planes) in entries)
            {
                w.Write(source);
                w.Write((ushort)0);
                foreach (var steps in planes)
                {
                    w.Write((byte)steps.Count);
                    w.Write(new byte[3]);
                    for (int s = 0; s < KbdLayerClient.NicolaMaxSteps; s++)
                    {
                        var (sc, isBreak) = s < steps.Count ? steps[s] : ((ushort)0, false);
                        w.Write(sc);
                        w.Write((byte)(isBreak ? 1 : 0));
                        w.Write((byte)0);
                    }
                }
            }
        }
        return ms.ToArray();
    }

    /// <summary>ドライバに入っている blob の要約（GET_NICOLA の結果の表示用）。</summary>
    public static string DescribeBlob(byte[] blob)
    {
        if (blob.Length < KbdLayerClient.NicolaHeaderSize) return "未設定";
        uint enabled = BitConverter.ToUInt32(blob, 4);
        uint layer = BitConverter.ToUInt32(blob, 8);
        ushort thumbL = BitConverter.ToUInt16(blob, 12);
        ushort thumbR = BitConverter.ToUInt16(blob, 14);
        uint simul = BitConverter.ToUInt32(blob, 24);
        uint keyCount = BitConverter.ToUInt32(blob, 40);
        string Name(ushort sc) => KeycodeTable.NameForScanCode(sc) ?? $"0x{sc:X4}";
        return $"設定あり({(enabled != 0 ? "有効" : "無効")}、レイヤー{layer}、文字キー{keyCount}個、" +
               $"親指 {Name(thumbL)} / {Name(thumbR)}、判定時間 {simul}ms)";
    }
}
