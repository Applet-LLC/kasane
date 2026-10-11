using System.Windows;

namespace KasaneLicense;

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
        _singleInstance = Kasane.Interop.SingleInstance.TryAcquire(@"Local\kasane-license", e.Args);
        if (_singleInstance == null)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            ClientDebugLog.Log($"DispatcherUnhandledException: {args.Exception}");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            ClientDebugLog.Log($"AppDomain UnhandledException: {args.ExceptionObject}");
        };

        // MainWindow（StartupUri）が生成される前に表示言語を確定させる。
        LocalizationHelper.Initialize();
        base.OnStartup(e);
    }
}
