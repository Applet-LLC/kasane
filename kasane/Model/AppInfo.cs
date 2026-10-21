using System.IO;
using System.Reflection;

namespace Kasane.Model;

/// <summary>
/// 画面に出すアプリの名前とバージョン（kasane.exe とトレイで共有する）。
/// 名前は日本語版の表記「かさね」。バージョンは exe のアセンブリのバージョン（AppVersion.props）を、「主.副」の2桁（1.00）で出す。
/// </summary>
public static class AppInfo
{
    public static string DisplayName => Loc.T("かさね", "kasane");
    public const string Publisher = "Applet LLC";
    public const string Copyright = "Copyright 2026 Applet LLC";
    public const string Website = "https://appletllc.com/";

    /// <summary>「かさね 1.00」。</summary>
    public static string NameWithVersion => $"{DisplayName} {VersionText}";

    /// <summary>1.00 の形（実行中の exe のバージョンから）。</summary>
    public static string VersionText => FormatVersion(Assembly.GetEntryAssembly()?.GetName().Version);


    /// <summary>実行中の exe のフルパス（インストール先を含むファイル名）。</summary>
    public static string ExePath => Environment.ProcessPath ?? "";

    /// <summary>
    /// exe のビルド日時（ローカル時刻）。Release のビルドに埋め込んだメタデータ BuildDate（AppVersion.props）を使い、無ければ（Debug）exe の更新日時。
    /// 分からなければ null。
    /// </summary>
    public static DateTime? BuildTime
    {
        get
        {
            var meta = Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "BuildDate")?.Value;
            if (ParseBuildDate(meta) is { } fromMeta) return fromMeta;
            try { return ExePath.Length > 0 && File.Exists(ExePath) ? File.GetLastWriteTime(ExePath) : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    /// <summary>「2026-10-18 15:31:26」の形。分からなければ「?」。</summary>
    public static string BuildTimeText => BuildTime is { } t ? t.ToString("yyyy-MM-dd HH:mm:ss") : "?";

    /// <summary>メタデータの BuildDate（UTC の ISO 8601）を、ローカル時刻にする。読めなければ null。</summary>
    public static DateTime? ParseBuildDate(string? text) =>
        DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var utc)
            ? utc.ToLocalTime() : null;

    /// <summary>「主.副」を2桁で（1.0.0 → 1.00、1.12.3 → 1.12）。分からなければ「?」。</summary>
    public static string FormatVersion(Version? version) =>
        version == null ? "?" : $"{version.Major}.{version.Minor:D2}";
}
