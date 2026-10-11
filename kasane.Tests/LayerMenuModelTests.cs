using Kasane.Interop;
using Kasane.Tray;

namespace Kasane.Tests;

public class LayerMenuModelTests
{
    private static LayerState State(uint effective, bool enabled = true) => new(1, 0, 0, 0, effective, enabled, false);

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
    public void SkipsKeyboardsWithMapOff_AndSortsById()
    {
        var kbs = LayerMenuModel.Build(new Dictionary<uint, LayerState> { [9] = State(1), [2] = State(1, enabled: false), [5] = State(1) });
        Assert.Equal(new uint[] { 5, 9 }, kbs.Select(k => k.UniqId).ToArray());
    }

    [Fact]
    public void Empty_WhenNoKeyboards() => Assert.Empty(LayerMenuModel.Build(new Dictionary<uint, LayerState>()));
}
