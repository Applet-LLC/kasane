namespace Kasane.Model;

/// <summary>NICOLAの文字キー1つ分: 単独打鍵・親指左との同時打鍵・親指右との同時打鍵で出す「かな」（空 = 何も出さない）。</summary>
public sealed class NicolaKeyDef
{
    /// <summary>文字キー（KC_*。物理キーの既定のキーコード名）。</summary>
    public string Key { get; set; } = "";
    /// <summary>右手側のキー（nicola.json の "right"）。false なら左手側（"left"）。</summary>
    public bool RightHand { get; set; }
    public string Single { get; set; } = "";
    public string ThumbLeft { get; set; } = "";
    public string ThumbRight { get; set; } = "";
}

/// <summary>
/// NICOLA（親指シフト）の設定。CLIの nicola.json（driver/KeymapTool/samples/nicola.json）と同じ内容を持つ。
/// nicola.json の "left"/"right" の配列は [単独, 同じ側の親指, 反対側の親指] だが、こちらは
/// 親指の左右で持つ（読み書きは NicolaService が変換する）。
/// </summary>
public sealed class NicolaDef
{
    /// <summary>"romaji"（ローマ字入力のIME向け）か "jis-kana"（JISかな入力のIME向け）。</summary>
    public string Output { get; set; } = "romaji";
    /// <summary>false: 設定はドライバに置いたまま判定しない。</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>このレイヤーがアクティブな間だけ親指シフトの判定をする。</summary>
    public int Layer { get; set; } = 1;
    public string ThumbLeft { get; set; } = "KC_MHEN";
    public string ThumbRight { get; set; } = "KC_HENK";
    /// <summary>親指キーの単独打鍵で送るキー（空 = 何も送らない）。</summary>
    public string ThumbLeftTap { get; set; } = "KC_MHEN";
    public string ThumbRightTap { get; set; } = "KC_HENK";
    /// <summary>判定が有効・無効になったときに1回タップするキー（IMEのオン・オフ等。空 = なし）。</summary>
    public string OnEnter { get; set; } = "";
    public string OnExit { get; set; } = "";
    public uint SimulTermMs { get; set; } = 100;
    public double OverlapRatio { get; set; } = 1.0;
    public uint MinOverlapMs { get; set; }
    public bool ContinuousShift { get; set; }
    public List<NicolaKeyDef> Keys { get; set; } = new();

    public NicolaDef Clone()
    {
        var c = (NicolaDef)MemberwiseClone();
        c.Keys = Keys.Select(k => new NicolaKeyDef
        {
            Key = k.Key, RightHand = k.RightHand, Single = k.Single, ThumbLeft = k.ThumbLeft, ThumbRight = k.ThumbRight,
        }).ToList();
        return c;
    }
}
