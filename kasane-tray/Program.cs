namespace Kasane.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 診断: 3秒待って（その間に、IMEを調べたいウィンドウを前面にする）、IMEの状態を %TEMP%\kasane-ime-probe.txt に書く。
        if (args.Contains("--probe-ime"))
        {
            Thread.Sleep(3000);
            var state = new Win32ImeSource().ReadWithDetail(out string detail);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "kasane-ime-probe.txt"),
                $"{DateTime.Now:HH:mm:ss} Known={state.Known} Open={state.Open} ConversionMode=0x{state.ConversionMode:X} KanaInput={state.IsKanaInput}{Environment.NewLine}{detail}{Environment.NewLine}");
            return;
        }

        // 診断: TSF で IME の状態を読めるかの調査（kasane-tray.exe --probe-tsf [回数 [間隔秒]]）。3秒待ってから、前面のウィンドウについて、
        // IMM32 と TSF（グローバル・自分のスレッド）の値を、%TEMP%\kasane-tsf-probe.txt に追記する。アプリや IME の状態を切り替えながら測る。
        int probeIndex = Array.IndexOf(args, "--probe-tsf");
        if (probeIndex >= 0)
        {
            int count = probeIndex + 1 < args.Length && int.TryParse(args[probeIndex + 1], out int c) ? Math.Clamp(c, 1, 600) : 1;
            int interval = probeIndex + 2 < args.Length && int.TryParse(args[probeIndex + 2], out int s) ? Math.Clamp(s, 1, 60) : 3;
            TsfImeProbe.Run(Path.Combine(Path.GetTempPath(), "kasane-tsf-probe.txt"), count, interval);
            return;
        }

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
            string path = Path.Combine(Kasane.Model.HelperConfig.BaseDirectory, "tray.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > 200_000) File.Delete(path);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception) { /* 記録できなくても、トレイは続ける */ }
    }
}
