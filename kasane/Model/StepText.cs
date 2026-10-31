using System.Globalization;
using System.Text;

namespace Kasane.Model;

/// <summary>
/// ヘルパー動作の手順（<see cref="HelperStep"/>）の、テキスト表記（2026-10-08、P4）。設定画面の入力欄で編集するための形で、
/// 1行が1手順。空行と、先頭が # の行は無視する。命令は大文字小文字を区別しない。
/// <code>
/// key LCTL(KC_C)        キーを押して離す（"KC_A"、"LCTL(KC_C)"、"SC_E06A" など）
/// down KC_A             キーを押す（修飾付きなら修飾キーも）
/// up KC_A               キーを離す
/// vk IME_ON             仮想キー（VK_*）を押して離す（"IME_ON"、"0x16"、"LSHIFT+F1" など。VK_ は付けなくてよい）
/// vkdown IME_ON         仮想キーを押す。vkup IME_ON は離す
/// text こんにちは\n     文字列を打つ（\n = Enter、\t = Tab、\\ = バックスラッシュ）
/// delay 50              待つ（ミリ秒、0〜10000）
/// mouse left            ボタン（left / right / middle / x1 / x2）をクリック。mouse left down / mouse left up も
/// move 10 -5            マウスを今の位置から動かす
/// moveto 100 200        マウスを画面（仮想デスクトップ）の座標へ動かす
/// wheel -120            縦ホイール（120 で1ノッチ）
/// hwheel 120            横ホイール
/// ime off               IME を OFF にする（on / off / toggle。今の状態を見て、違うときだけ変える）
/// ime on tsf            方式を固定する（imm / tsf / key / altkanji。書かなければ自動）
/// ime off key abort     切り替えに失敗したら、この動作の残りをやめる（書かなければ続ける）
/// </code>
/// </summary>
public static class StepText
{
    /// <summary>手順をテキストにする。1つの手順が複数の項目を持つときは、実行する順に複数行になる。</summary>
    public static string Format(IEnumerable<HelperStep>? steps)
    {
        var sb = new StringBuilder();
        foreach (var s in steps ?? Enumerable.Empty<HelperStep>())
        {
            if (!string.IsNullOrEmpty(s.Key)) sb.Append("key ").AppendLine(s.Key);
            if (!string.IsNullOrEmpty(s.Down)) sb.Append("down ").AppendLine(s.Down);
            if (!string.IsNullOrEmpty(s.Up)) sb.Append("up ").AppendLine(s.Up);
            if (!string.IsNullOrEmpty(s.Vk)) sb.Append("vk ").AppendLine(s.Vk);
            if (!string.IsNullOrEmpty(s.VkDown)) sb.Append("vkdown ").AppendLine(s.VkDown);
            if (!string.IsNullOrEmpty(s.VkUp)) sb.Append("vkup ").AppendLine(s.VkUp);
            if (!string.IsNullOrEmpty(s.Text)) sb.Append("text ").AppendLine(EscapeText(s.Text));
            if (s.DelayMs is int d) sb.Append("delay ").AppendLine(d.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(s.Mouse))
            {
                string action = (s.MouseAction ?? "click").Trim().ToLowerInvariant();
                sb.Append("mouse ").Append(s.Mouse.Trim().ToLowerInvariant());
                if (action != "click") sb.Append(' ').Append(action);
                sb.AppendLine();
            }
            if (s.MoveX != null || s.MoveY != null)
            {
                sb.Append(s.Absolute ? "moveto " : "move ")
                  .Append((s.MoveX ?? 0).ToString(CultureInfo.InvariantCulture)).Append(' ')
                  .AppendLine((s.MoveY ?? 0).ToString(CultureInfo.InvariantCulture));
            }
            if (s.Wheel is int w && w != 0) sb.Append("wheel ").AppendLine(w.ToString(CultureInfo.InvariantCulture));
            if (s.HWheel is int h && h != 0) sb.Append("hwheel ").AppendLine(h.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(s.Ime))
            {
                sb.Append("ime ").Append(s.Ime.Trim().ToLowerInvariant());
                if (ImeNames.TryParseMethod(s.ImeMethod, out var m) && m != ImeMethod.Auto) sb.Append(' ').Append(ImeNames.MethodName(m));
                if (ImeNames.TryParseOnFail(s.ImeOnFail, out bool abort) && abort) sb.Append(" abort");
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// テキストを手順にする。書き方の誤りは、行番号つきで <paramref name="errors"/> に入れ、その行は飛ばす。
    /// 誤りが無ければ true。
    /// </summary>
    public static bool TryParse(string? text, out List<HelperStep> steps, out List<string> errors)
    {
        steps = new List<HelperStep>();
        errors = new List<string>();
        int lineNo = 0;
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            lineNo++;
            string line = raw.TrimStart();
            if (line.Length == 0 || line[0] == '#') continue;

            int space = line.IndexOf(' ');
            string command = (space < 0 ? line : line[..space]).ToLowerInvariant();
            string arg = space < 0 ? "" : line[(space + 1)..];
            string at = Loc.T($"{lineNo}行目: ", $"Line {lineNo}: ");

            switch (command)
            {
                case "key":
                case "down":
                case "up":
                {
                    string token = arg.Trim();
                    if (token.Length == 0) { errors.Add(at + Loc.T($"{command} にキーコードがありません", $"{command} has no key code")); break; }
                    if (!SequenceCompiler.TryResolveKey(token, out _, out _))
                    {
                        errors.Add(at + Loc.T($"'{token}' はキーとして使えません(KC_A、LCTL(KC_C)、SC_E06A のような書き方)", $"'{token}' can't be used as a key (write it like KC_A, LCTL(KC_C), or SC_E06A)"));
                        break;
                    }
                    var step = new HelperStep();
                    if (command == "key") step.Key = token;
                    else if (command == "down") step.Down = token;
                    else step.Up = token;
                    steps.Add(step);
                    break;
                }
                case "vk":
                case "vkdown":
                case "vkup":
                {
                    string token = arg.Trim();
                    if (token.Length == 0) { errors.Add(at + Loc.T($"{command} に仮想キーがありません", $"{command} has no virtual key")); break; }
                    if (!Keycodes.VkTable.TryResolve(token, out _, out _))
                    {
                        errors.Add(at + Loc.T($"'{token}' は仮想キーとして使えません(IME_ON、0x16、LSHIFT+F1 のような書き方)", $"'{token}' can't be used as a virtual key (write it like IME_ON, 0x16, or LSHIFT+F1)"));
                        break;
                    }
                    var step = new HelperStep();
                    if (command == "vk") step.Vk = token;
                    else if (command == "vkdown") step.VkDown = token;
                    else step.VkUp = token;
                    steps.Add(step);
                    break;
                }
                case "text":
                    if (arg.Length == 0) errors.Add(at + Loc.T("text に文字列がありません", "text has no string"));
                    else steps.Add(new HelperStep { Text = UnescapeText(arg) });
                    break;
                case "delay":
                    if (!TryInt(arg, out int ms) || ms < 0 || ms > SequenceCompiler.MaxDelayMs)
                        errors.Add(at + Loc.T($"delay は 0〜{SequenceCompiler.MaxDelayMs} のミリ秒で書きます", $"delay must be 0 to {SequenceCompiler.MaxDelayMs} milliseconds"));
                    else steps.Add(new HelperStep { DelayMs = ms });
                    break;
                case "mouse":
                {
                    var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length is < 1 or > 2 || !HelperMouseButtons.TryParse(parts[0], out _))
                    {
                        errors.Add(at + Loc.T("mouse は left / right / middle / x1 / x2 のどれかで書きます(例: mouse left、mouse left down)", "mouse takes one of left / right / middle / x1 / x2 (for example: mouse left, mouse left down)"));
                        break;
                    }
                    string action = parts.Length == 2 ? parts[1].ToLowerInvariant() : "click";
                    if (action is not ("click" or "down" or "up"))
                    {
                        errors.Add(at + Loc.T("mouse の後ろは click / down / up のどれかです", "After mouse, write one of click / down / up"));
                        break;
                    }
                    steps.Add(new HelperStep { Mouse = parts[0].ToLowerInvariant(), MouseAction = action == "click" ? null : action });
                    break;
                }
                case "move":
                case "moveto":
                {
                    var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2 || !TryInt(parts[0], out int x) || !TryInt(parts[1], out int y))
                    {
                        errors.Add(at + Loc.T($"{command} は整数を2つ(x y)で書きます", $"{command} takes two integers (x y)"));
                        break;
                    }
                    steps.Add(new HelperStep { MoveX = x, MoveY = y, Absolute = command == "moveto" });
                    break;
                }
                case "wheel":
                case "hwheel":
                    if (!TryInt(arg, out int amount) || amount == 0) errors.Add(at + Loc.T($"{command} は 0 以外の整数で書きます", $"{command} takes a non-zero integer"));
                    else steps.Add(command == "wheel" ? new HelperStep { Wheel = amount } : new HelperStep { HWheel = amount });
                    break;
                case "ime":
                {
                    var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0 || !ImeNames.TryParseTarget(parts[0], out _))
                    {
                        errors.Add(at + Loc.T("ime は on / off / toggle のどれかで書きます(例: ime off、ime on tsf、ime off key abort)", "ime takes one of on / off / toggle (for example: ime off, ime on tsf, ime off key abort)"));
                        break;
                    }
                    string? method = null;
                    bool abort = false, bad = false;
                    for (int i = 1; i < parts.Length; i++)
                    {
                        string p = parts[i].ToLowerInvariant();
                        if (p == "abort") abort = true;
                        else if (p == "continue") abort = false;
                        else if (method == null && p != "auto" && ImeNames.TryParseMethod(p, out _)) method = p;
                        else if (method == null && p == "auto") method = null;
                        else { bad = true; break; }
                    }
                    if (bad)
                    {
                        errors.Add(at + Loc.T("ime の後ろは、方式(imm / tsf / key / altkanji)と abort だけ書けます(方式を書かないと、自動で選びます)", "After ime, only a method (imm / tsf / key / altkanji) and abort can be written (without a method, one is chosen automatically)"));
                        break;
                    }
                    steps.Add(new HelperStep { Ime = parts[0].ToLowerInvariant(), ImeMethod = method, ImeOnFail = abort ? "abort" : null });
                    break;
                }
                default:
                    errors.Add(at + Loc.T($"'{command}' は命令ではありません(key / down / up / vk / vkdown / vkup / text / delay / mouse / move / moveto / wheel / hwheel / ime)", $"'{command}' is not a command (key / down / up / vk / vkdown / vkup / text / delay / mouse / move / moveto / wheel / hwheel / ime)"));
                    break;
            }
        }
        return errors.Count == 0;
    }

    private static bool TryInt(string s, out int value) =>
        int.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    private static string EscapeText(string text) =>
        text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    private static string UnescapeText(string text)
    {
        if (text.IndexOf('\\') < 0) return text;
        var sb = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\\' || i + 1 >= text.Length) { sb.Append(c); continue; }
            char n = text[++i];
            switch (n)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case '\\': sb.Append('\\'); break;
                default: sb.Append('\\').Append(n); break; // 知らない書き方は、そのまま
            }
        }
        return sb.ToString();
    }
}
