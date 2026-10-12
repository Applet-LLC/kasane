using System.IO;

namespace Kasane.Layout;

/// <summary>
/// レイアウト（KLE の JSON）の置き場。同梱のもの（exe の隣の <c>Layouts\</c>）と、利用者が「KLEから追加」で保存したもの。
/// 利用者の置き場の決め方は、**ここ 1 か所**にまとめる（設定フォルダを環境変数 <c>KASANE</c> で変えられるようにする
/// feature/kasane-data-dir が入ったら、<see cref="UserDirectory"/> をその設定フォルダの <c>Layouts</c> に差し替える）。
/// </summary>
public static class LayoutFolders
{
    public static string BundledDirectory => Path.Combine(AppContext.BaseDirectory, "Layouts");

    public static string UserDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "applet", "kasane", "Layouts");

    /// <summary>利用者のレイアウトのファイルのパス（<c>kle-&lt;名前&gt;.json</c>）。</summary>
    public static string UserFilePath(string name) => Path.Combine(UserDirectory, $"kle-{name}.json");

    /// <summary>名前が、同梱のレイアウトと重なるか（重なる名前は、利用者のレイアウトに使えない。大文字小文字は区別しない）。</summary>
    public static bool IsBundledName(string name) =>
        KeyboardLayout.EnumerateBundledLayoutFiles().Any(f => KeyboardLayout.NameFromPath(f).Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>同梱のレイアウトに続けて、利用者のレイアウト（同梱と名前が重なるものは、同梱が優先なので出さない）。</summary>
    public static IEnumerable<string> EnumerateAll()
    {
        var bundled = KeyboardLayout.EnumerateBundledLayoutFiles().ToList();
        var names = new HashSet<string>(bundled.Select(KeyboardLayout.NameFromPath), StringComparer.OrdinalIgnoreCase);
        var user = Directory.Exists(UserDirectory)
            ? Directory.GetFiles(UserDirectory, "kle-*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Where(f => names.Add(KeyboardLayout.NameFromPath(f)))
            : Enumerable.Empty<string>();
        return bundled.Concat(user);
    }
}
