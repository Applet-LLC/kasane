namespace KbdLayerKeymapGui.Model;

public sealed class MacroDef
{
    public int Id { get; set; }
    /// <summary>順にタップ（押下+解放）するキーコード（KC_*のみ）。</summary>
    public List<string> Keys { get; set; } = new();
}

public sealed class ComboDef
{
    public int Id { get; set; }
    /// <summary>メンバー2キー（レイアウトのスロット番号。スキャンコードは適用時に解決する）。</summary>
    public int Slot1 { get; set; }
    public int Slot2 { get; set; }
    /// <summary>同時押しで送るキー（KC_*のみ）。</summary>
    public string Output { get; set; } = "";
}

public sealed class TapDanceDef
{
    public int Id { get; set; }
    public string SingleTap { get; set; } = "";
    public string SingleHold { get; set; } = "";
    public string DoubleTap { get; set; } = "";
    public string TripleTap { get; set; } = "";
}

/// <summary>
/// 1台のキーボード分の設定一式（%LOCALAPPDATA%\LayerDriver\&lt;UniqId&gt;\project.json）。
/// </summary>
public sealed class KeymapProject
{
    public int Version { get; set; } = 1;
    public uint UniqId { get; set; }
    public string? HardwareId { get; set; }

    /// <summary>レイアウト名（同梱のLayouts\kle-&lt;名前&gt;.json）。</summary>
    public string LayoutName { get; set; } = "";
    /// <summary>同梱以外のKLEファイルを使う場合のフルパス（同梱なら null）。</summary>
    public string? LayoutPath { get; set; }

    /// <summary>実測したスキャンコード（スロット番号 → Enc16）。既定値と異なるキーだけを記録する。</summary>
    public Dictionary<int, ushort> MeasuredScanCodes { get; set; } = new();

    /// <summary>レイヤーごとの、スロット順のキーコード文字列。</summary>
    public List<List<string>> Layers { get; set; } = new();

    public List<MacroDef> Macros { get; set; } = new();
    public List<ComboDef> Combos { get; set; } = new();
    public List<TapDanceDef> TapDances { get; set; } = new();

    /// <summary>0 = ドライバの既定値（TapTerm 200ms / ComboTerm 50ms / TapDanceTerm 200ms）。</summary>
    public uint TapTermMs { get; set; }
    public uint ComboTermMs { get; set; }
    public uint TapDanceTermMs { get; set; }
}
