using Kasane.Interop;
using Kasane.Tray;

namespace Kasane.Tests;

public class LayerDisplayTests
{
    private static LayerState State(uint effective, bool enabled = true, bool nicola = false) =>
        new(1, 0, 0, 0, effective, enabled, nicola);

    private static IReadOnlyDictionary<uint, LayerState> One(LayerState s) => new Dictionary<uint, LayerState> { [1] = s };

    [Fact]
    public void BaseLayerOnly_ShowsNothing()
    {
        Assert.Null(LayerDisplay.Compute(One(State(0b1))));
        Assert.Null(LayerDisplay.Compute(new Dictionary<uint, LayerState>()));
    }

    [Fact]
    public void TopLayerIsShown()
    {
        var b = LayerDisplay.Compute(One(State(0b1011)))!;
        Assert.Equal(3, b.Layer);
        Assert.Equal('3', b.Badge);
        Assert.Contains("レイヤー 3", b.Text);
    }

    [Fact]
    public void LayersAbove9UseLetters()
    {
        Assert.Equal('A', LayerDisplay.Compute(One(State(1u << 10)))!.Badge);
        Assert.Equal('F', LayerDisplay.Compute(One(State(1u << 15)))!.Badge);
    }

    [Fact]
    public void DisabledMapIsIgnored()
    {
        Assert.Null(LayerDisplay.Compute(One(State(0b10, enabled: false))));
    }

    [Fact]
    public void MultipleKeyboards_HighestLayerWins_AndTiePrefersLowerId()
    {
        var states = new Dictionary<uint, LayerState>
        {
            [5] = State(0b101, nicola: false),
            [2] = State(0b1001, nicola: true),
            [9] = State(0b1001, nicola: false),
        };
        var b = LayerDisplay.Compute(states)!;
        Assert.Equal(3, b.Layer);
        Assert.Contains("NICOLA", b.Text); // 同点は UniqId の小さい 2 番
    }

    [Fact]
    public void Tooltip_KeepsPrefixAndFitsLimit()
    {
        var badge = new LayerBadge(1, '1', "レイヤー 1");
        string t = LayerDisplay.AppendToTooltip("kasane — 動作中(2台)", badge);
        Assert.StartsWith("kasane", t);
        Assert.EndsWith("レイヤー 1", t);
        string longText = LayerDisplay.AppendToTooltip(new string('x', 200), badge);
        Assert.Equal(LayerDisplay.TooltipMax, longText.Length);
        Assert.Equal("kasane", LayerDisplay.AppendToTooltip("kasane", null));
    }
}
