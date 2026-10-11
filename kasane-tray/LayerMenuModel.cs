using Kasane.Interop;

namespace Kasane.Tray;

/// <summary>メニューの項目1つ。Enabled が false のときは、別の入力源が立てているレイヤーで、メニューからは切れない（チェックは付いたまま灰色）。</summary>
public sealed record LayerMenuItem(int Layer, bool Checked, bool Enabled = true);

public sealed record LayerMenuKeyboard(uint UniqId, string Label, IReadOnlyList<LayerMenuItem> Items);

/// <summary>
/// トレイの「レイヤー」サブメニューの中身（B-5a）。レイヤーマップがONのキーボードごとに、レイヤー 1〜15（0 は基底なので出さない）の項目を作る。
/// チェックは、そのレイヤーが今有効（<see cref="LayerState.EffectiveLayerMask"/>）かどうか。選ぶと、"action" の入力源でそのレイヤーを反転する
/// （<see cref="ActionLayerController"/>）。
/// 有効なのにメニューが立てたものでないレイヤー（キーボード側の TG・MO、IME 連動・アプリ別規則が立てたもの。OR で合わさるため、メニューからは下ろせない）は、
/// チェックを付けたまま灰色にする（選んでもチェックが外れない、という戸惑いを避ける）。メニューが立てたもの（<c>menuHeld</c>）は、選べる（外せる）。
/// </summary>
public static class LayerMenuModel
{
    public static IReadOnlyList<LayerMenuKeyboard> Build(IReadOnlyDictionary<uint, LayerState> states, ISet<(uint UniqId, int Layer)>? menuHeld = null)
    {
        var result = new List<LayerMenuKeyboard>();
        foreach (var (id, s) in states.OrderBy(p => p.Key))
        {
            if (!s.LayerMapEnabled) continue;
            var items = Enumerable.Range(1, KbdLayerClient.MaxLayers - 1).Select(layer =>
            {
                bool effective = (s.EffectiveLayerMask >> layer & 1) != 0;
                bool byMenu = menuHeld != null && menuHeld.Contains((id, layer)) && (s.AppLayerMask >> layer & 1) != 0;
                return new LayerMenuItem(layer, effective, Enabled: !effective || byMenu);
            }).ToList();
            result.Add(new LayerMenuKeyboard(id, $"キーボード {id:X8}", items));
        }
        return result;
    }
}
