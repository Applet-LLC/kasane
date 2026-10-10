using System.Text;

namespace Kasane.Import.Nodoka;

/// <summary>のどかの設定ファイルの文字コードの判定（nodoka4 の readFile と同じ順: BOM → 厳密な UTF-8 → CP932）。</summary>
public static class NodokaText
{
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932).GetString(bytes);
        }
    }

    /// <summary>
    /// 行に分ける。区切りは "\r\n"・"\n"・U+2028。単独の "\r" は行の中の文字として残す（nodoka4 の getLine と同じ）。
    /// </summary>
    public static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n' || c == '\u2028')
            {
                lines.Add(text[start..i]);
                start = i + 1;
            }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                lines.Add(text[start..i]);
                start = i + 2;
                i++;
            }
        }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }
}
