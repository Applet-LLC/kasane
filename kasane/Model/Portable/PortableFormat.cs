using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kasane.Model.Portable;

public enum PortableScope { All, Keyboard, Layer }

/// <summary>
/// 設定の交換ファイル（*.kasane.json）。キーボードのレイアウトに依存しないよう、レイヤーとコンボのキーは
/// スキャンコード（SC_XXXX）で持つ。項目が null のものは「ファイルに含まれていない」（読み込み時に触らない）、
/// 空のリストは「あるが空」（置き換えなら空にする）という意味。
/// </summary>
public sealed class PortableFile
{
    public const string FormatName = "kasane-export";
    public const int CurrentVersion = 1;

    public string Format { get; set; } = FormatName;
    public int Version { get; set; } = CurrentVersion;
    /// <summary>"All" / "Keyboard" / "Layer"。</summary>
    public string Scope { get; set; } = nameof(PortableScope.Keyboard);
    public string? Exported { get; set; }
    public string? App { get; set; }
    public List<PortableKeyboard> Keyboards { get; set; } = new();
    /// <summary>トレイの設定（全体一括のときだけ）。</summary>
    public PortableTray? Tray { get; set; }
    /// <summary>復旧用の設定（全体一括で、利用者が選んだときだけ。同じ PC への復元向け）。無ければ null。</summary>
    public PortableRecovery? Recovery { get; set; }

    [JsonIgnore]
    public PortableScope? ParsedScope =>
        Enum.TryParse<PortableScope>(Scope, ignoreCase: true, out var s) && Enum.IsDefined(s) ? s : null;
}

public sealed class PortableTray
{
    public bool? ImeLink { get; set; }
    /// <summary>全キーボード共通のヘルパー設定。</summary>
    public HelperConfig? Helper { get; set; }
}

/// <summary>
/// 復旧用の設定。ふだんのエクスポートには入らない設定（トレイの表示まわり、ドライバ全体の設定、利用者のレイアウト）を、
/// 同じ PC への復元のために、全体一括に加える。レイヤーだけ・このキーボードだけのエクスポート（他の人に渡す用）には入れない。
/// 項目が null のものは「ファイルに含まれていない」（読み込み時に触らない）。
/// </summary>
public sealed class PortableRecovery
{
    /// <summary>トレイの設定のうち、IME 連動と共通のヘルパー設定（<see cref="PortableTray"/>）以外。</summary>
    public PortableTraySettings? TraySettings { get; set; }
    /// <summary>緊急解除の組み合わせ（スキャンコード。ドライバ全体で1つ）。</summary>
    public List<ushort>? EmergencyRelease { get; set; }
    public PortableI8042? I8042 { get; set; }
    /// <summary>キーボードごとの共有範囲（0 = 同じ型番で共有、1 = この1台だけ、2 = すべてのキーボードで共有）。</summary>
    public List<PortableDeviceScope>? DeviceScopes { get; set; }
    /// <summary>利用者が「KLEから追加」で保存したレイアウト（同梱のものは含めない）。</summary>
    public List<PortableUserLayout>? Layouts { get; set; }
}

public sealed class PortableTraySettings
{
    public string? ImeSource { get; set; }
    public string? ColorMode { get; set; }
    public string? UiLanguage { get; set; }
    public bool? ShowLayerOnIcon { get; set; }
    public bool? RemapOnlyWhileTrayRuns { get; set; }
    /// <summary>デスクトップのアイコンの表示方法（0〜4）。null は「変えない」。</summary>
    public int? DesktopViewMode { get; set; }
}

public sealed class PortableI8042
{
    public bool Enabled { get; set; }
    public List<PortableI8042Profile> Profiles { get; set; } = new();
}

public sealed class PortableI8042Profile
{
    public uint ModelKey { get; set; }
    public uint? Type { get; set; }
    public uint? Subtype { get; set; }
    public string? Identifier { get; set; }
    public string? LayerJpn { get; set; }
    public string? LayerKor { get; set; }
}

public sealed class PortableDeviceScope
{
    /// <summary>"0xCDEC0000"（<see cref="PortableKeyboard.UniqId"/> と同じ形）。</summary>
    public string? UniqId { get; set; }
    public uint Binding { get; set; }
}

public sealed class PortableUserLayout
{
    /// <summary>レイアウトの名前（ファイル名から「kle-」と「.json」を除いたもの）。</summary>
    public string Name { get; set; } = "";
    /// <summary>レイアウトのファイルの中身（KLE の JSON）。</summary>
    public string Json { get; set; } = "";
}

public sealed class PortableLayer
{
    /// <summary>元のキーボードでのレイヤー番号。</summary>
    public int Index { get; set; }
    /// <summary>レイヤーの名前（任意）。</summary>
    public string? Name { get; set; }
    /// <summary>"SC_001E" → "KC_B"。透過（KC_TRNS）のキーは省く。</summary>
    public Dictionary<string, string> Keys { get; set; } = new();
}

public sealed class PortableCombo
{
    public int Id { get; set; }
    public string Key1 { get; set; } = "";
    public string Key2 { get; set; } = "";
    public string Output { get; set; } = "";
    /// <summary>無効にする（定義は残すが、キーボードへ適用しない）。false のときはファイルに書かない。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Disabled { get; set; }
}

public sealed class PortableTiming
{
    public uint TapTermMs { get; set; }
    public uint ComboTermMs { get; set; }
    public uint TapDanceTermMs { get; set; }
    public uint TapHoldMode { get; set; }
    public uint QuickTapTermMs { get; set; }
    public uint TappingToggle { get; set; }
}

public sealed class PortableKeyboard
{
    /// <summary>"0xCDEC0000"。</summary>
    public string? UniqId { get; set; }
    public string? HardwareId { get; set; }
    /// <summary>利用者がつけた、このキーボードの名前（任意）。</summary>
    public string? Name { get; set; }
    public string? LayoutName { get; set; }

    public List<PortableLayer>? Layers { get; set; }
    public List<MacroDef>? Macros { get; set; }
    public List<PortableCombo>? Combos { get; set; }
    public List<TapDanceDef>? TapDances { get; set; }
    public List<KeyOverrideDef>? KeyOverrides { get; set; }
    /// <summary>マクロ・コンボ・タップダンス・キー置換の名前（番号 → 名前。任意）。中身が空の番号の名前も入る。</summary>
    public Dictionary<int, string>? MacroNames { get; set; }
    public Dictionary<int, string>? ComboNames { get; set; }
    public Dictionary<int, string>? TapDanceNames { get; set; }
    public Dictionary<int, string>? KeyOverrideNames { get; set; }
    public PortableTiming? Timing { get; set; }
    public uint? EnabledFeatures { get; set; }
    public NicolaDef? Nicola { get; set; }
    /// <summary>そのキーボードの helper.json。</summary>
    public HelperConfig? Helper { get; set; }
    /// <summary>実測したスキャンコード（スロット番号 → "SC_XXXX"）。同じ機器へのバックアップのときだけ含める。</summary>
    public Dictionary<string, string>? MeasuredScanCodes { get; set; }

    [JsonIgnore]
    public uint? UniqIdValue => ParseUniqId(UniqId);

    public static string FormatUniqId(uint uniqId) => "0x" + uniqId.ToString("X8");

    public static uint? ParseUniqId(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, System.Globalization.NumberStyles.AllowHexSpecifier,
                             System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}

/// <summary>読み書き（UTF-8・BOM なし・日本語はそのまま・一時ファイル経由で置き換え）。</summary>
public static class PortableIO
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string ToJson(PortableFile file) => JsonSerializer.Serialize(file, Options);

    public static bool TryParse(string json, out PortableFile? file, out string? error)
    {
        file = null;
        error = null;
        try
        {
            var f = JsonSerializer.Deserialize<PortableFile>(json, Options);
            if (f == null) { error = Loc.T("中身が空です", "The file is empty"); return false; }
            if (!string.Equals(f.Format, PortableFile.FormatName, StringComparison.Ordinal))
            {
                error = Loc.T("kasane の設定ファイルではありません(Format が kasane-export ではありません)", "This is not a kasane settings file (Format is not kasane-export)");
                return false;
            }
            if (f.Version > PortableFile.CurrentVersion)
            {
                error = Loc.T($"新しい版(Version {f.Version})のファイルです。この版の kasane は Version {PortableFile.CurrentVersion} まで読めます", $"This file is from a newer version (Version {f.Version}). This version of kasane can read up to Version {PortableFile.CurrentVersion}");
                return false;
            }
            if (f.ParsedScope == null) { error = Loc.T($"Scope「{f.Scope}」は All / Keyboard / Layer のいずれかにしてください", $"Scope \"{f.Scope}\" must be one of All / Keyboard / Layer"); return false; }
            f.Keyboards ??= new();
            if (f.Keyboards.Count == 0 && f.Recovery == null) { error = Loc.T("キーボードの設定が入っていません", "The file contains no keyboard settings"); return false; }
            if (f.Keyboards.Any(k => k == null)) { error = Loc.T("Keyboards に空の項目があります", "Keyboards contains an empty entry"); return false; }
            file = f;
            return true;
        }
        catch (JsonException ex)
        {
            // .NET のメッセージは英語なので、場所だけを日本語で添える。
            error = ex.LineNumber is long line
                ? Loc.T($"JSON として読めません({line + 1}行目付近)。kasane が書き出したファイルを選んでください", $"Cannot read this as JSON (near line {line + 1}). Choose a file exported by kasane")
                : Loc.T("JSON として読めません。kasane が書き出したファイルを選んでください", "Cannot read this as JSON. Choose a file exported by kasane");
            return false;
        }
    }

    public static bool TryRead(string path, out PortableFile? file, out string? error)
    {
        file = null;
        try { return TryParse(File.ReadAllText(path, Encoding.UTF8), out file, out error); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static void Write(string path, PortableFile file)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, ToJson(file), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch (Exception) { /* 片付けられなくても、元の失敗を返す */ }
            throw;
        }
    }
}

public enum ReportLevel { Info, Warning, Error }

public sealed record ReportLine(ReportLevel Level, string Text);

/// <summary>書き出し・読み込みの結果の報告（画面に出す）。</summary>
public sealed class PortableReport
{
    public List<ReportLine> Lines { get; } = new();

    public void Info(string text) => Lines.Add(new ReportLine(ReportLevel.Info, text));
    public void Warn(string text) => Lines.Add(new ReportLine(ReportLevel.Warning, text));
    public void Error(string text) => Lines.Add(new ReportLine(ReportLevel.Error, text));

    public bool HasErrors => Lines.Any(l => l.Level == ReportLevel.Error);
    public int WarningCount => Lines.Count(l => l.Level == ReportLevel.Warning);

    public override string ToString() => string.Join("\n", Lines.Select(l =>
        (l.Level switch { ReportLevel.Error => Loc.T("エラー: ", "Error: "), ReportLevel.Warning => Loc.T("注意: ", "Note: "), _ => "" }) + l.Text));
}
