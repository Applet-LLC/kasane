using System.Globalization;
using System.Threading;
using Microsoft.Win32;

namespace KbdLayerLicenseGui;

/// <summary>
/// GUIの表示言語（日本語/英語）の自動判定・手動切替・永続化を担う。
/// 自動判定: OSのUI表示言語が日本語(ja)でなければ英語とする。
/// 手動切替: HKCU\Software\kbdlayer-license\UiLanguage に保存し、次回起動時も優先される。
/// インストーラ（ランチャー）側は自動判定のみで手動切替は持たない。手動ドロップダウンはこのGUI内のみ。
/// </summary>
internal static class LocalizationHelper
{
    private const string RegKeyPath = @"Software\kbdlayer-license";
    private const string RegValueName = "UiLanguage";

    public static void Initialize()
    {
        string? saved = ReadSavedLanguage();
        string osDetected = DetectFromOs();
        string language = saved ?? osDetected;
        ClientDebugLog.Log($"LocalizationHelper.Initialize: saved='{saved}' osDetected='{osDetected}' -> using '{language}'");
        Apply(language);
    }

    public static void SetLanguage(string language)
    {
        Apply(language);
        SaveLanguage(language);
    }

    public static string CurrentLanguage =>
        Thread.CurrentThread.CurrentUICulture.TwoLetterISOLanguageName == "ja" ? "ja" : "en";

    private static string DetectFromOs() =>
        CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "ja" ? "ja" : "en";

    private static void Apply(string language)
    {
        var culture = new CultureInfo(language == "ja" ? "ja-JP" : "en-US");
        Thread.CurrentThread.CurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
    }

    private static string? ReadSavedLanguage()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegKeyPath);
            string? value = key?.GetValue(RegValueName) as string;
            return value is "ja" or "en" ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveLanguage(string language)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegKeyPath, writable: true);
            key?.SetValue(RegValueName, language, RegistryValueKind.String);
        }
        catch
        {
            // ベストエフォート。保存に失敗してもGUI動作に影響させない。
        }
    }
}
