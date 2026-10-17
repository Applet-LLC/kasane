using System.IO;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>
/// ファイル・フォルダを選ぶ画面の、開き始めのフォルダの既定。
/// 初めて開くときは、kasane の設定の保存先（環境変数 KASANE のフォルダ。未設定なら %LOCALAPPDATA%\applet\kasane。
/// <see cref="HelperConfig.BaseDirectory"/>）を開く（何も指定しないと、プログラムの作業フォルダ、つまり exe やソースのある
/// フォルダが開き、書き出したファイルがそこに溜まってしまう）。
/// 目的ごとに <c>ClientGuid</c> を決めておくと、Windows が、その目的で最後に使ったフォルダを覚えて、次回はそこから開く。
/// </summary>
internal static class DialogDefaults
{
    /// <summary>設定の保存先。無ければ作る（作れなければ、利用者の「ドキュメント」フォルダ）。</summary>
    public static string DataFolder
    {
        get
        {
            string dir = HelperConfig.BaseDirectory;
            try
            {
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }
        }
    }

    // 目的ごとの識別子（変えると、覚えているフォルダが別になる）。
    public static readonly Guid SettingsExport = new("0b3f6a1e-5a3c-4d36-9a52-6f6c1d0b7a01");
    public static readonly Guid SettingsImport = new("0b3f6a1e-5a3c-4d36-9a52-6f6c1d0b7a02");
    public static readonly Guid KeymapToolExport = new("0b3f6a1e-5a3c-4d36-9a52-6f6c1d0b7a03");
    public static readonly Guid Nicola = new("0b3f6a1e-5a3c-4d36-9a52-6f6c1d0b7a04");
    public static readonly Guid Kle = new("0b3f6a1e-5a3c-4d36-9a52-6f6c1d0b7a05");
    public static readonly Guid Nodoka = new("0b3f6a1e-5a3c-4d36-9a52-6f6c1d0b7a06");
}
