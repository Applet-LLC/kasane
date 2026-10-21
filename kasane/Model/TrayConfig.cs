using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kasane.Model;

/// <summary>
/// トレイ（kasane-tray）のユーザーごとの設定（%LOCALAPPDATA%\applet\kasane\tray.json）。エディタ（kasane）の設定画面が書き、
/// トレイが数秒ごとに読む。トレイにもリンクして使う（同じソースを共有する）。
/// </summary>
public sealed class TrayConfig
{
    /// <summary>
    /// IME連動。IMEがかなモード（ON）のときだけ、NICOLAのレイヤーを自動で立てる（トレイが、アプリ管理レイヤーとして立てる）。
    /// 既定は<b>オフ</b>（2026-10-19。以前はON）。利用者がチェックして保存したあとは、その選択がずっと残る（このファイルは利用者のフォルダにあり、
    /// インストール・再インストールでは書き換えない）。NICOLAを設定していないキーボードには何もしない。
    /// NICOLAを使わずに、IMEのON/OFFとレイヤーだけを連動させたいときは、<see cref="HelperConfig.ImeLayers"/>（設定の「IME連動」タブ）を使う。
    /// </summary>
    public bool ImeLink { get; set; }

    /// <summary>
    /// IME の状態の取得方法（<see cref="Model.ImeSourceMode"/> の名前。Imm32 / ImmThenTsf / TsfThenImm）。既定は Imm32（IMM32 のみ。天狼（sirius）のフックの DLL を入れない。
    /// 2026-10-20: Explorer の遅れが解決できず、TSF が補える場面も見つからなかったので、既定を IMM32 のみにした）。ImmThenTsf は IMM32 を先に試し、
    /// 取れなかったときだけ TSF（TSF は、フックの DLL が前面のアプリの中で読んだ値）。TsfThenImm はその逆。
    /// 無い・読めない値は既定。設定の「IME連動」タブで選ぶ。
    /// </summary>
    public string ImeSource { get; set; } = nameof(ImeSourceMode.Imm32);

    /// <summary><see cref="ImeSource"/> を解釈したもの（保存はしない）。</summary>
    [JsonIgnore]
    public ImeSourceMode ImeSourceMode => ImeSourceModes.Parse(ImeSource);

    /// <summary>
    /// 色のモード（<see cref="Model.ColorMode"/> の名前。Light / Dark。2026-10-21）。既定は Light。kasane の全画面・トレイのメニューとバージョン情報・ライセンス管理に効く。
    /// 無い・読めない値は既定。設定の「全般」タブで選ぶ。
    /// </summary>
    [JsonPropertyName("ColorMode")]
    public string ColorModeName { get; set; } = nameof(ColorMode.Light);

    /// <summary><see cref="ColorModeName"/> を解釈したもの（保存はしない）。</summary>
    [JsonIgnore]
    public ColorMode ColorModeValue => ColorModes.Parse(ColorModeName);

    /// <summary>トレイのアイコンの右下に、今のレイヤー番号を重ねる（基底のレイヤーだけのときは重ねない）。既定はON。</summary>
    public bool ShowLayerOnIcon { get; set; } = true;

    /// <summary>
    /// トレイが動いている間だけ、リマップを有効にする（既定はON。2026-10-13）。ドライバは永続化された設定で、ログオン画面や起動直後でも
    /// リマップを続けるので、このモードでは、トレイの終了・ログオフ・シャットダウン、セッションが非アクティブな間（ロック・ユーザー切り替え）に
    /// キーボードのリマップをOFFにし、トレイが動いてセッションがアクティブになったらONに戻す（kasane-tray の AutoOffController）。
    /// tray.json が無い・読めないときもON。
    /// </summary>
    public bool RemapOnlyWhileTrayRuns { get; set; } = true;

    /// <summary>
    /// デスクトップのアイコンの表示方法（のどかの def option DesktopListView と同じ番号。2026-10-14）。トレイの起動時に1回、合わせる。
    /// 0 = 通常表示、1 = 詳細表示、2 = 小さなアイコン表示、3 = リスト表示、4 = タイル表示。null（既定）は何もしない。キーのリマップとは無関係。
    /// </summary>
    public int? DesktopViewMode { get; set; }

    /// <summary>
    /// 全キーボード共通のヘルパー設定（前面のアプリの規則、HLP の動作など。2026-10-08）。キーボードごとの分は
    /// &lt;UniqId:X8&gt;\helper.json（<see cref="HelperConfig"/>）。無ければ null。
    /// </summary>
    public HelperConfig? Helper { get; set; }

    public static string DefaultPath => Path.Combine(HelperConfig.BaseDirectory, "tray.json");

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

/// <summary>IME の状態の取得方法（tray.json の ImeSource）。</summary>
public enum ImeSourceMode
{
    /// <summary>IMM32 だけ（天狼のフックは入れない。既定）。</summary>
    Imm32,
    /// <summary>IMM32 を先に使い、取れなかったときだけ TSF。</summary>
    ImmThenTsf,
    /// <summary>TSF を先に使い、取れなかったときは IMM32。</summary>
    TsfThenImm,
}

public static class ImeSourceModes
{
    /// <summary>名前から。無い・知らない名前は既定（Imm32）。大文字小文字は区別しない。</summary>
    public static ImeSourceMode Parse(string? text) =>
        Enum.TryParse<ImeSourceMode>(text?.Trim(), ignoreCase: true, out var mode) && Enum.IsDefined(mode) ? mode : ImeSourceMode.Imm32;

    /// <summary>天狼のフック（TSF）を使う取得方法か。</summary>
    public static bool UsesTsf(this ImeSourceMode mode) => mode != ImeSourceMode.Imm32;
}
