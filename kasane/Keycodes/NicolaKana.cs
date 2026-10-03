namespace Kasane.Keycodes;

/// <summary>
/// NICOLAの「かな」→ 送出するキー（スキャンコード + Shiftの要否）の変換。
/// driver/KeymapTool/NicolaTables.cpp の移植。表は tools/Check-NicolaTables.ps1 でC++側と照合する
/// （どちらかを変えたら実行すること）。
/// </summary>
public static class NicolaKana
{
    public const ushort LeftShift = 0x002A;

    public readonly record struct KeyStroke(ushort ScanCode, bool Shift);

    // --- ローマ字入力用 ---
    // IMEのローマ字かな変換に渡すASCII列。英字と - . , / だけを使う（これらはUS/JIS配列で
    // 同じ物理キーなので、OS側のキーボードレイアウトに依存しない）。
    private static readonly (string Kana, string Romaji)[] RomajiTable =
    {
        ("あ", "a"),   ("い", "i"),   ("う", "u"),   ("え", "e"),   ("お", "o"),
        ("か", "ka"),  ("き", "ki"),  ("く", "ku"),  ("け", "ke"),  ("こ", "ko"),
        ("さ", "sa"),  ("し", "si"),  ("す", "su"),  ("せ", "se"),  ("そ", "so"),
        ("た", "ta"),  ("ち", "ti"),  ("つ", "tu"),  ("て", "te"),  ("と", "to"),
        ("な", "na"),  ("に", "ni"),  ("ぬ", "nu"),  ("ね", "ne"),  ("の", "no"),
        ("は", "ha"),  ("ひ", "hi"),  ("ふ", "hu"),  ("へ", "he"),  ("ほ", "ho"),
        ("ま", "ma"),  ("み", "mi"),  ("む", "mu"),  ("め", "me"),  ("も", "mo"),
        ("や", "ya"),  ("ゆ", "yu"),  ("よ", "yo"),
        ("ら", "ra"),  ("り", "ri"),  ("る", "ru"),  ("れ", "re"),  ("ろ", "ro"),
        ("わ", "wa"),  ("を", "wo"),  ("ん", "nn"),
        ("が", "ga"),  ("ぎ", "gi"),  ("ぐ", "gu"),  ("げ", "ge"),  ("ご", "go"),
        ("ざ", "za"),  ("じ", "zi"),  ("ず", "zu"),  ("ぜ", "ze"),  ("ぞ", "zo"),
        ("だ", "da"),  ("ぢ", "di"),  ("づ", "du"),  ("で", "de"),  ("ど", "do"),
        ("ば", "ba"),  ("び", "bi"),  ("ぶ", "bu"),  ("べ", "be"),  ("ぼ", "bo"),
        ("ぱ", "pa"),  ("ぴ", "pi"),  ("ぷ", "pu"),  ("ぺ", "pe"),  ("ぽ", "po"),
        ("ヴ", "vu"),  ("ゔ", "vu"),
        ("ぁ", "xa"),  ("ぃ", "xi"),  ("ぅ", "xu"),  ("ぇ", "xe"),  ("ぉ", "xo"),
        ("ゃ", "xya"), ("ゅ", "xyu"), ("ょ", "xyo"), ("っ", "xtu"), ("ゎ", "xwa"),
        ("ー", "-"),   ("。", "."),   ("、", ","),   ("・", "/"),
        // 全角の ，／． はローマ字入力では直接打てない。IMEの句読点設定（「，．」等）に
        // 従って変換される , と . を送る。
        ("，", ","),   ("．", "."),
    };

    private const string Letters = "abcdefghijklmnopqrstuvwxyz";
    private static readonly ushort[] LetterCodes =
    {
        0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32,
        0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C,
    };

    private static bool AsciiToStroke(char c, out KeyStroke stroke)
    {
        int i = Letters.IndexOf(c);
        if (i >= 0)
        {
            stroke = new KeyStroke(LetterCodes[i], false);
            return true;
        }
        ushort? sc = c switch
        {
            '-' => 0x0C,
            '.' => 0x34,
            ',' => 0x33,
            '/' => 0x35,
            _ => null,
        };
        stroke = new KeyStroke(sc ?? 0, false);
        return sc != null;
    }

    private static bool RomajiStrokes(string kana, List<KeyStroke> output)
    {
        foreach (var (k, romaji) in RomajiTable)
        {
            if (k != kana) continue;
            foreach (char c in romaji)
            {
                if (!AsciiToStroke(c, out var s)) return false;
                output.Add(s);
            }
            return true;
        }
        return false;
    }

    // --- JISかな入力用 ---
    // JISかな配列（Set1スキャンコード）。濁音・半濁音は清音 + ゛(0x1A) / ゜(0x1B) で表す。
    private static readonly (string Kana, ushort ScanCode, bool Shift)[] JisKanaTable =
    {
        ("ぬ", 0x02, false), ("ふ", 0x03, false), ("あ", 0x04, false), ("う", 0x05, false),
        ("え", 0x06, false), ("お", 0x07, false), ("や", 0x08, false), ("ゆ", 0x09, false),
        ("よ", 0x0A, false), ("わ", 0x0B, false), ("ほ", 0x0C, false), ("へ", 0x0D, false),
        ("ー", 0x7D, false),
        ("ぁ", 0x04, true),  ("ぅ", 0x05, true),  ("ぇ", 0x06, true),  ("ぉ", 0x07, true),
        ("ゃ", 0x08, true),  ("ゅ", 0x09, true),  ("ょ", 0x0A, true),  ("を", 0x0B, true),
        ("た", 0x10, false), ("て", 0x11, false), ("い", 0x12, false), ("す", 0x13, false),
        ("か", 0x14, false), ("ん", 0x15, false), ("な", 0x16, false), ("に", 0x17, false),
        ("ら", 0x18, false), ("せ", 0x19, false), ("゛", 0x1A, false), ("゜", 0x1B, false),
        ("ぃ", 0x12, true),
        ("ち", 0x1E, false), ("と", 0x1F, false), ("し", 0x20, false), ("は", 0x21, false),
        ("き", 0x22, false), ("く", 0x23, false), ("ま", 0x24, false), ("の", 0x25, false),
        ("り", 0x26, false), ("れ", 0x27, false), ("け", 0x28, false), ("む", 0x2B, false),
        ("つ", 0x2C, false), ("さ", 0x2D, false), ("そ", 0x2E, false), ("ひ", 0x2F, false),
        ("こ", 0x30, false), ("み", 0x31, false), ("も", 0x32, false), ("ね", 0x33, false),
        ("る", 0x34, false), ("め", 0x35, false), ("ろ", 0x73, false),
        ("っ", 0x2C, true),  ("、", 0x33, true),  ("。", 0x34, true),  ("・", 0x35, true),
        // 全角の ，／． はかな入力では直接打てないため、句読点で代用する。
        ("，", 0x33, true),  ("．", 0x34, true),
    };

    private static readonly (string Kana, string Base, bool Handakuten)[] DakutenTable =
    {
        ("が", "か", false), ("ぎ", "き", false), ("ぐ", "く", false), ("げ", "け", false), ("ご", "こ", false),
        ("ざ", "さ", false), ("じ", "し", false), ("ず", "す", false), ("ぜ", "せ", false), ("ぞ", "そ", false),
        ("だ", "た", false), ("ぢ", "ち", false), ("づ", "つ", false), ("で", "て", false), ("ど", "と", false),
        ("ば", "は", false), ("び", "ひ", false), ("ぶ", "ふ", false), ("べ", "へ", false), ("ぼ", "ほ", false),
        ("ぱ", "は", true),  ("ぴ", "ひ", true),  ("ぷ", "ふ", true),  ("ぺ", "へ", true),  ("ぽ", "ほ", true),
        ("ヴ", "う", false), ("ゔ", "う", false),
    };

    private static bool JisKanaStroke(string kana, List<KeyStroke> output)
    {
        foreach (var (k, sc, shift) in JisKanaTable)
        {
            if (k != kana) continue;
            output.Add(new KeyStroke(sc, shift));
            return true;
        }
        return false;
    }

    private static bool JisKanaStrokes(string kana, List<KeyStroke> output)
    {
        if (JisKanaStroke(kana, output)) return true;
        foreach (var (k, b, handakuten) in DakutenTable)
        {
            if (k != kana) continue;
            return JisKanaStroke(b, output) && JisKanaStroke(handakuten ? "゜" : "゛", output);
        }
        return false;
    }

    /// <summary>
    /// 文字列（通常は1文字）を送出ステップ列（スキャンコード, 解放か）に変換する。変換できない文字が
    /// あれば false を返し、badChar にその文字を入れる。jisKana=false はローマ字入力。
    /// </summary>
    public static bool TextToSteps(string text, bool jisKana, List<(ushort ScanCode, bool IsBreak)> steps, out string badChar)
    {
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            string ch = e.GetTextElement();
            var strokes = new List<KeyStroke>();
            bool ok = jisKana ? JisKanaStrokes(ch, strokes) : RomajiStrokes(ch, strokes);
            if (!ok)
            {
                badChar = ch;
                return false;
            }
            foreach (var s in strokes)
            {
                if (s.Shift) steps.Add((LeftShift, false));
                steps.Add((s.ScanCode, false));
                steps.Add((s.ScanCode, true));
                if (s.Shift) steps.Add((LeftShift, true));
            }
        }
        badChar = "";
        return true;
    }
}
