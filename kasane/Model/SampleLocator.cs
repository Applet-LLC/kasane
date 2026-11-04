using System.IO;
using System.Linq;

namespace Kasane.Model;

/// <summary>
/// 同梱の設定例（インストール先の <c>samples\&lt;name&gt;\</c>）の場所。エディタ（<c>&lt;install&gt;\keymap-gui\</c>）から見て、1つ上の <c>samples</c>。
/// 開発時は、<c>kasane.sln</c> のあるフォルダの <c>samples\&lt;name&gt;\</c>。見つからなければ null。
/// </summary>
public static class SampleLocator
{
    /// <summary>Emacs 風のキー操作の設定例（<c>emacs.kasane.json</c>）。</summary>
    public const string EmacsFolder = "emacs";
    public const string EmacsFile = "emacs.kasane.json";

    public static string? Find(string baseDir, string folder, string fileName)
    {
        string installed = Path.GetFullPath(Path.Combine(baseDir, "..", "samples", folder, fileName));
        if (File.Exists(installed)) return installed;

        for (var dir = new DirectoryInfo(baseDir); dir != null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "kasane.sln"))) continue;
            string dev = Path.Combine(dir.FullName, "samples", folder, fileName);
            return File.Exists(dev) ? dev : null;
        }
        return null;
    }

    public static string? FindEmacs(string baseDir) => Find(baseDir, EmacsFolder, EmacsFile);
}
