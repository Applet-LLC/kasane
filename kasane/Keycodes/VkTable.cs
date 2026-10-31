namespace Kasane.Keycodes;

/// <summary>
/// 仮想キーコード（VK_*）の名前と値（2026-10-31）。Sequence の vk 手順で使う。スキャンコードを持たないキー
/// （VK_IME_ON / VK_IME_OFF など。Microsoft の日本語キーボード向け IME の仕様）を、そのまま送るための表。
/// 名前は VK_ を除いて書く（IME_ON）。VK_IME_ON と書いても受ける。数値は 0x16 か 10進（1〜254）。
/// </summary>
public static class VkTable
{
    public const ushort ImeOn = 0x16, ImeOff = 0x1A;

    // 修飾キーは左右を区別して送る。名前だけの SHIFT / CTRL / ALT / WIN は左の別名。
    private static readonly (string Name, ushort Vk)[] Modifiers =
    {
        ("LSHIFT", 0xA0), ("RSHIFT", 0xA1), ("LCTRL", 0xA2), ("RCTRL", 0xA3),
        ("LALT", 0xA4), ("RALT", 0xA5), ("LWIN", 0x5B), ("RWIN", 0x5C),
        ("SHIFT", 0xA0), ("CTRL", 0xA2), ("ALT", 0xA4), ("WIN", 0x5B),
    };

    private static readonly Dictionary<string, ushort> Names = BuildNames();

    private static Dictionary<string, ushort> BuildNames()
    {
        var d = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            // 日本語 IME・かな漢字の切り替え
            ["IME_ON"] = 0x16, ["IME_OFF"] = 0x1A,
            ["KANA"] = 0x15, ["HANGUL"] = 0x15, ["JUNJA"] = 0x17, ["FINAL"] = 0x18,
            ["KANJI"] = 0x19, ["HANJA"] = 0x19,
            ["CONVERT"] = 0x1C, ["NONCONVERT"] = 0x1D, ["ACCEPT"] = 0x1E, ["MODECHANGE"] = 0x1F,
            ["PROCESSKEY"] = 0xE5,
            ["DBE_ALPHANUMERIC"] = 0xF0, ["DBE_KATAKANA"] = 0xF1, ["DBE_HIRAGANA"] = 0xF2,
            ["DBE_SBCSCHAR"] = 0xF3, ["DBE_DBCSCHAR"] = 0xF4, ["DBE_ROMAN"] = 0xF5, ["DBE_NOROMAN"] = 0xF6,
            ["DBE_ENTERWORDREGISTERMODE"] = 0xF7, ["DBE_ENTERIMECONFIGMODE"] = 0xF8, ["DBE_FLUSHSTRING"] = 0xF9,
            ["DBE_CODEINPUT"] = 0xFA, ["DBE_NOCODEINPUT"] = 0xFB, ["DBE_DETERMINESTRING"] = 0xFC,
            ["DBE_ENTERDLGCONVERSIONMODE"] = 0xFD,
            // よく使う編集・移動のキー
            ["BACK"] = 0x08, ["TAB"] = 0x09, ["RETURN"] = 0x0D, ["ESCAPE"] = 0x1B, ["SPACE"] = 0x20,
            ["PRIOR"] = 0x21, ["NEXT"] = 0x22, ["END"] = 0x23, ["HOME"] = 0x24,
            ["LEFT"] = 0x25, ["UP"] = 0x26, ["RIGHT"] = 0x27, ["DOWN"] = 0x28,
            ["INSERT"] = 0x2D, ["DELETE"] = 0x2E,
            ["APPS"] = 0x5D,
        };
        for (int i = 1; i <= 24; i++) d["F" + i] = (ushort)(0x6F + i);
        foreach (var (name, vk) in Modifiers) d[name] = vk;
        return d;
    }

    /// <summary>メニュー・説明用の、日本語 IME 系の名前（VK_ なし）。</summary>
    public static readonly IReadOnlyList<string> ImeKeyNames = new[]
    {
        "IME_ON", "IME_OFF", "KANJI", "KANA", "CONVERT", "NONCONVERT", "ACCEPT", "MODECHANGE",
        "DBE_ALPHANUMERIC", "DBE_KATAKANA", "DBE_HIRAGANA", "DBE_SBCSCHAR", "DBE_DBCSCHAR", "DBE_ROMAN", "DBE_NOROMAN",
    };

    /// <summary>名前（VK_ は付けても付けなくてもよい）か数値（0x16 / 22）を VK にする。0 と 255 以上は不可。</summary>
    public static bool TryResolveKey(string? token, out ushort vk)
    {
        vk = 0;
        string t = (token ?? "").Trim();
        if (t.Length == 0) return false;
        if (t.StartsWith("VK_", StringComparison.OrdinalIgnoreCase)) t = t[3..];
        if (Names.TryGetValue(t, out vk)) return true;

        int value;
        bool ok = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(t[2..], System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out value)
            : int.TryParse(t, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value);
        if (!ok || value < 1 || value > 254) return false;
        vk = (ushort)value;
        return true;
    }

    /// <summary>"LSHIFT+IME_ON" のように、+ でつないだ修飾キー（左右の VK）と、本体のキー。修飾キーは書いた順。</summary>
    public static bool TryResolve(string? token, out ushort[] modifiers, out ushort vk)
    {
        modifiers = Array.Empty<ushort>();
        vk = 0;
        var parts = (token ?? "").Split('+');
        if (parts.Length == 0 || parts.Length > 5) return false;
        var mods = new List<ushort>();
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!TryResolveModifier(parts[i], out ushort m)) return false;
            mods.Add(m);
        }
        if (!TryResolveKey(parts[^1], out vk)) return false;
        modifiers = mods.ToArray();
        return true;
    }

    private static bool TryResolveModifier(string name, out ushort vk)
    {
        string t = name.Trim();
        if (t.StartsWith("VK_", StringComparison.OrdinalIgnoreCase)) t = t[3..];
        foreach (var (n, v) in Modifiers)
            if (string.Equals(n, t, StringComparison.OrdinalIgnoreCase)) { vk = v; return true; }
        vk = 0;
        return false;
    }

    /// <summary>SendInput で KEYEVENTF_EXTENDEDKEY を付けるべきキーか。</summary>
    public static bool IsExtended(ushort vk) => vk is
        0xA3 or 0xA5 or 0x5B or 0x5C or 0x5D          // 右Ctrl・右Alt・Win・Apps
        or (>= 0x21 and <= 0x28)                       // PgUp/PgDn/End/Home/矢印
        or 0x2D or 0x2E or 0x6F or 0x90;               // Insert/Delete/テンキーの/ NumLock
}
