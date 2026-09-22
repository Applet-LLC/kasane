using System.Globalization;
using System.Resources;

namespace KbdLayerLicenseGui.Properties;

/// <summary>
/// Strings.resx（既定=英語）/ Strings.ja.resx（日本語サテライト）から
/// 現在の CurrentUICulture に応じた文言を取得する薄いラッパー。
/// Visual Studio の ResXFileCodeGenerator（Designer.cs 自動生成）に依存せず、
/// dotnet build/publish のみでも確実にビルドできるようにするため手書きにしている。
/// </summary>
internal static class Strings
{
    private static readonly ResourceManager Rm =
        new("KbdLayerLicenseGui.Properties.Strings", typeof(Strings).Assembly);

    public static string Get(string key) =>
        Rm.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentUICulture, Get(key), args);
}
