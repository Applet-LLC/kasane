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

    /// <summary>
    /// IME のON/OFFに合わせて、アプリ管理レイヤーを立てる・下ろす設定（2026-10-19。NICOLA とは無関係。無ければ null）。
    /// キーボードごと（helper.json）が、共通（tray.json の Helper）より先（<see cref="HelperConfigSet.ImeLinkFor"/>）。
    /// </summary>
    public ImeLayerLink? ImeLayers { get; set; }

    /// <summary>
    /// NICOLAのIME連動（IMEが日本語入力モードのときだけ NICOLA のレイヤーを立てる）を、このキーボードで使うか（2026-10-19）。
    /// null は、全キーボード共通の設定（tray.json の <see cref="TrayConfig.ImeLink"/>）に従う。キーボードごとの設定（helper.json）が、共通より先
    /// （<see cref="HelperConfigSet.NicolaLinkFor"/>）。共通の設定の側（tray.json の Helper）では使わない（共通は ImeLink）。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? NicolaImeLink { get; set; }

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

    /// <summary>データフォルダを上書きする環境変数。インストーラーがマシン全体に設定できる（管理者保護で昇格先が別ユーザーになっても同じ場所を使うため）。</summary>
    public const string DataDirEnvironmentVariable = "KASANE";

    /// <summary>
    /// 設定ファイルの保存先。環境変数 KASANE が絶対パスならそれ、無い・相対パスなら %LOCALAPPDATA%\applet\kasane。
    /// 値は呼ぶたびに読む（トレイは常駐するので、変数の変更を次の読み直しで拾える）。
    /// </summary>
    public static string BaseDirectory => ResolveBaseDirectory(Environment.GetEnvironmentVariable(DataDirEnvironmentVariable));

    public static string ResolveBaseDirectory(string? overrideValue)
    {
        string? value = overrideValue?.Trim().Trim('"');
        if (!string.IsNullOrEmpty(value) && Path.IsPathFullyQualified(value))
            return Path.GetFullPath(value);
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "applet", "kasane");
    }

    /// <summary>キーボードごとの helper.json の場所（project.json と同じフォルダ）。</summary>
    public static string DevicePath(uint uniqId, string? baseDirectory = null) =>
        Path.Combine(baseDirectory ?? BaseDirectory, uniqId.ToString("X8"), FileName);

    /// <summary>読めない・壊れている・無いときは null（呼び出し側が「設定なし」として扱う）。</summary>
    public static HelperConfig? Load(string path) => TryLoad(path, out var config, out _) ? config : null;

    /// <summary>
    /// 読み込む。ファイルが無いのは成功（<paramref name="config"/> は null）。ファイルがあるのに読めない（構文エラー・アクセス拒否など）
    /// ときだけ false で、理由を <paramref name="error"/> に入れる。エディタは、壊れたファイルを黙って上書きしないために使う。
    /// </summary>
    public static bool TryLoad(string path, out HelperConfig? config, out string? error)
    {
        config = null;
        error = null;
        try
        {
            if (!File.Exists(path)) return true;
            config = FromJson(File.ReadAllText(path));
            if (config == null) { error = Loc.T("中身が空です", "The file is empty"); return false; }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            config = null;
            error = ex.Message;
            return false;
        }
    }

    public static HelperConfig? FromJson(string json) => JsonSerializer.Deserialize<HelperConfig>(json, JsonOptions);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>一時ファイルに書いてから置き換える（トレイが読む途中の、書きかけのファイルを見ないように）。</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, ToJson());
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch (Exception) { /* 片付けられなくても、元の失敗を返す */ }
            throw;
        }
    }
}

/// <summary>前面のアプリの規則1つ。</summary>
public sealed class AppLayerRule
{
    /// <summary>表示用の名前（任意）。</summary>
    public string? Name { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>どの規則にも一致しなかったときに使う規則（<see cref="Match"/> は見ない）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Default { get; set; }

    public AppMatch Match { get; set; } = new();

    /// <summary>立てるアプリ管理レイヤー（0〜15）。範囲外は無視する。</summary>
    public List<int> Layers { get; set; } = new();

    /// <summary>このアプリが前面の間、そのキーボードのリマップをOFF（素通し）にする（ゲームなど）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
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
    /// <summary>名前（任意。一覧と、割り当ての一覧に出す）。動作（<see cref="HelperAction"/>）の名前と同じ。</summary>
    public string? Name { get; set; }
    public string Key { get; set; } = "";
    /// <summary>"MOD_LSFT|MOD_LCTL" の形（空 = 修飾キーなし）。</summary>
    public string? Mods { get; set; }
    public HelperActionBody Action { get; set; } = new();
}

/// <summary>
/// 動作の中身（P3）。種類（<see cref="Type"/>）:
///  - Launch: <see cref="Path"/> のプログラムを <see cref="Args"/> 付きで起動する（<see cref="WorkDir"/> が作業フォルダ。昇格はしない）。
///  - Open: <see cref="Path"/> の URL・ファイル・フォルダを、関連付けられたアプリで開く。
///  - Sequence: <see cref="Steps"/> を順に実行する（キー・文字列・遅延・マウス）。キーを離したときは <see cref="ReleaseSteps"/>。
///  - AppLayer: <see cref="Layers"/> のアプリ管理レイヤーを、<see cref="LayerOp"/>（On / Off / Toggle / Hold）で操作する
///    （Hold は、キーを押している間だけ立てる）。対象は、そのキーを押したキーボード。
///  - PauseToggle: トレイの一時停止・再開を切り替える（一時停止中でも効く）。
///  - KillLine: Emacs の C-k（行末までの切り取り）。Shift+End、Ctrl+X で行末まで切り取り、続けて実行した分をクリップボードに連結する
///    （のどかの &amp;EmacsEditKillLine と同じ）。行末なら改行を連結して Delete を送る。設定は要らない。
/// <see cref="Repeat"/> が true のときだけ、キーのオートリピートで繰り返し実行する。
/// </summary>
public sealed class HelperActionBody
{
    public const string Launch = "Launch";
    public const string Open = "Open";
    public const string Sequence = "Sequence";
    public const string AppLayer = "AppLayer";
    public const string PauseToggle = "PauseToggle";
    /// <summary>Emacs の C-k（行末までを切り取り、連続した分をクリップボードに連結する）。</summary>
    public const string KillLine = "KillLine";
    /// <summary>文字列（<see cref="Text"/>）を、そのまま入力する（"Apple 漢字" など。エディタの現在位置に追記される）。種類名は "Text"。</summary>
    public const string TextInput = "Text";

    /// <summary><see cref="Text"/> の最大文字数（1文字につき入力が2件で、手順の上限 <see cref="SequenceCompiler.MaxOps"/> に収めるため）。</summary>
    public const int MaxTextLength = 900;

    public string Type { get; set; } = "";
    public string? Path { get; set; }
    public string? Args { get; set; }
    public string? WorkDir { get; set; }
    /// <summary>TextInput の入力する文字列。そのまま送る（改行は Enter、タブは Tab。バックスラッシュなどの特別な書き方は無い）。</summary>
    public string? Text { get; set; }
    public List<int>? Layers { get; set; }
    public string? LayerOp { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Repeat { get; set; }
    /// <summary>
    /// true のとき、トリガー（キー＋修飾キー）で動かす Sequence の実行中も、押している修飾キーを OS に残す（既定の false は、実行の間だけ隠す。
    /// Ctrl+F1 でマクロを出すときに、出力へ Ctrl が掛からないようにする）。トリガー以外（HLP）には関係ない。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool KeepMods { get; set; }
    public List<HelperStep>? Steps { get; set; }
    /// <summary>キーを離したときに実行する手順（マウスのボタンを押している間だけ押す、など）。</summary>
    public List<HelperStep>? ReleaseSteps { get; set; }
    /// <summary>
    /// true のとき、TextInput の入力の間だけ IME を OFF にして、終わったら元の状態に戻す（IME が OFF なら何もしない）。
    /// 方式は <see cref="ImeMethod"/>、失敗したときの扱いは <see cref="ImeOnFail"/>。TextInput 以外には関係ない。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ImeOffDuring { get; set; }
    /// <summary><see cref="ImeOffDuring"/> の方式。"Auto"（既定。IMM32 → IME キーの順に試す）か、固定する方式（"Imm" / "Tsf" / "Key" / "AltKanji"）。</summary>
    public string? ImeMethod { get; set; }
    /// <summary><see cref="ImeOffDuring"/> の IME の切り替えに失敗したときの扱い。"Continue"（既定。記録して入力を続ける）か "Abort"（入力しない）。</summary>
    public string? ImeOnFail { get; set; }
}

/// <summary>
/// <see cref="HelperActionBody.Steps"/> の1手順。指定した項目を、下の順に実行する（普通は1つだけ書く）。
///  - Key: キーを押して離す（"KC_A"、"LCTL(KC_C)"、"SC_E06A" など。ドライバのキーコードと同じ書き方）。
///  - Down / Up: キーを押す / 離す（修飾付きなら、修飾キーも）。
///  - Vk / VkDown / VkUp: 仮想キー（VK_*）を押して離す / 押す / 離す。"IME_ON"、"0x16"、"LSHIFT+IME_ON" のように書く（名前は VK_ なし。
///    + でつないだ左の分は修飾キー）。スキャンコードを持たないキー（VK_IME_ON / VK_IME_OFF など）用で、常に SendInput で送る。
///  - Text: 文字列を打つ（Unicode のまま。改行は Enter、タブは Tab）。
///  - DelayMs: 待つ（0〜10000ms）。
///  - Mouse: ボタン（Left / Right / Middle / X1 / X2）を <see cref="MouseAction"/>（Click（既定）/ Down / Up）する。
///  - MoveX / MoveY: マウスを動かす。<see cref="Absolute"/> が true なら画面（仮想デスクトップ）の座標、そうでなければ今の位置からの相対。
///  - Wheel / HWheel: ホイール（120 で1ノッチ。縦は奥へ回すと正）。
///  - Ime: IME を "On" / "Off" / "Toggle" にする（今の状態を見て、違うときだけ変える）。方式は <see cref="ImeMethod"/>（"Auto"（既定）は
///    IMM32 → IME キーの順に試す。"Imm" / "Tsf" / "Key" / "AltKanji" は、その方式に固定して、ほかへは切り替えない）。
///    失敗したときは <see cref="ImeOnFail"/>（"Continue"（既定。記録して続ける）か "Abort"（この動作の残りをやめる））。
/// </summary>
public sealed class HelperStep
{
    public string? Key { get; set; }
    public string? Down { get; set; }
    public string? Up { get; set; }
    public string? Vk { get; set; }
    public string? VkDown { get; set; }
    public string? VkUp { get; set; }
    public string? Text { get; set; }
    public int? DelayMs { get; set; }
    public string? Mouse { get; set; }
    public string? MouseAction { get; set; }
    public int? MoveX { get; set; }
    public int? MoveY { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Absolute { get; set; }
    public int? Wheel { get; set; }
    public int? HWheel { get; set; }
    public string? Ime { get; set; }
    public string? ImeMethod { get; set; }
    public string? ImeOnFail { get; set; }
}

/// <summary>
/// IME のON/OFFとレイヤーの連動（NICOLA を使わない）。IME がON（<see cref="ModeOn"/>。半角英数のモードも含む）、または
/// かな入力（<see cref="ModeKana"/>。IME ON で日本語の入力モード）のとき、<see cref="Layers"/> のアプリ管理レイヤーを立て、そうでなければ下ろす。
/// タスクトレイの kasane が動いているときだけ働く。
/// </summary>
public sealed class ImeLayerLink
{
    public const string ModeOn = "On";
    public const string ModeKana = "Kana";

    /// <summary>連動するか。false にすると、共通の設定がある場合でも、このキーボードだけ連動しない。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>立てるレイヤー（0〜15）。</summary>
    public List<int> Layers { get; set; } = new();

    /// <summary>"On"（IME が ON のとき。既定）か "Kana"（日本語入力モードのときだけ）。</summary>
    public string Mode { get; set; } = ModeOn;

    [JsonIgnore]
    public bool KanaOnly => string.Equals(Mode, ModeKana, StringComparison.OrdinalIgnoreCase);

    /// <summary>連動して立てるレイヤーがあるか（有効で、レイヤーが1つ以上）。</summary>
    [JsonIgnore]
    public bool IsActive => Enabled && Layers.Any(l => l is >= 0 and < 16);
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

    /// <summary>
    /// そのキーボードに効く IME 連動のレイヤー設定。キーボードごとの設定があればそれを、無ければ共通の設定を使う
    /// （キーボードごとの設定が「無効」なら、共通の設定があっても、そのキーボードは連動しない）。無ければ null。
    /// </summary>
    public ImeLayerLink? ImeLinkFor(uint uniqId)
    {
        if (PerDevice.TryGetValue(uniqId, out var device) && device.ImeLayers != null) return device.ImeLayers;
        return Global?.ImeLayers;
    }

    /// <summary>そのキーボードで NICOLA の IME 連動を使うか。キーボードごとの設定があればそれを、無ければ共通の設定（<paramref name="global"/>＝tray.json の ImeLink）に従う。</summary>
    public bool NicolaLinkFor(uint uniqId, bool global) =>
        PerDevice.TryGetValue(uniqId, out var device) && device.NicolaImeLink is bool own ? own : global;

    /// <summary>どれかのキーボードで NICOLA の IME 連動を使うか（共通がオン、またはキーボードごとにオンがある）。トレイが IME の状態を見るかどうかの判定。</summary>
    public bool AnyNicolaLink(bool global) => global || PerDevice.Values.Any(c => c.NicolaImeLink == true);

    /// <summary>IME 連動のレイヤー設定が1つでも有効か（トレイが IME の状態を見るかどうかの判定）。</summary>
    public bool HasAnyImeLayerLink => (Global?.ImeLayers?.IsActive ?? false) || PerDevice.Values.Any(c => c.ImeLayers?.IsActive ?? false);

    /// <summary>HLP(id) の動作が1つでもあるか（トレイがドライバのヘルパーイベントを待つかどうかの判定）。</summary>
    public bool HasAnyActions => (Global?.Actions.Count ?? 0) > 0 || PerDevice.Values.Any(c => c.Actions.Count > 0);

    /// <summary>キーフックで受けるトリガーが1つでもあるか（無ければ、トレイはキーボードフックを張らない）。</summary>
    public bool HasAnyTriggers => (Global?.Triggers.Count ?? 0) > 0 || PerDevice.Values.Any(c => c.Triggers.Count > 0);

    public bool HasAnyRules => (Global?.AppLayerRules.Count ?? 0) > 0 || PerDevice.Values.Any(c => c.AppLayerRules.Count > 0);

    /// <summary>
    /// フォルダ（%LOCALAPPDATA%\applet\kasane）の下の &lt;UniqId:X8&gt;\helper.json をすべて読み、共通の分と合わせる。
    /// </summary>
    public static HelperConfigSet Load(HelperConfig? global, string? baseDirectory = null, Action<string>? log = null)
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
                    string path = Path.Combine(sub, HelperConfig.FileName);
                    if (!HelperConfig.TryLoad(path, out var cfg, out var error)) log?.Invoke(Loc.T($"{path}: 読めないため無視します({error})", $"{path}: ignored because it could not be read ({error})"));
                    if (cfg != null) perDevice[id] = cfg;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new HelperConfigSet(global, perDevice);
    }
}
