using System.Text.Json;
using Kasane.Model;
using Kasane.Model.Portable;
using Xunit;

namespace Kasane.Tests;

/// <summary>
/// レイヤー・マクロ・コンボ・タップダンス・キー置換の名前（番号は変えず、表示のためだけの付加情報）。
/// 名前は定義とは別の表（番号 → 名前）に持つので、中身が空（未使用）の番号にも名前だけ付けておける。
/// </summary>
public class ItemNamesTests
{
    private static Kasane.Layout.KeyboardLayout Ten => LayoutFixture.TenKey;

    private static KeymapProject Named()
    {
        var p = ProjectService.CreateDefault(0xCDEC0001, "HID-TEST-1", Ten, true);
        p.Layers.Add(ProjectService.DefaultLayer(Ten, 1));
        p.Layers.Add(ProjectService.DefaultLayer(Ten, 2));
        ProjectService.SetLayerName(p, 1, "仕事");
        p.Macros.Add(new MacroDef { Id = 3, Keys = { "KC_A" } });
        p.MacroNames = new() { [3] = "あいさつ", [9] = "あとで作る" };         // 9 は中身が空
        p.TapDances.Add(new TapDanceDef { Id = 2, SingleTap = "KC_ESC", SingleHold = "KC_LCTL" });
        p.TapDanceNames = new() { [2] = "Esc/Ctrl" };
        p.Combos.Add(new ComboDef { Id = 0, Slot1 = 0, Slot2 = 1, Output = "KC_ENT" });
        p.ComboNames = new() { [0] = "確定", [5] = "予定" };                    // 5 は中身が空
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 0, Mods = "MOD_LSFT", Key = "KC_BSPC", Replacement = "KC_DEL" });
        p.KeyOverrideNames = new() { [0] = "Shift+BS=Del" };
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

    // ---- 名前の表（中身が空の番号にも付けられる） ----

    [Fact]
    public void CleanNames_DropsBlankAndOutOfRange_AndTrims()
    {
        var clean = ProjectService.CleanNames(new Dictionary<int, string> { [1] = " a ", [2] = "  ", [-1] = "x", [99] = "y" }, 16);
        Assert.Equal(new Dictionary<int, string> { [1] = "a" }, clean);
        Assert.Null(ProjectService.CleanNames(new Dictionary<int, string> { [1] = "" }, 16));
        Assert.Null(ProjectService.CleanNames(null, 16));
    }

    [Fact]
    public void NormalizeShape_KeepsNamesOfEmptySlots()
    {
        var p = Named();
        ProjectService.NormalizeShape(p, Ten);
        Assert.Equal("あとで作る", p.MacroNames![9]);   // 中身が無い番号の名前も残る
        Assert.Equal("予定", p.ComboNames![5]);
        Assert.DoesNotContain(p.Macros, m => m.Id == 9);
    }

    [Fact]
    public void EmptyNamedSlot_DoesNotChangeWhatIsAppliedToTheDriver()
    {
        // 名前だけの番号は、定義ではないので、ドライバに送る内容（検証・適用）に影響しない。
        var named = Named();
        var plain = ProjectService.Clone(named);
        plain.MacroNames = null; plain.ComboNames = null; plain.TapDanceNames = null; plain.KeyOverrideNames = null;
        var a = ProjectService.Compile(named, Ten);
        var b = ProjectService.Compile(plain, Ten);
        Assert.Equal(b.Warnings, a.Warnings);
        Assert.Equal(b.Macros.Keys.OrderBy(x => x), a.Macros.Keys.OrderBy(x => x));
        Assert.Equal(b.Combos.Keys.OrderBy(x => x), a.Combos.Keys.OrderBy(x => x));
        Assert.Equal(b.TapDances.Keys.OrderBy(x => x), a.TapDances.Keys.OrderBy(x => x));
    }

    // ---- project.json ----

    [Fact]
    public void ProjectJson_RoundTripsNames_AndReadsOldFilesWithoutThem()
    {
        var copy = ProjectService.Clone(Named());
        Assert.Equal("仕事", ProjectService.LayerName(copy, 1));
        Assert.Equal("あいさつ", copy.MacroNames![3]);
        Assert.Equal("あとで作る", copy.MacroNames[9]);
        Assert.Equal("Esc/Ctrl", copy.TapDanceNames![2]);
        Assert.Equal("予定", copy.ComboNames![5]);
        Assert.Equal("Shift+BS=Del", copy.KeyOverrideNames![0]);

        // 名前の項目が無い、古い project.json
        const string old = "{\"Version\":1,\"UniqId\":1,\"Layers\":[[\"KC_A\"]],\"Macros\":[{\"Id\":1,\"Keys\":[\"KC_A\"]}],\"TapDances\":[],\"Combos\":[],\"KeyOverrides\":[]}";
        var loaded = JsonSerializer.Deserialize<KeymapProject>(old)!;
        Assert.Null(loaded.LayerNames);
        Assert.Null(loaded.MacroNames);
    }

    // ---- エクスポート／インポート ----

    private static ImportResult Import(PortableKeyboard kb, KeymapProject target, ImportOptions? o = null) =>
        PortableImporter.Import(kb, target, Ten, null, null, o ?? new ImportOptions(), layerScope: false);

    [Fact]
    public void Export_Then_Replace_CarriesEveryName_IncludingEmptySlots()
    {
        var kb = PortableExporter.ExportKeyboard(Named(), Ten, null, new ExportOptions(), new PortableReport());
        Assert.Equal("仕事", kb.Layers![1].Name);
        Assert.Null(kb.Layers[0].Name);

        // ファイルを経由しても名前が残る（JSON の往復）
        var file = PortableExporter.NewFile(PortableScope.Keyboard);
        file.Keyboards.Add(kb);
        Assert.True(PortableIO.TryParse(PortableIO.ToJson(file), out var parsed, out _));

        var target = ProjectService.CreateDefault(0xAAAA0000, "HID-TEST-2", Ten, true);
        var res = Import(parsed!.Keyboards[0], target);
        Assert.False(res.Report.HasErrors);
        Assert.Equal("仕事", ProjectService.LayerName(target, 1));
        Assert.Equal("あいさつ", target.MacroNames![3]);
        Assert.Equal("あとで作る", target.MacroNames[9]);
        Assert.Equal("Esc/Ctrl", target.TapDanceNames![2]);
        Assert.Equal("確定", target.ComboNames![0]);
        Assert.Equal("予定", target.ComboNames[5]);
        Assert.Equal("Shift+BS=Del", target.KeyOverrideNames![0]);
    }

    [Fact]
    public void Append_KeepsTheTargetsOwnNames_AndFillsOnlyMissingOnes()
    {
        var kb = PortableExporter.ExportKeyboard(Named(), Ten, null, new ExportOptions(), new PortableReport());
        var target = ProjectService.CreateDefault(0xAAAA0000, "HID-TEST-2", Ten, true);
        target.Layers.Add(ProjectService.DefaultLayer(Ten, 1));
        ProjectService.SetLayerName(target, 1, "自分の名前");
        target.Macros.Add(new MacroDef { Id = 5, Keys = { "KC_A" } });   // 同じ内容 → 番号5を共用。名前が無いので引き継ぐ
        target.Macros.Add(new MacroDef { Id = 9, Keys = { "KC_B" } });   // 9 は使用中 → 空きの名前「あとで作る」は付けない
        var res = Import(kb, target, new ImportOptions { Mode = ImportMode.Append });

        Assert.False(res.Report.HasErrors);
        Assert.Equal("自分の名前", ProjectService.LayerName(target, 1)); // 上書きしない
        Assert.Equal("あいさつ", target.MacroNames![5]);
        Assert.False(target.MacroNames.ContainsKey(9));
    }

    [Fact]
    public void Append_PutsAnEmptySlotsNameOnTheSameNumber_WhenThatNumberIsFree()
    {
        var kb = PortableExporter.ExportKeyboard(Named(), Ten, null, new ExportOptions(), new PortableReport());
        var target = ProjectService.CreateDefault(0xAAAA0000, "HID-TEST-2", Ten, true);
        Import(kb, target, new ImportOptions { Mode = ImportMode.Append });
        Assert.Equal("あとで作る", target.MacroNames![9]);
        Assert.Equal("予定", target.ComboNames![5]);
    }

    [Fact]
    public void Append_FollowsRenumberedItems()
    {
        var kb = PortableExporter.ExportKeyboard(Named(), Ten, null, new ExportOptions(), new PortableReport());
        var target = ProjectService.CreateDefault(0xAAAA0000, "HID-TEST-2", Ten, true);
        target.Macros.Add(new MacroDef { Id = 3, Keys = { "KC_Z" } });   // 番号3が違う内容で使用中 → 取り込むマクロは別の番号になる
        Import(kb, target, new ImportOptions { Mode = ImportMode.Append });
        int newId = target.Macros.Single(m => m.Keys.SequenceEqual(new[] { "KC_A" })).Id;
        Assert.NotEqual(3, newId);
        Assert.Equal("あいさつ", target.MacroNames![newId]);
    }

    [Fact]
    public void FilesWithoutNames_StillImport()
    {
        var p = Named();
        p.LayerNames = null;
        p.MacroNames = null; p.ComboNames = null; p.TapDanceNames = null; p.KeyOverrideNames = null;
        var kb = PortableExporter.ExportKeyboard(p, Ten, null, new ExportOptions(), new PortableReport());
        var file = PortableExporter.NewFile(PortableScope.Keyboard);
        file.Keyboards.Add(kb);
        string json = PortableIO.ToJson(file);
        Assert.DoesNotContain("Names", json); // 名前の無い項目は書かない（古い版でも読める）
        Assert.True(PortableIO.TryParse(json, out var parsed, out _));
        var target = ProjectService.CreateDefault(0xAAAA0000, "HID-TEST-2", Ten, true);
        Assert.False(Import(parsed!.Keyboards[0], target).Report.HasErrors);
        Assert.Null(ProjectService.LayerName(target, 1));
        Assert.Null(target.MacroNames);
    }
}
