using System.IO;
using Kasane.Layout;

namespace Kasane.Model.Portable;

/// <summary>
/// 設定ファイル（*.kasane.json）の内容を、ドライバのレジストリ（LayerMaps\M_&lt;ModelKey&gt;）へ直接書く
/// （インストール時の取り込み。管理者保護の環境でも、インストール時の管理者権限で反映できるようにするため）。
/// キーボードの対応づけは、ファイルの HardwareId から計算した ModelKey による（保存先は「同じ型番で共有」）。
/// すでに設定がある保存先は置き換え、置き換える前の内容を「.bak」へ1世代だけ退避する（<see cref="PersistedSettings"/>）。
/// </summary>
public static class DriverImport
{
    public sealed record Result(int Written, int BackedUp, int Skipped);

    private static ImportOptions Options() => new()
    {
        Mode = ImportMode.Replace,
        Helper = false,            // ヘルパー設定はドライバの設定ではない（トレイ側。別に取り込む）
        MeasuredScanCodes = false,
    };

    /// <summary>ファイルの各キーボードを、ドライバの保存先へ書く。書けなかったものは報告に出して飛ばす。</summary>
    public static Result Apply(PortableFile file, PersistedSettings store, PortableReport report,
                               Func<string?, KeyboardLayout?>? resolveLayout = null)
    {
        resolveLayout ??= name => PortableService.FindLayout(name, null);
        int written = 0, backedUp = 0, skipped = 0;

        if (file.ParsedScope == PortableScope.Layer)
        {
            report.Error(Loc.T("レイヤー1枚だけのファイルは、インストール時の取り込みに使えません(キーボード全体のファイルを選んでください)", "A single-layer file cannot be used for import during installation (choose a whole-keyboard file)"));
            return new Result(0, 0, file.Keyboards.Count);
        }

        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < file.Keyboards.Count; i++)
        {
            var src = file.Keyboards[i];
            string tag = Loc.T($"キーボード{i + 1}" + (string.IsNullOrEmpty(src.HardwareId) ? "" : $"({src.HardwareId})"), $"Keyboard {i + 1}" + (string.IsNullOrEmpty(src.HardwareId) ? "" : $" ({src.HardwareId})"));

            uint? model = DeviceKeys.ModelKey(src.HardwareId);
            if (model == null)
            {
                report.Warn(Loc.T($"{tag}: HardwareId がファイルに無いため、保存先を決められず、飛ばしました", $"{tag}: skipped because the file has no HardwareId, so the destination cannot be determined"));
                skipped++;
                continue;
            }
            string name = DeviceKeys.ModelSubKeyName(model.Value);
            if (!done.Add(name))
            {
                report.Warn(Loc.T($"{tag}: 同じ型番({name})のキーボードが、ファイルに複数あります。最初の1つだけを書きます", $"{tag}: the file contains multiple keyboards of the same model ({name}). Only the first one is written"));
                skipped++;
                continue;
            }

            var layout = resolveLayout(src.LayoutName);
            if (layout == null)
            {
                report.Error(Loc.T($"{tag}: レイアウト「{src.LayoutName}」が見つからないため、飛ばしました", $"{tag}: skipped because the layout \"{src.LayoutName}\" was not found"));
                skipped++;
                continue;
            }

            try
            {
                var project = ProjectService.CreateDefault(0, src.HardwareId, layout, bundled: true);
                var res = PortableImporter.Import(src, project, layout, null, null, Options(), layerScope: false);
                foreach (var l in res.Report.Lines) report.Lines.Add(l with { Text = $"{tag}: {l.Text}" });

                var settings = ProjectService.Compile(project, layout);
                foreach (var w in settings.Warnings) report.Warn($"{tag}: {w}");

                bool bak = store.Write(name, settings);
                written++;
                if (bak) backedUp++;
                report.Info(Loc.T($"{tag}: {name} に書きました" + (bak ? $"(前の設定は {name}{PersistedSettings.BackupSuffix} に退避)" : ""), $"{tag}: written to {name}" + (bak ? $" (previous settings moved to {name}{PersistedSettings.BackupSuffix})" : "")));
            }
            catch (InvalidOperationException ex)
            {
                report.Error(Loc.T($"{tag}: 検証に失敗したため、飛ばしました({ex.Message.Replace('\n', ' ')})", $"{tag}: skipped because validation failed ({ex.Message.Replace('\n', ' ')})"));
                skipped++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                report.Error(Loc.T($"{tag}: レジストリに書けませんでした({ex.Message})", $"{tag}: could not write to the registry ({ex.Message})"));
                skipped++;
            }
        }

        if (file.Recovery != null) skipped += ApplyRecovery(file.Recovery, store, report);
        return new Result(written, backedUp, skipped);
    }

    /// <summary>
    /// 復旧用の設定のうち、ドライバのレジストリに入れるもの（緊急解除、i8042prt、キーボードごとの共有範囲）を書く。
    /// トレイの設定とレイアウトは、起動時の取り込み（<see cref="PendingImport"/>）が戻す。書けなかった項目の数を返す。
    /// 共有範囲は、キーボードが接続されていなくても書く（UniqId が同じ差し込み口に戻したときに効く）。
    /// </summary>
    public static int ApplyRecovery(PortableRecovery recovery, PersistedSettings store, PortableReport report)
    {
        int failed = 0;
        void Try(string what, Action write)
        {
            try { write(); report.Info(Loc.T($"{what}を書きました", $"Wrote {what}")); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                report.Error(Loc.T($"{what}を書けませんでした({ex.Message})", $"Could not write {what} ({ex.Message})"));
                failed++;
            }
        }

        if (recovery.EmergencyRelease is { } keys) Try(Loc.T("緊急解除の組み合わせ", "the emergency release combination"), () => store.WriteEmergencyRelease(keys));
        if (recovery.I8042 is { } i)
        {
            var profiles = i.Profiles.Select(p => new Kasane.Interop.I8042Profile(p.ModelKey, p.Type, p.Subtype, p.Identifier, p.LayerJpn, p.LayerKor)).ToList();
            Try(Loc.T("i8042prt の設定", "the i8042prt settings"), () => store.WriteI8042Config(new Kasane.Interop.I8042Config(i.Enabled, profiles)));
        }
        foreach (var s in recovery.DeviceScopes ?? new())
        {
            if (PortableKeyboard.ParseUniqId(s.UniqId) is not uint id) continue;
            Try(Loc.T($"{id:X8} の共有範囲", $"the sharing scope of {id:X8}"), () => store.WriteDeviceBinding(id, s.Binding));
        }
        return failed;
    }
}
