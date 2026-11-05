using Kasane.Model;
using Xunit;

namespace Kasane.Tests;

/// <summary>割り当てダイアログを開いたときの、各パネルの初期値（GUI 計画 1-1）。</summary>
public class KeyAssignPrefillTests
{
    private const int Layers = 16;

    [Fact]
    public void ModTap_IsExpandedIntoPanel()
    {
        var p = KeyAssignPrefill.From("MT(MOD_LSFT,KC_F)", "KC_J", Layers);
        Assert.Equal(KeyAssignPanel.ModTap, p.Panel);
        Assert.Equal("MOD_LSFT", p.ModName);
        Assert.Equal("KC_F", p.ModTapKey);
    }

    [Fact]
    public void LayerTap_IsExpandedIntoPanel()
    {
        var p = KeyAssignPrefill.From("LT(3,KC_ENTER)", "KC_SPACE", Layers);
        Assert.Equal(KeyAssignPanel.LayerTap, p.Panel);
        Assert.Equal(3, p.Layer);
        Assert.Equal("KC_ENTER", p.LayerTapKey);
    }

    [Theory]
    [InlineData("MO(2)", "MO", 2)]
    [InlineData("TG(4)", "TG", 4)]
    [InlineData("DF(0)", "DF", 0)]
    [InlineData("OSL(5)", "OSL", 5)]
    [InlineData("TO(6)", "TO", 6)]
    [InlineData("TT(7)", "TT", 7)]
    [InlineData("LM(3,MOD_LCTL)", "LM", 3)]
    public void LayerActions_AreExpanded(string token, string func, int layer)
    {
        var p = KeyAssignPrefill.From(token, null, Layers);
        Assert.Equal(KeyAssignPanel.Layer, p.Panel);
        Assert.Equal(func, p.LayerFunc);
        Assert.Equal(layer, p.Layer);
    }

    [Fact]
    public void OneShotMod_OpensLayerPanelWithOsm()
    {
        var p = KeyAssignPrefill.From("OSM(MOD_LSFT)", null, Layers);
        Assert.Equal(KeyAssignPanel.Layer, p.Panel);
        Assert.Equal("OSM", p.LayerFunc);
        Assert.Equal(1, p.Layer); // レイヤーは使わないので既定のまま
    }

    [Theory]
    [InlineData("MACRO(5)", "MACRO", 5)]
    [InlineData("TAPDANCE(2)", "TAPDANCE", 2)]
    public void MacroAndTapDance_AreExpanded(string token, string kind, int id)
    {
        var p = KeyAssignPrefill.From(token, null, Layers);
        Assert.Equal(KeyAssignPanel.Macro, p.Panel);
        Assert.Equal(kind, p.MacroKind);
        Assert.Equal(id, p.MacroId);
    }

    [Fact]
    public void Helper_SelectsId()
    {
        var p = KeyAssignPrefill.From("HLP(12)", null, Layers);
        Assert.Equal(KeyAssignPanel.Helper, p.Panel);
        Assert.Equal(12, p.HelperId);
    }

    [Fact]
    public void BaseKey_BecomesTapKey_AndKeepsKeysPanel()
    {
        var p = KeyAssignPrefill.From("KC_CAPSLOCK", "KC_A", Layers);
        Assert.Equal(KeyAssignPanel.Keys, p.Panel);
        Assert.Equal("KC_CAPSLOCK", p.ModTapKey);
        Assert.Equal("KC_CAPSLOCK", p.LayerTapKey);
        Assert.Equal("MO", p.LayerFunc);
        Assert.Equal(1, p.Layer);
    }

    [Theory]
    [InlineData("KC_TRNS")]
    [InlineData("KC_NO")]
    [InlineData("LCTL(KC_C)")]
    [InlineData("bogus")]
    public void NonBaseKey_UsesSlotDefault(string token)
    {
        var p = KeyAssignPrefill.From(token, "KC_J", Layers);
        Assert.Equal("KC_J", p.ModTapKey);
        Assert.Equal("KC_J", p.LayerTapKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("KC_NO")]
    [InlineData("KC_TRNS")]
    public void NoKey_FallsBackToOldDefaults(string? slotDefault)
    {
        var p = KeyAssignPrefill.From("KC_TRNS", slotDefault, Layers);
        Assert.Equal("KC_A", p.ModTapKey);
        Assert.Equal("KC_SPACE", p.LayerTapKey);
    }

    [Fact]
    public void SingleLayer_DefaultLayerIsZero()
    {
        Assert.Equal(0, KeyAssignPrefill.From("KC_A", null, 1).Layer);
    }
}
