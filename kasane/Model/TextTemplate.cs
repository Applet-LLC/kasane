using System.Globalization;
using System.Text;

namespace Kasane.Model;

/// <summary>
/// 「文字列を入力」の文字列に書ける、日付・時刻の置き換え。キーを押した時点の時刻で展開する。
///   {date} {date:short}  短い日付(Windows の地域設定。.NET の "d")
///   {date:long}          長い日付("D")
///   {time} {time:short}  短い時刻("t")
///   {time:long}          長い時刻("T")
///   {now:書式}           .NET のカスタム書式(例: {now:yyyy-MM-dd (ddd) h:mm})。1文字の書式は %h のように % を付ける
///   {ampm} / {AMPM}      am・pm / AM・PM(ASCII。言語に依存しない)
/// 上の形に当てはまらない { や } は、そのまま文字として入力する(コードの {} を含む既存の文字列を壊さないため)。
/// 文字どおり {date} と入力したいときは {{date} と書く(直前の { が1つ消える)。
/// </summary>
public static class TextTemplate
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetUserDefaultLocaleName(System.Text.StringBuilder name, int size);

    /// <summary>
    /// Windows の「地域」の形式(利用者が変えた短い日付などを含む)を、今の設定で返す。
    /// 画面の言語(Loc が設定する ja-JP / en-US)には従わない。英語表示でも、日本語の地域なら日本語の形式で出す。
    /// 常駐するプロセス(トレイ、編集画面)は、起動時の設定を覚えたままになる。そこで、呼ぶたびにキャッシュを捨て、
    /// 地域の名前を引き直して、新しい CultureInfo を作る(Loc が作った CultureInfo は、ClearCachedData では読み直されない)。
    /// </summary>
    public static CultureInfo CurrentUserCulture()
    {
        try
        {
            CultureInfo.CurrentCulture.ClearCachedData();
            var sb = new System.Text.StringBuilder(85); // LOCALE_NAME_MAX_LENGTH
            string name = GetUserDefaultLocaleName(sb, sb.Capacity) > 0 ? sb.ToString() : CultureInfo.CurrentCulture.Name;
            return new CultureInfo(name); // useUserOverride は既定の true
        }
        catch (Exception ex) when (ex is CultureNotFoundException or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        {
            return CultureInfo.CurrentCulture;
        }
    }

    /// <summary>置き換えがありうる文字列か。速い判定用(無ければ展開しない)。</summary>
    public static bool MayHavePlaceholder(string? text) => !string.IsNullOrEmpty(text) && text.Contains('{');

    /// <param name="now">展開に使う時刻。呼び出し側が1回の入力ごとに1回だけ取る。</param>
    /// <param name="culture">標準書式と曜日の言語。通常は <see cref="CurrentUserCulture"/>(ユーザーが地域設定で変えた書式を含む)。</param>
    /// <param name="errors">書式が不正な置き換えの説明を足す。null なら黙って原文のまま残す。</param>
    public static string Expand(string? template, DateTime now, CultureInfo culture, List<string>? errors = null)
    {
        if (!MayHavePlaceholder(template)) return template ?? "";
        var sb = new StringBuilder(template!.Length + 16);
        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];
            if (c != '{') { sb.Append(c); i++; continue; }

            // {{date} → 文字どおりの {date}
            if (i + 1 < template.Length && template[i + 1] == '{' && TryRead(template, i + 1, out _, out _, out int escEnd, out _))
            {
                sb.Append(template, i + 1, escEnd - (i + 1));
                i = escEnd;
                continue;
            }

            if (TryRead(template, i, out string name, out string? arg, out int end, out bool recognized))
            {
                string? value = Evaluate(name, arg, now, culture, out string? error);
                if (value != null) sb.Append(value);
                else
                {
                    errors?.Add(error!);
                    sb.Append(template, i, end - i);
                }
                i = end;
                continue;
            }

            if (recognized)
            {
                // 名前は合っているが閉じていない。
                errors?.Add(Loc.T($"'{{' が閉じていません: {Snippet(template, i)}", $"Unclosed '{{': {Snippet(template, i)}"));
            }
            sb.Append('{');
            i++;
        }
        return sb.ToString();
    }

    /// <summary>入力の検証用。展開結果は捨てて、エラーだけ返す。</summary>
    public static List<string> Validate(string? template)
    {
        var errors = new List<string>();
        Expand(template, DateTime.Now, CultureInfo.InvariantCulture, errors); // エラーを見るだけなので、言語は問わない
        return errors;
    }

    // i の { から始まる置き換えを読む。名前が date/time/now/ampm/AMPM のいずれかで、} で閉じていれば true。
    // 名前は合っているのに閉じていないときは false を返し、recognized を true にする。
    private static bool TryRead(string s, int i, out string name, out string? arg, out int end, out bool recognized)
    {
        name = "";
        arg = null;
        end = i;
        recognized = false;

        int p = i + 1;
        int nameStart = p;
        while (p < s.Length && char.IsAsciiLetter(s[p])) p++;
        if (p == nameStart) return false;

        string n = s.Substring(nameStart, p - nameStart);
        bool known = n is "ampm" or "AMPM" || n.ToLowerInvariant() is "date" or "time" or "now";
        if (!known) return false;
        if (p >= s.Length) { recognized = true; return false; } // 文字列が名前で終わっている
        if (s[p] != '}' && s[p] != ':') return false; // {dates} のような別の語

        recognized = true;
        int close = s.IndexOf('}', p);
        if (close < 0) return false;

        name = n is "ampm" or "AMPM" ? n : n.ToLowerInvariant();
        arg = s[p] == ':' ? s.Substring(p + 1, close - p - 1) : null;
        end = close + 1;
        return true;
    }

    private static string? Evaluate(string name, string? arg, DateTime now, CultureInfo culture, out string? error)
    {
        error = null;
        try
        {
            switch (name)
            {
                case "ampm":
                case "AMPM":
                    if (arg != null) { error = BadArg(name, arg, null); return null; }
                    string ap = now.Hour < 12 ? "am" : "pm";
                    return name == "ampm" ? ap : ap.ToUpperInvariant();

                case "date":
                    switch ((arg ?? "short").Trim().ToLowerInvariant())
                    {
                        case "short": return now.ToString("d", culture);
                        case "long": return now.ToString("D", culture);
                    }
                    error = BadArg(name, arg, "short / long");
                    return null;

                case "time":
                    switch ((arg ?? "short").Trim().ToLowerInvariant())
                    {
                        case "short": return now.ToString("t", culture);
                        case "long": return now.ToString("T", culture);
                    }
                    error = BadArg(name, arg, "short / long");
                    return null;

                default: // now
                    if (string.IsNullOrEmpty(arg))
                    {
                        error = Loc.T("{now} には書式が要ります(例: {now:yyyy-MM-dd})", "{now} needs a format (e.g. {now:yyyy-MM-dd})");
                        return null;
                    }
                    return now.ToString(arg, culture);
            }
        }
        catch (FormatException)
        {
            error = Loc.T($"書式 '{arg}' が正しくありません", $"Invalid format '{arg}'");
            return null;
        }
    }

    private static string BadArg(string name, string? arg, string? choices) =>
        choices == null
            ? Loc.T($"{{{name}}} に指定は付けられません: '{arg}'", $"{{{name}}} takes no argument: '{arg}'")
            : Loc.T($"{{{name}:{arg}}} は使えません({choices} のどちらか)", $"{{{name}:{arg}}} is not valid (use {choices})");

    private static string Snippet(string s, int i) => s.Length - i <= 20 ? s.Substring(i) : s.Substring(i, 20) + "…";
}
