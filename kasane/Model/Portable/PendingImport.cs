using System.IO;
using Kasane.Interop;

namespace Kasane.Model.Portable;

/// <summary>
/// 保存先のフォルダ（環境変数 KASANE で指定したフォルダ）に置かれた取り込みファイル（kasane-import.json）を、
/// 起動時に、<b>まだ設定が無いものだけ</b>取り込む（設定画面とトレイの分。ドライバへの反映は、インストール時に別に行う）。
///  - キーボード: 接続中のキーボードに、HardwareId（または UniqId）で対応づけ、その project.json がまだ無いものだけ。
///  - トレイの設定: tray.json がまだ無いときだけ。
/// すでに設定があるものは触らない（ファイルは残す。設定を消せば、次の起動でまた取り込まれる）。
/// 何度呼んでも、取り込むものが無ければ何もしない（ファイルを読んで、対応づけを調べるだけ）。
/// </summary>
public static class PendingImport
{
    public const string FileName = "kasane-import.json";

    public static string FilePath(string? baseDirectory = null) =>
        Path.Combine(baseDirectory ?? HelperConfig.BaseDirectory, FileName);

    public sealed record Result(bool FileFound, int KeyboardsImported, bool TrayImported);

    /// <param name="devices">接続中のキーボード（ドライバに接続できなければ空）。</param>
    public static Result Run(string? baseDirectory, IReadOnlyList<PortableService.TargetDevice> devices, PortableReport report,
                             Func<string?, Kasane.Layout.KeyboardLayout?>? resolveLayout = null)
    {
        string path = FilePath(baseDirectory);
        if (!File.Exists(path)) return new Result(false, 0, false);

        if (!PortableIO.TryRead(path, out var file, out string? error))
        {
            report.Error($"{FileName} を読めないため、取り込みません（{error}）");
            return new Result(true, 0, false);
        }
        if (file!.ParsedScope == PortableScope.Layer)
        {
            report.Error($"{FileName} は、レイヤー1枚だけのファイルのため、取り込めません");
            return new Result(true, 0, false);
        }

        // 接続中のキーボードに対応づけ、まだ project.json が無いものだけを残す。
        var unset = new List<PortableService.Mapping>();
        foreach (var m in PortableService.AutoMap(file, devices))
        {
            if (m.TargetUniqId is not uint uniq) continue; // 未接続（接続されたときの、次の起動で取り込む）
            if (File.Exists(ProjectService.ProjectPath(uniq, baseDirectory))) continue; // 設定がある
            unset.Add(m);
        }

        bool trayUnset = file.Tray != null && !File.Exists(PortableService.TrayPath(baseDirectory));
        if (unset.Count == 0 && !trayUnset) return new Result(true, 0, false);

        // 取り込むのは、設定が無いものだけ（トレイの設定は、tray.json が既にあれば触らない）。
        var target = new PortableFile
        {
            Format = file.Format, Version = file.Version, Scope = file.Scope, Exported = file.Exported, App = file.App,
            Keyboards = file.Keyboards,
            Tray = trayUnset ? file.Tray : null,
        };
        var options = new ImportOptions { Mode = ImportMode.Replace, Helper = true, MeasuredScanCodes = false };
        var changed = PortableService.ImportAll(target, unset, options, baseDirectory, report, resolveLayout);
        report.Info($"{FileName} から、キーボード{changed.Count}台" + (trayUnset ? "とトレイの設定" : "") + "を取り込みました（設定が無かったものだけ）");
        return new Result(true, changed.Count, trayUnset);
    }

    /// <summary>ドライバに接続して、接続中のキーボードを調べて実行する。接続できなければ、キーボードは無いものとして、トレイの設定だけを調べる。</summary>
    public static Result RunWithDriver(string? baseDirectory, PortableReport report)
    {
        var devices = new List<PortableService.TargetDevice>();
        using var client = KbdLayerClient.TryOpen(out _);
        if (client != null)
        {
            try
            {
                foreach (var d in client.ListDevices())
                    devices.Add(new PortableService.TargetDevice(d.UniqId, d.HardwareId, d.UniqId.ToString("X8")));
            }
            catch (KbdLayerException) { /* 一覧を取れなければ、キーボードは無いものとして進む */ }
        }
        return Run(baseDirectory, devices, report);
    }
}
