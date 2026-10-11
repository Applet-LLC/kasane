using Kasane.Interop;

namespace Kasane.Tray;

public sealed record LayerMenuItem(int Layer, bool Checked);

public sealed record LayerMenuKeyboard(uint UniqId, string Label, IReadOnlyList<LayerMenuItem> Items);

/// <summary>
/// トレイの「レイヤー」サブメニューの中身（B-5a）。レイヤーマップがONのキーボードごとに、レイヤー 1〜15（0 は基底なので出さない）の項目を作る。
/// チェックは、そのレイヤーが今有効（<see cref="LayerState.EffectiveLayerMask"/>）かどうか。選ぶと、"action" の入力源でそのレイヤーを反転する
/// （<see cref="ActionLayerController"/>）。キーボード側の TG や、IME 連動・アプリ別規則が立てたレイヤーは、メニューからは下ろせない（OR で合わさるため）。
/// </summary>
public static class LayerMenuModel
{
    public static IReadOnlyList<LayerMenuKeyboard> Build(IReadOnlyDictionary<uint, LayerState> states)
    {
        var result = new List<LayerMenuKeyboard>();
        foreach (var (id, s) in states.OrderBy(p => p.Key))
        {
            if (!s.LayerMapEnabled) continue;
            var items = Enumerable.Range(1, KbdLayerClient.MaxLayers - 1)
                .Select(layer => new LayerMenuItem(layer, (s.EffectiveLayerMask >> layer & 1) != 0))
                .ToList();
            result.Add(new LayerMenuKeyboard(id, $"キーボード {id:X8}", items));
        }
        return result;
    }
}
