using Kasane.Layout;
using Kasane.Model;
using Xunit;

namespace Kasane.Tests;

/// <summary>番号への参照の洗い出しと、外し方（GUI 計画 1-3。削除の確認と「割り当ても外す」）。</summary>
public class ProjectReferencesTests
{
    private static readonly KeyboardLayout Layout = LayoutFixture.TenKey;

    private static KeymapProject NewProject()
    {
        var p = ProjectService.CreateDefault(0, null, Layout, bundled: true);
        p.Layers.Add(Enumerable.Repeat("KC_TRNS", p.Layers[0].Count).ToList());
        return p;
    }

    [Theory]
    [InlineData("MACRO(3)", RefKind.Macro, 3, true)]
    [InlineData("MACRO(3)", RefKind.Macro, 4, false)]
    [InlineData("MACRO(3)", RefKind.TapDance, 3, false)]
    [InlineData("TAPDANCE(2)", RefKind.TapDance, 2, true)]
    [InlineData("HLP(12)", RefKind.Helper, 12, true)]
    [InlineData("HLP(0x0C)", RefKind.Helper, 12, true)]
    [InlineData("KC_A", RefKind.Macro, 0, false)]
    [InlineData("", RefKind.Macro, 0, false)]
    [InlineData("bogus(", RefKind.Macro, 0, false)]
    public void RefersTo(string token, RefKind kind, int id, bool expected) =>
        Assert.Equal(expected, ProjectService.RefersTo(token, kind, id));

    [Fact]
    public void FindReferences_CoversKeymapAndDefinitions()
    {
        var p = NewProject();
        p.Layers[0][0] = "MACRO(3)";
        p.Layers[1][2] = "MACRO(3)";
        p.Layers[1][1] = "MACRO(4)";
        p.Combos.Add(new ComboDef { Id = 1, Slot1 = 0, Slot2 = 1, Output = "MACRO(3)", Disabled = true }); // 無効も数える
        p.TapDances.Add(new TapDanceDef { Id = 2, SingleTap = "KC_A", DoubleTap = "MACRO(3)", TripleTap = "MACRO(3)" });
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 5, Mods = "MOD_LCTL", Key = "KC_A", Replacement = "MACRO(3)" });

        var refs = ProjectService.FindReferences(p, RefKind.Macro, 3);
        Assert.Equal(2, refs.Count(r => r.Place == RefPlace.Keymap));
        Assert.Contains(refs, r => r.Place == RefPlace.Keymap && r.Layer == 1 && r.Slot == 2);
        Assert.Contains(refs, r => r.Place == RefPlace.ComboOutput && r.DefId == 1);
        Assert.Equal(new[] { "DoubleTap", "TripleTap" }, refs.Where(r => r.Place == RefPlace.TapDanceField).Select(r => r.Field));
        Assert.Contains(refs, r => r.Place == RefPlace.KeyOverrideReplacement && r.DefId == 5);
        Assert.Empty(ProjectService.FindReferences(p, RefKind.Macro, 9));
    }

    [Fact]
    public void RemoveReferences_RestoresTransparent_ClearsFields_DisablesDefinitions()
    {
        var p = NewProject();
        p.Layers[0][0] = "MACRO(3)";
        p.Layers[1][1] = "MACRO(4)";
        p.Combos.Add(new ComboDef { Id = 1, Slot1 = 0, Slot2 = 1, Output = "MACRO(3)" });
        p.TapDances.Add(new TapDanceDef { Id = 2, SingleTap = "KC_A", DoubleTap = "MACRO(3)" });
        p.TapDances.Add(new TapDanceDef { Id = 3, SingleTap = "MACRO(3)" }); // これだけを指すタップダンスは、未使用になる
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 5, Mods = "MOD_LCTL", Key = "KC_A", Replacement = "MACRO(3)" });

        int n = ProjectService.RemoveReferences(p, RefKind.Macro, 3);

        Assert.Equal(5, n); // キー1、コンボ1、タップダンスの欄2、キー置換1
        Assert.Equal("KC_TRNS", p.Layers[0][0]);
        Assert.Equal("MACRO(4)", p.Layers[1][1]);                 // 別の番号はそのまま
        Assert.True(p.Combos.Single().Disabled);
        Assert.Equal("MACRO(3)", p.Combos.Single().Output);       // 中身は残す
        var td = Assert.Single(p.TapDances);
        Assert.Equal(2, td.Id);
        Assert.Equal("KC_A", td.SingleTap);
        Assert.Equal("", td.DoubleTap);
        Assert.True(p.KeyOverrides.Single().Disabled);
        Assert.Empty(ProjectService.FindReferences(p, RefKind.Macro, 3).Where(r => r.Place is RefPlace.Keymap or RefPlace.TapDanceField));
    }

    [Fact]
    public void RemoveReferences_Helper_InKeymapAndOverride()
    {
        var p = NewProject();
        p.Layers[0][2] = "HLP(7)";
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 0, Mods = "MOD_LCTL", Key = "KC_X", Replacement = "HLP(7)" });
        Assert.Equal(2, ProjectService.FindReferences(p, RefKind.Helper, 7).Count);
        Assert.Equal(2, ProjectService.RemoveReferences(p, RefKind.Helper, 7));
        Assert.Equal("KC_TRNS", p.Layers[0][2]);
        Assert.True(p.KeyOverrides[0].Disabled);
    }

    [Fact]
    public void DescribeReference_NamesTheKeyAndDefinition()
    {
        var p = NewProject();
        p.TapDanceNames = new() { [2] = "記号" };
        var slot = Layout.Slots.First(s => s.Index == 0);
        Assert.Equal($"レイヤー1 {slot.Description}", ProjectService.DescribeReference(p, Layout, new ProjectReference(RefPlace.Keymap, 1, 0, -1, null)));
        Assert.Equal("タップダンス2「記号」 の2回タップ", ProjectService.DescribeReference(p, Layout, new ProjectReference(RefPlace.TapDanceField, -1, -1, 2, "DoubleTap")));
        Assert.Contains("無効にします", ProjectService.DescribeReference(p, Layout, new ProjectReference(RefPlace.ComboOutput, -1, -1, 1, null)));
    }
}
