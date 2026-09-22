using System.Runtime.InteropServices;

namespace KbdLayerLicenseSvc;

/// <summary>
/// OutputDebugStringW 経由で DebugView (Sysinternals) から観測できるトレースを出力する。
/// System.Diagnostics.Debug.WriteLine は Release ビルドではコンパイル時に除去されるため使えない。
/// SYSTEM 権限のサービスから出すため、DebugView 側は管理者権限 + [Capture Global Win32] が必要。
///
/// 併せて C:\ProgramData\kbdlayer-license\debug.log にも追記する（一時的な調査用）。
/// DebugView が使えない環境でもファイルを直接読めば同じ内容を確認できる。
/// </summary>
internal static class DebugTrace
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern void OutputDebugStringW(string lpOutputString);

    private const string LogDir = @"C:\ProgramData\kbdlayer-license";
    private const string LogFile = LogDir + @"\debug.log";
    private static readonly object s_fileLock = new();

    public static void Log(string message)
    {
        string line = $"[kbdlayerLicenseSvc {DateTime.Now:HH:mm:ss.fff}] {message}";
        OutputDebugStringW(line);

        try
        {
            lock (s_fileLock)
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // ファイル出力はベストエフォート。失敗してもサービス動作に影響させない。
        }
    }
}
