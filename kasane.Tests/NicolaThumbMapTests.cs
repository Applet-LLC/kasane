using Kasane.Interop;
using Kasane.Keycodes;
using Kasane.Model;

namespace Kasane.Tests;

/// <summary>NICOLAを使うときに、NICOLAのレイヤーへ親指キーの割り当て（単独打鍵のキー）を自動で足す処理。</summary>
public class NicolaThumbMapTests
{
    private static NicolaDef Def(string leftTap = "KC_MHEN", string rightTap = "KC_HENK", int layer = 1)
    {
        var def = NicolaService.CreateStandard();
        def.Layer = layer;
        def.ThumbLeftTap = leftTap;
        def.ThumbRightTap = rightTap;
        return def;
    }

    private static KeymapProject Project(NicolaDef? nicola, int layers = 1)
    {
        var layout = LayoutFixture.TenKey;
        var p = ProjectService.CreateDefault(1, "HID\\TEST", layout, true);
        for (int i = 1; i < layers; i++) p.Layers.Add(ProjectService.DefaultLayer(layout, i));
        p.Nicola = nicola;
        return p;
    }

    private static List<ProjectService.SlotMapping> NoKeys() => new();

    // ------------------------------------------------------------------
    // ThumbLayerEntries
    // ------------------------------------------------------------------

    [Fact]
    public void Default_Thumb_Taps_Map_Each_Thumb_To_Itself()
    {
        var e = NicolaService.ThumbLayerEntries(Def());
        Assert.Equal(new (ushort, string)[] { (0x7B, "KC_MHEN"), (0x79, "KC_HENK") }, e);
    }

    [Fact]
    public void Enter_And_Space_Taps_Are_Used_As_The_Assignments()
    {
        var e = NicolaService.ThumbLayerEntries(Def("KC_ENTER", "KC_SPC"));
        Assert.Equal(new (ushort, string)[] { (0x7B, "KC_ENTER"), (0x79, "KC_SPC") }, e);
    }

    [Theory]
    [InlineData("")]
    [InlineData("KC_NO")]
    [InlineData("MO(1)")]       // 単純なキーではない
    [InlineData("KC_NOT_A_KEY")]
    public void An_Empty_Or_Unusable_Tap_Falls_Back_To_The_Thumb_Key_Itself(string tap)
    {
        var e = NicolaService.ThumbLayerEntries(Def(tap, "KC_SPC"));
        Assert.Equal((ushort)0x7B, e[0].ScanCode);
        Assert.Equal("KC_MHEN", e[0].Token);
        Assert.Equal("KC_SPC", e[1].Token);
    }

    [Fact]
    public void A_Thumb_Key_That_Is_Not_A_Plain_Key_Is_Skipped()
    {
        var def = Def();
        def.ThumbLeft = "MO(1)";
        var e = NicolaService.ThumbLayerEntries(def);
        Assert.Single(e);
        Assert.Equal((ushort)0x79, e[0].ScanCode);
    }

    // ------------------------------------------------------------------
    // BuildMapInputs
    // ------------------------------------------------------------------

    [Fact]
    public void Without_Nicola_The_Map_Is_Exactly_The_Mapped_Slots()
    {
        var project = Project(null);
        var layout = LayoutFixture.TenKey;
        var mapped = ProjectService.ResolveMappedSlots(project, layout, new List<string>(), new List<string>());
        var (inputs, layerCount) = ProjectService.BuildMapInputs(project, mapped);
        Assert.Equal(mapped.Count, inputs.Count);
        Assert.Equal(1, layerCount);
    }

    [Fact]
    public void Disabled_Nicola_Adds_Nothing()
    {
        var def = Def();
        def.Enabled = false;
        var (inputs, layerCount) = ProjectService.BuildMapInputs(Project(def), NoKeys());
        Assert.Empty(inputs);
        Assert.Equal(1, layerCount);
    }

    [Fact]
    public void An_Empty_Keymap_Still_Gets_A_Map_With_The_Thumb_Entries()
    {
        // NICOLAはレイヤーマップがONのときだけ動く。キー割り当てが1つも無くても、マップが空にならない。
        var project = Project(Def("KC_ENTER", "KC_SPC", layer: 1));
        var (inputs, layerCount) = ProjectService.BuildMapInputs(project, NoKeys());

        Assert.Equal(2, layerCount); // キーマップは1レイヤーだが、NICOLAのレイヤー1まで広げる
        Assert.Equal(2, inputs.Count);
        var left = inputs.Single(i => i.SourceScanCode == 0x7B);
        Assert.Equal(new[] { "KC_TRNS", "KC_ENTER" }, left.TokensPerLayer);
        var right = inputs.Single(i => i.SourceScanCode == 0x79);
        Assert.Equal(new[] { "KC_TRNS", "KC_SPC" }, right.TokensPerLayer);

        // 実際にblobにできる（キー2個）。
        var blob = BlobBuilder.Build(layerCount, inputs);
        Assert.Equal(2u, BitConverter.ToUInt32(blob, 4));
    }

    [Fact]
    public void Existing_Keys_Are_Padded_With_Transparent_Layers_Up_To_The_Nicola_Layer()
    {
        var project = Project(Def(layer: 2));
        var layout = LayoutFixture.TenKey;
        var mapped = ProjectService.ResolveMappedSlots(project, layout, new List<string>(), new List<string>());
        var (inputs, layerCount) = ProjectService.BuildMapInputs(project, mapped);
        Assert.Equal(3, layerCount);
        Assert.All(inputs.Take(mapped.Count), i => Assert.Equal(3, i.TokensPerLayer.Count));
        Assert.Equal(new[] { "KC_NUMLOCK", "KC_TRNS", "KC_TRNS" }, inputs[0].TokensPerLayer);
        Assert.Equal(mapped.Count + 2, inputs.Count);
    }

    [Fact]
    public void A_Thumb_Key_Already_In_The_Keymap_Gets_The_Tap_Key_Only_Where_The_Nicola_Layer_Is_Transparent()
    {
        // JIS配列では、無変換・変換がキーマップに入っている（既定のキーコードがKC_MHEN・KC_HENK）。
        var project = Project(Def("KC_ENTER", "KC_SPC", layer: 1), layers: 2);
        var layout = LayoutFixture.TenKey;
        project.Layers[0][0] = "KC_MHEN";
        project.Layers[0][1] = "KC_HENK";
        project.Layers[1][1] = "KC_LSFT"; // 利用者が、NICOLAのレイヤーで割り当てている
        var mapped = new List<ProjectService.SlotMapping>
        {
            new(layout.Slots[0], 0x7B),
            new(layout.Slots[1], 0x79),
        };

        var (inputs, layerCount) = ProjectService.BuildMapInputs(project, mapped);

        Assert.Equal(2, layerCount);
        Assert.Equal(2, inputs.Count); // 足さずに、既存のエントリを使う
        Assert.Equal(new[] { "KC_MHEN", "KC_ENTER" }, inputs[0].TokensPerLayer);   // 透過だったので入れた
        Assert.Equal(new[] { "KC_HENK", "KC_LSFT" }, inputs[1].TokensPerLayer);    // 利用者の割り当てを残した
    }

    [Fact]
    public void A_Nicola_Layer_Outside_The_Driver_Range_Is_Ignored()
    {
        var (inputs, layerCount) = ProjectService.BuildMapInputs(Project(Def(layer: KbdLayerClient.MaxLayers)), NoKeys());
        Assert.Empty(inputs);
        Assert.Equal(1, layerCount);
    }

    [Fact]
    public void Nicola_Layer_Zero_Maps_The_Thumbs_In_The_Base_Layer()
    {
        var (inputs, layerCount) = ProjectService.BuildMapInputs(Project(Def("KC_ENTER", "KC_SPC", layer: 0)), NoKeys());
        Assert.Equal(1, layerCount);
        Assert.Equal(new[] { "KC_ENTER" }, inputs.Single(i => i.SourceScanCode == 0x7B).TokensPerLayer);
    }
}
