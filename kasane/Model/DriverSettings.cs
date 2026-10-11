using Kasane.Interop;

namespace Kasane.Model;

/// <summary>
/// プロジェクトを検証・変換して、ドライバに設定する内容にしたもの（<see cref="ProjectService.Compile"/> の結果）。
/// IOCTL で送る（<see cref="ProjectService.Apply"/>）のにも、インストール時にレジストリへ直接書く
/// （<see cref="PersistedSettings"/>）のにも、同じものを使う。
/// </summary>
/// <param name="LayerMap">レイヤーマップのブロブ。割り当てが1つも無ければ null（消去）。</param>
/// <param name="Macros">マクロID → ステップ（定義のあるもの）。</param>
/// <param name="Combos">コンボID → 登録内容。</param>
/// <param name="Features">機能別ON/OFFのビット（null = 触らない）。</param>
/// <param name="Nicola">NICOLAのブロブ（null = 触らない）。</param>
/// <param name="ClearNicola">Nicola が null のとき、ドライバ側のNICOLAを消すか。</param>
public sealed record DriverSettings(
    byte[]? LayerMap,
    uint TapTermMs, uint ComboTermMs, uint TapDanceTermMs,
    uint TapHoldMode, uint QuickTapTermMs, uint TappingToggle,
    IReadOnlyDictionary<int, List<MacroStep>> Macros,
    IReadOnlyDictionary<int, ComboEntry?> Combos,
    IReadOnlyDictionary<int, TapDanceEntry?> TapDances,
    IReadOnlyDictionary<int, KeyOverrideEntry?> KeyOverrides,
    uint? Features,
    byte[]? Nicola,
    bool ClearNicola,
    List<string> Warnings);
