namespace Kasane.Keycodes;

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
        // F13–F24 は物理キーが無くても出力できる（2026-10-08）。
        new("ファンクション", Enumerable.Range(1, 24).Select(i => $"KC_F{i}").ToArray()),
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
        // ブラウザー・アプリ起動・電源のキーも、ここに並べる（2026-10-08。カテゴリの数を変えないため。
        // 表に名前の無いキーは、キーコード欄に SC_XXXX と書けば出力できる）。
        new("メディア・システム", new[]
        {
            "KC_MUTE", "KC_VOLD", "KC_VOLU", "KC_MPLY", "KC_MSTP", "KC_MNXT", "KC_MPRV", "KC_CALC",
            "KC_WSCH", "KC_WHOM", "KC_WBAK", "KC_WFWD", "KC_WSTP", "KC_WREF", "KC_WFAV",
            "KC_MAIL", "KC_MSEL", "KC_MYCM", "KC_SLEP", "KC_WAKE", "KC_PWR",
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
        ["KC_WSCH"] = "Search", ["KC_WHOM"] = "WebHome", ["KC_WBAK"] = "Back", ["KC_WFWD"] = "Fwd",
        ["KC_WSTP"] = "WebStop", ["KC_WREF"] = "Reload", ["KC_WFAV"] = "Fav", ["KC_MAIL"] = "Mail",
        ["KC_MSEL"] = "Media", ["KC_MYCM"] = "PC", ["KC_SLEP"] = "⚠Slp", ["KC_WAKE"] = "⚠Wake",
        ["KC_PWR"] = "⚠Pwr",
    };

    // 電源キー（KC_PWR / KC_SLEP / KC_WAKE）のスキャンコード。押すとPCがシャットダウン・スリープしうる。
    private static readonly ushort[] PowerScanCodes = { 0xE05E, 0xE05F, 0xE063 };

    /// <summary>
    /// 押すとPCの状態が変わりうる電源キー（単独・修飾付き、名前でも SC_XXXX でも）か。
    /// キー割り当て画面が、割り当ての前に確認を出すのに使う（2026-10-08、P1の試験の所見）。
    /// </summary>
    public static bool IsPowerKey(string token) =>
        KeycodeTable.TryResolve(token, out var a) &&
        (a.Type is ActionType.Key or ActionType.ModdedKey) &&
        Array.IndexOf(PowerScanCodes, a.Param2) >= 0;

    /// <summary>キートップに表示する短い名前。別名は正式名に寄せてから引く。</summary>
    public static string ShortName(string token)
    {
        if (ShortNames.TryGetValue(token, out var s)) return s;
        // 修飾付きキーは「^C」「⇧1」「^⇧T」のように、修飾キーの記号 + キー名で表す。
        // LMは「LM1^」のように、レイヤー番号 + 修飾キーの記号。
        if (KeycodeTable.TryResolve(token, out var modded))
        {
            if (modded.Type == ActionType.ModdedKey)
            {
                var baseName = KeycodeTable.NameForScanCode(modded.Param2);
                return ModSymbols(modded.Param1) + (baseName != null ? ShortName(baseName) : "?");
            }
            if (modded.Type == ActionType.LayerMod)
            {
                return $"LM{modded.Param1}" + ModSymbols((byte)(modded.Param2 & 0xFF));
            }
            if (modded.Type == ActionType.OneShotMod)
            {
                return "OS" + ModSymbols(modded.Param1);
            }
        }
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

    /// <summary>
    /// 修飾子マスク → 記号（Ctrl=^、Shift=⇧、Alt=⌥、Win=⊞。右側は前に「R」を付ける）。
    /// </summary>
    public static string ModSymbols(byte mask)
    {
        string[] sym = { "^", "⇧", "⌥", "⊞" };
        var sb = new System.Text.StringBuilder();
        for (int bit = 0; bit < 8; bit++)
        {
            if ((mask & (1 << bit)) == 0) continue;
            if (bit >= 4) sb.Append('R');
            sb.Append(sym[bit % 4]);
        }
        return sb.ToString();
    }
}
