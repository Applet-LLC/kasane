using Kasane.Interop;
using Kasane.Keycodes;
using Kasane.Layout;
using Kasane.Model;
using Xunit;

namespace Kasane.Tests;

/// <summary>キー置換のレイヤー条件（効くレイヤー。2026-11-25）。ドライバの FLAG_LAYERS と "KeyOverrideLayers" 値に対応する。</summary>
public class KeyOverrideLayersTests
{
    private static readonly KeyboardLayout Layout = LayoutFixture.TenKey;

    private static KeymapProject NewProject(int layers = 4)
    {
        var p = ProjectService.CreateDefault(0x55F70000, null, Layout, bundled: true);
        while (p.Layers.Count < layers) p.Layers.Add(Enumerable.Repeat("KC_TRNS", p.Layers[0].Count).ToList());
        return p;
    }

    private static KeySlot Slot(int index) => Layout.Slots.First(s => s.Index == index);

    private static KeyOverrideDef Def(int id, params int[] layers) => new()
    {
        Id = id, Mods = "MOD_LCTL", Key = "KC_L", Replacement = "LCTL(KC_PGUP)", Layers = layers.Length == 0 ? null : layers.ToList(),
    };

    // ---- 定義 → ドライバへ送る形 ----

    [Fact]
    public void NoLayers_MeansAllLayers_MaskZero()
    {
        var errors = new List<string>();
        var entry = ProjectService.KeyOverrideToEntry(Def(0), errors);
        Assert.Empty(errors);
        Assert.Equal(0, entry!.Layers);
    }

    [Fact]
    public void Layers_BecomeABitMask()
    {
        var errors = new List<string>();
        var entry = ProjectService.KeyOverrideToEntry(Def(0, 3, 5), errors);
        Assert.Empty(errors);
        Assert.Equal((1 << 3) | (1 << 5), entry!.Layers);
        // FlagLayers は、送るとき（SetKeyOverride・永続化）に Layers から立てる。定義から作る Flags には入れない。
        Assert.Equal(0, entry.Flags & KeyOverrideEntry.FlagLayers);
    }

    [Fact]
    public void Layer0AndLayer15_AreTheLimits()
    {
        var errors = new List<string>();
        var entry = ProjectService.KeyOverrideToEntry(Def(0, 0, 15), errors);
        Assert.Empty(errors);
        Assert.Equal(0x8001, entry!.Layers);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(-1)]
    public void LayerOutOfRange_IsAnError(int layer)
    {
        var errors = new List<string>();
        var entry = ProjectService.KeyOverrideToEntry(Def(0, layer), errors);
        Assert.Null(entry);
        Assert.Contains(errors, e => e.Contains("効くレイヤー"));
    }

    [Fact]
    public void Compile_RejectsALayerTheProjectDoesNotHave()
    {
        var p = NewProject(2);
        p.KeyOverrides.Add(Def(0, 3));
        var ex = Assert.Throws<InvalidOperationException>(() => ProjectService.Compile(p, Layout));
        Assert.Contains("効くレイヤー3", ex.Message);
    }

    [Fact]
    public void Compile_AcceptsALayerTheProjectHas_AndKeepsTheMask()
    {
        var p = NewProject(4);
        p.KeyOverrides.Add(Def(0, 3));
        var settings = ProjectService.Compile(p, Layout);
        Assert.Equal(1 << 3, settings.KeyOverrides[0]!.Layers);
    }

    [Fact]
    public void Compile_IgnoresALayerOfADisabledOverride()
    {
        var p = NewProject(2);
        var d = Def(0, 9);
        d.Disabled = true;
        p.KeyOverrides.Add(d);
        var settings = ProjectService.Compile(p, Layout); // 無効のものは検証しない
        Assert.DoesNotContain(0, settings.KeyOverrides.Where(kv => kv.Value != null).Select(kv => kv.Key));
    }

    [Fact]
    public void MaskAndListConvertBothWays()
    {
        Assert.Null(ProjectService.LayerMaskToList(0));
        Assert.Equal(new[] { 0, 3, 15 }, ProjectService.LayerMaskToList(0x8009));
        Assert.Null(ProjectService.NormalizeOverrideLayers(null));
        Assert.Null(ProjectService.NormalizeOverrideLayers(new List<int>()));
        Assert.Equal(new[] { 1, 4 }, ProjectService.NormalizeOverrideLayers(new[] { 4, 1, 4 }));
    }

    // ---- project.json ----

    [Fact]
    public void Layers_AreSavedOnlyWhenSet()
    {
        Assert.DoesNotContain("Layers", System.Text.Json.JsonSerializer.Serialize(Def(0)));
        Assert.Contains("\"Layers\":[3]", System.Text.Json.JsonSerializer.Serialize(Def(0, 3)));
    }

    // ---- レジストリの値（ドライバと同じ形） ----

    [Fact]
    public void KeyOverrideLayers_Is256Bytes_AndKeyOverridesStays1536()
    {
        var overrides = new Dictionary<int, KeyOverrideEntry?>
        {
            [2] = new KeyOverrideEntry(0x01, 0x0026, new KeyAction(ActionType.ModdedKey, 0x01, 0x0049), 0, 1 << 3),
        };
        Assert.Equal(256, PersistedSettings.SerializeKeyOverrideLayers(overrides).Length);
        Assert.Equal(1536, PersistedSettings.SerializeKeyOverrides(overrides).Length);
        Assert.Equal(256, PersistedSettings.SerializeKeyOverrideLayers(new Dictionary<int, KeyOverrideEntry?>()).Length);
    }

    [Fact]
    public void KeyOverrideLayers_PlacesTheMaskAtTheIdsPosition()
    {
        var blob = PersistedSettings.SerializeKeyOverrideLayers(new Dictionary<int, KeyOverrideEntry?>
        {
            [0] = new KeyOverrideEntry(0x01, 0x0026, new KeyAction(ActionType.Key, 0, 0x0049)),          // 条件なし
            [2] = new KeyOverrideEntry(0x01, 0x0026, new KeyAction(ActionType.Key, 0, 0x0049), 0, 0x0008), // レイヤー3
            [127] = new KeyOverrideEntry(0x01, 0x0026, new KeyAction(ActionType.Key, 0, 0x0049), 0, 0x8001),
        });
        Assert.Equal(0, BitConverter.ToUInt16(blob, 0));
        Assert.Equal(0x0008, BitConverter.ToUInt16(blob, 4));
        Assert.Equal(0x8001, BitConverter.ToUInt16(blob, 254));
        Assert.Equal(0, BitConverter.ToUInt16(blob, 2));
    }

    [Fact]
    public void KeyOverrides_SetFlagLayers_OnlyForLayeredEntries()
    {
        var blob = PersistedSettings.SerializeKeyOverrides(new Dictionary<int, KeyOverrideEntry?>
        {
            [0] = new KeyOverrideEntry(0x01, 0x0026, new KeyAction(ActionType.Key, 0, 0x0049)),
            [1] = new KeyOverrideEntry(0x01, 0x0026, new KeyAction(ActionType.Key, 0, 0x0049), 0, 0x0008),
            [2] = new KeyOverrideEntry(0x01, 0x0026, new KeyAction(ActionType.Macro, 3, 0), KeyOverrideEntry.FlagRepeat, 0x0008),
        });
        Assert.Equal(0x00, blob[0 * 12 + 5]);
        Assert.Equal(KeyOverrideEntry.FlagLayers, blob[1 * 12 + 5]);
        Assert.Equal(KeyOverrideEntry.FlagRepeat | KeyOverrideEntry.FlagLayers, blob[2 * 12 + 5]);
    }

    [Fact]
    public void BuildValues_WritesKeyOverrideLayers_OnlyWhenSomeEntryHasLayers()
    {
        DriverSettings Settings(Dictionary<int, KeyOverrideEntry?> ko) => new(
            null, 0, 0, 0, 0, 0, 0,
            new Dictionary<int, List<MacroStep>>(), new Dictionary<int, ComboEntry?>(), new Dictionary<int, TapDanceEntry?>(), ko,
            null, null, false, new List<string>());
        var plain = new Dictionary<int, KeyOverrideEntry?> { [0] = new KeyOverrideEntry(0x01, 0x0026, new KeyAction(ActionType.Key, 0, 0x0049)) };
        var layered = new Dictionary<int, KeyOverrideEntry?> { [0] = new KeyOverrideEntry(0x01, 0x0026, new KeyAction(ActionType.Key, 0, 0x0049), 0, 0x0008) };

        Assert.DoesNotContain(PersistedSettings.BuildValues(Settings(plain)), v => v.Name == "KeyOverrideLayers");
        var values = PersistedSettings.BuildValues(Settings(layered));
        var layers = Assert.Single(values, v => v.Name == "KeyOverrideLayers");
        Assert.Equal(256, ((byte[])layers.Value).Length);
    }

    // ---- 割り当て画面・一覧の表示 ----

    [Fact]
    public void AppliesInLayer_AllLayersWhenNoCondition()
    {
        var p = NewProject();
        Assert.True(KeyMarks.AppliesInLayer(p, Def(0), 0, Slot(0)));
        Assert.True(KeyMarks.AppliesInLayer(p, Def(0), 3, Slot(0)));
    }

    [Fact]
    public void AppliesInLayer_ConditionLayerAndTheLayersWhereTheKeyIsTransparentAbove()
    {
        var p = NewProject(5);
        p.Layers[4][0] = "KC_Q"; // レイヤー4では、キー0は別のキー。ほかは透過
        var d = Def(0, 3);
        Assert.False(KeyMarks.AppliesInLayer(p, d, 0, Slot(0)));
        Assert.False(KeyMarks.AppliesInLayer(p, d, 2, Slot(0)));
        Assert.True(KeyMarks.AppliesInLayer(p, d, 3, Slot(0)));
        // レイヤー4の画面で、キー1は透過なので、レイヤー3の割り当てが届く。キー0は、レイヤー4が別のキーなので届かない。
        Assert.True(KeyMarks.AppliesInLayer(p, d, 4, Slot(1)));
        Assert.False(KeyMarks.AppliesInLayer(p, d, 4, Slot(0)));
    }

    [Fact]
    public void KeyMarks_ShowsRonlyOnTheConditionLayer()
    {
        var p = NewProject(4);
        p.Layers[0][0] = "KC_L";
        p.KeyOverrides.Add(Def(0, 3));
        Assert.Equal("", KeyMarks.For(p, Layout, 0, Slot(0)).Marks);
        Assert.Equal("", KeyMarks.For(p, Layout, 2, Slot(0)).Marks);
        var onL3 = KeyMarks.For(p, Layout, 3, Slot(0));
        Assert.Equal("R", onL3.Marks);
        Assert.Contains("レイヤー 3 のとき", onL3.ToolTip);
    }

    [Fact]
    public void KeyMarks_ConditionedOverride_IgnoresALowerLayerThatIsNotActiveTogether()
    {
        // emacs-excel と同じ形: レイヤー 2（C-q の一時レイヤー）で L がマクロ。レイヤー 3 は空。条件のレイヤーは 3。
        // 実際には、レイヤー 2 とレイヤー 3 は同時に有効にならないので、レイヤー 3 の L は、レイヤー 0 の KC_L として働く。
        var p = NewProject(4);
        p.Layers[0][0] = "KC_L";
        p.Layers[2][0] = "MACRO(104)";
        p.Macros.Add(new MacroDef { Id = 104, Keys = { "KC_A" } });
        p.KeyOverrides.Add(Def(0, 3));
        Assert.Contains("R", KeyMarks.For(p, Layout, 3, Slot(0)).Marks);
        Assert.DoesNotContain("R", KeyMarks.For(p, Layout, 2, Slot(0)).Marks);
        Assert.DoesNotContain("R", KeyMarks.For(p, Layout, 0, Slot(0)).Marks);
        // 条件のレイヤー自身が、このキーを別のキーにしているときは、R は出ない。
        p.Layers[3][0] = "KC_Q";
        Assert.DoesNotContain("R", KeyMarks.For(p, Layout, 3, Slot(0)).Marks);
    }

    [Fact]
    public void KeyMarks_ShowsRonEveryLayerWhenNoCondition()
    {
        var p = NewProject(4);
        p.Layers[0][0] = "KC_L";
        p.KeyOverrides.Add(Def(0));
        Assert.Equal("R", KeyMarks.For(p, Layout, 0, Slot(0)).Marks);
        Assert.Equal("R", KeyMarks.For(p, Layout, 3, Slot(0)).Marks);
    }

    [Fact]
    public void RemapText_NotesTheConditionLayers()
    {
        var p = NewProject(4);
        p.KeyOverrides.Add(Def(0, 3));
        p.KeyOverrides.Add(Def(1, 2, 3));
        p.KeyOverrides.Add(Def(2));
        var text = RemapTextFormatter.Format(p, Layout);
        Assert.Contains("(レイヤー 3 のとき)", text);
        Assert.Contains("(レイヤー 2・3 のとき)", text);
        Assert.Equal(2, text.Split("のとき)").Length - 1);
    }
}
