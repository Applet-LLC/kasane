namespace Kasane.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 1つのログインセッションに、トレイは1つだけ（2つ目を起動しても、何もせずに終わる）。
        using var mutex = new Mutex(initiallyOwned: true, @"Local\kasane-tray", out bool createdNew);
        if (!createdNew) return;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => TrayLog.Write("ThreadException: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => TrayLog.Write("UnhandledException: " + e.ExceptionObject);

        using var context = new TrayContext();
        Application.Run(context);
        GC.KeepAlive(mutex);
    }
}

/// <summary>異常時の記録（%LOCALAPPDATA%\applet\kasane\tray.log）。常駐中に画面は出さないので、これだけが手がかりになる。大きくなったら作り直す。</summary>
internal static class TrayLog
{
    public static void Write(string message)
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "applet", "kasane", "tray.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > 200_000) File.Delete(path);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception) { /* 記録できなくても、トレイは続ける */ }
    }
}
