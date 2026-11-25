using Kasane.Layout;
using Kasane.Model;
using Kasane.Model.Portable;

namespace Kasane.Tests;

/// <summary>交換用ファイルでの、キー置換の「効くレイヤー」（2026-11-25）と、置き換え先の MACRO 番号の付け直し。</summary>
public class PortableKeyOverrideLayersTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kasane-portable-ko-" + Guid.NewGuid().ToString("N"));

    public PortableKeyOverrideLayersTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static KeyboardLayout Ten => LayoutFixture.TenKey;

    private static KeymapProject Project(int layers)
    {
        var p = ProjectService.CreateDefault(0xCDEC0001, "HID\\VID_1", Ten, true);
        for (int i = 1; i < layers; i++) p.Layers.Add(ProjectService.DefaultLayer(Ten, i));
        return p;
    }

    private static KeyOverrideDef Excel(int id, params int[] layers) => new()
    {
        Id = id, Mods = "MOD_LCTL", Key = "KC_L", Replacement = "LCTL(KC_PGUP)", Layers = layers.ToList(),
    };

    private static PortableKeyboard Export(KeymapProject p, PortableReport? report = null) =>
        PortableExporter.ExportKeyboard(p, Ten, null, new ExportOptions(), report ?? new PortableReport());

    [Fact]
    public void Layers_SurviveAFileRoundTrip()
    {
        var src = Project(4);
        src.KeyOverrides.Add(Excel(35, 3));
        src.KeyOverrides.Add(new KeyOverrideDef { Id = 1, Mods = "MOD_LSFT", Key = "KC_BSPC", Replacement = "KC_DEL" });
        var file = PortableExporter.NewFile(PortableScope.Keyboard);
        file.Keyboards.Add(Export(src));
        string path = Path.Combine(_dir, "a.kasane.json");
        PortableIO.Write(path, file);

        string text = File.ReadAllText(path);
        Assert.Matches("\"Layers\":\\s*\\[\\s*3\\s*\\]", text); // 書き出された
        Assert.True(PortableIO.TryRead(path, out var read, out var error), error);
        var ko = read!.Keyboards![0].KeyOverrides!;
        Assert.Equal(new[] { 3 }, ko.Single(k => k.Id == 35).Layers);
        Assert.Null(ko.Single(k => k.Id == 1).Layers); // 条件なしは、欄を書かない
    }

    [Fact]
    public void Export_WarnsThatOlderVersionsIgnoreTheCondition()
    {
        var src = Project(4);
        src.KeyOverrides.Add(Excel(35, 3));
        var report = new PortableReport();
        Export(src, report);
        Assert.Contains(report.Lines, l => l.Level == ReportLevel.Warning && l.Text.Contains("古い版"));

        var plain = Project(1);
        plain.KeyOverrides.Add(new KeyOverrideDef { Id = 0, Mods = "MOD_LSFT", Key = "KC_BSPC", Replacement = "KC_DEL" });
        var report2 = new PortableReport();
        Export(plain, report2);
        Assert.DoesNotContain(report2.Lines, l => l.Text.Contains("効くレイヤー"));
    }

    [Fact]
    public void ReplaceImport_KeepsTheLayers_WithoutRenumbering()
    {
        var src = Project(4);
        src.KeyOverrides.Add(Excel(35, 3));
        var target = Project(4);
        var res = PortableImporter.Import(Export(src), target, Ten, null, null, new ImportOptions(), layerScope: false);

        Assert.False(res.Report.HasErrors);
        Assert.Equal(new[] { 3 }, target.KeyOverrides.Single().Layers);
    }

    [Fact]
    public void AppendImport_TwiceIsIdempotent_AndDifferentLayersAreDifferentOverrides()
    {
        var src = Project(4);
        src.KeyOverrides.Add(Excel(35, 3));
        var kb = Export(src);
        var target = Project(4);
        var o = new ImportOptions { Mode = ImportMode.Append };

        PortableImporter.Import(kb, target, Ten, null, null, o, false);
        PortableImporter.Import(kb, target, Ten, null, null, o, false);
        Assert.Single(target.KeyOverrides);

        var other = Project(4);
        other.KeyOverrides.Add(Excel(7, 2)); // 同じキー・置き換え先で、効くレイヤーだけが違う
        PortableImporter.Import(Export(other), target, Ten, null, null, o, false);
        Assert.Equal(2, target.KeyOverrides.Count);
        Assert.Equal(new[] { 2 }, target.KeyOverrides.Single(k => k.Layers!.Contains(2)).Layers);
    }

    [Fact]
    public void Import_DropsAnOverrideWhoseLayersAllMissing_InsteadOfMakingItGlobal()
    {
        var src = Project(4);
        src.KeyOverrides.Add(Excel(35, 3));
        var target = Project(1); // レイヤー3が無い
        var res = PortableImporter.Import(Export(src), target, Ten, null, null, new ImportOptions { Layers = false }, layerScope: false);

        Assert.Empty(target.KeyOverrides);
        Assert.Contains(res.Report.Lines, l => l.Level == ReportLevel.Warning && l.Text.Contains("読み込みません"));
    }

    [Fact]
    public void Import_RemovesOnlyTheMissingLayers()
    {
        var src = Project(6);
        src.KeyOverrides.Add(Excel(35, 2, 5));
        var target = Project(3); // レイヤー5が無い
        var res = PortableImporter.Import(Export(src), target, Ten, null, null, new ImportOptions { Layers = false }, layerScope: false);

        Assert.Equal(new[] { 2 }, target.KeyOverrides.Single().Layers);
        Assert.Contains(res.Report.Lines, l => l.Level == ReportLevel.Warning && l.Text.Contains("外しました"));
    }

    [Fact]
    public void AppendImport_RenumbersTheMacroInTheReplacement()
    {
        var src = Project(1);
        src.Macros.Add(new MacroDef { Id = 3, Keys = { "KC_A" } });
        src.KeyOverrides.Add(new KeyOverrideDef { Id = 0, Mods = "MOD_LCTL", Key = "KC_K", Replacement = "MACRO(3)" });
        var target = Project(1);
        target.Macros.Add(new MacroDef { Id = 3, Keys = { "KC_Z" } }); // 同じ番号の別のマクロが、すでにある

        PortableImporter.Import(Export(src), target, Ten, null, null, new ImportOptions { Mode = ImportMode.Append }, false);

        var imported = target.Macros.Single(m => m.Keys.SequenceEqual(new[] { "KC_A" }));
        Assert.NotEqual(3, imported.Id); // 付け直された
        Assert.Equal($"MACRO({imported.Id})", target.KeyOverrides.Single().Replacement);
    }
}
