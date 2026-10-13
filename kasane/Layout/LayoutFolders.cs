using System.IO;

namespace Kasane.Layout;

/// <summary>
/// レイアウト（KLE の JSON）の置き場。同梱のもの（exe の隣の <c>Layouts\</c>。インストール先から直接読むので、コピーはしない）と、
/// 利用者が「KLEから追加」で保存したもの。利用者の置き場は、設定フォルダ（環境変数 <c>KASANE</c> があればそれ、
/// 無ければ %LOCALAPPDATA%\applet\kasane。<see cref="Kasane.Model.HelperConfig.BaseDirectory"/>）の <c>Layouts</c>。
/// 利用者の置き場の決め方は、**ここ 1 か所**にまとめる。
/// </summary>
public static class LayoutFolders
{
    public static string BundledDirectory => Path.Combine(AppContext.BaseDirectory, "Layouts");

    /// <summary>利用者のレイアウトの置き場（保存先）。環境変数 KASANE があればその下の <c>Layouts</c>。</summary>
    public static string UserDirectory => Path.Combine(Kasane.Model.HelperConfig.BaseDirectory, "Layouts");

    /// <summary>
    /// 環境変数 KASANE を指定する前に保存した利用者のレイアウトの置き場（%LOCALAPPDATA%\applet\kasane\Layouts）。
    /// <see cref="UserDirectory"/> と同じなら null。読むだけ（保存先ではない）。
    /// </summary>
    public static string? LegacyUserDirectory
    {
        get
        {
            string legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "applet", "kasane", "Layouts");
            return string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(UserDirectory), StringComparison.OrdinalIgnoreCase) ? null : legacy;
        }
    }

    /// <summary>利用者のレイアウトのファイルのパス（<c>kle-&lt;名前&gt;.json</c>）。</summary>
    public static string UserFilePath(string name) => Path.Combine(UserDirectory, $"kle-{name}.json");

    /// <summary>名前が、同梱のレイアウトと重なるか（重なる名前は、利用者のレイアウトに使えない。大文字小文字は区別しない）。</summary>
    public static bool IsBundledName(string name) =>
        KeyboardLayout.EnumerateBundledLayoutFiles().Any(f => KeyboardLayout.NameFromPath(f).Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 同梱のレイアウトに続けて、利用者のレイアウト（同梱と名前が重なるものは、同梱が優先なので出さない）。
    /// 利用者のレイアウトは、設定フォルダのものを先に、次に旧置き場（%LOCALAPPDATA%）のもの（同じ名前は設定フォルダが優先）。
    /// </summary>
    public static IEnumerable<string> EnumerateAll()
    {
        var bundled = KeyboardLayout.EnumerateBundledLayoutFiles().ToList();
        var names = new HashSet<string>(bundled.Select(KeyboardLayout.NameFromPath), StringComparer.OrdinalIgnoreCase);
        var user = new List<string>();
        foreach (var dir in new[] { UserDirectory, LegacyUserDirectory })
        {
            if (dir == null || !Directory.Exists(dir)) continue;
            user.AddRange(Directory.GetFiles(dir, "kle-*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Where(f => names.Add(KeyboardLayout.NameFromPath(f))));
        }
        return bundled.Concat(user);
    }
}
