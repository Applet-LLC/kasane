using System.IO;
using System.Text.Json;

namespace Kasane.Model;

/// <summary>
/// トレイ（kasane-tray）のユーザーごとの設定（%LOCALAPPDATA%\applet\kasane\tray.json）。エディタ（kasane）の設定画面が書き、
/// トレイが数秒ごとに読む。トレイにもリンクして使う（同じソースを共有する）。
/// </summary>
public sealed class TrayConfig
{
    /// <summary>
    /// IME連動。IMEがかなモード（ON）のときだけ、NICOLAのレイヤーを自動で立てる（トレイが、アプリ管理レイヤーとして立てる）。
    /// 既定はON。NICOLAを設定していないキーボードには何もしない。
    /// </summary>
    public bool ImeLink { get; set; } = true;

    /// <summary>トレイのアイコンの右下に、今のレイヤー番号を重ねる（基底のレイヤーだけのときは重ねない）。既定はON。</summary>
    public bool ShowLayerOnIcon { get; set; } = true;

    /// <summary>
    /// 全キーボード共通のヘルパー設定（前面のアプリの規則、HLP の動作など。2026-10-08）。キーボードごとの分は
    /// &lt;UniqId:X8&gt;\helper.json（<see cref="HelperConfig"/>）。無ければ null。
    /// </summary>
    public HelperConfig? Helper { get; set; }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "applet", "kasane", "tray.json");

    /// <summary>読めない・壊れている・無いときは、既定の設定を返す。</summary>
    public static TrayConfig Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return new TrayConfig();
            return JsonSerializer.Deserialize<TrayConfig>(File.ReadAllText(path)) ?? new TrayConfig();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new TrayConfig();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp"; // トレイが読む途中の、書きかけのファイルを見ないように
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, options));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch (Exception) { /* 片付けられなくても、元の失敗を返す */ }
            throw;
        }
    }
}
