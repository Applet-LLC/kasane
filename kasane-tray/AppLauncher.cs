using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kasane.Tray;

/// <summary>
/// エディタ（kasane.exe）とライセンス管理（kasane-license.exe）を開く。すでに起動していれば、新しく起動せず、
/// そのウィンドウを前面に出す。通常権限で起動する（UACを出さない。エディタの書き込み操作は、エディタの
/// 「管理者として再起動」を使う）。
/// </summary>
public static class AppLauncher
{
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>インストール先の並び（…\applet\kasane\tray\kasane-tray.exe の隣のフォルダ）と、リポジトリでの開発時の出力先から探す。</summary>
    public static string? Find(string installedSubFolder, string exeFileName, string devProjectFolder)
    {
        string baseDir = AppContext.BaseDirectory;

        // インストール先: <install>\tray\ の隣に <install>\keymap-gui\ と <install>\license-gui\ がある。
        string installed = Path.GetFullPath(Path.Combine(baseDir, "..", installedSubFolder, exeFileName));
        if (File.Exists(installed)) return installed;

        // 開発時: リポジトリの <project>\bin\<構成>\net8.0-windows\win-x64\ （kasane.slnのあるフォルダまでさかのぼる）。
        for (var dir = new DirectoryInfo(baseDir); dir != null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "kasane.sln"))) continue;
            foreach (var config in new[] { "Release", "Debug" })
            {
                string dev = Path.Combine(dir.FullName, devProjectFolder, "bin", config, "net8.0-windows", "win-x64", exeFileName);
                if (File.Exists(dev)) return dev;
            }
            break;
        }
        return null;
    }

    public static string? FindEditor() => Find("keymap-gui", "kasane.exe", "kasane");
    public static string? FindLicense() => Find("license-gui", "kasane-license.exe", "kasane-license");

    /// <summary>起動済みなら前面に出す。起動していなければ起動する。失敗したら理由を返す（成功は null）。</summary>
    /// <param name="onStarted">新しく起動したときだけ呼ぶ（準備中の表示を出すため）。受け取った側が Process を破棄する。</param>
    public static string? OpenOrActivate(string? exePath, string displayName, Action<Process>? onStarted = null)
    {
        if (exePath == null) return Kasane.Model.Loc.T($"{displayName}が見つかりません(インストールし直してください)。", $"{displayName} was not found (please reinstall).");

        string processName = Path.GetFileNameWithoutExtension(exePath);
        foreach (var p in Process.GetProcessesByName(processName))
        {
            try
            {
                p.Refresh();
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    if (IsIconic(p.MainWindowHandle)) ShowWindowAsync(p.MainWindowHandle, SW_RESTORE);
                    SetForegroundWindow(p.MainWindowHandle);
                    return null;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { /* 終了した・触れないものは飛ばす */ }
            finally { p.Dispose(); }
        }

        try
        {
            var started = Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exePath)! });
            if (started != null)
            {
                if (onStarted != null) onStarted(started);
                else started.Dispose();
            }
            return null;
        }
        catch (Win32Exception ex)
        {
            return Kasane.Model.Loc.T($"{displayName}を起動できませんでした: {ex.Message}", $"Could not start {displayName}: {ex.Message}");
        }
    }
}
