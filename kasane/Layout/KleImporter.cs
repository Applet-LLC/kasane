using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kasane.Keycodes;
using Loc = Kasane.Model.Loc;

namespace Kasane.Layout;

/// <summary>取り込んだキーの、既定キーコードの状態。</summary>
public enum KleKeyStatus
{
    /// <summary>ファイルに書かれていた、または、ラベルから一意に決まる（英字・最上段の数字・ファンクション・名前のあるキー）。</summary>
    Confirmed,
    /// <summary>位置や配列の種類から推定した（記号・左右の修飾キー・スペース・テンキー・日本語配列のキー）。</summary>
    Inferred,
    /// <summary>決まらなかった。利用者が選ぶ。</summary>
    Unassigned,
}

/// <param name="Slot">スロット番号（<see cref="KeyboardLayout.Load"/> と同じ、キーを平らにした順）。</param>
/// <param name="Label">図に出すラベル（凡例をまとめたもの）。</param>
public sealed record KleKeyResult(int Slot, string Label, double X, double Y, double W, double H,
    string? Keycode, KleKeyStatus Status, string Note);

public sealed class KleImportResult
{
    public IReadOnlyList<KleKeyResult> Keys { get; init; } = Array.Empty<KleKeyResult>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    /// <summary>回転したキー（r/rx/ry）がある。kasane は対応しないので、取り込めない。</summary>
    public bool HasRotation { get; init; }
    /// <summary>日本語配列（¥・無変換・変換・カナ・半角/全角のキー）とみなした。</summary>
    public bool IsJis { get; init; }
    public bool CanSave => !HasRotation && Keys.Count > 0 && Keys.Count <= KbdLayerLimits.MaxSlots;
}

internal static class KbdLayerLimits
{
    public const int MaxSlots = 256; // KBDLAYER_MAX_KEYS
}

/// <summary>
/// Keyboard Layout Editor（KLE）の生データ（Raw data / Download JSON）を、kasane のレイアウトとして使える形にする。
///  - 各キーの既定キーコードを、凡例のインデックス 4 に書く（同梱の Layouts\*.json と同じ。<see cref="KeyboardLayout.Load"/> が読む）。
///    インデックス 4 は、KLE の文字配置 <c>a</c> がどれでも「前面の凡例」（9 か 10）に当たるので、<c>a</c> は書き換えない。
///  - キーコードは、凡例（ラベル）と位置から推定する。決まらないものは未割り当てにして、利用者に選んでもらう。
///  - 回転したキー（r/rx/ry）は対応しない（取り込めない）。
/// 文字配置 <c>a</c> の並び替えの規則（<see cref="LabelMap"/>）は、KLE のファイル形式の仕様。
/// </summary>
public static class KleImporter
{
    // 凡例の位置（KLE の 12 か所。行ごとに: 0 左上 1 上中 2 右上 / 3 左中 4 中 5 右中 / 6 左下 7 下中 8 右下 / 9 前面左 10 前面中 11 前面右）。
    // LabelMap[a][n] = 文字列の n 番目の凡例が出る位置（-1 は、その配置では出ない）。a は文字配置（0〜7。既定は 4）。
    private static readonly int[][] LabelMap =
    {
        new[] { 0, 6, 2, 8, 9, 11, 3, 5, 1, 4, 7, 10 }, // 0
        new[] { 1, 7, -1, -1, 9, 11, 4, -1, -1, -1, -1, 10 }, // 1 中央（横）
        new[] { 3, -1, 5, -1, 9, 11, -1, -1, 4, -1, -1, 10 }, // 2 中央（縦）
        new[] { 4, -1, -1, -1, 9, 11, -1, -1, -1, -1, -1, 10 }, // 3 中央（縦横）
        new[] { 0, 6, 2, 8, 10, -1, 3, 5, 1, 4, 7, -1 }, // 4 前面は中央（既定）
        new[] { 1, 7, -1, -1, 10, -1, 4, -1, -1, -1, -1, -1 }, // 5
        new[] { 3, -1, 5, -1, 10, -1, -1, -1, 4, -1, -1, -1 }, // 6
        new[] { 4, -1, -1, -1, 10, -1, -1, -1, -1, -1, -1, -1 }, // 7
    };

    // 基準の凡例を探す順（下側を優先する。KLE では、下が Shift なしの字、上が Shift 付きの字）。
    private static readonly int[] PositionPriority = { 6, 7, 8, 4, 3, 5, 0, 1, 2, 9, 10, 11 };

    private const int KeycodeIndex = 4;

    private sealed class WalkedKey
    {
        public int Slot;
        public JsonArray Row = null!;
        public int ItemIndex;
        public string Raw = "";
        public double X, Y, W = 1, H = 1;
        public int Align = 4;
    }

    // KeyboardLayout.Load と同じ規則でキーを数える（スロット番号を一致させるため）。
    private static List<WalkedKey> Walk(JsonNode? root, out bool rotation)
    {
        rotation = false;
        if (root is not JsonArray rows) throw new FormatException(Loc.T("KLE の JSON は、行の配列である必要があります(Raw data のまま)。", "The KLE JSON must be an array of rows (use the Raw data as is)."));
        var keys = new List<WalkedKey>();
        double cy = 0;
        int align = 4;
        foreach (var rowNode in rows)
        {
            if (rowNode is not JsonArray row) continue; // 先頭のメタデータ
            double cx = 0, w = 1, h = 1;
            bool decal = false;
            for (int i = 0; i < row.Count; i++)
            {
                var item = row[i];
                if (item is JsonObject o)
                {
                    if (Num(o, "x") is double x) cx += x;
                    if (Num(o, "y") is double y) cy += y;
                    if (Num(o, "w") is double ww) w = ww;
                    if (Num(o, "h") is double hh) h = hh;
                    if (Num(o, "a") is double a) align = Math.Clamp((int)a, 0, 7);
                    if (o.TryGetPropertyValue("d", out var d) && d is JsonValue dv && dv.TryGetValue<bool>(out var db) && db) decal = true;
                    if (o.ContainsKey("r") || o.ContainsKey("rx") || o.ContainsKey("ry")) rotation = true;
                    continue;
                }
                if (item is not JsonValue v || !v.TryGetValue<string>(out var s)) continue;
                if (!decal)
                    keys.Add(new WalkedKey { Slot = keys.Count, Row = row, ItemIndex = i, Raw = s ?? "", X = cx, Y = cy, W = w, H = h, Align = align });
                cx += w;
                w = 1; h = 1;
                decal = false;
            }
            cy += 1;
        }
        return keys;
    }

    private static double? Num(JsonObject o, string name) =>
        o.TryGetPropertyValue(name, out var n) && n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    // ---- 解析 ----

    /// <summary>KLE の JSON を読み、各キーの既定キーコードを推定する。形式が違えば <see cref="FormatException"/>。</summary>
    public static KleImportResult Analyze(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException ex) { throw new FormatException(Loc.T(("JSON として読めません: " + ex.Message), ("Cannot read it as JSON: " + ex.Message)), ex); }
        var keys = Walk(root, out bool rotation);
        var warnings = new List<string>();
        if (rotation) warnings.Add(Loc.T("回転したキー(r / rx / ry)があります。kasane は回転に対応しないので、取り込めません(KLE で回転を外してください)。", "The layout has rotated keys (r / rx / ry). kasane does not support rotation, so it cannot be imported (remove the rotation in KLE)."));
        if (keys.Count == 0) warnings.Add(Loc.T("キーがありません。", "There are no keys."));
        if (keys.Count > KbdLayerLimits.MaxSlots) warnings.Add(Loc.T($"キーが {keys.Count} 個あります。上限は {KbdLayerLimits.MaxSlots} 個です。", $"The layout has {keys.Count} keys. The limit is {KbdLayerLimits.MaxSlots}."));

        var views = keys.Select(k => new KeyView(k)).ToList();
        bool isJis = views.Any(v => v.Legends.Any(l => IsJisLegend(l.Text)));
        var ctx = new Context(views, isJis);

        var results = new List<KleKeyResult>();
        foreach (var v in views)
        {
            var (kc, status, note) = Resolve(v, ctx);
            results.Add(new KleKeyResult(v.Key.Slot, v.PrintedLabel, v.Key.X, v.Key.Y, v.Key.W, v.Key.H, kc, status, note));
        }

        foreach (var g in results.Where(r => r.Keycode != null).GroupBy(r => r.Keycode!).Where(g => g.Count() > 1))
            warnings.Add(Loc.T($"{g.Key} が {g.Count()} 個のキーに割り当てられています(スロット {string.Join("、", g.Select(r => "#" + (r.Slot + 1)))})。", $"{g.Key} is assigned to {g.Count()} keys (slots {string.Join(", ", g.Select(r => "#" + (r.Slot + 1)))})."));
        int unassigned = results.Count(r => r.Status == KleKeyStatus.Unassigned);
        if (unassigned > 0) warnings.Add(Loc.T($"キーコードが決まらなかったキーが {unassigned} 個あります。画面で選んでください(選ばなくても保存できます)。", $"{unassigned} keys have no key code yet. Choose them on this screen (you can also save without choosing)."));

        return new KleImportResult { Keys = results, Warnings = warnings, HasRotation = rotation, IsJis = isJis };
    }

    /// <summary>
    /// 元の JSON の、各キーの凡例インデックス 4 に、キーコードを書いた JSON を返す（他の内容は、そのまま）。
    /// <paramref name="keycodes"/> は、スロット番号 → キーコード。無い（または空の）キーは、元のまま。
    /// </summary>
    public static string Apply(string json, IReadOnlyDictionary<int, string> keycodes)
    {
        JsonNode? root = JsonNode.Parse(json);
        var keys = Walk(root, out bool rotation);
        if (rotation) throw new FormatException(Loc.T("回転したキーがあるので、取り込めません。", "The layout has rotated keys, so it cannot be imported."));
        foreach (var k in keys)
        {
            if (!keycodes.TryGetValue(k.Slot, out var kc) || string.IsNullOrWhiteSpace(kc)) continue;
            if (!KeycodeTable.TryResolveBaseKey(kc, out _)) throw new FormatException(Loc.T($"スロット #{k.Slot + 1} のキーコード「{kc}」が分かりません。", $"Unknown key code \"{kc}\" in slot #{k.Slot + 1}."));
            var parts = k.Raw.Split('\n').ToList();
            while (parts.Count <= KeycodeIndex) parts.Add("");
            parts[KeycodeIndex] = kc;
            k.Row[k.ItemIndex] = JsonValue.Create(string.Join("\n", parts));
        }
        return root!.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>名前（<c>kle-&lt;名前&gt;.json</c> の &lt;名前&gt;）として使えるか。英数字とハイフン、1〜32 文字。</summary>
    public static bool IsValidName(string? name) => name != null && Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9\-]{0,31}$");

    // ---- 凡例の読み取り ----

    private sealed record Legend(int Position, string Text);

    private sealed class KeyView
    {
        public WalkedKey Key { get; }
        public List<Legend> Legends { get; } = new(); // 優先順（下側から）
        public string? Existing { get; }              // すでに書かれているキーコード
        public string? FrontText { get; }             // 書かれていたが、キーコードではない前面の凡例
        public string PrintedLabel { get; }

        public KeyView(WalkedKey key)
        {
            Key = key;
            var raw = key.Raw.Split('\n');
            if (raw.Length > KeycodeIndex)
            {
                var front = Clean(raw[KeycodeIndex]);
                if (front.Length > 0)
                {
                    if (KeycodeTable.TryResolveBaseKey(front, out _)) Existing = front;
                    else FrontText = front;
                }
            }
            var map = LabelMap[key.Align];
            var found = new List<Legend>();
            for (int i = 0; i < raw.Length && i < map.Length; i++)
            {
                if (i == KeycodeIndex && Existing != null) continue;
                var text = Clean(raw[i]);
                if (text.Length == 0 || map[i] < 0) continue;
                found.Add(new Legend(map[i], text));
            }
            Legends = found.OrderBy(l => Array.IndexOf(PositionPriority, l.Position)).ToList();
            PrintedLabel = string.Join(" / ", Legends.Select(l => l.Text));
        }
    }

    // KLE の凡例は、HTML（<br> や <i>）や実体参照を含みうる。
    private static string Clean(string s)
    {
        s = Regex.Replace(s, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", "");
        return WebUtility.HtmlDecode(s).Trim();
    }

    private static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), @"[\s　]", "");

    // ---- キーコードの推定 ----

    private sealed class Context
    {
        public List<KeyView> Views { get; }
        public bool IsJis { get; }
        public double MidX { get; }
        public double? NumPadX { get; }
        public double? NumPadY { get; }

        public Context(List<KeyView> views, bool isJis)
        {
            Views = views;
            IsJis = isJis;
            if (views.Count > 0) MidX = (views.Min(v => v.Key.X) + views.Max(v => v.Key.X + v.Key.W)) / 2;
            var num = views.FirstOrDefault(v => v.Legends.Any(l => NumLockNames.Contains(Norm(l.Text))));
            if (num != null) { NumPadX = num.Key.X; NumPadY = num.Key.Y; }
        }

        public bool InNumPad(KeyView v) => NumPadX != null && v.Key.X >= NumPadX - 0.01 && v.Key.Y >= NumPadY - 0.01;

        private static bool SameRow(KeyView a, KeyView b) => Math.Abs(a.Key.Y - b.Key.Y) < 0.01;

        /// <summary>左右のある修飾キーが、左側か。同じ行にスペースバーがあれば、その左右。無ければ、同じ行の同じ種類のキーの両端。それも無ければ、配列の真ん中より左か。</summary>
        public bool IsLeftSide(KeyView v, int family)
        {
            double center = v.Key.X + v.Key.W / 2;
            var row = Views.Where(x => SameRow(x, v)).ToList();
            var space = row.FirstOrDefault(IsSpaceLike);
            if (space != null) return center < space.Key.X + space.Key.W / 2;
            var same = row.Where(x => x.Legends.Any(l => FamilyOf(Norm(l.Text)) == family)).OrderBy(x => x.Key.X).ToList();
            if (same.Count >= 2)
                return ReferenceEquals(same[0], v) || (!ReferenceEquals(same[^1], v) && center < (same[0].Key.X + same[^1].Key.X + same[^1].Key.W) / 2);
            return center < MidX;
        }
    }

    private static bool IsSpaceLike(KeyView v) => (v.Legends.Count == 0 && v.Key.W >= 2.5) || v.Legends.Any(l => Norm(l.Text) == "space");

    private static readonly HashSet<string> NumLockNames = new() { "numlock", "numlk", "nmlk" };

    private static (string? Kc, KleKeyStatus Status, string Note) Resolve(KeyView v, Context ctx)
    {
        string note = v.FrontText != null ? Loc.T($"前面の凡例「{v.FrontText}」は、キーコードで置き換わります。", $"The front legend \"{v.FrontText}\" will be replaced by the key code.") : "";
        if (v.Existing != null) return (v.Existing, KleKeyStatus.Confirmed, Loc.T("ファイルに書かれていたキーコード", "Key code written in the file"));

        if (ctx.InNumPad(v) && ResolveNumPad(v) is { } kp) return (kp, KleKeyStatus.Inferred, Join(Loc.T("テンキー", "Numpad"), note));

        foreach (var l in v.Legends)
        {
            if (ResolveNamed(l.Text, v, ctx) is { } named) return (named.Kc, named.Status, Join(named.Note, note));
        }
        foreach (var l in v.Legends)
        {
            if (ResolveSymbol(l.Text, ctx.IsJis) is { } sym) return (sym, KleKeyStatus.Inferred, Join(ctx.IsJis ? Loc.T("記号(日本語配列として)", "Symbol (as a Japanese layout)") : Loc.T("記号(US 配列として)", "Symbol (as a US layout)"), note));
        }
        if (IsSpaceLike(v))
            return ("KC_SPACE", KleKeyStatus.Inferred, Join(Loc.T("凡例の無い幅の広いキー(スペースバーとして)", "Wide key with no legend (treated as the space bar)"), note));
        return (null, KleKeyStatus.Unassigned, Join(Loc.T("ラベルから決まりませんでした", "Could not be determined from the label"), note));
    }

    private static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + " " + b;

    private static (string Kc, KleKeyStatus Status, string Note)? ResolveNamed(string text, KeyView v, Context ctx)
    {
        string n = Norm(text);
        if (n.Length == 1 && char.IsAsciiLetter(n[0])) return ($"KC_{char.ToUpperInvariant(n[0])}", KleKeyStatus.Confirmed, "");
        if (n.Length == 1 && char.IsAsciiDigit(n[0])) return ($"KC_{n[0]}", KleKeyStatus.Confirmed, Loc.T("最上段の数字", "Number row digit"));
        var fm = Regex.Match(n, @"^f(\d{1,2})$");
        if (fm.Success && int.Parse(fm.Groups[1].Value) is >= 1 and <= 24) return ($"KC_F{fm.Groups[1].Value}", KleKeyStatus.Confirmed, "");

        if (Named.TryGetValue(n, out var kc)) return (kc, KleKeyStatus.Confirmed, "");

        // 左右がある修飾キー。ラベルに左右があればそれ、無ければ、キーの位置から決める。
        foreach (var (stems, left, right) in Modifiers)
        {
            string? side = null;
            if (n.StartsWith("left", StringComparison.Ordinal) && stems.Contains(n[4..])) side = "L";
            else if (n.StartsWith("right", StringComparison.Ordinal) && stems.Contains(n[5..])) side = "R";
            else if (n.StartsWith("l", StringComparison.Ordinal) && stems.Contains(n[1..])) side = "L";
            else if (n.StartsWith("r", StringComparison.Ordinal) && stems.Contains(n[1..])) side = "R";
            if (side != null) return (side == "L" ? left : right, KleKeyStatus.Confirmed, "");
        }
        if (FamilyOf(n) is int fam)
            return (ctx.IsLeftSide(v, fam) ? Modifiers[fam].Left : Modifiers[fam].Right, KleKeyStatus.Inferred, Loc.T("左右は、キーの位置から決めました", "Left/right decided from the key position"));
        if (n is "altgr") return ("KC_RALT", KleKeyStatus.Confirmed, "");
        return null;
    }

    private static readonly Dictionary<string, string> Named = new()
    {
        ["esc"] = "KC_ESC", ["escape"] = "KC_ESC", ["tab"] = "KC_TAB",
        ["capslock"] = "KC_CAPSLOCK", ["caps"] = "KC_CAPSLOCK",
        ["backspace"] = "KC_BSPACE", ["bs"] = "KC_BSPACE", ["bksp"] = "KC_BSPACE",
        ["enter"] = "KC_ENTER", ["return"] = "KC_ENTER",
        ["space"] = "KC_SPACE",
        ["insert"] = "KC_INSERT", ["ins"] = "KC_INSERT", ["delete"] = "KC_DELETE", ["del"] = "KC_DELETE",
        ["home"] = "KC_HOME", ["end"] = "KC_END",
        ["pgup"] = "KC_PGUP", ["pageup"] = "KC_PGUP", ["pgdn"] = "KC_PGDOWN", ["pgdown"] = "KC_PGDOWN", ["pagedown"] = "KC_PGDOWN",
        ["prtsc"] = "KC_PSCREEN", ["prtscn"] = "KC_PSCREEN", ["prtscr"] = "KC_PSCREEN", ["printscreen"] = "KC_PSCREEN", ["sysrq"] = "KC_PSCREEN",
        ["scrolllock"] = "KC_SCROLLLOCK", ["scrlk"] = "KC_SCROLLLOCK", ["scrlock"] = "KC_SCROLLLOCK",
        ["pause"] = "KC_PAUSE", ["break"] = "KC_PAUSE", ["pausebreak"] = "KC_PAUSE",
        ["numlock"] = "KC_NUMLOCK", ["numlk"] = "KC_NUMLOCK", ["nmlk"] = "KC_NUMLOCK",
        ["menu"] = "KC_APP", ["app"] = "KC_APP", ["apps"] = "KC_APP", ["context"] = "KC_APP",
        ["↑"] = "KC_UP", ["↓"] = "KC_DOWN", ["←"] = "KC_LEFT", ["→"] = "KC_RIGHT",
        ["up"] = "KC_UP", ["down"] = "KC_DOWN", ["left"] = "KC_LEFT", ["right"] = "KC_RIGHT",
        // 日本語配列
        ["無変換"] = "KC_MHEN", ["変換"] = "KC_HENK",
        ["カナ"] = "KC_KANA", ["かな"] = "KC_KANA", ["ひらがな"] = "KC_KANA", ["カタカナ"] = "KC_KANA",
        ["ひらがなカタカナ"] = "KC_KANA", ["カタカナひらがな"] = "KC_KANA", ["カタカナひらがなローマ字"] = "KC_KANA",
        ["半角全角"] = "KC_GRAVE", ["半角/全角"] = "KC_GRAVE", ["半角"] = "KC_GRAVE", ["全角"] = "KC_GRAVE", ["漢字"] = "KC_GRAVE",
        ["半角/全角/漢字"] = "KC_GRAVE", ["半角全角漢字"] = "KC_GRAVE",
        ["¥"] = "KC_JYEN", ["￥"] = "KC_JYEN",
    };

    private static readonly (HashSet<string> Stems, string Left, string Right)[] Modifiers =
    {
        (new HashSet<string> { "shift", "shft", "sft" }, "KC_LSHIFT", "KC_RSHIFT"),
        (new HashSet<string> { "ctrl", "control", "ctl" }, "KC_LCTRL", "KC_RCTRL"),
        (new HashSet<string> { "alt", "option" }, "KC_LALT", "KC_RALT"),
        (new HashSet<string> { "win", "windows", "super", "gui", "meta", "cmd", "command", "⊞", "❖" }, "KC_LGUI", "KC_RGUI"),
    };

    // ラベル（正規化済み）が、どの修飾キーの種類か（左右の指定なし）。
    private static int? FamilyOf(string n)
    {
        for (int i = 0; i < Modifiers.Length; i++)
            if (Modifiers[i].Stems.Contains(n)) return i;
        return null;
    }

    private static bool IsJisLegend(string text)
    {
        string n = Norm(text);
        return n is "¥" or "￥" or "無変換" or "変換" or "カナ" or "かな" or "ひらがな" or "英数"
               || n.Contains("半角") || n.Contains("全角") || n.Contains("ひらがな");
    }

    // 記号のキー（Shift なしの字、Shift 付きの字の順に探す）。US 配列と日本語配列で、同じ字が別のキーになる。
    private static readonly Dictionary<string, string> UsBase = new()
    {
        ["`"] = "KC_GRAVE", ["-"] = "KC_MINUS", ["="] = "KC_EQUAL", ["["] = "KC_LBRACKET", ["]"] = "KC_RBRACKET", ["\\"] = "KC_BSLASH",
        [";"] = "KC_SCOLON", ["'"] = "KC_QUOTE", [","] = "KC_COMMA", ["."] = "KC_DOT", ["/"] = "KC_SLASH",
    };

    private static readonly Dictionary<string, string> UsShifted = new()
    {
        ["~"] = "KC_GRAVE", ["_"] = "KC_MINUS", ["+"] = "KC_EQUAL", ["{"] = "KC_LBRACKET", ["}"] = "KC_RBRACKET", ["|"] = "KC_BSLASH",
        [":"] = "KC_SCOLON", ["\""] = "KC_QUOTE", ["<"] = "KC_COMMA", [">"] = "KC_DOT", ["?"] = "KC_SLASH",
        ["!"] = "KC_1", ["@"] = "KC_2", ["#"] = "KC_3", ["$"] = "KC_4", ["%"] = "KC_5", ["^"] = "KC_6", ["&"] = "KC_7", ["*"] = "KC_8", ["("] = "KC_9", [")"] = "KC_0",
    };

    private static readonly Dictionary<string, string> JisBase = new()
    {
        ["-"] = "KC_MINUS", ["^"] = "KC_EQUAL", ["@"] = "KC_LBRACKET", ["["] = "KC_RBRACKET", [";"] = "KC_SCOLON", [":"] = "KC_QUOTE",
        ["]"] = "KC_BSLASH", [","] = "KC_COMMA", ["."] = "KC_DOT", ["/"] = "KC_SLASH", ["\\"] = "KC_RO", ["¥"] = "KC_JYEN", ["￥"] = "KC_JYEN",
    };

    private static readonly Dictionary<string, string> JisShifted = new()
    {
        ["="] = "KC_MINUS", ["~"] = "KC_EQUAL", ["`"] = "KC_LBRACKET", ["{"] = "KC_RBRACKET", ["+"] = "KC_SCOLON", ["*"] = "KC_QUOTE",
        ["}"] = "KC_BSLASH", ["<"] = "KC_COMMA", [">"] = "KC_DOT", ["?"] = "KC_SLASH", ["_"] = "KC_RO", ["|"] = "KC_JYEN",
        ["!"] = "KC_1", ["\""] = "KC_2", ["#"] = "KC_3", ["$"] = "KC_4", ["%"] = "KC_5", ["&"] = "KC_6", ["'"] = "KC_7", ["("] = "KC_8", [")"] = "KC_9",
    };

    private static string? ResolveSymbol(string text, bool jis)
    {
        string t = text.Trim();
        var basis = jis ? JisBase : UsBase;
        var shifted = jis ? JisShifted : UsShifted;
        if (basis.TryGetValue(t, out var kc)) return kc;
        return shifted.TryGetValue(t, out kc) ? kc : null;
    }

    // テンキー: 数字・記号を優先し、無ければ、ナビゲーションの字（Home、↑ など）から決める。
    private static readonly Dictionary<string, string> KpNav = new()
    {
        ["ins"] = "KC_KP_0", ["insert"] = "KC_KP_0", ["del"] = "KC_KP_DOT", ["delete"] = "KC_KP_DOT",
        ["end"] = "KC_KP_1", ["↓"] = "KC_KP_2", ["pgdn"] = "KC_KP_3", ["pagedown"] = "KC_KP_3",
        ["←"] = "KC_KP_4", ["→"] = "KC_KP_6", ["home"] = "KC_KP_7", ["↑"] = "KC_KP_8", ["pgup"] = "KC_KP_9", ["pageup"] = "KC_KP_9",
    };

    private static string? ResolveNumPad(KeyView v)
    {
        var texts = v.Legends.Select(l => l.Text.Trim().TrimStart('_')).ToList();
        foreach (var t in texts)
            if (t.Length == 1 && char.IsAsciiDigit(t[0])) return $"KC_KP_{t[0]}";
        foreach (var t in texts)
        {
            switch (t)
            {
                case ".": return "KC_KP_DOT";
                case "+": return "KC_KP_PLUS";
                case "-": return "KC_KP_MINUS";
                case "*": return "KC_KP_ASTERISK";
                case "/": return "KC_KP_SLASH";
            }
        }
        foreach (var t in texts)
        {
            string n = Norm(t);
            if (n == "enter") return "KC_KP_ENTER";
            if (n is "numlock" or "numlk" or "nmlk") return "KC_NUMLOCK";
            if (KpNav.TryGetValue(n, out var kc)) return kc;
        }
        return null;
    }
}
