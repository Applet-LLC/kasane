namespace KbdLayerKeymapGui.Keycodes;

/// <summary>キー割り当てダイアログに並べるキーコードの分類と、キートップ表示用の短い名前。</summary>
public static class KeycodeCatalog
{
    public sealed record Category(string Name, string[] Keycodes);

    public static readonly Category[] Categories =
    {
        new("基本", new[] { "KC_TRNS", "KC_NO" }),
        new("英字", Enumerable.Range('A', 26).Select(c => $"KC_{(char)c}").ToArray()),
        new("数字", new[] { "KC_1", "KC_2", "KC_3", "KC_4", "KC_5", "KC_6", "KC_7", "KC_8", "KC_9", "KC_0" }),
        new("記号・編集", new[]
        {
            "KC_ESC", "KC_TAB", "KC_ENTER", "KC_BSPACE", "KC_SPACE", "KC_CAPSLOCK",
            "KC_GRAVE", "KC_MINUS", "KC_EQUAL", "KC_LBRACKET", "KC_RBRACKET", "KC_BSLASH",
            "KC_SCOLON", "KC_QUOTE", "KC_COMMA", "KC_DOT", "KC_SLASH", "KC_NONUS_BSLASH",
        }),
        new("ファンクション", Enumerable.Range(1, 12).Select(i => $"KC_F{i}").ToArray()),
        new("修飾キー", new[]
        {
            "KC_LCTRL", "KC_LSHIFT", "KC_LALT", "KC_LGUI", "KC_RCTRL", "KC_RSHIFT", "KC_RALT", "KC_RGUI", "KC_APP",
        }),
        new("ナビゲーション", new[]
        {
            "KC_PSCREEN", "KC_SCROLLLOCK", "KC_PAUSE", "KC_INSERT", "KC_HOME", "KC_PGUP",
            "KC_DELETE", "KC_END", "KC_PGDOWN", "KC_UP", "KC_DOWN", "KC_LEFT", "KC_RIGHT",
        }),
        new("テンキー", new[]
        {
            "KC_NUMLOCK", "KC_KP_SLASH", "KC_KP_ASTERISK", "KC_KP_MINUS", "KC_KP_PLUS", "KC_KP_ENTER",
            "KC_KP_1", "KC_KP_2", "KC_KP_3", "KC_KP_4", "KC_KP_5", "KC_KP_6", "KC_KP_7", "KC_KP_8", "KC_KP_9",
            "KC_KP_0", "KC_KP_DOT",
        }),
        new("JIS", new[] { "KC_ZKHK", "KC_RO", "KC_KANA", "KC_JYEN", "KC_HENK", "KC_MHEN" }),
        new("メディア", new[]
        {
            "KC_MUTE", "KC_VOLD", "KC_VOLU", "KC_MPLY", "KC_MSTP", "KC_MNXT", "KC_MPRV", "KC_CALC",
        }),
    };

    private static readonly Dictionary<string, string> ShortNames = new(StringComparer.Ordinal)
    {
        ["KC_TRNS"] = "▽", ["KC_NO"] = "✕",
        ["KC_ESC"] = "Esc", ["KC_TAB"] = "Tab", ["KC_ENTER"] = "Enter", ["KC_BSPACE"] = "BS",
        ["KC_SPACE"] = "Space", ["KC_CAPSLOCK"] = "Caps", ["KC_GRAVE"] = "`", ["KC_ZKHK"] = "半/全",
        ["KC_MINUS"] = "-", ["KC_EQUAL"] = "=", ["KC_LBRACKET"] = "[", ["KC_RBRACKET"] = "]",
        ["KC_BSLASH"] = "\\", ["KC_SCOLON"] = ";", ["KC_QUOTE"] = "'", ["KC_COMMA"] = ",",
        ["KC_DOT"] = ".", ["KC_SLASH"] = "/", ["KC_NONUS_BSLASH"] = "ISO\\",
        ["KC_LCTRL"] = "LCtrl", ["KC_LSHIFT"] = "LShift", ["KC_LALT"] = "LAlt", ["KC_LGUI"] = "LWin",
        ["KC_RCTRL"] = "RCtrl", ["KC_RSHIFT"] = "RShift", ["KC_RALT"] = "RAlt", ["KC_RGUI"] = "RWin",
        ["KC_APP"] = "Menu",
        ["KC_PSCREEN"] = "PrtSc", ["KC_SCROLLLOCK"] = "ScrLk", ["KC_PAUSE"] = "Pause",
        ["KC_INSERT"] = "Ins", ["KC_HOME"] = "Home", ["KC_PGUP"] = "PgUp", ["KC_DELETE"] = "Del",
        ["KC_END"] = "End", ["KC_PGDOWN"] = "PgDn",
        ["KC_UP"] = "↑", ["KC_DOWN"] = "↓", ["KC_LEFT"] = "←", ["KC_RIGHT"] = "→",
        ["KC_NUMLOCK"] = "NumLk", ["KC_KP_SLASH"] = "P/", ["KC_KP_ASTERISK"] = "P*", ["KC_KP_MINUS"] = "P-",
        ["KC_KP_PLUS"] = "P+", ["KC_KP_ENTER"] = "PEnt", ["KC_KP_DOT"] = "P.",
        ["KC_RO"] = "ろ", ["KC_KANA"] = "かな", ["KC_JYEN"] = "¥", ["KC_HENK"] = "変換", ["KC_MHEN"] = "無変換",
        ["KC_MUTE"] = "Mute", ["KC_VOLD"] = "Vol-", ["KC_VOLU"] = "Vol+", ["KC_MPLY"] = "Play",
        ["KC_MSTP"] = "Stop", ["KC_MNXT"] = "Next", ["KC_MPRV"] = "Prev", ["KC_CALC"] = "Calc",
    };

    /// <summary>キートップに表示する短い名前。別名は正式名に寄せてから引く。</summary>
    public static string ShortName(string token)
    {
        if (ShortNames.TryGetValue(token, out var s)) return s;
        // 別名（KC_ENT等）は正式名（KC_ENTER等）に正規化して引き直す。
        if (KeycodeTable.TryResolve(token, out var action) && action.Type == ActionType.Key)
        {
            var canonical = KeycodeTable.NameForScanCode(action.Param2);
            if (canonical != null && ShortNames.TryGetValue(canonical, out s)) return s;
            token = canonical ?? token;
        }
        if (token.StartsWith("KC_KP_")) return "P" + token[6..];
        if (token.StartsWith("KC_")) return token[3..];
        return token; // MO(1), LT(1,KC_A) 等はそのまま
    }
}
