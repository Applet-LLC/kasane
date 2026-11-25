using Kasane.Layout;
using Kasane.Model;
using Kasane.Model.Portable;

namespace Kasane.Tests;

/// <summary>交換用ファイルの取り込みで、読み込み先の先頭レイヤー N を指定したとき（2026-11-25）。</summary>
public class PortableLayerOffsetTests
{
    private static KeyboardLayout Ten => LayoutFixture.TenKey;

    private static KeymapProject Project(int layers)
    {
        var p = ProjectService.CreateDefault(0xCDEC0002, "HID\\VID_2", Ten, true);
        for (int i = 1; i < layers; i++) p.Layers.Add(ProjectService.DefaultLayer(Ten, i));
        return p;
    }

    /// <summary>レイヤー 0〜2 の、Emacs 風の小さな設定。</summary>
    private static PortableKeyboard Source(HelperConfig? helper = null)
    {
        var p = Project(3);
        p.Layers[0][0] = "KC_Z";           // 刻印のままではないキー
        p.Layers[0][1] = "OSL(1)";
        p.Layers[1][0] = "MO(2)";
        p.Layers[1][1] = "LT(2,KC_A)";
        p.Layers[2][0] = "LM(1,MOD_LCTL)";
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 32, Mods = "MOD_LCTL", Key = "KC_X", Replacement = "OSL(1)" });
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 34, Mods = "MOD_LCTL", Key = "KC_Y", Replacement = "OSL(2)" });
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 35, Mods = "MOD_LCTL", Key = "KC_L", Replacement = "KC_PGUP", Layers = new List<int> { 2 } });
        p.TapDances.Add(new TapDanceDef { Id = 0, SingleTap = "KC_A", SingleHold = "LM(1,MOD_LSFT)" });
        return PortableExporter.ExportKeyboard(p, Ten, helper, new ExportOptions(), new PortableReport());
    }

    private static ImportResult Run(PortableKeyboard src, KeymapProject target, ImportOptions o) =>
        PortableImporter.Import(src, target, Ten, null, null, o, layerScope: false);

    // ---- トークンのずらし ----

    [Theory]
    [InlineData("MO(1)", "MO(4)")]
    [InlineData("TG(2)", "TG(5)")]
    [InlineData("OSL(1)", "OSL(4)")]
    [InlineData("TO(0)", "TO(3)")]
    [InlineData("LT(1,KC_A)", "LT(4,KC_A)")]
    [InlineData("LM(1,MOD_LCTL)", "LM(4,MOD_LCTL)")]
    [InlineData("KC_A", "KC_A")]
    [InlineData("LCTL(KC_A)", "LCTL(KC_A)")]
    [InlineData("MACRO(2)", "MACRO(2)")]
    public void TokenIds_ShiftLayerReferences(string token, string expected)
    {
        Assert.Equal(expected, TokenIds.Rewrite(token, null, null, null, 3));
    }

    [Fact]
    public void TokenIds_OffsetZero_ChangesNothing()
    {
        Assert.Equal("MO(1)", TokenIds.Rewrite("MO(1)", null, null, null));
        Assert.Equal("LT(1,KC_A)", TokenIds.Rewrite("LT(1,KC_A)", null, null, null, 0));
    }

    // ---- 置き換え ----

    [Fact]
    public void Replace_OffsetThree_PutsFileLayersAtThreeToFive_AndKeepsTheLowerLayers()
    {
        var target = Project(5);
        target.Layers[0][5] = "KC_Q";   // 元のレイヤー 0〜2 の内容は残る
        target.Layers[2][4] = "KC_W";
        target.Layers[4][2] = "KC_E";   // 置き換える範囲（3〜5）の中身は、消える
        var res = Run(Source(), target, new ImportOptions { LayerOffset = 3 });

        Assert.False(res.Report.HasErrors);
        Assert.Equal(6, target.Layers.Count);
        Assert.Equal("KC_Q", target.Layers[0][5]);
        Assert.Equal("KC_W", target.Layers[2][4]);
        Assert.Equal("KC_TRNS", target.Layers[4][2]);
        Assert.Equal("MO(5)", target.Layers[4][0]);
        Assert.Equal("LT(5,KC_A)", target.Layers[4][1]);
        Assert.Equal("LM(4,MOD_LCTL)", target.Layers[5][0]);
        Assert.Equal("OSL(4)", target.Layers[3][1]);   // ファイルのレイヤー 0 のキー
        Assert.Equal("KC_Z", target.Layers[3][0]);
    }

    [Fact]
    public void Replace_OffsetThree_AddsEmptyLayersInBetween()
    {
        var target = Project(1);
        Run(Source(), target, new ImportOptions { LayerOffset = 3 });
        Assert.Equal(6, target.Layers.Count);
        Assert.All(target.Layers[1], t => Assert.Equal("KC_TRNS", t));
        Assert.All(target.Layers[2], t => Assert.Equal("KC_TRNS", t));
    }

    [Fact]
    public void Replace_OffsetThree_Layers0PassThroughKeysBecomeTransparent()
    {
        var target = Project(1);
        Run(Source(), target, new ImportOptions { LayerOffset = 3 });
        var layer3 = target.Layers[3];
        Assert.Equal("KC_Z", layer3[0]);
        Assert.Equal("KC_TRNS", layer3[2]); // 刻印のままのキーは、下のレイヤーを隠さない

        var keep = Project(1);
        Run(Source(), keep, new ImportOptions { LayerOffset = 3, BaseLayerPassThroughToTransparent = false });
        Assert.NotEqual("KC_TRNS", keep.Layers[3][2]); // OFF なら、レイヤー 0 のキーをそのまま置く
    }

    [Fact]
    public void Replace_OffsetThree_KeyOverridesAreShiftedAndScoped()
    {
        var target = Project(1);
        Run(Source(), target, new ImportOptions { LayerOffset = 3 });

        var o32 = target.KeyOverrides.Single(k => k.Key == "KC_X");
        Assert.Equal("OSL(4)", o32.Replacement);                         // 置き換え先のレイヤー番号
        Assert.Equal(new[] { 3, 4, 5 }, o32.Layers);                     // 条件なしの置き換えに、読み込んだレイヤーの条件
        Assert.Equal("OSL(5)", target.KeyOverrides.Single(k => k.Key == "KC_Y").Replacement);
        Assert.Equal(new[] { 5 }, target.KeyOverrides.Single(k => k.Key == "KC_L").Layers); // 元の条件は、ずらすだけ
    }

    [Fact]
    public void Replace_OffsetThree_ScopeOff_LeavesUnconditionalOverridesUnconditional()
    {
        var target = Project(1);
        Run(Source(), target, new ImportOptions { LayerOffset = 3, ScopeKeyOverridesToLayers = false });
        Assert.Null(target.KeyOverrides.Single(k => k.Key == "KC_X").Layers);
        Assert.Equal(new[] { 5 }, target.KeyOverrides.Single(k => k.Key == "KC_L").Layers);
    }

    [Fact]
    public void Replace_OffsetThree_TapDanceLayerReferencesAreShifted()
    {
        var target = Project(1);
        Run(Source(), target, new ImportOptions { LayerOffset = 3 });
        Assert.Equal("LM(4,MOD_LSFT)", target.TapDances.Single().SingleHold);
    }

    [Fact]
    public void Replace_OffsetThree_ReportsHowToTurnTheLayerOn()
    {
        var res = Run(Source(), Project(1), new ImportOptions { LayerOffset = 3 });
        Assert.Contains(res.Report.Lines, l => l.Level == ReportLevel.Warning && l.Text.Contains("レイヤー3 になりました"));
    }

    // ---- 範囲 ----

    [Fact]
    public void Replace_OffsetFourteen_DropsLayersBeyondFifteen_WithAWarning()
    {
        var target = Project(1);
        var res = Run(Source(), target, new ImportOptions { LayerOffset = 14 });
        Assert.Equal(16, target.Layers.Count);
        Assert.Contains(res.Report.Lines, l => l.Level == ReportLevel.Warning && l.Text.Contains("読み込みません"));
    }

    // ---- 追加 ----

    [Fact]
    public void Append_OffsetThree_LaysTheFileOverTheShiftedLayers()
    {
        var target = Project(4);
        target.Layers[3][5] = "KC_Q";
        var res = Run(Source(), target, new ImportOptions { Mode = ImportMode.Append, LayerOffset = 3 });
        Assert.False(res.Report.HasErrors);
        Assert.Equal("KC_Q", target.Layers[3][5]);        // 追加は、今の内容を残す
        Assert.Equal("OSL(4)", target.Layers[3][1]);
        Assert.Equal("MO(5)", target.Layers[4][0]);
    }

    // ---- ヘルパー設定 ----

    [Fact]
    public void OffsetThree_ShiftsTheLayersInTheHelperSettings()
    {
        var helper = new HelperConfig();
        helper.AppLayerRules.Add(new AppLayerRule { Name = "r", Layers = new List<int> { 1 } });
        helper.Actions.Add(new HelperAction { Id = 1, Action = new HelperActionBody { Type = HelperActionBody.AppLayer, Layers = new List<int> { 2 }, LayerOp = "On" } });
        helper.ImeLayers = new ImeLayerLink { Layers = new List<int> { 1 } };
        var res = Run(Source(helper), Project(1), new ImportOptions { LayerOffset = 3 });

        Assert.Equal(new[] { 4 }, res.Helper!.AppLayerRules.Single().Layers);
        Assert.Equal(new[] { 5 }, res.Helper.Actions.Single().Action.Layers);
        Assert.Equal(new[] { 4 }, res.Helper.ImeLayers!.Layers);
    }

    // ---- 先頭 0 のときは、今までと同じ ----

    [Fact]
    public void OffsetZero_IsTheSameAsBefore()
    {
        var withOption = Project(1);
        var without = Project(1);
        Run(Source(), withOption, new ImportOptions { LayerOffset = 0 });
        Run(Source(), without, new ImportOptions());
        Assert.Equal(3, withOption.Layers.Count);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(without.Layers), System.Text.Json.JsonSerializer.Serialize(withOption.Layers));
        Assert.Equal("OSL(1)", withOption.KeyOverrides.Single(k => k.Key == "KC_X").Replacement);
        Assert.Null(withOption.KeyOverrides.Single(k => k.Key == "KC_X").Layers);
    }

    [Fact]
    public void LayerScopedFile_IgnoresTheOffset()
    {
        var src = Source();
        src.Layers = new List<PortableLayer> { src.Layers![1] };
        var target = Project(2);
        PortableImporter.Import(src, target, Ten, null, null, new ImportOptions { LayerOffset = 5, TargetLayer = 1 }, layerScope: true);
        Assert.Equal(2, target.Layers.Count);
        Assert.Equal("MO(2)", target.Layers[1][0]); // 1枚のファイルは、今のまま（番号をずらさない）
    }
}
