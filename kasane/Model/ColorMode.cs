using System.IO;
using System.Text.Json;

namespace Kasane.Model;

/// <summary>色のモード（tray.json の ColorMode。設定の「全般」タブ。2026-10-21）。</summary>
public enum ColorMode
{
    /// <summary>ライト（既定。以前の見た目）。</summary>
    Light,
    /// <summary>ダーク。</summary>
    Dark,
}

public static class ColorModes
{
    /// <summary>名前から。無い・知らない名前は既定（Light）。大文字小文字は区別しない。</summary>
    public static ColorMode Parse(string? text) =>
        Enum.TryParse<ColorMode>(text?.Trim(), ignoreCase: true, out var mode) && Enum.IsDefined(mode) ? mode : ColorMode.Light;
}

/// <summary>
/// 色のモードだけを、tray.json から読む（kasane・トレイ・ライセンス管理が、それぞれ起動時などに読む。ほかの設定の型を持たない
/// ライセンス管理でも使えるよう、<see cref="TrayConfig"/> や <see cref="HelperConfig"/> には依存しない）。
/// tray.json の場所は、環境変数 KASANE のフォルダ（フルパスのとき）、無ければ %LOCALAPPDATA%\applet\kasane（<see cref="HelperConfig.BaseDirectory"/> と同じ規則）。
/// 読めない・無い・値が不明なときは Light。
/// </summary>
public static class ColorModeStore
{
    public static string DefaultPath
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable("KASANE")?.Trim().Trim('"');
            string dir = !string.IsNullOrEmpty(value) && Path.IsPathFullyQualified(value)
                ? Path.GetFullPath(value)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "applet", "kasane");
            return Path.Combine(dir, "tray.json");
        }
    }

    public static ColorMode Read(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return ColorMode.Light;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("ColorMode", out var v) && v.ValueKind == JsonValueKind.String
                ? ColorModes.Parse(v.GetString())
                : ColorMode.Light;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return ColorMode.Light;
        }
    }
}
