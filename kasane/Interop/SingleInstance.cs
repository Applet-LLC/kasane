using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kasane.Interop;

/// <summary>
/// アプリを1つのログインセッションにつき1つだけにする（2つ目は、先のウィンドウを前面に出して終わる）。
/// kasane（設定画面）と kasane-license で共有する（kasane-license.csproj からファイルをリンクしている）。
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    /// <summary>「管理者として再起動」で、元のプロセスが終わるのを待たせる引数（--wait-pid &lt;pid&gt;）。</summary>
    public const string WaitPidArgument = "--wait-pid";

    private const int SW_RESTORE = 9;
    private Mutex? _mutex;

    private SingleInstance(Mutex mutex) { _mutex = mutex; }

    /// <summary>
    /// 先に起動しているものが無ければ、そのしるしを返す（アプリが終わるまで保持する）。あれば先のウィンドウを前面に出して null を返す。
    /// 昇格したプロセスが作ったミューテックスは、通常権限からは開けない（UnauthorizedAccessException）。その場合も「先に居る」と扱う。
    /// </summary>
    public static SingleInstance? TryAcquire(string mutexName, string[] args)
    {
        WaitForParent(args);

        Mutex? mutex = null;
        bool created;
        try
        {
            mutex = new Mutex(initiallyOwned: true, mutexName, out created);
        }
        catch (UnauthorizedAccessException)
        {
            created = false;
        }

        if (created) return new SingleInstance(mutex!);

        mutex?.Dispose();
        ActivateExisting();
        return null;
    }

    /// <summary>--wait-pid で渡された元のプロセスが終わるまで（最大 15 秒）待つ。終わっていた・触れないときは待たない。</summary>
    private static void WaitForParent(string[] args)
    {
        int i = Array.IndexOf(args, WaitPidArgument);
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out int pid)) return;
        try
        {
            using var parent = Process.GetProcessById(pid);
            parent.WaitForExit(15_000);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // もう終わっている、または触れない。待たずに進む。
        }
    }

    private static void ActivateExisting()
    {
        using var self = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(self.ProcessName))
        {
            try
            {
                if (p.Id == self.Id) continue;
                p.Refresh();
                IntPtr hwnd = p.MainWindowHandle;
                if (hwnd == IntPtr.Zero) continue; // 起動途中。そのまま任せる。
                if (IsIconic(hwnd)) ShowWindowAsync(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { /* 終了した・触れないものは飛ばす */ }
            finally { p.Dispose(); }
        }
    }

    public void Dispose()
    {
        if (_mutex == null) return;
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* 別スレッドで解放済み */ }
        _mutex.Dispose();
        _mutex = null;
    }

    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
}
