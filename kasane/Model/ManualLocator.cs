using System.IO;
using System.Linq;
using System.ComponentModel;
using System.Diagnostics;

namespace Kasane.Model;

/// <summary>
/// マニュアル（インストール先の <c>manual\ja\index.html</c> と <c>manual\en\index.html</c>）の場所と、開く処理。エディタ（kasane.exe）とトレイ（kasane-tray.exe）で共有する。
/// 英語の表示では、英語版（<c>manual\en</c>）を開く。英語版が無い章（まだ訳していない章）は、日本語版を開く。
/// 開くときは、エクスプローラー（explorer.exe）にファイルを渡す。どのブラウザで開くかは、Windows の設定（既定のアプリ）に任せる。
/// </summary>
public static class ManualLocator
{
    /// <summary>インストール先の <c>manual</c> フォルダの下の、入口のページ。</summary>
    public static readonly string[] IndexRelativeParts = { "manual", "ja", "index.html" };

    /// <summary>
    /// マニュアルの入口のページを探す。インストール先では、エディタは <c>&lt;install&gt;\keymap-gui\</c>、トレイは <c>&lt;install&gt;\tray\</c> にあり、
    /// どちらも <c>&lt;install&gt;\manual\ja\index.html</c> の1つ下。開発時は、<c>kasane.sln</c> のあるフォルダの <c>docs\manual\ja\index.html</c>。見つからなければ null。
    /// </summary>
    /// <param name="baseDir">実行ファイルのあるフォルダ（<c>AppContext.BaseDirectory</c>）。</param>
    public static string? Find(string baseDir) => Find(baseDir, english: false);

    /// <summary><paramref name="english"/> が true のときは、英語版の入口（<c>manual\en\index.html</c>）を先に探し、無ければ日本語版。</summary>
    public static string? Find(string baseDir, bool english)
    {
        if (english)
        {
            string? en = FindLanguage(baseDir, "en");
            if (en != null) return en;
        }
        return FindLanguage(baseDir, "ja");
    }

    private static string? FindLanguage(string baseDir, string lang)
    {
        string installed = Path.GetFullPath(Path.Combine(new[] { baseDir, "..", IndexRelativeParts[0], lang, IndexRelativeParts[2] }));
        if (File.Exists(installed)) return installed;

        for (var dir = new DirectoryInfo(baseDir); dir != null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "kasane.sln"))) continue;
            string dev = Path.Combine(dir.FullName, "docs", "manual", lang, "index.html");
            return File.Exists(dev) ? dev : null;
        }
        return null;
    }

    /// <summary>
    /// 章（<paramref name="page"/>）のファイルを探す。<paramref name="english"/> が true なら英語版の章を先に探し、無ければ日本語版の同じ章、
    /// それも無ければ入口。マニュアルが無ければ null。
    /// </summary>
    public static string? FindPage(string baseDir, string page, bool english)
    {
        if (english)
        {
            string? enIndex = FindLanguage(baseDir, "en");
            if (enIndex != null)
            {
                string enPage = Path.Combine(Path.GetDirectoryName(enIndex)!, page);
                if (File.Exists(enPage)) return enPage;
            }
        }
        string? jaIndex = FindLanguage(baseDir, "ja");
        if (jaIndex == null) return english ? FindLanguage(baseDir, "en") : null;
        string jaPage = Path.Combine(Path.GetDirectoryName(jaIndex)!, page);
        return File.Exists(jaPage) ? jaPage : Find(baseDir, english);
    }

    private static string NotFound => Loc.T("マニュアルが見つかりません(インストールし直してください)。", "The manual was not found (please reinstall).");

    /// <summary>マニュアルを、エクスプローラー経由で開く。失敗したら理由を返す（成功は null）。</summary>
    public static string? Open(string baseDir)
    {
        string? path = Find(baseDir, english: !Loc.IsJapanese);
        return path == null ? NotFound : OpenFile(path);
    }

    /// <summary>
    /// マニュアルの章（<paramref name="page"/>。例: 08-key-assign.html）の、節（<paramref name="anchor"/>。例: combo-keyoverride）を開く
    /// （2026-11-08、画面の「?」ボタンと F1）。失敗したら理由を返す（成功は null）。
    /// エクスプローラーにはファイルのパスしか渡せず、<c>#アンカー</c> が落ちる。そこで、一時フォルダに中継の HTML（<see cref="BuildRelayHtml"/>）を書いて開き、
    /// そのページから節へ移る。中継を書けなければ、章の先頭を開く。章のファイルが無ければ、入口を開く。
    /// </summary>
    public static string? OpenSection(string baseDir, string page, string? anchor)
    {
        string? path = FindPage(baseDir, page, english: !Loc.IsJapanese); // 英語の表示では、英語版の章。英語版に無い章は、日本語版の同じ章
        if (path == null) return NotFound;
        if (string.IsNullOrEmpty(anchor)) return OpenFile(path);
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "kasane-help");
            Directory.CreateDirectory(dir);
            string relay = Path.Combine(dir, "open.html");
            File.WriteAllText(relay, BuildRelayHtml(path, anchor), new System.Text.UTF8Encoding(false));
            return OpenFile(relay);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OpenFile(path); // 中継を書けなければ、章の先頭
        }
    }

    /// <summary>章の節へ移る URL（<c>file:///…/08-key-assign.html#combo-keyoverride</c>）。空白や日本語を含むパスは、URL の形に直す。</summary>
    public static string SectionUrl(string pagePath, string anchor) => new Uri(Path.GetFullPath(pagePath)).AbsoluteUri + "#" + Uri.EscapeDataString(anchor);

    /// <summary>中継の HTML。meta refresh で節へ移る（スクリプトは使わない）。移らないブラウザーのために、リンクも置く。</summary>
    public static string BuildRelayHtml(string pagePath, string anchor)
    {
        string url = System.Net.WebUtility.HtmlEncode(SectionUrl(pagePath, anchor));
        return "<!DOCTYPE html>\n<html lang=\"ja\"><head><meta charset=\"utf-8\">" +
               $"<meta http-equiv=\"refresh\" content=\"0;url={url}\"><title>kasane</title></head>" +
               $"<body><p><a href=\"{url}\">{System.Net.WebUtility.HtmlEncode(Loc.T("マニュアルを開く", "Open the manual"))}</a></p></body></html>\n";
    }

    private static string? OpenFile(string path)
    {
        try
        {
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            info.ArgumentList.Add(path);
            Process.Start(info)?.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return Loc.T($"マニュアルを開けませんでした: {ex.Message}", $"Could not open the manual: {ex.Message}");
        }
    }
}
