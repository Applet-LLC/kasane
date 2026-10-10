using Kasane.Model;

namespace Kasane.Tray;

/// <summary>
/// 読み込んだヘルパー設定（helper.json と tray.json の Helper）の置き場（P3、2026-10-08）。前面のアプリの規則・HLP の動作・キーフックの
/// トリガーが、同じ設定を見る。UI スレッドが <see cref="Reload"/> で丸ごと差し替え、ほかのスレッドは <see cref="Current"/> を読む。
/// </summary>
public sealed class HelperConfigStore
{
    private HelperConfigSet _current = HelperConfigSet.Empty;
    private string _stamp = "";

    public HelperConfigSet Current => Volatile.Read(ref _current);

    /// <summary>
    /// 設定を読み直す。helper.json の更新時刻と大きさ、tray.json の Helper が変わったときだけ作り直す。変わったら true。
    /// </summary>
    public bool Reload(TrayConfig config)
    {
        string stamp = Stamp(config);
        if (stamp == _stamp) return false;
        _stamp = stamp;
        Volatile.Write(ref _current, HelperConfigSet.Load(config.Helper, log: TrayLog.Write));
        return true;
    }

    // 各 helper.json の更新時刻と大きさ、共通の分の JSON をつないだもの（読み直すかどうかの判定だけに使う）。
    private static string Stamp(TrayConfig config)
    {
        var sb = new System.Text.StringBuilder(config.Helper?.ToJson() ?? "");
        try
        {
            string dir = HelperConfig.BaseDirectory;
            if (Directory.Exists(dir))
            {
                foreach (var path in Directory.EnumerateFiles(dir, HelperConfig.FileName, SearchOption.AllDirectories).OrderBy(p => p))
                {
                    var info = new FileInfo(path);
                    sb.Append('|').Append(path).Append('@').Append(info.LastWriteTimeUtc.Ticks).Append(':').Append(info.Length);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return sb.ToString();
    }
}
