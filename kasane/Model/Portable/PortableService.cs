using System.Globalization;
using System.IO;
using Kasane.Layout;

namespace Kasane.Model.Portable;

/// <summary>保存先のフォルダ（%LOCALAPPDATA%\applet\kasane）にある設定を、まとめて書き出す・読み込む。</summary>
public static class PortableService
{
    public sealed record StoredKeyboard(uint UniqId, KeymapProject Project, KeyboardLayout? Layout, HelperConfig? Helper);

    /// <summary>1つの対応: ファイルの何番目のキーボードを、どの UniqId のキーボードに入れるか（null = 読み込まない）。</summary>
    public sealed record Mapping(int SourceIndex, uint? TargetUniqId);

    /// <summary>接続中のキーボード（対応付けの候補）。</summary>
    public sealed record TargetDevice(uint UniqId, string? HardwareId, string DisplayName);

    private static string Base(string? baseDirectory) => baseDirectory ?? HelperConfig.BaseDirectory;

    public static string TrayPath(string? baseDirectory) =>
        baseDirectory == null ? TrayConfig.DefaultPath : Path.Combine(baseDirectory, "tray.json");

    /// <summary>レイアウトを探す（保存されたパス → 同梱の名前）。</summary>
    public static KeyboardLayout? FindLayout(string? name, string? layoutPath)
    {
        try
        {
            if (layoutPath != null && File.Exists(layoutPath)) return KeyboardLayout.Load(layoutPath);
            if (string.IsNullOrEmpty(name)) return null;
            var file = KeyboardLayout.EnumerateLayoutFiles()
                .FirstOrDefault(f => KeyboardLayout.NameFromPath(f).Equals(name, StringComparison.OrdinalIgnoreCase));
            return file != null ? KeyboardLayout.Load(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>保存先にあるキーボードの設定をすべて読む（読めないものは、報告に出して飛ばす）。</summary>
    public static List<StoredKeyboard> LoadAll(string? baseDirectory, PortableReport report)
    {
        var list = new List<StoredKeyboard>();
        string dir = Base(baseDirectory);
        if (!Directory.Exists(dir)) return list;
        foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(sub);
            if (name.Length != 8 || !uint.TryParse(name, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint uniq)) continue;
            if (!File.Exists(Path.Combine(sub, "project.json"))) continue;
            KeymapProject? project;
            try { project = ProjectService.TryLoad(uniq, baseDirectory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                report.Warn($"{name}: project.json を読めないため、飛ばします（{ex.Message}）");
                continue;
            }
            if (project == null) continue;
            project.UniqId = uniq;
            HelperConfig.TryLoad(HelperConfig.DevicePath(uniq, baseDirectory), out var helper, out string? helperError);
            if (helperError != null) report.Warn($"{name}: helper.json を読めないため、ヘルパー設定は含めません（{helperError}）");
            list.Add(new StoredKeyboard(uniq, project, FindLayout(project.LayoutName, project.LayoutPath), helper));
        }
        return list;
    }

    /// <summary>全体一括: 保存先のすべてのキーボードと、トレイの設定を1つのファイルにする。</summary>
    public static PortableFile ExportAll(string? baseDirectory, ExportOptions options, PortableReport report)
    {
        var file = PortableExporter.NewFile(PortableScope.All);
        foreach (var kb in LoadAll(baseDirectory, report))
        {
            if (kb.Layout == null)
            {
                report.Warn($"{kb.UniqId:X8}: レイアウト「{kb.Project.LayoutName}」が見つからないため、書き出せませんでした");
                continue;
            }
            ProjectService.NormalizeShape(kb.Project, kb.Layout);
            var sub = new PortableReport();
            file.Keyboards.Add(PortableExporter.ExportKeyboard(kb.Project, kb.Layout, kb.Helper, options, sub));
            foreach (var l in sub.Lines) report.Lines.Add(l with { Text = $"{kb.UniqId:X8}: {l.Text}" });
        }
        var tray = TrayConfig.Load(TrayPath(baseDirectory));
        file.Tray = new PortableTray
        {
            ImeLink = tray.ImeLink,
            Helper = options.IncludeHelper && tray.Helper != null && PortableExporter.HasContent(tray.Helper)
                ? PortableExporter.CloneHelper(tray.Helper) : null,
        };
        report.Info($"キーボード{file.Keyboards.Count}台を書き出しました");
        return file;
    }

    /// <summary>今の設定を、置き換える前に自動でバックアップする（実測値も含める）。書いたファイルの場所を返す。</summary>
    public static string BackupAll(string? baseDirectory, PortableReport report, string prefix = "before-import")
    {
        var sub = new PortableReport();
        var file = ExportAll(baseDirectory, new ExportOptions { IncludeMeasured = true, IncludeHelper = true }, sub);
        string path = Path.Combine(Base(baseDirectory), "backup", $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.kasane.json");
        PortableIO.Write(path, file);
        report.Info($"今の設定をバックアップしました: {path}");
        return path;
    }

    /// <summary>
    /// ファイルのキーボードを、接続中のキーボードに自動で対応付ける。UniqId が同じものを先に、次に HardwareId が同じもの
    /// （1台ずつ）を割り当てる。保存先にだけあるキーボードも、UniqId が同じなら対応付ける。
    /// </summary>
    public static List<Mapping> AutoMap(PortableFile file, IReadOnlyList<TargetDevice> devices)
    {
        var maps = new Mapping?[file.Keyboards.Count];
        var taken = new HashSet<uint>();
        for (int i = 0; i < file.Keyboards.Count; i++)
        {
            if (file.Keyboards[i].UniqIdValue is uint u && devices.Any(d => d.UniqId == u) && taken.Add(u)) maps[i] = new Mapping(i, u);
        }
        for (int i = 0; i < file.Keyboards.Count; i++)
        {
            if (maps[i] != null) continue;
            string? hw = file.Keyboards[i].HardwareId;
            var d = string.IsNullOrEmpty(hw) ? null
                : devices.FirstOrDefault(x => !taken.Contains(x.UniqId) && string.Equals(x.HardwareId, hw, StringComparison.OrdinalIgnoreCase));
            if (d != null) { taken.Add(d.UniqId); maps[i] = new Mapping(i, d.UniqId); }
        }
        return Enumerable.Range(0, maps.Length).Select(i => maps[i] ?? new Mapping(i, null)).ToList();
    }

    /// <summary>
    /// 全体一括の読み込み。置き換えの前に、今の設定をバックアップする（バックアップできなければ、何も変えない）。
    /// プロジェクトとヘルパー設定は、保存先に直接書く（「適用」はしない）。書き換えた UniqId を返す。
    /// </summary>
    public static List<uint> ImportAll(PortableFile file, IReadOnlyList<Mapping> mappings, ImportOptions o, string? baseDirectory,
                                       PortableReport report, Func<string?, KeyboardLayout?>? resolveLayout = null)
    {
        resolveLayout ??= name => FindLayout(name, null); // 新しく作るキーボードのレイアウト（ファイルのレイアウト名で探す）
        var changed = new List<uint>();
        try { BackupAll(baseDirectory, report); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Error($"今の設定をバックアップできなかったため、読み込みを中止しました: {ex.Message}");
            return changed;
        }

        var tray = TrayConfig.Load(TrayPath(baseDirectory));
        foreach (var map in mappings)
        {
            if (map.TargetUniqId is not uint uniq || map.SourceIndex < 0 || map.SourceIndex >= file.Keyboards.Count) continue;
            var src = file.Keyboards[map.SourceIndex];
            string tag = $"{uniq:X8}";

            KeymapProject? project;
            try { project = ProjectService.TryLoad(uniq, baseDirectory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                report.Error($"{tag}: project.json を読めないため、飛ばしました（{ex.Message}）");
                continue;
            }
            KeyboardLayout? layout = project != null ? FindLayout(project.LayoutName, project.LayoutPath) : null;
            if (project == null)
            {
                layout = resolveLayout(src.LayoutName);
                if (layout == null) { report.Error($"{tag}: レイアウト「{src.LayoutName}」が見つからないため、飛ばしました"); continue; }
                project = ProjectService.CreateDefault(uniq, src.HardwareId, layout, bundled: true);
            }
            else if (layout == null)
            {
                report.Error($"{tag}: レイアウト「{project.LayoutName}」が見つからないため、飛ばしました");
                continue;
            }
            project.UniqId = uniq;

            bool helperReadable = HelperConfig.TryLoad(HelperConfig.DevicePath(uniq, baseDirectory), out var helper, out string? helperError);
            if (!helperReadable)
            {
                report.Warn($"{tag}: helper.json を読めないため、ヘルパー設定は読み込みません（{helperError}）");
                helper = null;
            }
            var oo = Clone(o);
            if (!helperReadable) oo.Helper = false;

            var res = PortableImporter.Import(src, project, layout!, helper, tray.Helper, oo, layerScope: false);
            foreach (var l in res.Report.Lines) report.Lines.Add(l with { Text = $"{tag}: {l.Text}" });
            try
            {
                ProjectService.Save(project, baseDirectory);
                if (res.Helper != null) res.Helper.Save(HelperConfig.DevicePath(uniq, baseDirectory));
                changed.Add(uniq);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Error($"{tag}: 保存できませんでした（{ex.Message}）");
            }
        }

        if (file.Tray != null && o.Helper)
        {
            try
            {
                if (o.Mode == ImportMode.Replace)
                {
                    if (file.Tray.ImeLink is bool il) tray.ImeLink = il;
                    if (file.Tray.Helper != null) tray.Helper = PortableExporter.CloneHelper(file.Tray.Helper);
                }
                else if (file.Tray.Helper != null)
                {
                    tray.Helper = HelperMerge.Merge(tray.Helper, null, file.Tray.Helper, ImportMode.Append, out _, report);
                }
                tray.Save(TrayPath(baseDirectory));
                report.Info("トレイの設定（共通）を読み込みました");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Error($"トレイの設定を保存できませんでした（{ex.Message}）");
            }
        }
        return changed;
    }

    private static ImportOptions Clone(ImportOptions o) => new()
    {
        Layers = o.Layers, Macros = o.Macros, Combos = o.Combos, TapDances = o.TapDances, KeyOverrides = o.KeyOverrides,
        Timing = o.Timing, Features = o.Features, Nicola = o.Nicola, Helper = o.Helper, MeasuredScanCodes = o.MeasuredScanCodes,
        Mode = o.Mode, TargetLayer = o.TargetLayer,
    };
}
