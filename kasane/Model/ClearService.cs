using System.IO;
using Kasane.Interop;
using Kasane.Layout;
using Kasane.Model.Portable;

namespace Kasane.Model;

/// <summary>消す項目（「インポート／エクスポート」の「クリア」タブ。2026-10-21）。選んだ項目だけを初期状態に戻し、選ばなかった項目は残す。</summary>
public sealed class ClearOptions
{
    public bool Layers { get; set; }
    public bool Macros { get; set; }
    public bool Combos { get; set; }
    public bool TapDances { get; set; }
    public bool KeyOverrides { get; set; }
    public bool Timing { get; set; }
    public bool Features { get; set; }
    public bool Nicola { get; set; }
    /// <summary>ヘルパー設定（キーボードごとの helper.json。全キーボードを対象にしたときは、トレイの共通のヘルパー設定も）。</summary>
    public bool Helper { get; set; }
    public bool Measured { get; set; }

    public bool Any => Layers || Macros || Combos || TapDances || KeyOverrides || Timing || Features || Nicola || Helper || Measured;

    /// <summary>全初期化（すべての項目）。</summary>
    public static ClearOptions Everything() => new()
    {
        Layers = true, Macros = true, Combos = true, TapDances = true, KeyOverrides = true,
        Timing = true, Features = true, Nicola = true, Helper = true, Measured = true,
    };

    /// <summary>「ヘルパー設定」の表示名（Names() に入る名前と同じ）。</summary>
    public static string HelperName => Loc.T("ヘルパー設定", "Helper settings");

    /// <summary>確認画面・報告に出す、消す項目の名前。</summary>
    public List<string> Names()
    {
        var n = new List<string>();
        if (Layers) n.Add(Loc.T("レイヤー(キー割り当て)", "Layers (key assignments)"));
        if (Macros) n.Add(Loc.T("マクロ", "Macros"));
        if (Combos) n.Add(Loc.T("コンボ", "Combos"));
        if (TapDances) n.Add(Loc.T("タップダンス", "Tap dances"));
        if (KeyOverrides) n.Add(Loc.T("キー置換", "Key overrides"));
        if (Timing) n.Add(Loc.T("タイミング", "Timing"));
        if (Features) n.Add(Loc.T("機能のON/OFF", "Feature ON/OFF"));
        if (Nicola) n.Add("NICOLA");
        if (Helper) n.Add(HelperName);
        if (Measured) n.Add(Loc.T("実測したスキャンコード", "Measured scan codes"));
        return n;
    }
}

/// <summary>
/// 設定のクリア。画面の設定（project.json）・ヘルパー設定（helper.json）・ドライバに保存された設定を、選んだ項目だけ初期状態へ戻す。
/// 戻す前に、今の設定を自動でバックアップする（ドライバにだけあって画面の設定にない内容は、バックアップに入らない）。
/// </summary>
public static class ClearService
{
    /// <summary>画面の設定（プロジェクト）の、選んだ項目を初期状態にする。選ばなかった項目は触らない。</summary>
    public static void ResetProject(KeymapProject project, KeyboardLayout? layout, ClearOptions o)
    {
        if (o.Layers)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout), Loc.T("レイヤーを消すには、レイアウトが要ります", "A layout is required to clear layers"));
            project.Layers = new List<List<string>> { ProjectService.DefaultLayer(layout, 0) };
            project.LayerNames = null;
        }
        if (o.Macros) { project.Macros = new(); project.MacroNames = null; }
        if (o.Combos) { project.Combos = new(); project.ComboNames = null; }
        if (o.TapDances) { project.TapDances = new(); project.TapDanceNames = null; }
        if (o.KeyOverrides) { project.KeyOverrides = new(); project.KeyOverrideNames = null; }
        if (o.Timing)
        {
            project.TapTermMs = 0;
            project.ComboTermMs = 0;
            project.TapDanceTermMs = 0;
            project.TapHoldMode = 0;
            project.QuickTapTermMs = 0;
            project.TappingToggle = 0;
        }
        if (o.Features) project.EnabledFeatures = null; // GUI では管理しない（ドライバは、全機能が有効の状態に戻す）
        if (o.Nicola)
        {
            project.Nicola = null;
            project.ClearNicola = true; // 次の「適用」でも、ドライバの NICOLA を消す（ドライバをここで消せたときは、呼び出し側が false に戻す）
        }
        if (o.Measured) project.MeasuredScanCodes = new();
    }

    /// <summary>
    /// ドライバに保存された設定の、選んだ項目を初期状態にする。消した内容を、報告に出す。
    /// レイヤーを消すときは、先にOFFにする。同じ型番で共有している設定（保存先が「同じ型番」「すべて」）は、同じ型番のキーボードにも効く。
    /// </summary>
    public static void ClearDriver(KbdLayerClient client, uint uniqId, ClearOptions o, PortableReport report)
    {
        string tag = $"{uniqId:X8}";
        if (o.Layers)
        {
            client.SetEnabled(uniqId, false);
            client.ClearLayerMap(uniqId);
            report.Info(Loc.T($"{tag}: ドライバのレイヤーマップを消しました(OFF)", $"{tag}: Cleared the driver layer map (OFF)"));
        }
        if (o.Macros)
        {
            int n = 0;
            for (int m = 0; m < KbdLayerClient.MaxMacroCount; m++)
            {
                if (client.GetMacro(uniqId, m).Count == 0) continue;
                client.SetMacro(uniqId, m, new List<MacroStep>());
                n++;
            }
            report.Info(Loc.T($"{tag}: ドライバのマクロを消しました({n}件)", $"{tag}: Cleared driver macros ({n})"));
        }
        if (o.Combos)
        {
            int n = 0;
            for (int c = 0; c < KbdLayerClient.MaxComboCount; c++)
            {
                if (client.GetCombo(uniqId, c) == null) continue;
                client.SetCombo(uniqId, c, null);
                n++;
            }
            report.Info(Loc.T($"{tag}: ドライバのコンボを消しました({n}件)", $"{tag}: Cleared driver combos ({n})"));
        }
        if (o.TapDances)
        {
            int n = 0;
            for (int t = 0; t < KbdLayerClient.MaxTapDanceCount; t++)
            {
                if (client.GetTapDance(uniqId, t) == null) continue;
                client.SetTapDance(uniqId, t, null);
                n++;
            }
            report.Info(Loc.T($"{tag}: ドライバのタップダンスを消しました({n}件)", $"{tag}: Cleared driver tap dances ({n})"));
        }
        if (o.KeyOverrides)
        {
            try
            {
                int n = 0;
                for (int k = 0; k < KbdLayerClient.MaxKeyOverrideCount; k++)
                {
                    if (client.GetKeyOverride(uniqId, k) == null) continue;
                    client.SetKeyOverride(uniqId, k, null);
                    n++;
                }
                report.Info(Loc.T($"{tag}: ドライバのキー置換を消しました({n}件)", $"{tag}: Cleared driver key overrides ({n})"));
            }
            catch (KbdLayerException)
            {
                report.Info(Loc.T($"{tag}: このドライバはキー置換に対応していないため、飛ばしました", $"{tag}: Skipped because this driver does not support key overrides"));
            }
        }
        if (o.Timing)
        {
            client.SetConfig(uniqId, 0, 0, 0, 0, 0, 0);
            report.Info(Loc.T($"{tag}: ドライバのタイミングを既定に戻しました", $"{tag}: Reset driver timing to defaults"));
        }
        if (o.Features)
        {
            try { client.SetFeatures(uniqId, KbdLayerClient.FeatureAll); }
            catch (KbdLayerException) { /* 機能別ON/OFFより前のドライバ。全機能が有効なので、送れなくても結果は同じ */ }
            report.Info(Loc.T($"{tag}: ドライバの機能のON/OFFを、すべて有効に戻しました", $"{tag}: Reset driver feature ON/OFF to all enabled"));
        }
        if (o.Nicola)
        {
            if (client.GetNicola(uniqId).Length > 0) client.SetNicola(uniqId, Array.Empty<byte>());
            report.Info(Loc.T($"{tag}: ドライバのNICOLAを消しました", $"{tag}: Cleared driver NICOLA settings"));
        }
    }

    /// <summary>
    /// 保存先（project.json・helper.json・全キーボードのときは tray.json の共通のヘルパー設定）と、ドライバ（<paramref name="client"/> が null でなければ）を、
    /// 選んだ項目だけ消す。最初に今の設定をバックアップし、できなければ何も変えない。消せたキーボードの UniqId を返す。
    /// </summary>
    /// <param name="includeTrayHelper">全キーボードを対象にしたとき true（トレイの共通のヘルパー設定も消す）。</param>
    public static List<uint> Clear(IReadOnlyList<uint> uniqIds, ClearOptions o, KbdLayerClient? client, bool includeTrayHelper,
                                   string? baseDirectory, PortableReport report)
    {
        var cleared = new List<uint>();
        if (!o.Any) { report.Warn(Loc.T("消す項目が選ばれていません", "No items are selected to clear")); return cleared; }
        try { PortableService.BackupAll(baseDirectory, report, "before-clear"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Error(Loc.T($"今の設定をバックアップできなかったため、クリアを中止しました: {ex.Message}", $"Clearing was canceled because the current settings could not be backed up: {ex.Message}"));
            return cleared;
        }

        foreach (uint uniq in uniqIds.Distinct())
        {
            string tag = $"{uniq:X8}";
            bool driverCleared = false;
            try
            {
                // ドライバを先に（消せなければ、画面の設定を残したまま、報告に出す）。
                if (client != null)
                {
                    ClearDriver(client, uniq, o, report);
                    driverCleared = true;
                }

                var project = ProjectService.TryLoad(uniq, baseDirectory);
                if (project != null)
                {
                    var layout = PortableService.FindLayout(project.LayoutName, project.LayoutPath);
                    if (layout == null && o.Layers) report.Warn(Loc.T($"{tag}: レイアウト「{project.LayoutName}」が見つからないため、レイヤーは画面の設定から消せませんでした", $"{tag}: Layout “{project.LayoutName}” was not found, so layers could not be cleared from the app settings"));
                    else
                    {
                        ResetProject(project, layout, o);
                        if (o.Nicola && driverCleared) project.ClearNicola = false;
                        ProjectService.Save(project, baseDirectory);
                        report.Info(Loc.T($"{tag}: 画面の設定(project.json)の " + string.Join("・", o.Names().Where(n => n != ClearOptions.HelperName)) + " を初期状態にしました", $"{tag}: Reset " + string.Join(", ", o.Names().Where(n => n != ClearOptions.HelperName)) + " in the app settings (project.json)"));
                    }
                }

                if (o.Helper)
                {
                    string helperPath = HelperConfig.DevicePath(uniq, baseDirectory);
                    if (File.Exists(helperPath)) { File.Delete(helperPath); report.Info(Loc.T($"{tag}: ヘルパー設定(helper.json)を消しました", $"{tag}: Deleted helper settings (helper.json)")); }
                }
                cleared.Add(uniq);
            }
            catch (KbdLayerException ex)
            {
                report.Error(Loc.T($"{tag}: ドライバの設定を消せませんでした({ex.Message})。途中まで消えている可能性があります", $"{tag}: Could not clear the driver settings ({ex.Message}). Some may already have been cleared"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                report.Error(Loc.T($"{tag}: 保存できませんでした({ex.Message})", $"{tag}: Could not save ({ex.Message})"));
            }
        }

        if (o.Helper && includeTrayHelper)
        {
            try
            {
                string trayPath = PortableService.TrayPath(baseDirectory);
                var tray = TrayConfig.Load(trayPath);
                if (tray.Helper != null)
                {
                    tray.Helper = null;
                    tray.Save(trayPath);
                    report.Info(Loc.T("トレイの共通のヘルパー設定(tray.json の Helper)を消しました", "Cleared the tray's shared helper settings (Helper in tray.json)"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Error(Loc.T($"tray.json に書けませんでした({ex.Message})", $"Could not write tray.json ({ex.Message})"));
            }
        }
        return cleared;
    }
}
