using System.Reflection;

namespace Kasane.Model;

/// <summary>
/// 画面に出すアプリの名前とバージョン（kasane.exe とトレイで共有する）。
/// 名前は日本語版の表記「かさね」。バージョンは exe のアセンブリのバージョン（AppVersion.props）を、「主.副」の2桁（1.00）で出す。
/// </summary>
public static class AppInfo
{
    public const string DisplayName = "かさね";
    public const string Publisher = "Applet LLC";

    /// <summary>「かさね 1.00」。</summary>
    public static string NameWithVersion => $"{DisplayName} {VersionText}";

    /// <summary>1.00 の形（実行中の exe のバージョンから）。</summary>
    public static string VersionText => FormatVersion(Assembly.GetEntryAssembly()?.GetName().Version);

    /// <summary>1.0.0 の形（ビルド番号まで）。</summary>
    public static string BuildText => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";

    /// <summary>「主.副」を2桁で（1.0.0 → 1.00、1.12.3 → 1.12）。分からなければ「?」。</summary>
    public static string FormatVersion(Version? version) =>
        version == null ? "?" : $"{version.Major}.{version.Minor:D2}";
}
