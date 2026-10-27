using System.IO;
using Kasane.Interop;

namespace Kasane.Model.Portable;

/// <summary>
/// 保存先のフォルダ（環境変数 KASANE で指定したフォルダ）に置かれた取り込みファイル（kasane-import.json）を、
/// 起動時に、<b>まだ設定が無いものだけ</b>取り込む（設定画面とトレイの分。ドライバへの反映は、インストール時に別に行う）。
///  - キーボード: 接続中のキーボードに、HardwareId（または UniqId）で対応づけ、その project.json がまだ無いものだけ。
///  - トレイの設定: tray.json がまだ無いときだけ。
///  - 復旧用の設定のうち、ファイルに書くもの: トレイの設定（表示・色・言語・デスクトップなど）は tray.json がまだ無いときだけ、
///    利用者のレイアウトは、その名前のファイルがまだ無いものだけ。ドライバに入れるもの（緊急解除・i8042prt・共有範囲）は、
///    インストール時の取り込み（<see cref="DriverImport"/>）が書く。
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
            report.Error(Loc.T($"{FileName} を読めないため、取り込みません({error})", $"Not importing {FileName}: it could not be read ({error})"));
            return new Result(true, 0, false);
        }
        if (file!.ParsedScope == PortableScope.Layer)
        {
            report.Error(Loc.T($"{FileName} は、レイヤー1枚だけのファイルのため、取り込めません", $"Can't import {FileName}: it contains only a single layer"));
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

        bool trayFileMissing = !File.Exists(PortableService.TrayPath(baseDirectory));
        bool trayUnset = file.Tray != null && trayFileMissing;
        var recovery = RecoveryImporter.OnlyMissing(file.Recovery, trayFileMissing, baseDirectory);
        if (unset.Count == 0 && !trayUnset && recovery == null) return new Result(true, 0, false);

        // 取り込むのは、設定が無いものだけ（トレイの設定は、tray.json が既にあれば触らない）。
        var target = new PortableFile
        {
            Format = file.Format, Version = file.Version, Scope = file.Scope, Exported = file.Exported, App = file.App,
            Keyboards = file.Keyboards,
            Tray = trayUnset ? file.Tray : null,
            Recovery = recovery,
        };
        var options = new ImportOptions { Mode = ImportMode.Replace, Helper = true, MeasuredScanCodes = false, Recovery = recovery != null };
        var changed = PortableService.ImportAll(target, unset, options, baseDirectory, report, resolveLayout);
        bool trayImported = trayUnset || recovery?.TraySettings != null;
        report.Info(Loc.T($"{FileName} から、キーボード{changed.Count}台" + (trayImported ? "とトレイの設定" : "") + (recovery?.Layouts != null ? "とレイアウト" : "") + "を取り込みました(設定が無かったものだけ)",
            $"Imported {changed.Count} keyboard(s)" + (trayImported ? " and the tray settings" : "") + (recovery?.Layouts != null ? " and layouts" : "") + $" from {FileName} (only those that had no settings)"));
        return new Result(true, changed.Count, trayImported);
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
