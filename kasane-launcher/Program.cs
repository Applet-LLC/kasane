using System.Runtime.InteropServices;

namespace KasaneLauncher;

internal static class Program
{
    // Global\ にして、別のログオンセッション（RDP・ユーザー切り替え）からの同時起動も止める。
    // ランチャーは msiexec の終了まで開いたままなので、mutex もインストール中は保持される。
    private const string MutexName = @"Global\kasane-launcher";

    [STAThread]
    private static void Main()
    {
        bool createdNew;
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // 別の権限（昇格の有無）で動いている先行のランチャーが作った mutex は、開けない。起動中とみなす。
            createdNew = false;
        }

        if (!createdNew)
        {
            ActivateExisting();
            mutex?.Dispose();
            return;
        }

        using (mutex)
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    /// <summary>先に起動しているランチャーのウィンドウを前面に出す。見つからない（別セッション・タイトルが変わった）ときは何もしない。</summary>
    private static void ActivateExisting()
    {
        foreach (string title in Strings.AllWindowTitles)
        {
            IntPtr hwnd = FindWindow(null, title);
            if (hwnd == IntPtr.Zero) continue;
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
            return;
        }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
