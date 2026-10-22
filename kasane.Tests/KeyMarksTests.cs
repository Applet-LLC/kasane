using Kasane.Interop;
using Kasane.Layout;
using Kasane.Model;
using Xunit;

namespace Kasane.Tests;

public class KeyMarksTests
{
    private static readonly KeyboardLayout Layout = LayoutFixture.TenKey;

    private static KeymapProject NewProject() => ProjectService.CreateDefault(0, null, Layout, bundled: true);

    private static KeySlot Slot(int index) => Layout.Slots.First(s => s.Index == index);

    [Fact]
    public void PlainKey_HasNoMarks()
    {
        var p = NewProject();
        Assert.Equal("", KeyMarks.For(p, Layout, 0, Slot(0)).Marks);
    }

    [Fact]
    public void MacroAndTapDanceAssignments_GetMAndD()
    {
        var p = NewProject();
        p.Layers[0][0] = "MACRO(2)";
        p.Layers[0][1] = "TAPDANCE(3)";
        p.MacroNames = new() { [2] = "挨拶" };

        var m = KeyMarks.For(p, Layout, 0, Slot(0));
        Assert.Equal("M", m.Marks);
        Assert.Contains("マクロ2「挨拶」", m.ToolTip);
        Assert.Equal("D", KeyMarks.For(p, Layout, 0, Slot(1)).Marks);
    }

    [Fact]
    public void Combo_MarksBothKeys_OnEveryLayer()
    {
        var p = NewProject();
        p.Layers.Add(Enumerable.Repeat("KC_TRNS", p.Layers[0].Count).ToList()); // レイヤー1（全キー透過）
        p.Combos.Add(new ComboDef { Id = 1, Slot1 = 0, Slot2 = 1, Output = "KC_ESC" });

        foreach (int layer in new[] { 0, 1 })
        {
            Assert.Equal("C", KeyMarks.For(p, Layout, layer, Slot(0)).Marks);
            Assert.Equal("C", KeyMarks.For(p, Layout, layer, Slot(1)).Marks);
            Assert.Equal("", KeyMarks.For(p, Layout, layer, Slot(2)).Marks);
        }
        Assert.Contains("KC_ESC", KeyMarks.For(p, Layout, 0, Slot(0)).ToolTip);
    }

    [Fact]
    public void KeyOverride_MarksKeysThatOutputTheTrigger_OnlyOnLayersThatDoSo()
    {
        var p = NewProject();
        p.Layers[0][0] = "KC_A";
        p.Layers.Add(Enumerable.Repeat("KC_TRNS", p.Layers[0].Count).ToList());
        p.Layers[1][2] = "KC_A";      // レイヤー1では、キー2も A を出す
        p.Layers[1][0] = "KC_B";      // レイヤー1では、キー0は B に変わる
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 3, Mods = "MOD_LSFT", Key = "KC_A", Replacement = "KC_Z" });

        var onL0 = KeyMarks.For(p, Layout, 0, Slot(0));
        Assert.Equal("R", onL0.Marks);
        Assert.Contains("キー置換3", onL0.ToolTip);
        Assert.Contains("KC_Z", onL0.ToolTip);

        Assert.Equal("", KeyMarks.For(p, Layout, 1, Slot(0)).Marks);   // レイヤー1のキー0は B
        Assert.Equal("R", KeyMarks.For(p, Layout, 1, Slot(2)).Marks);  // レイヤー1のキー2は A
    }

    [Fact]
    public void TransparentKey_InheritsTheLowerLayersAssignment()
    {
        var p = NewProject();
        p.Layers[0][0] = "KC_A";
        p.Layers.Add(Enumerable.Repeat("KC_TRNS", p.Layers[0].Count).ToList());
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 0, Mods = "MOD_LCTL", Key = "KC_A", Replacement = "KC_B" });
        Assert.Equal("KC_A", KeyMarks.EffectiveToken(p, 1, Slot(0)));
        Assert.Equal("R", KeyMarks.For(p, Layout, 1, Slot(0)).Marks);
    }

    [Fact]
    public void ModdedKeyAssignment_MatchesOverrideByOutputKey()
    {
        var p = NewProject();
        p.Layers[0][0] = "LALT(KC_ZKHK)";    // Caps Lock を IME のオン・オフにする割り当て
        p.Layers[0][1] = "LALT(KC_A)";
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 0, Mods = "MOD_LSFT", Key = "KC_ZKHK", Replacement = "KC_CAPSLOCK" });

        var m = KeyMarks.For(p, Layout, 0, Slot(0));
        Assert.Equal("R", m.Marks);
        Assert.Contains("キー置換0", m.ToolTip);
        Assert.Equal("", KeyMarks.For(p, Layout, 0, Slot(1)).Marks);   // 出力キーが違う
    }

    [Fact]
    public void MarksAreOrderedMDCR()
    {
        var p = NewProject();
        p.Layers[0][0] = "MACRO(1)";
        p.Combos.Add(new ComboDef { Id = 0, Slot1 = 0, Slot2 = 1, Output = "KC_ESC" });
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 0, Mods = "MOD_LSFT", Key = "MACRO(1)", Replacement = "KC_B" }); // 同じトークンに当たる（印の並びの確認用）
        Assert.Equal("MCR", KeyMarks.For(p, Layout, 0, Slot(0)).Marks);
    }

    [Theory]
    [InlineData(0, true, "マクロ...")]
    [InlineData(2, true, "マクロ(2)...")]
    [InlineData(2, false, "マクロ(2・OFF)...")]
    [InlineData(0, false, "マクロ...")] // 定義が無ければ、OFF でも出さない
    public void ButtonText(int count, bool on, string expected) => Assert.Equal(expected, KeyMarks.ButtonText("マクロ", count, on));

    [Fact]
    public void FeatureOn_NullMeansAllOn()
    {
        var p = NewProject();
        Assert.True(KeyMarks.FeatureOn(p, KbdLayerClient.FeatureCombo));
        p.EnabledFeatures = KbdLayerClient.FeatureAll & ~KbdLayerClient.FeatureCombo;
        Assert.False(KeyMarks.FeatureOn(p, KbdLayerClient.FeatureCombo));
        Assert.True(KeyMarks.FeatureOn(p, KbdLayerClient.FeatureMacro));
    }
}
