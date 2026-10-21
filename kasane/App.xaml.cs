using System.Windows;

namespace Kasane;

public partial class App : System.Windows.Application
{
    private Kasane.Interop.SingleInstance? _singleInstance;

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 画面を出さないコマンド（インストーラーの取り込み・復元）。単一起動の確認より前に実行して、終わったら終了する。
        if (Kasane.Cli.HeadlessCommands.TryRun(e.Args, out int headlessExitCode))
        {
            Shutdown(headlessExitCode);
            return;
        }

        // 設定画面は1つだけ。2つ目は先のウィンドウを前面に出して終わる（保存の取り合いを防ぐ）。
        _singleInstance = Kasane.Interop.SingleInstance.TryAcquire(@"Local\kasane-editor", e.Args);
        if (_singleInstance == null)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.ToString(), $"{Kasane.Model.AppInfo.DisplayName} - エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        // 表示言語（日本語／英語）。最初の画面を作る前に決める。
        Kasane.Model.Loc.Initialize();
        // 色のモード（ライト／ダーク）。最初の画面を作る前に適用する。
        Kasane.Views.AppTheme.Initialize(Kasane.Model.ColorModeStore.Read());
        base.OnStartup(e);
    }
}
