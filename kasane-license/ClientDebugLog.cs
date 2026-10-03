using System.IO;

namespace KasaneLicense;

/// <summary>
/// GUI 側の調査用ログ。C:\ProgramData\kbdlayer-license\gui-debug.log に追記する。
/// GUI は非昇格プロセスとして動くが、C:\ProgramData は通常ユーザーでも書き込み可能なため問題ない。
/// パイプ接続例外の内容（従来は catch で握りつぶして詳細不明だった）を記録するために使う。
/// </summary>
internal static class ClientDebugLog
{
    private const string LogDir = @"C:\ProgramData\kbdlayer-license";
    private const string LogFile = LogDir + @"\gui-debug.log";
    private static readonly object s_lock = new();

    public static void Log(string message)
    {
        try
        {
            lock (s_lock)
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(LogFile,
                    $"[kasane-license {DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // ベストエフォート。失敗しても GUI 動作に影響させない。
        }
    }
}
