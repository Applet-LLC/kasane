using System.Text.Json;
using Kasane.Model;
using Kasane.Model.Portable;
using Xunit;

namespace Kasane.Tests;

/// <summary>レイヤー・マクロ・コンボ・タップダンス・キー置換の名前（番号は変えず、表示のためだけの付加情報）。</summary>
public class ItemNamesTests
{
    private static Kasane.Layout.KeyboardLayout Ten => LayoutFixture.TenKey;

    private static KeymapProject Named()
    {
        var p = ProjectService.CreateDefault(0xCDEC0001, "HID-TEST-1", Ten, true);
        p.Layers.Add(ProjectService.DefaultLayer(Ten, 1));
        p.Layers.Add(ProjectService.DefaultLayer(Ten, 2));
        ProjectService.SetLayerName(p, 1, "仕事");
        p.Macros.Add(new MacroDef { Id = 3, Name = "あいさつ", Keys = { "KC_A" } });
        p.TapDances.Add(new TapDanceDef { Id = 2, Name = "Esc/Ctrl", SingleTap = "KC_ESC", SingleHold = "KC_LCTL" });
        p.Combos.Add(new ComboDef { Id = 0, Name = "確定", Slot1 = 0, Slot2 = 1, Output = "KC_ENT" });
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 0, Name = "Shift+BS=Del", Mods = "MOD_LSFT", Key = "KC_BSPC", Replacement = "KC_DEL" });
        return p;
    }

    // ---- レイヤー名 ----

    [Fact]
    public void LayerName_SetGetClear_AndLabel()
    {
        var p = Named();
        Assert.Equal("仕事", ProjectService.LayerName(p, 1));
        Assert.Null(ProjectService.LayerName(p, 0));
        Assert.Null(ProjectService.LayerName(p, 9));
        Assert.Equal("レイヤー1「仕事」", ProjectService.LayerLabel(p, 1));
        Assert.Equal("レイヤー0", ProjectService.LayerLabel(p, 0));

        ProjectService.SetLayerName(p, 1, "  ");
        Assert.Null(ProjectService.LayerName(p, 1));
        Assert.Null(p.LayerNames); // 名前が1つも無ければ、項目ごと無くす
    }

    [Fact]
    public void SetLayerName_IgnoresLayersThatDoNotExist()
    {
        var p = Named();
        ProjectService.SetLayerName(p, 7, "x");
        ProjectService.SetLayerName(p, -1, "x");
        Assert.Equal("仕事", ProjectService.LayerName(p, 1));
        Assert.Equal(p.Layers.Count, p.LayerNames!.Count);
    }

    [Fact]
    public void NormalizeLayerNames_FollowsTheNumberOfLayers()
    {
        var p = Named();
        p.LayerNames = new List<string?> { null, "仕事" };             // 短い → 足す
        ProjectService.NormalizeLayerNames(p);
        Assert.Equal(3, p.LayerNames!.Count);

        p.Layers.RemoveAt(2);
        p.Layers.RemoveAt(1);                                          // レイヤーを減らす → 名前も捨てる
        ProjectService.NormalizeLayerNames(p);
        Assert.Null(p.LayerNames);
    }

    // ---- project.json ----

    [Fact]
    public void ProjectJson_RoundTripsNames_AndReadsOldFilesWithoutThem()
    {
        var p = Named();
        var copy = ProjectService.Clone(p);
        Assert.Equal("仕事", ProjectService.LayerName(copy, 1));
        Assert.Equal("あいさつ", copy.Macros[0].Name);
        Assert.Equal("Esc/Ctrl", copy.TapDances[0].Name);
        Assert.Equal("確定", copy.Combos[0].Name);
        Assert.Equal("Shift+BS=Del", copy.KeyOverrides[0].Name);

        // 名前の項目が無い、古い project.json
        const string old = "{\"Version\":1,\"UniqId\":1,\"Layers\":[[\"KC_A\"]],\"Macros\":[{\"Id\":1,\"Keys\":[\"KC_A\"]}],\"TapDances\":[],\"Combos\":[],\"KeyOverrides\":[]}";
        var loaded = JsonSerializer.Deserialize<KeymapProject>(old)!;
        Assert.Null(loaded.LayerNames);
        Assert.Null(loaded.Macros[0].Name);
    }

    // ---- エクスポート／インポート ----

    private static ImportResult Import(PortableKeyboard kb, KeymapProject target, ImportOptions? o = null) =>
        PortableImporter.Import(kb, target, Ten, null, null, o ?? new ImportOptions(), layerScope: false);

    [Fact]
    public void Export_Then_Replace_CarriesEveryName()
    {
        var kb = PortableExporter.ExportKeyboard(Named(), Ten, null, new ExportOptions(), new PortableReport());
        Assert.Equal("仕事", kb.Layers![1].Name);
        Assert.Null(kb.Layers[0].Name);
        Assert.Equal("確定", kb.Combos![0].Name);

        // ファイルを経由しても名前が残る（JSON の往復）
        var file = PortableExporter.NewFile(PortableScope.Keyboard);
        file.Keyboards.Add(kb);
        Assert.True(PortableIO.TryParse(PortableIO.ToJson(file), out var parsed, out _));

        var target = ProjectService.CreateDefault(0xAAAA0000, "HID-TEST-2", Ten, true);
        var res = Import(parsed!.Keyboards[0], target);
        Assert.False(res.Report.HasErrors);
        Assert.Equal("仕事", ProjectService.LayerName(target, 1));
        Assert.Equal("あいさつ", target.Macros.Single().Name);
        Assert.Equal("Esc/Ctrl", target.TapDances.Single().Name);
        Assert.Equal("確定", target.Combos.Single().Name);
        Assert.Equal("Shift+BS=Del", target.KeyOverrides.Single().Name);
    }

    [Fact]
    public void Append_KeepsTheTargetsOwnNames_AndFillsOnlyMissingOnes()
    {
        var kb = PortableExporter.ExportKeyboard(Named(), Ten, null, new ExportOptions(), new PortableReport());
        var target = ProjectService.CreateDefault(0xAAAA0000, "HID-TEST-2", Ten, true);
        target.Layers.Add(ProjectService.DefaultLayer(Ten, 1));
        ProjectService.SetLayerName(target, 1, "自分の名前");
        target.Macros.Add(new MacroDef { Id = 5, Keys = { "KC_A" } }); // 同じ内容で名前が無い → 名前だけ入る
        var res = Import(kb, target, new ImportOptions { Mode = ImportMode.Append });

        Assert.False(res.Report.HasErrors);
        Assert.Equal("自分の名前", ProjectService.LayerName(target, 1)); // 上書きしない
        Assert.Equal("あいさつ", target.Macros.Single(m => m.Id == 5).Name);
    }

    [Fact]
    public void FilesWithoutNames_StillImport()
    {
        var p = Named();
        p.LayerNames = null;
        p.Macros[0].Name = null;
        var kb = PortableExporter.ExportKeyboard(p, Ten, null, new ExportOptions(), new PortableReport());
        var file = PortableExporter.NewFile(PortableScope.Keyboard);
        file.Keyboards.Add(kb);
        string json = PortableIO.ToJson(file);
        Assert.DoesNotContain("\"Name\"", json.Split("\"Combos\"")[0]); // 名前の無い項目は書かない（古い版でも読める）
        Assert.True(PortableIO.TryParse(json, out var parsed, out _));
        var target = ProjectService.CreateDefault(0xAAAA0000, "HID-TEST-2", Ten, true);
        Assert.False(Import(parsed!.Keyboards[0], target).Report.HasErrors);
        Assert.Null(ProjectService.LayerName(target, 1));
    }
}
