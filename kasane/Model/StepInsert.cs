namespace Kasane.Model;

/// <summary>
/// Sequence の手順を書く複数行の欄（StepText の書式）に、1行を挿入する。入力補助ボタン（キーを押して入力・選ぶ・マウス）が使う。
/// 画面の部品に依存しない純粋な関数にして、単体テストできるようにしてある。
/// </summary>
public static class StepInsert
{
    /// <summary>
    /// カーソルがある行の次に、<paramref name="line"/> を1行として挿入する。カーソルが範囲外なら末尾として扱う。
    /// 欄が空・末尾に改行が無いときは、必要な改行を足す。戻り値は、新しい文字列と、挿入した行の末尾（改行の前）にあたるカーソル位置。
    /// 改行は、欄にすでにある改行（\r\n か \n）に合わせる。無ければ \r\n（WPF の TextBox の改行）。
    /// </summary>
    public static (string Text, int Caret) InsertLine(string? text, int caretIndex, string line)
    {
        text ??= "";
        if (text.Length == 0) return (line, line.Length);

        string nl = text.Contains("\r\n") ? "\r\n" : text.Contains('\n') ? "\n" : "\r\n";
        if (caretIndex < 0 || caretIndex > text.Length) caretIndex = text.Length;

        // カーソルのある行の終わり（その行の改行）。カーソルが行頭にあっても、その行の次に入れる。
        int lineEnd = text.IndexOf('\n', caretIndex);
        if (lineEnd < 0)
        {
            string head = text.EndsWith('\n') ? text : text + nl;
            string appended = head + line;
            return (appended, appended.Length);
        }

        string before = text[..(lineEnd + 1)];
        string after = text[(lineEnd + 1)..];
        return (before + line + nl + after, before.Length + line.Length);
    }
}
