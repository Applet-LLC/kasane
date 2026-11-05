using System.IO;
using System.Text;
using Kasane.Interop;
using Kasane.Layout;

namespace Kasane.Model.Portable;

/// <summary>復旧用の設定のうち、ドライバに入っているもの（緊急解除・i8042prt・共有範囲）の読み書き。テストで差し替えられるように、窓口を分ける。</summary>
public interface IRecoveryDriver
{
    List<ushort> GetEmergencyRelease();
    void SetEmergencyRelease(IReadOnlyList<ushort> scanCodes);
    I8042Config GetI8042Config();
    void SetI8042Config(I8042Config config);
    DeviceScope GetDeviceScope(uint uniqId);
    DeviceScope SetDeviceScope(uint uniqId, uint binding);
}

/// <summary><see cref="KbdLayerClient"/> を <see cref="IRecoveryDriver"/> として使う。</summary>
public sealed class ClientRecoveryDriver : IRecoveryDriver
{
    private readonly KbdLayerClient _client;
    public ClientRecoveryDriver(KbdLayerClient client) => _client = client;

    public List<ushort> GetEmergencyRelease() => _client.GetEmergencyRelease();
    public void SetEmergencyRelease(IReadOnlyList<ushort> scanCodes) => _client.SetEmergencyRelease(scanCodes);
    public I8042Config GetI8042Config() => _client.GetI8042Config();
    public void SetI8042Config(I8042Config config) => _client.SetI8042Config(config);
    public DeviceScope GetDeviceScope(uint uniqId) => _client.GetDeviceScope(uniqId);
    public DeviceScope SetDeviceScope(uint uniqId, uint binding) => _client.SetDeviceScope(uniqId, binding);
}

/// <summary>全体一括のエクスポートに、復旧用の設定を集める。</summary>
public static class RecoveryExporter
{
    /// <summary>
    /// 集める。<paramref name="driver"/> が null（ドライバに接続していない）ときは、ドライバに入っている項目は含めず、報告に出す。
    /// 読めない項目も、報告に出して飛ばす（ほかの項目は集める）。
    /// </summary>
    /// <param name="connectedUniqIds">共有範囲を読むキーボード（接続中のもの）。</param>
    public static PortableRecovery Collect(IRecoveryDriver? driver, IEnumerable<uint> connectedUniqIds, string? baseDirectory, PortableReport report)
    {
        var recovery = new PortableRecovery();

        var tray = TrayConfig.Load(PortableService.TrayPath(baseDirectory));
        recovery.TraySettings = new PortableTraySettings
        {
            ImeSource = tray.ImeSource,
            ImeSwitch = tray.ImeSwitchValue.ToString(),
            ColorMode = tray.ColorModeName,
            UiLanguage = tray.UiLanguageName,
            ShowLayerOnIcon = tray.ShowLayerOnIcon,
            RemapOnlyWhileTrayRuns = tray.RemapOnlyWhileTrayRuns,
            DesktopViewMode = tray.DesktopViewMode,
        };

        var layouts = CollectLayouts(baseDirectory, report);
        if (layouts.Count > 0) recovery.Layouts = layouts;

        if (driver == null)
        {
            report.Warn(Loc.T("ドライバに接続していないため、緊急解除・i8042prt・共有範囲は含めません", "Emergency release, i8042prt and sharing scopes are not included because the driver is not connected"));
            return recovery;
        }

        try { recovery.EmergencyRelease = driver.GetEmergencyRelease(); }
        catch (Exception ex) when (ex is KbdLayerException or InvalidOperationException)
        {
            report.Warn(Loc.T($"緊急解除の組み合わせを読めないため、含めません({ex.Message})", $"The emergency release combination is not included because it could not be read ({ex.Message})"));
        }

        try
        {
            var config = driver.GetI8042Config();
            recovery.I8042 = new PortableI8042
            {
                Enabled = config.Enabled,
                Profiles = config.Profiles.Select(p => new PortableI8042Profile
                {
                    ModelKey = p.ModelKey, Type = p.Type, Subtype = p.Subtype, Identifier = p.Identifier, LayerJpn = p.LayerJpn, LayerKor = p.LayerKor,
                }).ToList(),
            };
        }
        catch (Exception ex) when (ex is KbdLayerException or InvalidOperationException)
        {
            report.Warn(Loc.T($"i8042prt の設定を読めないため、含めません({ex.Message})", $"The i8042prt settings are not included because they could not be read ({ex.Message})"));
        }

        var scopes = new List<PortableDeviceScope>();
        foreach (uint id in connectedUniqIds.Where(i => i != 0).Distinct())
        {
            try { scopes.Add(new PortableDeviceScope { UniqId = PortableKeyboard.FormatUniqId(id), Binding = driver.GetDeviceScope(id).Binding }); }
            catch (Exception ex) when (ex is KbdLayerException or InvalidOperationException)
            {
                report.Warn(Loc.T($"{id:X8}: 共有範囲を読めないため、含めません({ex.Message})", $"{id:X8}: the sharing scope is not included because it could not be read ({ex.Message})"));
            }
        }
        if (scopes.Count > 0) recovery.DeviceScopes = scopes;

        report.Info(Loc.T("復旧用の設定(トレイの設定、緊急解除、i8042prt、共有範囲、利用者のレイアウト)を含めました",
                          "Included the recovery settings (tray settings, emergency release, i8042prt, sharing scopes, user layouts)"));
        return recovery;
    }

    /// <summary>利用者のレイアウト（kle-&lt;名前&gt;.json）を集める。同梱のレイアウトと名前が重なるものは含めない。</summary>
    private static List<PortableUserLayout> CollectLayouts(string? baseDirectory, PortableReport report)
    {
        var list = new List<PortableUserLayout>();
        var dirs = baseDirectory == null
            ? new[] { LayoutFolders.UserDirectory, LayoutFolders.LegacyUserDirectory }
            : new[] { Path.Combine(baseDirectory, "Layouts"), null };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            if (dir == null || !Directory.Exists(dir)) continue;
            foreach (string file in Directory.GetFiles(dir, "kle-*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string name = KeyboardLayout.NameFromPath(file);
                if (!seen.Add(name) || LayoutFolders.IsBundledName(name)) continue;
                try { list.Add(new PortableUserLayout { Name = name, Json = File.ReadAllText(file, Encoding.UTF8) }); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    report.Warn(Loc.T($"レイアウト「{name}」を読めないため、含めません({ex.Message})", $"Layout \"{name}\" is not included because it could not be read ({ex.Message})"));
                }
            }
        }
        return list;
    }
}

/// <summary>全体一括の読み込みで、復旧用の設定を戻す。</summary>
public static class RecoveryImporter
{
    /// <summary>
    /// 起動時の取り込み（<see cref="PendingImport"/>）用に、まだ無いものだけを残した復旧用の設定を作る。
    /// トレイの設定は tray.json が無いとき（<paramref name="trayFileMissing"/>）だけ、レイアウトはその名前のファイルが無いものだけ。
    /// ドライバに入れる項目（緊急解除・i8042prt・共有範囲）は、ここでは扱わないので入れない。残るものが無ければ null。
    /// </summary>
    public static PortableRecovery? OnlyMissing(PortableRecovery? recovery, bool trayFileMissing, string? baseDirectory)
    {
        if (recovery == null) return null;
        string dir = Path.Combine(baseDirectory ?? HelperConfig.BaseDirectory, "Layouts");
        var layouts = recovery.Layouts?.Where(l => IsSafeName(l.Name) && !File.Exists(Path.Combine(dir, $"kle-{l.Name}.json"))).ToList();
        var result = new PortableRecovery
        {
            TraySettings = trayFileMissing ? recovery.TraySettings : null,
            Layouts = layouts is { Count: > 0 } ? layouts : null,
        };
        return result.TraySettings == null && result.Layouts == null ? null : result;
    }

    /// <summary>
    /// ファイルに書くもの（トレイの設定、利用者のレイアウト）を戻す。管理者権限は要らない。
    /// トレイの設定は、ファイルにあるものだけを上書きする（無い項目は今のまま）。レイアウトは、同じ名前のファイルがあれば上書きしない。
    /// </summary>
    public static void ApplyFiles(PortableRecovery recovery, string? baseDirectory, PortableReport report)
    {
        if (recovery.TraySettings is { } t)
        {
            try
            {
                string path = PortableService.TrayPath(baseDirectory);
                var tray = TrayConfig.Load(path);
                if (t.ImeSource != null && Enum.TryParse<ImeSourceMode>(t.ImeSource, out _)) tray.ImeSource = t.ImeSource;
                if (t.ImeSwitch != null && Enum.TryParse<ImeSwitch>(t.ImeSwitch, ignoreCase: true, out var imeSwitch) && Enum.IsDefined(imeSwitch))
                    tray.ImeSwitchName = imeSwitch == ImeSwitch.Auto ? null : imeSwitch.ToString(); // 既定は書かない
                if (t.ColorMode != null && Enum.TryParse<ColorMode>(t.ColorMode, out _)) tray.ColorModeName = t.ColorMode;
                if (t.UiLanguage != null && Enum.TryParse<UiLanguage>(t.UiLanguage, out _)) tray.UiLanguageName = t.UiLanguage;
                if (t.ShowLayerOnIcon is bool show) tray.ShowLayerOnIcon = show;
                if (t.RemapOnlyWhileTrayRuns is bool only) tray.RemapOnlyWhileTrayRuns = only;
                if (t.DesktopViewMode is int dv && dv >= 0 && dv <= 4) tray.DesktopViewMode = dv;
                tray.Save(path);
                report.Info(Loc.T("トレイの設定(表示・色・言語・デスクトップなど)を読み込みました", "Imported the tray settings (display, color, language, desktop, etc.)"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Error(Loc.T($"トレイの設定を保存できませんでした({ex.Message})", $"Could not save the tray settings ({ex.Message})"));
            }
        }

        if (recovery.Layouts is { Count: > 0 } layouts) ApplyLayouts(layouts, baseDirectory, report);
    }

    private static void ApplyLayouts(List<PortableUserLayout> layouts, string? baseDirectory, PortableReport report)
    {
        string dir = Path.Combine(baseDirectory ?? HelperConfig.BaseDirectory, "Layouts");
        int written = 0;
        foreach (var layout in layouts)
        {
            // ファイルの内容は信用しない: 名前にパスの区切りや「..」があれば、保存先の外へ書けてしまうので、飛ばす。
            if (!IsSafeName(layout.Name) || string.IsNullOrWhiteSpace(layout.Json))
            {
                report.Warn(Loc.T($"レイアウト「{layout.Name}」は、名前または中身が不正なので、飛ばしました", $"Skipped layout \"{layout.Name}\" because its name or contents are invalid"));
                continue;
            }
            if (LayoutFolders.IsBundledName(layout.Name))
            {
                report.Warn(Loc.T($"レイアウト「{layout.Name}」は、同梱のレイアウトと名前が重なるので、飛ばしました", $"Skipped layout \"{layout.Name}\" because its name is the same as a bundled layout"));
                continue;
            }
            string path = Path.Combine(dir, $"kle-{layout.Name}.json");
            try
            {
                if (File.Exists(path))
                {
                    report.Info(Loc.T($"レイアウト「{layout.Name}」は、すでにあるので、そのままにしました", $"Layout \"{layout.Name}\" already exists, so it was left as is"));
                    continue;
                }
                Directory.CreateDirectory(dir);
                File.WriteAllText(path, layout.Json, new UTF8Encoding(false));
                written++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Error(Loc.T($"レイアウト「{layout.Name}」を保存できませんでした({ex.Message})", $"Could not save layout \"{layout.Name}\" ({ex.Message})"));
            }
        }
        if (written > 0) report.Info(Loc.T($"利用者のレイアウトを{written}件、読み込みました", $"Imported {written} user layout(s)"));
    }

    /// <summary>ファイル名に使える名前か（区切り、「..」、予約文字を含まない）。</summary>
    internal static bool IsSafeName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 100 && name == name.Trim()
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.Contains("..") && name != ".";

    /// <summary>
    /// ドライバに入れるもの（緊急解除、i8042prt、共有範囲）を戻す。管理者権限のクライアントが要る。
    /// 共有範囲は、いま接続しているキーボード（<paramref name="connectedUniqIds"/>）で、UniqId が同じものだけ。
    /// </summary>
    public static void ApplyDriver(PortableRecovery recovery, IRecoveryDriver driver, IReadOnlyCollection<uint> connectedUniqIds, PortableReport report)
    {
        if (recovery.EmergencyRelease is { } keys)
        {
            try
            {
                driver.SetEmergencyRelease(keys);
                report.Info(Loc.T("緊急解除の組み合わせを読み込みました", "Imported the emergency release combination"));
            }
            catch (Exception ex) when (ex is KbdLayerException or ArgumentException or InvalidOperationException)
            {
                report.Error(Loc.T($"緊急解除の組み合わせを書けませんでした({ex.Message})", $"Could not write the emergency release combination ({ex.Message})"));
            }
        }

        if (recovery.I8042 is { } i)
        {
            try
            {
                var profiles = i.Profiles.Select(p => new I8042Profile(p.ModelKey, p.Type, p.Subtype, p.Identifier, p.LayerJpn, p.LayerKor)).ToList();
                driver.SetI8042Config(new I8042Config(i.Enabled, profiles));
                report.Info(Loc.T("i8042prt の設定を読み込みました", "Imported the i8042prt settings"));
            }
            catch (Exception ex) when (ex is KbdLayerException or ArgumentException or InvalidOperationException)
            {
                report.Error(Loc.T($"i8042prt の設定を書けませんでした({ex.Message})", $"Could not write the i8042prt settings ({ex.Message})"));
            }
        }

        if (recovery.DeviceScopes is { Count: > 0 } scopes)
        {
            int applied = 0;
            foreach (var s in scopes)
            {
                if (PortableKeyboard.ParseUniqId(s.UniqId) is not uint id || !connectedUniqIds.Contains(id)) continue;
                if (s.Binding > KbdLayerClient.BindingGlobal) continue;
                try { driver.SetDeviceScope(id, s.Binding); applied++; }
                catch (Exception ex) when (ex is KbdLayerException or InvalidOperationException)
                {
                    report.Error(Loc.T($"{id:X8}: 共有範囲を書けませんでした({ex.Message})", $"{id:X8}: could not write the sharing scope ({ex.Message})"));
                }
            }
            report.Info(Loc.T($"共有範囲を{applied}台に読み込みました(接続中のキーボードのうち、UniqId が同じもの)", $"Imported the sharing scope for {applied} keyboard(s) (connected keyboards with the same UniqId)"));
        }
    }
}
