namespace KbdLayerKeymapGui.Keycodes;

/// <summary>KBDLAYER_ACTION（4バイト: Type, Param1, Param2）。</summary>
public readonly record struct KeyAction(byte Type, byte Param1, ushort Param2)
{
    public static readonly KeyAction None = new(ActionType.None, 0, 0);
    public static readonly KeyAction Transparent = new(ActionType.Transparent, 0, 0);
}

/// <summary>KBDLAYER_ACTION_*（driver/common/kbdlayer_ioctl.h）。</summary>
public static class ActionType
{
    public const byte None = 0;
    public const byte Transparent = 1;
    public const byte Key = 2;
    public const byte Momentary = 3;
    public const byte Toggle = 4;
    public const byte Default = 5;
    public const byte OneShotLayer = 6;
    public const byte OneShotMod = 7;
    public const byte ModTap = 8;
    public const byte LayerTap = 9;
    public const byte Macro = 10;
    public const byte Custom = 11;
    public const byte TapDance = 12;
    /// <summary>修飾付きキー（LCTL(kc)等）: Param1=修飾子マスク, Param2=スキャンコード。</summary>
    public const byte ModdedKey = 13;
    /// <summary>LM(layer, mod): Param1=レイヤー番号, Param2の下位8bit=修飾子マスク。</summary>
    public const byte LayerMod = 14;
    /// <summary>TO(layer): そのレイヤーだけを有効にする（キーボード側の他のレイヤーは無効）。</summary>
    public const byte LayerTo = 15;
    /// <summary>TT(layer): 押している間はMO、5回続けてタップするとTGと同じく固定・解除。</summary>
    public const byte LayerTapToggle = 16;

    /// <summary>Param1がレイヤー番号のアクションか（存在しないレイヤーへの参照を調べるのに使う）。</summary>
    public static bool ReferencesLayer(byte type) => type is Momentary or Toggle or Default or OneShotLayer
        or LayerTap or LayerMod or LayerTo or LayerTapToggle;
}

/// <summary>
/// QMKキーコード（KC_*等）→ KBDLAYER_ACTION の変換。
/// driver/KeymapTool/KeycodeTable.cpp の移植。**表を変更するときは両方を同じように変更し、
/// tools/Check-KeycodeTables.ps1 で一致を確認すること。**
/// </summary>
public static class KeycodeTable
{
    // Enc16形式: 上位8bit = 0x00/0xE0/0xE1（拡張プレフィックス）、下位8bit = Set1 MakeCode。
    private static ushort SC(byte prefix, byte makeCode) => (ushort)((prefix << 8) | makeCode);
    private static ushort SC1(byte makeCode) => SC(0x00, makeCode);
    private static ushort SCE0(byte makeCode) => SC(0xE0, makeCode);

    // 同じスキャンコードに複数の名前がある場合、先に書いたものが逆変換（スキャンコード→名前）で
    // 使われる正式名になる（C++版の並び順と同じ）。
    private static readonly (string Name, ushort ScanCode)[] BaseKeys =
    {
        // 英字
        ("KC_A", SC1(0x1E)), ("KC_B", SC1(0x30)), ("KC_C", SC1(0x2E)), ("KC_D", SC1(0x20)),
        ("KC_E", SC1(0x12)), ("KC_F", SC1(0x21)), ("KC_G", SC1(0x22)), ("KC_H", SC1(0x23)),
        ("KC_I", SC1(0x17)), ("KC_J", SC1(0x24)), ("KC_K", SC1(0x25)), ("KC_L", SC1(0x26)),
        ("KC_M", SC1(0x32)), ("KC_N", SC1(0x31)), ("KC_O", SC1(0x18)), ("KC_P", SC1(0x19)),
        ("KC_Q", SC1(0x10)), ("KC_R", SC1(0x13)), ("KC_S", SC1(0x1F)), ("KC_T", SC1(0x14)),
        ("KC_U", SC1(0x16)), ("KC_V", SC1(0x2F)), ("KC_W", SC1(0x11)), ("KC_X", SC1(0x2D)),
        ("KC_Y", SC1(0x15)), ("KC_Z", SC1(0x2C)),
        // 数字（最上段）
        ("KC_1", SC1(0x02)), ("KC_2", SC1(0x03)), ("KC_3", SC1(0x04)), ("KC_4", SC1(0x05)),
        ("KC_5", SC1(0x06)), ("KC_6", SC1(0x07)), ("KC_7", SC1(0x08)), ("KC_8", SC1(0x09)),
        ("KC_9", SC1(0x0A)), ("KC_0", SC1(0x0B)),
        // 記号・編集キー
        ("KC_ENTER", SC1(0x1C)), ("KC_ENT", SC1(0x1C)),
        ("KC_ESCAPE", SC1(0x01)), ("KC_ESC", SC1(0x01)),
        ("KC_BSPACE", SC1(0x0E)), ("KC_BSPC", SC1(0x0E)),
        ("KC_TAB", SC1(0x0F)),
        ("KC_SPACE", SC1(0x39)), ("KC_SPC", SC1(0x39)),
        ("KC_MINUS", SC1(0x0C)), ("KC_MINS", SC1(0x0C)),
        ("KC_EQUAL", SC1(0x0D)), ("KC_EQL", SC1(0x0D)),
        ("KC_LBRACKET", SC1(0x1A)), ("KC_LBRC", SC1(0x1A)),
        ("KC_RBRACKET", SC1(0x1B)), ("KC_RBRC", SC1(0x1B)),
        ("KC_BSLASH", SC1(0x2B)), ("KC_BSLS", SC1(0x2B)),
        ("KC_NONUS_HASH", SC1(0x2B)), ("KC_NUHS", SC1(0x2B)),
        ("KC_SCOLON", SC1(0x27)), ("KC_SCLN", SC1(0x27)),
        ("KC_QUOTE", SC1(0x28)), ("KC_QUOT", SC1(0x28)),
        ("KC_GRAVE", SC1(0x29)), ("KC_GRV", SC1(0x29)), ("KC_ZKHK", SC1(0x29)),
        ("KC_COMMA", SC1(0x33)), ("KC_COMM", SC1(0x33)),
        ("KC_DOT", SC1(0x34)),
        ("KC_SLASH", SC1(0x35)), ("KC_SLSH", SC1(0x35)),
        ("KC_NONUS_BSLASH", SC1(0x56)), ("KC_NUBS", SC1(0x56)),
        ("KC_CAPSLOCK", SC1(0x3A)), ("KC_CAPS", SC1(0x3A)),
        // JIS配列専用キー
        ("KC_RO", SC1(0x73)), ("KC_INT1", SC1(0x73)),
        ("KC_KANA", SC1(0x70)), ("KC_INT2", SC1(0x70)),
        ("KC_JYEN", SC1(0x7D)), ("KC_INT3", SC1(0x7D)),
        ("KC_HENK", SC1(0x79)), ("KC_INT4", SC1(0x79)),
        ("KC_MHEN", SC1(0x7B)), ("KC_INT5", SC1(0x7B)),
        // ファンクションキー
        ("KC_F1", SC1(0x3B)), ("KC_F2", SC1(0x3C)), ("KC_F3", SC1(0x3D)), ("KC_F4", SC1(0x3E)),
        ("KC_F5", SC1(0x3F)), ("KC_F6", SC1(0x40)), ("KC_F7", SC1(0x41)), ("KC_F8", SC1(0x42)),
        ("KC_F9", SC1(0x43)), ("KC_F10", SC1(0x44)), ("KC_F11", SC1(0x57)), ("KC_F12", SC1(0x58)),
        // 修飾キー
        ("KC_LCTRL", SC1(0x1D)), ("KC_LCTL", SC1(0x1D)),
        ("KC_LSHIFT", SC1(0x2A)), ("KC_LSFT", SC1(0x2A)),
        ("KC_LALT", SC1(0x38)),
        ("KC_LGUI", SCE0(0x5B)), ("KC_LCMD", SCE0(0x5B)), ("KC_LWIN", SCE0(0x5B)),
        ("KC_RCTRL", SCE0(0x1D)), ("KC_RCTL", SCE0(0x1D)),
        ("KC_RSHIFT", SC1(0x36)), ("KC_RSFT", SC1(0x36)),
        ("KC_RALT", SCE0(0x38)), ("KC_ALGR", SCE0(0x38)),
        ("KC_RGUI", SCE0(0x5C)), ("KC_RCMD", SCE0(0x5C)), ("KC_RWIN", SCE0(0x5C)),
        ("KC_APP", SCE0(0x5D)), ("KC_APPLICATION", SCE0(0x5D)),
        // MT/LTのタップ時キーコード引数用のプレースホルダー（単独トークンのKC_NOはResolveで特別扱い）
        ("KC_NO", SC1(0x00)),
        // ナビゲーションクラスタ
        ("KC_PSCREEN", SCE0(0x37)), ("KC_PSCR", SCE0(0x37)),
        ("KC_SCROLLLOCK", SC1(0x46)), ("KC_SCRL", SC1(0x46)),
        ("KC_PAUSE", SC(0xE1, 0x1D)), ("KC_PAUS", SC(0xE1, 0x1D)), ("KC_BRK", SC(0xE1, 0x1D)),
        ("KC_INSERT", SCE0(0x52)), ("KC_INS", SCE0(0x52)),
        ("KC_HOME", SCE0(0x47)),
        ("KC_PGUP", SCE0(0x49)),
        ("KC_DELETE", SCE0(0x53)), ("KC_DEL", SCE0(0x53)),
        ("KC_END", SCE0(0x4F)),
        ("KC_PGDOWN", SCE0(0x51)), ("KC_PGDN", SCE0(0x51)),
        ("KC_RIGHT", SCE0(0x4D)), ("KC_RGHT", SCE0(0x4D)),
        ("KC_LEFT", SCE0(0x4B)),
        ("KC_DOWN", SCE0(0x50)),
        ("KC_UP", SCE0(0x48)),
        ("KC_NUMLOCK", SC1(0x45)), ("KC_NUM", SC1(0x45)),
        // テンキー
        ("KC_KP_SLASH", SCE0(0x35)), ("KC_PSLS", SCE0(0x35)),
        ("KC_KP_ASTERISK", SC1(0x37)), ("KC_PAST", SC1(0x37)),
        ("KC_KP_MINUS", SC1(0x4A)), ("KC_PMNS", SC1(0x4A)),
        ("KC_KP_PLUS", SC1(0x4E)), ("KC_PPLS", SC1(0x4E)),
        ("KC_KP_ENTER", SCE0(0x1C)), ("KC_PENT", SCE0(0x1C)),
        ("KC_KP_1", SC1(0x4F)), ("KC_P1", SC1(0x4F)),
        ("KC_KP_2", SC1(0x50)), ("KC_P2", SC1(0x50)),
        ("KC_KP_3", SC1(0x51)), ("KC_P3", SC1(0x51)),
        ("KC_KP_4", SC1(0x4B)), ("KC_P4", SC1(0x4B)),
        ("KC_KP_5", SC1(0x4C)), ("KC_P5", SC1(0x4C)),
        ("KC_KP_6", SC1(0x4D)), ("KC_P6", SC1(0x4D)),
        ("KC_KP_7", SC1(0x47)), ("KC_P7", SC1(0x47)),
        ("KC_KP_8", SC1(0x48)), ("KC_P8", SC1(0x48)),
        ("KC_KP_9", SC1(0x49)), ("KC_P9", SC1(0x49)),
        ("KC_KP_0", SC1(0x52)), ("KC_P0", SC1(0x52)),
        ("KC_KP_DOT", SC1(0x53)), ("KC_PDOT", SC1(0x53)),
        // 主要メディア/システムキー
        ("KC_MUTE", SCE0(0x20)), ("KC_KB_MUTE", SCE0(0x20)),
        ("KC_VOLD", SCE0(0x2E)), ("KC_KB_VOLUME_DOWN", SCE0(0x2E)),
        ("KC_VOLU", SCE0(0x30)), ("KC_KB_VOLUME_UP", SCE0(0x30)),
        ("KC_MPLY", SCE0(0x22)), ("KC_MEDIA_PLAY_PAUSE", SCE0(0x22)),
        ("KC_MSTP", SCE0(0x24)), ("KC_MEDIA_STOP", SCE0(0x24)),
        ("KC_MNXT", SCE0(0x19)), ("KC_MEDIA_NEXT_TRACK", SCE0(0x19)),
        ("KC_MPRV", SCE0(0x10)), ("KC_MEDIA_PREV_TRACK", SCE0(0x10)),
        ("KC_CALC", SCE0(0x21)), ("KC_CALCULATOR", SCE0(0x21)),
    };

    // MT(mod,kc)のParam1に使うビット番号。driver/kbdlayer/LayerEngine.cのs_ModifierScanCodesと同じ割り当て。
    public static readonly string[] ModifierNames =
        { "MOD_LCTL", "MOD_LSFT", "MOD_LALT", "MOD_LGUI", "MOD_RCTL", "MOD_RSFT", "MOD_RALT", "MOD_RGUI" };

    /// <summary>
    /// 修飾付きキー（LCTL(kc)等）の関数名 → 修飾子マスク（KBDLAYER_MOD_*のビット。MT用のビット番号とは
    /// 表し方が違う）。C()/S()/A()/G()はQMKの短い別名。driver/KeymapTool/KeycodeTable.cpp の
    /// ModFunctionTable と同じ内容・並びにすること（tools/Check-KeycodeTables.ps1 が照合する）。
    /// </summary>
    public static readonly (string Name, byte Mask)[] ModFunctions =
    {
        ("LCTL", 0x01), ("LSFT", 0x02), ("LALT", 0x04), ("LGUI", 0x08),
        ("RCTL", 0x10), ("RSFT", 0x20), ("RALT", 0x40), ("RGUI", 0x80),
        ("C", 0x01), ("S", 0x02), ("A", 0x04), ("G", 0x08),
    };

    /// <summary>修飾子マスクのビット番号（0=LCTL ... 7=RGUI）→ その修飾キーのスキャンコード（Enc16）。</summary>
    public static readonly ushort[] ModifierScanCodes =
        { 0x001D, 0x002A, 0x0038, 0xE05B, 0xE01D, 0x0036, 0xE038, 0xE05C };

    /// <summary>修飾子マスク → 入れ子の正規形（例: 0x03 と "KC_T" → "LCTL(LSFT(KC_T))"）。</summary>
    public static string WrapWithMods(byte mask, string inner)
    {
        string s = inner;
        for (int bit = 7; bit >= 0; bit--)
        {
            if ((mask & (1 << bit)) != 0) s = $"{ModFunctions[bit].Name}({s})";
        }
        return s;
    }

    /// <summary>修飾子マスク → "MOD_LCTL|MOD_LSFT"。</summary>
    public static string ModMaskToNames(byte mask) =>
        string.Join("|", Enumerable.Range(0, 8).Where(b => (mask & (1 << b)) != 0).Select(b => ModifierNames[b]));

    private static bool TryParseModdedKey(string token, out byte mask, out ushort scanCode)
    {
        mask = 0;
        scanCode = 0;
        string cur = token;
        for (int depth = 0; depth < 8; depth++)
        {
            int open = cur.IndexOf('(');
            if (open <= 0 || !cur.EndsWith(')')) break;
            string name = cur[..open];
            int idx = Array.FindIndex(ModFunctions, f => f.Name == name);
            if (idx < 0) return false;
            mask |= ModFunctions[idx].Mask;
            cur = cur[(open + 1)..^1].Trim();
        }
        return mask != 0 && TryResolveBaseKey(cur, out scanCode) && scanCode != 0;
    }

    /// <summary>"MOD_LCTL|MOD_LSFT" → 修飾子マスク（KBDLAYER_MOD_*のOR）。空・不明な名前はfalse。</summary>
    public static bool TryParseModMask(string s, out byte mask)
    {
        mask = 0;
        foreach (var part in s.Split('|').Select(p => p.Trim()))
        {
            int bit = Array.IndexOf(ModifierNames, part);
            if (bit < 0) return false;
            mask |= (byte)(1 << bit);
        }
        return mask != 0;
    }

    private static readonly Dictionary<string, ushort> NameToScan = BuildNameToScan();
    private static readonly Dictionary<ushort, string> ScanToName = BuildScanToName();

    private static Dictionary<string, ushort> BuildNameToScan()
    {
        var d = new Dictionary<string, ushort>(StringComparer.Ordinal);
        foreach (var (name, sc) in BaseKeys) d[name] = sc;
        return d;
    }

    private static Dictionary<ushort, string> BuildScanToName()
    {
        var d = new Dictionary<ushort, string>();
        foreach (var (name, sc) in BaseKeys)
        {
            if (sc != 0 && !d.ContainsKey(sc)) d[sc] = name;
        }
        return d;
    }

    /// <summary>全ベースキー名（別名を含む）。</summary>
    public static IEnumerable<string> AllBaseKeyNames => BaseKeys.Select(k => k.Name);

    public static bool TryResolveBaseKey(string token, out ushort scanCode) =>
        NameToScan.TryGetValue(token, out scanCode);

    /// <summary>スキャンコード → 正式なキーコード名（無ければ null）。</summary>
    public static string? NameForScanCode(ushort scanCode) =>
        ScanToName.TryGetValue(scanCode, out var n) ? n : null;

    public static bool TryResolve(string token, out KeyAction action, out string? error)
    {
        action = default;
        error = null;
        token = token.Trim();
        if (token.Length == 0) { error = "キーコードが空です"; return false; }

        if (token is "KC_TRNS" or "_______" or "KC_TRANSPARENT") { action = KeyAction.Transparent; return true; }
        if (token is "KC_NO" or "XXXXXXX") { action = KeyAction.None; return true; }

        if (TryResolveBaseKey(token, out var sc))
        {
            action = new KeyAction(ActionType.Key, 0, sc);
            return true;
        }

        if (TryParseModdedKey(token, out var modMask, out var modSc))
        {
            action = new KeyAction(ActionType.ModdedKey, modMask, modSc);
            return true;
        }

        int open = token.IndexOf('(');
        if (open > 0 && token.EndsWith(')'))
        {
            string name = token[..open];
            string[] parts = token[(open + 1)..^1].Split(',').Select(p => p.Trim()).ToArray();

            switch (name)
            {
                case "LM" when parts.Length == 2:
                {
                    if (!ParseLayer(parts[0], out var layer, out error)) return false;
                    if (!TryParseModMask(parts[1], out var mask))
                    {
                        error = $"LMの修飾キー「{parts[1]}」は MOD_LCTL や MOD_LCTL|MOD_LSFT の形で指定してください";
                        return false;
                    }
                    action = new KeyAction(ActionType.LayerMod, layer, mask);
                    return true;
                }
                case "MO" or "TG" or "DF" or "OSL" or "TO" or "TT" when parts.Length == 1:
                {
                    if (!ParseLayer(parts[0], out var layer, out error)) return false;
                    byte type = name switch
                    {
                        "MO" => ActionType.Momentary,
                        "TG" => ActionType.Toggle,
                        "DF" => ActionType.Default,
                        "TO" => ActionType.LayerTo,
                        "TT" => ActionType.LayerTapToggle,
                        _ => ActionType.OneShotLayer,
                    };
                    action = new KeyAction(type, layer, 0);
                    return true;
                }
                case "MT" when parts.Length == 2:
                {
                    int bit = Array.IndexOf(ModifierNames, parts[0]);
                    if (bit < 0) { error = $"修飾キー「{parts[0]}」は MOD_LCTL 等で指定してください"; return false; }
                    if (!ParseTapKey(parts[1], out var tapSc, out error)) return false;
                    action = new KeyAction(ActionType.ModTap, (byte)bit, tapSc);
                    return true;
                }
                case "LT" when parts.Length == 2:
                {
                    if (!ParseLayer(parts[0], out var layer, out error)) return false;
                    if (!ParseTapKey(parts[1], out var tapSc, out error)) return false;
                    action = new KeyAction(ActionType.LayerTap, layer, tapSc);
                    return true;
                }
                case "MACRO" when parts.Length == 1:
                {
                    if (!ParseId(parts[0], Interop.KbdLayerClient.MaxMacroCount, "マクロ", out var id, out error)) return false;
                    action = new KeyAction(ActionType.Macro, id, 0);
                    return true;
                }
                case "TAPDANCE" when parts.Length == 1:
                {
                    if (!ParseId(parts[0], Interop.KbdLayerClient.MaxTapDanceCount, "タップダンス", out var id, out error)) return false;
                    action = new KeyAction(ActionType.TapDance, id, 0);
                    return true;
                }
            }
        }

        error = $"「{token}」は認識できないキーコードです";
        return false;
    }

    private static bool ParseLayer(string s, out byte layer, out string? error)
    {
        error = null;
        if (!byte.TryParse(s, out layer) || layer >= Interop.KbdLayerClient.MaxLayers)
        {
            error = $"レイヤー番号「{s}」は0〜{Interop.KbdLayerClient.MaxLayers - 1}で指定してください";
            return false;
        }
        return true;
    }

    private static bool ParseId(string s, int max, string what, out byte id, out string? error)
    {
        error = null;
        if (!byte.TryParse(s, out id) || id >= max)
        {
            error = $"{what}番号「{s}」は0〜{max - 1}で指定してください";
            return false;
        }
        return true;
    }

    private static bool ParseTapKey(string s, out ushort tapSc, out string? error)
    {
        error = null;
        if (!TryResolveBaseKey(s, out tapSc))
        {
            error = $"タップ時のキー「{s}」は単純なキーコード(KC_*)で指定してください";
            return false;
        }
        return true;
    }

    public static bool TryResolve(string token, out KeyAction action) => TryResolve(token, out action, out _);

    /// <summary>KBDLAYER_ACTION → キーコード文字列（ドライバの現在設定の読み込み用）。</summary>
    public static string ToToken(KeyAction a)
    {
        string Key(ushort sc) => NameForScanCode(sc) ?? (sc == 0 ? "KC_NO" : $"KC_UNKNOWN_{sc:X4}");
        return a.Type switch
        {
            ActionType.None => "KC_NO",
            ActionType.Transparent => "KC_TRNS",
            ActionType.Key => Key(a.Param2),
            ActionType.Momentary => $"MO({a.Param1})",
            ActionType.Toggle => $"TG({a.Param1})",
            ActionType.Default => $"DF({a.Param1})",
            ActionType.OneShotLayer => $"OSL({a.Param1})",
            ActionType.LayerTo => $"TO({a.Param1})",
            ActionType.LayerTapToggle => $"TT({a.Param1})",
            ActionType.ModTap => $"MT({(a.Param1 < ModifierNames.Length ? ModifierNames[a.Param1] : "MOD_LCTL")},{Key(a.Param2)})",
            ActionType.LayerTap => $"LT({a.Param1},{Key(a.Param2)})",
            ActionType.Macro => $"MACRO({a.Param1})",
            ActionType.TapDance => $"TAPDANCE({a.Param1})",
            ActionType.ModdedKey => WrapWithMods(a.Param1, Key(a.Param2)),
            ActionType.LayerMod => $"LM({a.Param1},{ModMaskToNames((byte)(a.Param2 & 0xFF))})",
            _ => "KC_NO",
        };
    }
}
