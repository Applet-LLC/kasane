using System.IO;
using System.Linq;
using System.ComponentModel;
using System.Diagnostics;

namespace Kasane.Model;

/// <summary>
/// マニュアル（インストール先の <c>manual\ja\index.html</c>）の場所と、開く処理。エディタ（kasane.exe）とトレイ（kasane-tray.exe）で共有する。
/// 英語の表示でも、同じ HTML を開く（英語版のマニュアルは、まだ無い）。
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
    public static string? Find(string baseDir)
    {
        string installed = Path.GetFullPath(Path.Combine(new[] { baseDir, ".." }.Concat(IndexRelativeParts).ToArray()));
        if (File.Exists(installed)) return installed;

        for (var dir = new DirectoryInfo(baseDir); dir != null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "kasane.sln"))) continue;
            string dev = Path.Combine(dir.FullName, "docs", "manual", "ja", "index.html");
            return File.Exists(dev) ? dev : null;
        }
        return null;
    }

    /// <summary>マニュアルを、エクスプローラー経由で開く。失敗したら理由を返す（成功は null）。</summary>
    public static string? Open(string baseDir)
    {
        string? path = Find(baseDir);
        if (path == null)
            return Loc.T("マニュアルが見つかりません(インストールし直してください)。", "The manual was not found (please reinstall).");
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
