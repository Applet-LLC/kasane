using System.Windows;

namespace KbdLayerLicenseGui;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
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
