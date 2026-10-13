namespace Kasane.Model;

public sealed class MacroDef
{
    public int Id { get; set; }
    /// <summary>利用者がつけた名前（任意。画面の表示だけに使う。番号が実際の識別）。</summary>
    public string? Name { get; set; }
    /// <summary>順にタップ（押下+解放）するキーコード（KC_*のみ）。</summary>
    public List<string> Keys { get; set; } = new();
}

public sealed class ComboDef
{
    public int Id { get; set; }
    /// <summary>利用者がつけた名前（任意。画面の表示だけに使う。番号が実際の識別）。</summary>
    public string? Name { get; set; }
    /// <summary>メンバー2キー（レイアウトのスロット番号。スキャンコードは適用時に解決する）。</summary>
    public int Slot1 { get; set; }
    public int Slot2 { get; set; }
    /// <summary>同時押しで送るキー（KC_*のみ）。</summary>
    public string Output { get; set; } = "";
}

public sealed class TapDanceDef
{
    public int Id { get; set; }
    /// <summary>利用者がつけた名前（任意。画面の表示だけに使う。番号が実際の識別）。</summary>
    public string? Name { get; set; }
    public string SingleTap { get; set; } = "";
    public string SingleHold { get; set; } = "";
    public string DoubleTap { get; set; } = "";
    public string TripleTap { get; set; } = "";
}

/// <summary>Key Override（キー置換）: Mods を押しながら Key を押すと Replacement を送る。</summary>
public sealed class KeyOverrideDef
{
    public int Id { get; set; }
    /// <summary>利用者がつけた名前（任意。画面の表示だけに使う。番号が実際の識別）。</summary>
    public string? Name { get; set; }
    /// <summary>条件の修飾キー（MOD_LSFT、MOD_LCTL|MOD_LSFT など。左右は区別しない）。</summary>
    public string Mods { get; set; } = "";
    /// <summary>条件のキー（KC_*。レイヤーで解決した後の出力キー）。</summary>
    public string Key { get; set; } = "";
    /// <summary>置き換え先（KC_* か LCTL(KC_Z) のような修飾付きキー）。</summary>
    public string Replacement { get; set; } = "";
}

/// <summary>
/// 1台のキーボード分の設定一式（%LOCALAPPDATA%\applet\kasane\&lt;UniqId&gt;\project.json）。
/// </summary>
public sealed class KeymapProject
{
    public int Version { get; set; } = 1;
    public uint UniqId { get; set; }
    public string? HardwareId { get; set; }

    /// <summary>利用者がつけた、このキーボードの名前（任意。画面の表示だけに使う）。無ければ null。</summary>
    public string? KeyboardName { get; set; }

    /// <summary>レイアウト名（同梱のLayouts\kle-&lt;名前&gt;.json）。</summary>
    public string LayoutName { get; set; } = "";
    /// <summary>同梱以外のKLEファイルを使う場合のフルパス（同梱なら null）。</summary>
    public string? LayoutPath { get; set; }

    /// <summary>実測したスキャンコード（スロット番号 → Enc16）。既定値と異なるキーだけを記録する。</summary>
    public Dictionary<int, ushort> MeasuredScanCodes { get; set; } = new();

    /// <summary>レイヤーごとの、スロット順のキーコード文字列。</summary>
    public List<List<string>> Layers { get; set; } = new();

    /// <summary>
    /// レイヤーの名前（レイヤーの番号と同じ並び。任意。画面の表示だけに使う）。null か、<see cref="Layers"/> より短い／長いときは、
    /// <see cref="ProjectService.NormalizeShape"/> が合わせる。名前のないレイヤーは null。
    /// </summary>
    public List<string?>? LayerNames { get; set; }

    public List<MacroDef> Macros { get; set; } = new();
    public List<ComboDef> Combos { get; set; } = new();
    public List<TapDanceDef> TapDances { get; set; } = new();
    public List<KeyOverrideDef> KeyOverrides { get; set; } = new();

    /// <summary>0 = ドライバの既定値（TapTerm 200ms / ComboTerm 50ms / TapDanceTerm 200ms）。</summary>
    public uint TapTermMs { get; set; }
    public uint ComboTermMs { get; set; }
    public uint TapDanceTermMs { get; set; }

    /// <summary>MT/LTの判定方式（Interop.TapHoldModes。0 = 別のキーを押したらすぐホールド）。タップダンスは対象外。</summary>
    public uint TapHoldMode { get; set; }
    /// <summary>Quick Tap の時間。0 = TapTermと同じ、KbdLayerClient.QuickTapTermDisabled = 使わない。</summary>
    public uint QuickTapTermMs { get; set; }
    /// <summary>TT(layer)の固定に必要なタップ回数。0 = 既定(5)。</summary>
    public uint TappingToggle { get; set; }

    /// <summary>
    /// 機能別ON/OFF（KbdLayerClient.Feature* のOR）。null = GUIでは管理しない（適用してもドライバの
    /// 設定を変えない。設定ダイアログを開いた時点でドライバの値（オフラインなら全機能有効）が入る）。
    /// </summary>
    public uint? EnabledFeatures { get; set; }

    /// <summary>
    /// NICOLA（親指シフト）。null = GUIでは管理しない（適用してもドライバの設定を変えない。CLIの
    /// --set-nicola で入れた設定を消さないため）。ClearNicola = true なら、適用時にドライバの設定を消す。
    /// </summary>
    public NicolaDef? Nicola { get; set; }
    public bool ClearNicola { get; set; }
}
