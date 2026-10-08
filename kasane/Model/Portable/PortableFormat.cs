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

public sealed class PortableLayer
{
    /// <summary>元のキーボードでのレイヤー番号。</summary>
    public int Index { get; set; }
    /// <summary>"SC_001E" → "KC_B"。透過（KC_TRNS）のキーは省く。</summary>
    public Dictionary<string, string> Keys { get; set; } = new();
}

public sealed class PortableCombo
{
    public int Id { get; set; }
    public string Key1 { get; set; } = "";
    public string Key2 { get; set; } = "";
    public string Output { get; set; } = "";
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
    public string? LayoutName { get; set; }

    public List<PortableLayer>? Layers { get; set; }
    public List<MacroDef>? Macros { get; set; }
    public List<PortableCombo>? Combos { get; set; }
    public List<TapDanceDef>? TapDances { get; set; }
    public List<KeyOverrideDef>? KeyOverrides { get; set; }
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
            if (f == null) { error = "中身が空です"; return false; }
            if (!string.Equals(f.Format, PortableFile.FormatName, StringComparison.Ordinal))
            {
                error = "kasane の設定ファイルではありません（Format が kasane-export ではありません）";
                return false;
            }
            if (f.Version > PortableFile.CurrentVersion)
            {
                error = $"新しい版（Version {f.Version}）のファイルです。この版の kasane は Version {PortableFile.CurrentVersion} まで読めます";
                return false;
            }
            if (f.ParsedScope == null) { error = $"Scope「{f.Scope}」は All / Keyboard / Layer のいずれかにしてください"; return false; }
            f.Keyboards ??= new();
            if (f.Keyboards.Count == 0) { error = "キーボードの設定が入っていません"; return false; }
            if (f.Keyboards.Any(k => k == null)) { error = "Keyboards に空の項目があります"; return false; }
            file = f;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
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
        (l.Level switch { ReportLevel.Error => "エラー: ", ReportLevel.Warning => "注意: ", _ => "" }) + l.Text));
}
