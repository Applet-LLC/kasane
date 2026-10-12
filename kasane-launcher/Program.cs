namespace KasaneLauncher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // ランチャー（インストーラーの起動画面）は1つだけ。2つ目は何もせずに終わる。
        using var mutex = new Mutex(initiallyOwned: true, @"Local\kasane-launcher", out bool createdNew);
        if (!createdNew) return;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
