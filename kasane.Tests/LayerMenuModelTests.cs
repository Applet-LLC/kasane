using Kasane.Interop;
using Kasane.Tray;

namespace Kasane.Tests;

public class LayerMenuModelTests
{
    private static LayerState State(uint effective, bool enabled = true, uint app = 0) => new(1, effective & ~app, app, 0, effective, enabled, false);

    [Fact]
    public void ListsLayers1To15_AndChecksTheEffectiveOnes()
    {
        var kb = Assert.Single(LayerMenuModel.Build(new Dictionary<uint, LayerState> { [7] = State(0b1_0000_0101) }));
        Assert.Equal(7u, kb.UniqId);
        Assert.Equal(15, kb.Items.Count);
        Assert.Equal(1, kb.Items[0].Layer);
        Assert.Equal(15, kb.Items[^1].Layer);
        Assert.Equal(new[] { 2, 8 }, kb.Items.Where(i => i.Checked).Select(i => i.Layer).ToArray()); // 基底（0）は項目に出さない
    }

    [Fact]
    public void LayersOnBySomethingElse_AreCheckedButDisabled()
    {
        // レイヤー 2 はキーボードの TG（Active）、レイヤー 3 は IME 連動か規則（App）。メニューは何も立てていない。
        var items = Assert.Single(LayerMenuModel.Build(new Dictionary<uint, LayerState> { [1] = State(0b1101, app: 0b1000) })).Items;
        var l2 = items.Single(i => i.Layer == 2); var l3 = items.Single(i => i.Layer == 3);
        Assert.True(l2.Checked); Assert.False(l2.Enabled);
        Assert.True(l3.Checked); Assert.False(l3.Enabled);
        Assert.True(items.Single(i => i.Layer == 1).Enabled); // 無効なレイヤーは選べる
        Assert.False(items.Single(i => i.Layer == 1).Checked);
    }

    [Fact]
    public void LayersHeldByTheMenu_StayEnabled_EvenIfAlsoOnElsewhere()
    {
        var states = new Dictionary<uint, LayerState> { [1] = State(0b1100, app: 0b1000) };
        var held = new HashSet<(uint, int)> { (1u, 3) };
        var items = Assert.Single(LayerMenuModel.Build(states, held)).Items;
        Assert.True(items.Single(i => i.Layer == 3).Enabled);  // メニューが立てたもの: 外せる
        Assert.False(items.Single(i => i.Layer == 2).Enabled); // キーボード側: 外せない
    }

    [Fact]
    public void MenuHeldButNotActuallyOn_IsIgnored()
    {
        // メニューが立てたつもりでも、ドライバの App にビットが無ければ（下ろされた）、他の入力源の扱い。
        var states = new Dictionary<uint, LayerState> { [1] = State(0b100) }; // レイヤー 2 は Active（TG）だけ
        var held = new HashSet<(uint, int)> { (1u, 2) };
        Assert.False(Assert.Single(LayerMenuModel.Build(states, held)).Items.Single(i => i.Layer == 2).Enabled);
    }

    [Fact]
    public void SkipsKeyboardsWithMapOff_AndSortsById()
    {
        var kbs = LayerMenuModel.Build(new Dictionary<uint, LayerState> { [9] = State(1), [2] = State(1, enabled: false), [5] = State(1) });
        Assert.Equal(new uint[] { 5, 9 }, kbs.Select(k => k.UniqId).ToArray());
    }

    [Fact]
    public void Empty_WhenNoKeyboards() => Assert.Empty(LayerMenuModel.Build(new Dictionary<uint, LayerState>()));
}
