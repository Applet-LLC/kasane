using System.Runtime.InteropServices;

namespace KasaneProActivator;

/// <summary>
/// OutputDebugStringW 経由で DebugView (Sysinternals) から観測できるトレースを出力しつつ、
/// SYSTEM の一時フォルダ（C:\Windows\Temp）の kasane-pro-activator.log にも追記する。
/// MSI の deferred CustomAction として実行される場合、プロセスの標準出力は msiexec ログに
/// 自動的には残らないため、インストール失敗時の調査用にファイルへも記録する。
/// </summary>
internal static class Log
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern void OutputDebugStringW(string lpOutputString);

    // インストール後にフォルダを残さないよう、ProgramData ではなく一時フォルダに書く。
    // SYSTEM の一時フォルダ（C:\Windows\Temp）は、一般ユーザーが他のユーザーのファイルを読めない。
    private static readonly string LogDir = Path.GetTempPath();
    private static string LogFile => Path.Combine(LogDir, "kasane-pro-activator.log");
    private static readonly object s_fileLock = new();

    public static void Trace(string message)
    {
        string line = $"[kasane-pro-activator {DateTime.Now:HH:mm:ss.fff}] {message}";
        OutputDebugStringW(line);

        try
        {
            lock (s_fileLock)
            {
                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // ファイル出力はベストエフォート。失敗しても検証処理自体は継続する。
        }
    }
}
