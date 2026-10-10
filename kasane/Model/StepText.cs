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
/// text こんにちは\n      文字列を打つ（\n = Enter、\t = Tab、\\ = バックスラッシュ）
/// delay 50              待つ（ミリ秒、0〜10000）
/// mouse left            ボタン（left / right / middle / x1 / x2）をクリック。mouse left down / mouse left up も
/// move 10 -5            マウスを今の位置から動かす
/// moveto 100 200        マウスを画面（仮想デスクトップ）の座標へ動かす
/// wheel -120            縦ホイール（120 で1ノッチ）
/// hwheel 120            横ホイール
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
            string at = $"{lineNo}行目: ";

            switch (command)
            {
                case "key":
                case "down":
                case "up":
                {
                    string token = arg.Trim();
                    if (token.Length == 0) { errors.Add(at + $"{command} にキーコードがありません"); break; }
                    if (!SequenceCompiler.TryResolveKey(token, out _, out _))
                    {
                        errors.Add(at + $"'{token}' はキーとして使えません（KC_A、LCTL(KC_C)、SC_E06A のような書き方）");
                        break;
                    }
                    var step = new HelperStep();
                    if (command == "key") step.Key = token;
                    else if (command == "down") step.Down = token;
                    else step.Up = token;
                    steps.Add(step);
                    break;
                }
                case "text":
                    if (arg.Length == 0) errors.Add(at + "text に文字列がありません");
                    else steps.Add(new HelperStep { Text = UnescapeText(arg) });
                    break;
                case "delay":
                    if (!TryInt(arg, out int ms) || ms < 0 || ms > SequenceCompiler.MaxDelayMs)
                        errors.Add(at + $"delay は 0〜{SequenceCompiler.MaxDelayMs} のミリ秒で書きます");
                    else steps.Add(new HelperStep { DelayMs = ms });
                    break;
                case "mouse":
                {
                    var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length is < 1 or > 2 || !HelperMouseButtons.TryParse(parts[0], out _))
                    {
                        errors.Add(at + "mouse は left / right / middle / x1 / x2 のどれかで書きます（例: mouse left、mouse left down）");
                        break;
                    }
                    string action = parts.Length == 2 ? parts[1].ToLowerInvariant() : "click";
                    if (action is not ("click" or "down" or "up"))
                    {
                        errors.Add(at + "mouse の後ろは click / down / up のどれかです");
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
                        errors.Add(at + $"{command} は整数を2つ（x y）で書きます");
                        break;
                    }
                    steps.Add(new HelperStep { MoveX = x, MoveY = y, Absolute = command == "moveto" });
                    break;
                }
                case "wheel":
                case "hwheel":
                    if (!TryInt(arg, out int amount) || amount == 0) errors.Add(at + $"{command} は 0 以外の整数で書きます");
                    else steps.Add(command == "wheel" ? new HelperStep { Wheel = amount } : new HelperStep { HWheel = amount });
                    break;
                default:
                    errors.Add(at + $"'{command}' は命令ではありません（key / down / up / text / delay / mouse / move / moveto / wheel / hwheel）");
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
