using System.Globalization;
using System.Text;

namespace Kasane.Import.Nodoka;

public enum TokenType { String, Number, Regexp, OpenParen, CloseParen, Comma }

public sealed class Token
{
    public TokenType Type { get; }
    /// <summary>文字列・正規表現・数値の元のテキスト。</summary>
    public string Text { get; private set; }
    public bool Quoted { get; }
    public long Number { get; }

    public Token(TokenType type) { Type = type; Text = ""; }
    public Token(string text, bool quoted, bool regexp = false) { Type = regexp ? TokenType.Regexp : TokenType.String; Text = text; Quoted = quoted; }
    public Token(long number, string display) { Type = TokenType.Number; Number = number; Text = display; }

    public bool IsString => Type == TokenType.String;
    public bool IsNumber => Type == TokenType.Number;
    public bool IsRegexp => Type == TokenType.Regexp;
    public bool IsOpen => Type == TokenType.OpenParen;
    public bool IsClose => Type == TokenType.CloseParen;
    public bool IsComma => Type == TokenType.Comma;

    /// <summary>大小文字を区別せずに、文字列のトークンと比べる（のどかの tstringi と同じ）。</summary>
    public bool Is(string s) => Type == TokenType.String && string.Equals(Text, s, StringComparison.OrdinalIgnoreCase);

    internal void Append(string s) => Text += s;

    public override string ToString() => Type switch
    {
        TokenType.OpenParen => "(",
        TokenType.CloseParen => ")",
        TokenType.Comma => ",",
        _ => Text,
    };
}

/// <summary>1つの論理行（行末の "\" でつないだもの）。<see cref="Line"/> は最初の物理行の番号（1から）。</summary>
public sealed record NodokaLine(int Line, List<Token> Tokens);

/// <summary>
/// nodoka4 の parser.cpp（Parser::getLine）の写し。前置トークン（"S-", "M0-", "=" など）の最長一致、引用符つきの文字列、
/// "/…/" の正規表現、"#" のコメント、数値（0x・8進）、"(,,)" の空の引数、行末の "\" による継続を扱う。
/// </summary>
public static class NodokaTokenizer
{
    /// <summary>の どかが設定を読むときの前置トークン（setting.cpp の SettingLoader::load）。長いものから照合する。</summary>
    public static readonly string[] Prefixes = BuildPrefixes();

    private static string[] BuildPrefixes()
    {
        var p = new List<string>
        {
            "=", "=>", "&&", "||", ":", "$", "&", "-=", "+=", "!!!!", "!!!", "!!", "!",
            "E0-", "E1-", "S-", "A-", "M-", "C-", "W-", "*", "~", "U-", "D-",
            "R-", "IL-", "IC-", "I-", "NL-", "CL-", "SL-", "KL-", "MAX-", "MIN-", "MMAX-", "MMIN-",
            "T-", "TS-", "TL-", "TLS-", "TR-", "TRS-",
            "IH-", "IK-", "IJ-", "IW-", "DP-",
        };
        for (int i = 0; i <= 9; i++) p.Add($"M{i}-");
        foreach (char c in "0123456789ABCDEF") p.Add($"L{c}-");
        for (int i = 0; i <= 7; i++) p.Add($"K{i}-");
        return p.OrderByDescending(s => s.Length).ToArray(); // 安定ソート: 同じ長さは上の順のまま
    }

    private static bool IsSpace(char c) => c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\v' || c == '\f' || char.IsWhiteSpace(c);

    private static bool IsAsciiPunct(char c) => c < 0x80 && !char.IsLetterOrDigit(c) && c > 0x20 && c != 0x7F;

    /// <summary>のどかの isSymbolChar。英数字・非ASCIIの表示できる文字・"-+/?_\\" ・矢印。それ以外の記号は含まれない。</summary>
    private static bool IsSymbolChar(char c)
    {
        if (c == '\0') return false;
        if (c >= 0x80) return !char.IsControl(c) && !char.IsWhiteSpace(c);
        if (char.IsLetterOrDigit(c)) return true;
        if (IsAsciiPunct(c)) return "-+/?_\\".IndexOf(c) >= 0;
        return false;
    }

    public static List<NodokaLine> Tokenize(IReadOnlyList<string> lines, Action<int, string> onError)
    {
        var result = new List<NodokaLine>();
        int idx = 0;
        int internalLine = 1;
        while (true)
        {
            int startLine = internalLine;
            var tokens = new List<Token>();
            bool isTokenExist = false;
            bool failed = false;

            while (idx < lines.Count)
            {
                string line = lines[idx++];
                internalLine++;
                bool continued = false;
                int t = 0;
                try
                {
                    ScanLine(line, ref t, tokens, ref isTokenExist, ref continued);
                }
                catch (FormatException ex)
                {
                    onError(startLine, ex.Message);
                    failed = true;
                    tokens.Clear();
                    break;
                }
                if (continued) continue;
                if (tokens.Count > 0) break;
                startLine = internalLine;
                isTokenExist = false;
            }

            if (failed) { isTokenExist = false; if (idx >= lines.Count) break; continue; }
            if (tokens.Count == 0) break;
            result.Add(new NodokaLine(startLine, tokens));
        }
        return result;
    }

    private static void ScanLine(string s, ref int t, List<Token> tokens, ref bool isTokenExist, ref bool continued)
    {
        int n = s.Length;
        char At(int i) => i < n ? s[i] : '\0';
        while (true)
        {
            while (t < n && IsSpace(s[t])) t++;
            if (t >= n || s[t] == '#') return;
            if (s[t] == '\\' && t + 1 >= n) { continued = true; return; }

            int tokenStart = t;

            if (s[t] == ',')
            {
                if (!isTokenExist) tokens.Add(new Token("", false));
                isTokenExist = false;
                tokens.Add(new Token(TokenType.Comma));
                t++;
                continue;
            }
            if (s[t] == '(')
            {
                tokens.Add(new Token(TokenType.OpenParen));
                isTokenExist = false;
                t++;
                continue;
            }
            if (s[t] == ')')
            {
                if (!isTokenExist) tokens.Add(new Token("", false));
                isTokenExist = true;
                tokens.Add(new Token(TokenType.CloseParen));
                t++;
                continue;
            }

            isTokenExist = true;

            // 前置トークン
            string? prefix = null;
            foreach (var p in Prefixes)
                if (string.Compare(s, tokenStart, p, 0, p.Length, StringComparison.OrdinalIgnoreCase) == 0 && tokenStart + p.Length <= n)
                {
                    prefix = p;
                    break;
                }
            if (prefix != null)
            {
                tokens.Add(new Token(prefix, false));
                t += prefix.Length;
                continue;
            }

            // 引用符つき、または正規表現
            if (s[t] == '"' || s[t] == '\'' || s[t] == '/' || (s[t] == '\\' && At(t + 1) == 'm' && At(t + 2) != '\0'))
            {
                bool isRegexp = !(s[t] == '"' || s[t] == '\'');
                char q = s[t++];
                if (q == '\\')
                {
                    t++;
                    q = At(t);
                    t++;
                }
                int bodyStart = t;
                while (t < n && s[t] != q)
                {
                    if (s[t] == '\\' && t + 1 < n) t++;
                    t++;
                }
                string str = Interpret(s.AsSpan(bodyStart, Math.Min(t, n) - bodyStart), q, isRegexp);
                if (!isRegexp && tokens.Count > 0 && tokens[^1].IsString && tokens[^1].Quoted) tokens[^1].Append(str);
                else tokens.Add(new Token(str, true, isRegexp));
                if (t < n) t++;
                continue;
            }

            // 引用符なし
            {
                while (t < n && IsSymbolChar(s[t]))
                {
                    if (s[t] == '\\')
                    {
                        if (t + 1 < n) t++;
                        else break;
                    }
                    t++;
                }
                if (t == tokenStart)
                    throw new FormatException($"invalid character U+{(int)s[t]:x4}" + (char.IsControl(s[t]) ? "" : $"({s[t]})"));

                string word = s[tokenStart..t];
                if (TryParseCNumber(word, out long value, out int used))
                {
                    tokens.Add(new Token(value, word[..used]));
                    t = tokenStart + used;
                }
                else
                {
                    tokens.Add(new Token(Interpret(word.AsSpan(), '\0', false), false));
                }
            }
        }
    }

    /// <summary>C の strtol(…, 0) と同じ: 0x 16進、0 で始まる8進、それ以外は10進。先頭の符号もよい。数が無ければ false。</summary>
    private static bool TryParseCNumber(string s, out long value, out int used)
    {
        value = 0;
        used = 0;
        int i = 0;
        bool neg = false;
        if (i < s.Length && (s[i] == '+' || s[i] == '-')) { neg = s[i] == '-'; i++; }
        int radix = 10;
        if (i + 1 < s.Length && s[i] == '0' && (s[i + 1] == 'x' || s[i + 1] == 'X') && i + 2 < s.Length && Uri.IsHexDigit(s[i + 2])) { radix = 16; i += 2; }
        else if (i < s.Length && s[i] == '0') radix = 8;
        int digitsStart = i;
        long v = 0;
        while (i < s.Length)
        {
            int d = s[i] >= '0' && s[i] <= '9' ? s[i] - '0' : radix == 16 && Uri.IsHexDigit(s[i]) ? Convert.ToInt32(s[i].ToString(), 16) : -1;
            if (d < 0 || d >= radix) break;
            v = unchecked(v * radix + d);
            i++;
        }
        if (i == digitsStart) return false;
        value = neg ? -v : v;
        used = i;
        return true;
    }

    /// <summary>interpretMetaCharacters の写し（\n \t \xHH \0 など）。正規表現では \1〜\7（後方参照）をそのまま残す。</summary>
    private static string Interpret(ReadOnlySpan<char> s, char quote, bool isRegexp)
    {
        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] != '\\') { sb.Append(s[i++]); continue; }
            if (i + 1 >= s.Length) { i++; continue; }
            i++;
            char c = s[i];
            if (quote != '\0' && c == quote) { sb.Append(c); i++; continue; }
            switch (c)
            {
                case 'a': sb.Append('\a'); i++; break;
                case 'e': sb.Append('\u001b'); i++; break;
                case 'f': sb.Append('\f'); i++; break;
                case 'n': sb.Append('\n'); i++; break;
                case 'r': sb.Append('\r'); i++; break;
                case 't': sb.Append('\t'); i++; break;
                case 'v': sb.Append('\v'); i++; break;
                case '\'': sb.Append('\''); i++; break;
                case '"': sb.Append('"'); i++; break;
                case '\\': sb.Append('\\'); i++; break;
                case 'x':
                case 'X':
                {
                    i++;
                    bool brace = i < s.Length && s[i] == '{';
                    if (brace) i++;
                    int v = 0;
                    while (i < s.Length && Uri.IsHexDigit(s[i])) { v = v * 16 + Convert.ToInt32(s[i].ToString(), 16); i++; }
                    if (brace && i < s.Length && s[i] == '}') i++;
                    if (v > 0) sb.Append((char)v);
                    break;
                }
                case >= '1' and <= '7' when isRegexp:
                    sb.Append('\\').Append(c);
                    i++;
                    break;
                case >= '0' and <= '7':
                {
                    int v = 0;
                    while (i < s.Length && s[i] >= '0' && s[i] <= '7') { v = v * 8 + (s[i] - '0'); i++; }
                    if (v > 0) sb.Append((char)v);
                    break;
                }
                case 'c':
                    i++;
                    if (i < s.Length) { sb.Append((char)(char.ToUpperInvariant(s[i]) & 0x1F)); i++; }
                    break;
                default:
                    sb.Append('\\').Append(c);
                    i++;
                    break;
            }
        }
        return sb.ToString();
    }
}
