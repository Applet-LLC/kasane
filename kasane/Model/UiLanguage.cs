using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Kasane.Model;

/// <summary>表示言語の設定（tray.json の UiLanguage。設定の「全般」タブ。2026-10-21）。</summary>
public enum UiLanguage
{
    /// <summary>自動認識（既定）。Windows の表示言語が日本語なら日本語、それ以外は英語。</summary>
    Auto,
    /// <summary>日本語。</summary>
    Japanese,
    /// <summary>英語。</summary>
    English,
}

public static class UiLanguages
{
    /// <summary>名前から。無い・知らない名前は既定（Auto）。大文字小文字は区別しない。</summary>
    public static UiLanguage Parse(string? text) =>
        Enum.TryParse<UiLanguage>(text?.Trim(), ignoreCase: true, out var value) && Enum.IsDefined(value) ? value : UiLanguage.Auto;
}

/// <summary>表示言語だけを tray.json から読む（<see cref="ColorModeStore"/> と同じ場所・同じ規則）。</summary>
public static class UiLanguageStore
{
    public static UiLanguage Read(string? path = null)
    {
        try
        {
            path ??= ColorModeStore.DefaultPath;
            if (!File.Exists(path)) return UiLanguage.Auto;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("UiLanguage", out var v) && v.ValueKind == JsonValueKind.String
                ? UiLanguages.Parse(v.GetString())
                : UiLanguage.Auto;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return UiLanguage.Auto;
        }
    }
}

/// <summary>
/// 画面の文言の言語。起動時に <see cref="Initialize"/> を1回呼ぶ（kasane・トレイ・ライセンス管理）。以後、<see cref="T"/> で
/// 日本語と英語の文言を並べて渡すと、選ばれた言語のほうを返す。言語の変更は、再起動後に効く。
/// </summary>
public static class Loc
{
    public static bool IsJapanese { get; private set; } = true;

    /// <summary>起動時の言語の設定（変更を検知するために覚えておく）。</summary>
    public static UiLanguage Setting { get; private set; } = UiLanguage.Auto;

    public static void Initialize(UiLanguage? setting = null)
    {
        Setting = setting ?? UiLanguageStore.Read();
        IsJapanese = Resolve(Setting);
        var culture = new CultureInfo(IsJapanese ? "ja-JP" : "en-US");
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
    }

    public static bool Resolve(UiLanguage setting) => setting switch
    {
        UiLanguage.Japanese => true,
        UiLanguage.English => false,
        _ => CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "ja",
    };

    /// <summary>選ばれた言語の文言を返す。</summary>
    public static string T(string ja, string en) => IsJapanese ? ja : en;
}
