using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Kasane.Model;

/// <summary>
/// トレイ（kasane-tray）の「ヘルパー」の設定（2026-10-08）。キーボードごとの
/// %LOCALAPPDATA%\applet\kasane\&lt;UniqId:X8&gt;\helper.json と、全キーボード共通の分（tray.json の
/// <see cref="TrayConfig.Helper"/>）がある。管理者権限は要らない（エディタの設定画面が書き、トレイが数秒ごとに読む）。
///  - <see cref="AppLayerRules"/>: 前面のアプリに応じて、アプリ管理レイヤーを立てる・リマップをOFFにする（P2）。
///  - <see cref="Actions"/> / <see cref="Triggers"/>: HLP(id) のキーや F13 などのキーで動かす動作（P3。スキーマのみ）。
/// トレイにもリンクして使う（同じソースを共有する）。
/// </summary>
public sealed class HelperConfig
{
    public const int CurrentVersion = 1;
    public const string FileName = "helper.json";

    public int Version { get; set; } = CurrentVersion;

    /// <summary>前面のアプリの規則。上から順に見て、最初に一致したものを使う（<see cref="AppLayerRule.Default"/> は最後に見る）。</summary>
    public List<AppLayerRule> AppLayerRules { get; set; } = new();

    /// <summary>HLP(id) のキー（ドライバのヘルパーイベント）で動かす動作（P3）。</summary>
    public List<HelperAction> Actions { get; set; } = new();

    /// <summary>F13 などのキーを、トレイのキーボードフックで受けて動かす動作（P3）。</summary>
    public List<HelperTrigger> Triggers { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 日本語の名前をそのまま読めるように
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true, // 手で書いた helper.json も読めるように
    };

    public static string BaseDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "applet", "kasane");

    /// <summary>キーボードごとの helper.json の場所（project.json と同じフォルダ）。</summary>
    public static string DevicePath(uint uniqId, string? baseDirectory = null) =>
        Path.Combine(baseDirectory ?? BaseDirectory, uniqId.ToString("X8"), FileName);

    /// <summary>読めない・壊れている・無いときは null（呼び出し側が「設定なし」として扱う）。</summary>
    public static HelperConfig? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return FromJson(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static HelperConfig? FromJson(string json) => JsonSerializer.Deserialize<HelperConfig>(json, JsonOptions);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ToJson());
    }
}

/// <summary>前面のアプリの規則1つ。</summary>
public sealed class AppLayerRule
{
    /// <summary>表示用の名前（任意）。</summary>
    public string? Name { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>どの規則にも一致しなかったときに使う規則（<see cref="Match"/> は見ない）。</summary>
    public bool Default { get; set; }

    public AppMatch Match { get; set; } = new();

    /// <summary>立てるアプリ管理レイヤー（0〜15）。範囲外は無視する。</summary>
    public List<int> Layers { get; set; } = new();

    /// <summary>このアプリが前面の間、そのキーボードのリマップをOFF（素通し）にする（ゲームなど）。</summary>
    public bool MapOff { get; set; }
}

/// <summary>
/// 規則の照合条件。指定した項目（null・空でないもの）が、すべて一致したときに一致とする。
///  - <see cref="Exe"/>: exe のファイル名（例 code.exe）。「\」を含めばフルパスとして比べる。大文字小文字は区別しない。
///  - <see cref="PathRegex"/> / <see cref="TitleRegex"/>: exe のフルパス / トップレベルのウィンドウのタイトルの正規表現（部分一致、大文字小文字を区別しない）。
///  - <see cref="Class"/>: トップレベルのウィンドウのクラス名（完全一致、大文字小文字を区別しない）。
///  - <see cref="NodokaClass"/> / <see cref="NodokaTitle"/>: のどかの window の正規表現と同じ文字列
///    （"exeのフルパス:トップレベルのクラス:…:フォーカスのクラス"、タイトルも同じ形）に対する部分一致。
///    両方あるときは <see cref="NodokaOr"/> で &amp;&amp;（既定）か || かを選ぶ（のどかの ( /cls/ &amp;&amp; /title/ ) と ( /cls/ || /title/ )）。
/// </summary>
public sealed class AppMatch
{
    public string? Exe { get; set; }
    public string? PathRegex { get; set; }
    public string? TitleRegex { get; set; }
    public string? Class { get; set; }
    public string? NodokaClass { get; set; }
    public string? NodokaTitle { get; set; }
    public bool NodokaOr { get; set; }

    // 正規表現は、最初に使うときに作って持つ（不正な式は「一致しない」）。
    private Regex? _path, _title, _nodokaClass, _nodokaTitle;
    private bool _compiled;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static Regex? Compile(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return null;
        try { return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout); }
        catch (ArgumentException) { return InvalidRegex; }
    }

    // 不正な正規表現の印（何にも一致しない）。
    private static readonly Regex InvalidRegex = new("(?!)", RegexOptions.None, RegexTimeout);

    /// <summary>正規表現が正しく書けているか（エディタの検証用）。不正なら、その項目の名前を返す。</summary>
    [JsonIgnore]
    public string? InvalidRegexField
    {
        get
        {
            EnsureCompiled();
            if (ReferenceEquals(_path, InvalidRegex)) return nameof(PathRegex);
            if (ReferenceEquals(_title, InvalidRegex)) return nameof(TitleRegex);
            if (ReferenceEquals(_nodokaClass, InvalidRegex)) return nameof(NodokaClass);
            if (ReferenceEquals(_nodokaTitle, InvalidRegex)) return nameof(NodokaTitle);
            return null;
        }
    }

    /// <summary>何も指定していない（すべてに一致してしまう）条件か。こうした規則は、照合に使わない。</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrEmpty(Exe) && string.IsNullOrEmpty(PathRegex) && string.IsNullOrEmpty(TitleRegex) &&
        string.IsNullOrEmpty(Class) && string.IsNullOrEmpty(NodokaClass) && string.IsNullOrEmpty(NodokaTitle);

    private void EnsureCompiled()
    {
        if (_compiled) return;
        _path = Compile(PathRegex);
        _title = Compile(TitleRegex);
        _nodokaClass = Compile(NodokaClass);
        _nodokaTitle = Compile(NodokaTitle);
        _compiled = true;
    }

    private static bool Search(Regex re, string input)
    {
        try { return re.IsMatch(input); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    public bool IsMatch(ForegroundWindowInfo w)
    {
        if (IsEmpty) return false;
        EnsureCompiled();

        if (!string.IsNullOrEmpty(Exe))
        {
            bool full = Exe.Contains('\\');
            string actual = full ? w.ExePath : Path.GetFileName(w.ExePath);
            if (!string.Equals(actual, Exe, StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (_path != null && !Search(_path, w.ExePath)) return false;
        if (_title != null && !Search(_title, w.Title)) return false;
        if (!string.IsNullOrEmpty(Class) && !string.Equals(w.ClassName, Class, StringComparison.OrdinalIgnoreCase)) return false;

        if (_nodokaClass != null || _nodokaTitle != null)
        {
            bool? cls = _nodokaClass != null ? Search(_nodokaClass, w.NodokaClass) : null;
            bool? title = _nodokaTitle != null ? Search(_nodokaTitle, w.NodokaTitle) : null;
            bool ok = (cls, title) switch
            {
                ({ } c, { } t) => NodokaOr ? c || t : c && t,
                ({ } c, null) => c,
                (null, { } t) => t,
                _ => true,
            };
            if (!ok) return false;
        }
        return true;
    }
}

/// <summary>
/// 前面のウィンドウの情報（トレイが Win32 で取る。照合のテストでは直接作る）。
/// NodokaClass / NodokaTitle は、のどかの window の照合に使う文字列（"exeのフルパス:トップレベル:…:フォーカス"）。
/// </summary>
public sealed record ForegroundWindowInfo(string ExePath, string ClassName, string Title, string NodokaClass, string NodokaTitle);

/// <summary>HLP(id) で動かす動作（P3）。</summary>
public sealed class HelperAction
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public HelperActionBody Action { get; set; } = new();
}

/// <summary>F13 などのキー（＋修飾キー）で動かす動作（P3）。</summary>
public sealed class HelperTrigger
{
    public string Key { get; set; } = "";
    /// <summary>"MOD_LSFT|MOD_LCTL" の形（空 = 修飾キーなし）。</summary>
    public string? Mods { get; set; }
    public HelperActionBody Action { get; set; } = new();
}

/// <summary>動作の中身（P3 で実装する。種類: Launch / Open / Sequence / AppLayer / PauseToggle）。</summary>
public sealed class HelperActionBody
{
    public string Type { get; set; } = "";
    public string? Path { get; set; }
    public string? Args { get; set; }
    public string? WorkDir { get; set; }
    public List<int>? Layers { get; set; }
    public string? LayerOp { get; set; }
    public bool Repeat { get; set; }
    public List<JsonElement>? Steps { get; set; }
}

/// <summary>
/// 読み込んだヘルパー設定の一式（全キーボード共通の分と、キーボードごとの分）。トレイは、これを丸ごと差し替えて使う
/// （読み込みと使う側が別のスレッドでも、途中の状態が見えない）。
/// </summary>
public sealed class HelperConfigSet
{
    public static readonly HelperConfigSet Empty = new(null, new Dictionary<uint, HelperConfig>());

    public HelperConfigSet(HelperConfig? global, IReadOnlyDictionary<uint, HelperConfig> perDevice)
    {
        Global = global;
        PerDevice = perDevice;
    }

    public HelperConfig? Global { get; }
    public IReadOnlyDictionary<uint, HelperConfig> PerDevice { get; }

    /// <summary>
    /// そのキーボードに効く前面のアプリの規則（キーボードごとの規則 → 共通の規則の順）。
    /// <see cref="AppLayerRule.Default"/> の規則は、キーボードごとの分があればそれを、無ければ共通の分を使う。
    /// </summary>
    public IEnumerable<AppLayerRule> RulesFor(uint uniqId)
    {
        PerDevice.TryGetValue(uniqId, out var device);
        var rules = (device?.AppLayerRules ?? Enumerable.Empty<AppLayerRule>())
            .Concat(Global?.AppLayerRules ?? Enumerable.Empty<AppLayerRule>())
            .Where(r => r.Enabled);
        return rules;
    }

    public bool HasAnyRules => (Global?.AppLayerRules.Count ?? 0) > 0 || PerDevice.Values.Any(c => c.AppLayerRules.Count > 0);

    /// <summary>
    /// フォルダ（%LOCALAPPDATA%\applet\kasane）の下の &lt;UniqId:X8&gt;\helper.json をすべて読み、共通の分と合わせる。
    /// </summary>
    public static HelperConfigSet Load(HelperConfig? global, string? baseDirectory = null)
    {
        var perDevice = new Dictionary<uint, HelperConfig>();
        string dir = baseDirectory ?? HelperConfig.BaseDirectory;
        try
        {
            if (Directory.Exists(dir))
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    string name = Path.GetFileName(sub);
                    if (name.Length != 8 || !uint.TryParse(name, System.Globalization.NumberStyles.AllowHexSpecifier, null, out var id))
                        continue;
                    var cfg = HelperConfig.Load(Path.Combine(sub, HelperConfig.FileName));
                    if (cfg != null) perDevice[id] = cfg;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new HelperConfigSet(global, perDevice);
    }
}
