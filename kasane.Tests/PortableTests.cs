using Kasane.Layout;
using Kasane.Model;
using Kasane.Model.Portable;

namespace Kasane.Tests;

public class PortableTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kasane-portable-" + Guid.NewGuid().ToString("N"));

    public PortableTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static KeyboardLayout Ten => LayoutFixture.TenKey;

    /// <summary>テンキーと順番が違い、KC_KP_9 / KC_KP_8 / KC_KP_7 と、テンキーに無い KC_A だけのレイアウト。</summary>
    private KeyboardLayout Small()
    {
        string path = Path.Combine(_dir, "kle-small.json");
        File.WriteAllText(path, "[[\"9\\n\\n\\n\\nKC_KP_9\",\"8\\n\\n\\n\\nKC_KP_8\",\"7\\n\\n\\n\\nKC_KP_7\",\"A\\n\\n\\n\\nKC_A\"]]");
        return KeyboardLayout.Load(path);
    }

    private static int SlotOf(KeyboardLayout l, string keycode) => l.Slots.First(s => s.DefaultKeycode == keycode).Index;

    private static KeymapProject Sample()
    {
        var p = ProjectService.CreateDefault(0xCDEC0001, "HID\\VID_1", Ten, true);
        p.Layers.Add(ProjectService.DefaultLayer(Ten, 1));
        p.Layers[0][SlotOf(Ten, "KC_KP_7")] = "MO(1)";
        p.Layers[0][SlotOf(Ten, "KC_KP_8")] = "MACRO(3)";
        p.Layers[1][SlotOf(Ten, "KC_KP_4")] = "KC_F13";
        p.Layers[1][SlotOf(Ten, "KC_KP_5")] = "TAPDANCE(2)";
        p.Layers[1][SlotOf(Ten, "KC_KP_6")] = "HLP(7)";
        p.Macros.Add(new MacroDef { Id = 3, Keys = { "KC_A", "LCTL(KC_C)" } });
        p.TapDances.Add(new TapDanceDef { Id = 2, SingleTap = "KC_B", DoubleTap = "MACRO(3)" });
        p.Combos.Add(new ComboDef { Id = 0, Slot1 = SlotOf(Ten, "KC_KP_1"), Slot2 = SlotOf(Ten, "KC_KP_2"), Output = "KC_ESC" });
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 0, Mods = "MOD_LSFT", Key = "KC_BSPC", Replacement = "KC_DEL" });
        p.TapTermMs = 180;
        p.EnabledFeatures = 3;
        return p;
    }

    private static HelperConfig SampleHelper() => new()
    {
        Actions = { new HelperAction { Id = 7, Name = "メモ帳", Action = new HelperActionBody { Type = HelperActionBody.Launch, Path = "notepad.exe" } } },
    };

    // ---- 形式 ----

    [Fact]
    public void ExportKeyboard_Layer0IncludesEveryKeyButOtherLayersOmitTransparent()
    {
        var kb = PortableExporter.ExportKeyboard(Sample(), Ten, null, new ExportOptions(), new PortableReport());

        Assert.Equal(Ten.Slots.Count, kb.Layers![0].Keys.Count);
        Assert.Equal("MO(1)", kb.Layers[0].Keys["SC_0047"]);
        Assert.Equal(3, kb.Layers[1].Keys.Count);
        Assert.Equal("KC_F13", kb.Layers[1].Keys["SC_004B"]);
        Assert.Equal("0xCDEC0001", kb.UniqId);
        Assert.Null(kb.MeasuredScanCodes);
    }

    [Fact]
    public void FileRoundTrip_KeepsJapaneseAndRejectsOtherFormats()
    {
        var file = PortableExporter.NewFile(PortableScope.Keyboard);
        file.Keyboards.Add(PortableExporter.ExportKeyboard(Sample(), Ten, SampleHelper(), new ExportOptions(), new PortableReport()));
        string path = Path.Combine(_dir, "a.kasane.json");
        PortableIO.Write(path, file);

        string text = File.ReadAllText(path);
        Assert.Contains("メモ帳", text); // エスケープされていない
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(0, File.ReadAllBytes(path)[0] == 0xEF ? 1 : 0); // BOM なし

        Assert.True(PortableIO.TryRead(path, out var back, out var err), err);
        Assert.Equal(PortableScope.Keyboard, back!.ParsedScope);
        Assert.Equal("メモ帳", back.Keyboards[0].Helper!.Actions[0].Name);

        Assert.False(PortableIO.TryParse("{\"Format\":\"other\"}", out _, out var e1));
        Assert.Contains("kasane", e1);
        Assert.False(PortableIO.TryParse("{\"Format\":\"kasane-export\",\"Version\":99,\"Scope\":\"All\",\"Keyboards\":[{}]}", out _, out var e2));
        Assert.Contains("新しい版", e2);
        Assert.False(PortableIO.TryParse("{ broken", out _, out _));
        Assert.False(PortableIO.TryParse("{\"Format\":\"kasane-export\",\"Version\":1,\"Scope\":\"Everything\",\"Keyboards\":[{}]}", out _, out _));
    }

    // ---- キーボード単位 ----

    [Fact]
    public void ReplaceImport_IntoAFreshProject_ReproducesTheSettings()
    {
        var src = Sample();
        var kb = PortableExporter.ExportKeyboard(src, Ten, SampleHelper(), new ExportOptions(), new PortableReport());

        var target = ProjectService.CreateDefault(0xCDEC0002, "HID\\VID_2", Ten, true);
        var res = PortableImporter.Import(kb, target, Ten, null, null, new ImportOptions(), layerScope: false);

        Assert.False(res.Report.HasErrors);
        Assert.Equal(src.Layers, target.Layers);
        Assert.Equal(new[] { 3 }, target.Macros.Select(m => m.Id));
        Assert.Equal(new[] { "KC_A", "LCTL(KC_C)" }, target.Macros[0].Keys);
        Assert.Equal("MACRO(3)", target.TapDances[0].DoubleTap);
        Assert.Equal(src.Combos[0].Slot1, target.Combos[0].Slot1);
        Assert.Equal("KC_DEL", target.KeyOverrides[0].Replacement);
        Assert.Equal(180u, target.TapTermMs);
        Assert.Equal(3u, target.EnabledFeatures);
        Assert.Equal(7, res.Helper!.Actions[0].Id);
    }

    [Fact]
    public void Import_ToAnotherLayout_MatchesByScanCodeAndReportsMissingKeys()
    {
        var src = Sample();
        src.Layers[0][SlotOf(Ten, "KC_KP_9")] = "KC_Z";
        var kb = PortableExporter.ExportKeyboard(src, Ten, null, new ExportOptions(), new PortableReport());

        var small = Small();
        var target = ProjectService.CreateDefault(0xCDEC0003, null, small, true);
        var res = PortableImporter.Import(kb, target, small, null, null, new ImportOptions(), layerScope: false);

        Assert.Equal("KC_Z", target.Layers[0][SlotOf(small, "KC_KP_9")]);
        Assert.Equal("MACRO(3)", target.Layers[0][SlotOf(small, "KC_KP_8")]);
        Assert.Equal("MO(1)", target.Layers[0][SlotOf(small, "KC_KP_7")]);
        Assert.Equal("KC_A", target.Layers[0][SlotOf(small, "KC_A")]); // ファイルに無いキーは、そのキー本来の動作のまま
        Assert.Contains(res.Report.Lines, l => l.Level == ReportLevel.Warning && l.Text.Contains("レイアウトに無いキー") && l.Text.Contains("SC_0045"));
        Assert.DoesNotContain(res.Report.Lines, l => l.Level == ReportLevel.Error);
    }

    [Fact]
    public void Import_FromAnotherLayoutIntoTen_LeavesUnlistedKeysAtTheirOwnAction()
    {
        var small = Small();
        var src = ProjectService.CreateDefault(1, null, small, true);
        src.Layers[0][SlotOf(small, "KC_KP_9")] = "KC_X";
        var kb = PortableExporter.ExportKeyboard(src, small, null, new ExportOptions(), new PortableReport());

        var target = ProjectService.CreateDefault(2, null, Ten, true);
        PortableImporter.Import(kb, target, Ten, null, null, new ImportOptions(), layerScope: false);

        Assert.Equal("KC_X", target.Layers[0][SlotOf(Ten, "KC_KP_9")]);
        Assert.Equal("KC_KP_5", target.Layers[0][SlotOf(Ten, "KC_KP_5")]);
    }

    [Fact]
    public void AppendImport_OverlaysLayersAndRenumbersCollidingIds()
    {
        var kb = PortableExporter.ExportKeyboard(Sample(), Ten, SampleHelper(), new ExportOptions(), new PortableReport());

        var target = ProjectService.CreateDefault(5, null, Ten, true);
        target.Macros.Add(new MacroDef { Id = 3, Keys = { "KC_Q" } });        // 同じ番号で別の中身
        target.TapDances.Add(new TapDanceDef { Id = 2, SingleTap = "KC_W" });
        var existingHelper = new HelperConfig { Actions = { new HelperAction { Id = 7, Action = new HelperActionBody { Type = HelperActionBody.Open, Path = "https://example.com" } } } };

        var res = PortableImporter.Import(kb, target, Ten, existingHelper, null,
            new ImportOptions { Mode = ImportMode.Append, Timing = false }, layerScope: false);

        var macro = target.Macros.Single(m => m.Keys.Contains("LCTL(KC_C)"));
        Assert.NotEqual(3, macro.Id);
        Assert.Equal(2, target.Macros.Count);
        Assert.Equal($"MACRO({macro.Id})", target.Layers[0][SlotOf(Ten, "KC_KP_8")]);
        var td = target.TapDances.Single(t => t.SingleTap == "KC_B");
        Assert.NotEqual(2, td.Id);
        Assert.Equal($"MACRO({macro.Id})", td.DoubleTap);
        Assert.Equal($"TAPDANCE({td.Id})", target.Layers[1][SlotOf(Ten, "KC_KP_5")]);

        int newHelperId = res.Helper!.Actions.Single(a => a.Action.Path == "notepad.exe").Id;
        Assert.NotEqual(7, newHelperId);
        Assert.Equal($"HLP({newHelperId})", target.Layers[1][SlotOf(Ten, "KC_KP_6")]);
        Assert.Equal(2, res.Helper.Actions.Count);
        Assert.Equal(0u, target.TapTermMs); // 追加では、タイミングを触らない
    }

    [Fact]
    public void AppendImport_TwiceIsIdempotent()
    {
        var kb = PortableExporter.ExportKeyboard(Sample(), Ten, SampleHelper(), new ExportOptions(), new PortableReport());
        var target = ProjectService.CreateDefault(5, null, Ten, true);
        var o = new ImportOptions { Mode = ImportMode.Append };

        var first = PortableImporter.Import(kb, target, Ten, null, null, o, false);
        var second = PortableImporter.Import(kb, target, Ten, first.Helper, null, o, false);

        Assert.Single(target.Macros);
        Assert.Single(target.TapDances);
        Assert.Single(target.Combos);
        Assert.Single(target.KeyOverrides);
        Assert.Single(second.Helper!.Actions);
    }

    [Fact]
    public void Import_ItemsNotChosenAreLeftAlone()
    {
        var kb = PortableExporter.ExportKeyboard(Sample(), Ten, SampleHelper(), new ExportOptions(), new PortableReport());
        var target = ProjectService.CreateDefault(5, null, Ten, true);
        target.Macros.Add(new MacroDef { Id = 9, Keys = { "KC_Q" } });

        var res = PortableImporter.Import(kb, target, Ten, null, null,
            new ImportOptions { Macros = false, Combos = false, Helper = false, Timing = false }, false);

        Assert.Equal(new[] { 9 }, target.Macros.Select(m => m.Id));
        Assert.Empty(target.Combos);
        Assert.Null(res.Helper);
        Assert.Equal(0u, target.TapTermMs);
        Assert.Equal(2, target.Layers.Count);
    }

    [Fact]
    public void Import_CombosAreMatchedByScanCode()
    {
        var kb = new PortableKeyboard { Combos = new List<PortableCombo>() };
        var target = ProjectService.CreateDefault(5, null, Ten, true);
        var keys = Ten.Slots.Select(s => PortableExporter.ScName(s.DefaultScanCode!.Value)).ToList();
        for (int i = 0; i + 1 < keys.Count; i += 2) kb.Combos.Add(new PortableCombo { Id = i / 2, Key1 = keys[i], Key2 = keys[i + 1], Output = "KC_A" });

        var res = PortableImporter.Import(kb, target, Ten, null, null, new ImportOptions(), false);

        Assert.Equal(8, target.Combos.Count); // 17キー → 8組
        Assert.DoesNotContain(res.Report.Lines, l => l.Level == ReportLevel.Error);
    }

    // ---- レイヤー単位 ----

    [Fact]
    public void ExportLayer_CarriesReferencedMacrosTapDancesAndHelperActions()
    {
        var report = new PortableReport();
        var kb = PortableExporter.ExportLayer(Sample(), Ten, 1, SampleHelper(), null, report);

        Assert.Single(kb.Layers!);
        Assert.Equal(1, kb.Layers![0].Index);
        Assert.Equal(new[] { 2 }, kb.TapDances!.Select(t => t.Id));
        Assert.Equal(new[] { 3 }, kb.Macros!.Select(m => m.Id)); // タップダンスから参照されるマクロ
        Assert.Equal(new[] { 7 }, kb.Helper!.Actions.Select(a => a.Id));
        Assert.Null(kb.Combos);
        Assert.Empty(report.Lines);
    }

    [Fact]
    public void ImportLayer_AsANewLayerOrOverwrite()
    {
        var kb = PortableExporter.ExportLayer(Sample(), Ten, 1, SampleHelper(), null, new PortableReport());

        var target = ProjectService.CreateDefault(5, null, Ten, true);
        var added = PortableImporter.Import(kb, target, Ten, null, null, new ImportOptions { TargetLayer = null }, layerScope: true);
        Assert.Equal(2, target.Layers.Count);
        Assert.Equal("KC_F13", target.Layers[1][SlotOf(Ten, "KC_KP_4")]);
        Assert.Equal("TAPDANCE(2)", target.Layers[1][SlotOf(Ten, "KC_KP_5")]);
        Assert.Equal("HLP(7)", target.Layers[1][SlotOf(Ten, "KC_KP_6")]);
        Assert.Equal("KC_KP_7", target.Layers[0][SlotOf(Ten, "KC_KP_7")]);

        target.Layers[0][SlotOf(Ten, "KC_KP_1")] = "KC_Y";
        var over = PortableImporter.Import(kb, target, Ten, added.Helper, null, new ImportOptions { TargetLayer = 0 }, layerScope: true);
        Assert.Equal("KC_F13", target.Layers[0][SlotOf(Ten, "KC_KP_4")]);
        Assert.Equal("KC_KP_1", target.Layers[0][SlotOf(Ten, "KC_KP_1")]); // 上書き: 元の内容は消える（レイヤー0は本来の動作に戻る）
        Assert.Single(over.Helper!.Actions);
    }

    [Fact]
    public void ImportLayer_RefusesAnAddWhenThereAreSixteenLayersAndWarnsAboutLayerKeys()
    {
        var src = Sample();
        src.Layers[1][SlotOf(Ten, "KC_KP_4")] = "TO(0)";
        var kb = PortableExporter.ExportLayer(src, Ten, 1, SampleHelper(), null, new PortableReport());
        var target = ProjectService.CreateDefault(5, null, Ten, true);
        while (target.Layers.Count < 16) target.Layers.Add(ProjectService.DefaultLayer(Ten, target.Layers.Count));

        var res = PortableImporter.Import(kb, target, Ten, null, null, new ImportOptions(), true);
        Assert.True(res.Report.HasErrors);
        Assert.Equal(16, target.Layers.Count);

        var ok = PortableImporter.Import(kb, target, Ten, null, null, new ImportOptions { TargetLayer = 3 }, true);
        Assert.False(ok.Report.HasErrors);
        Assert.Contains(ok.Report.Lines, l => l.Level == ReportLevel.Warning && l.Text.Contains("レイヤーを切り替えるキー"));
    }

    // ---- 実測値 ----

    [Fact]
    public void MeasuredScanCodes_AreOnlyExportedOnRequestAndOnlyImportedForTheSameLayout()
    {
        var src = Sample();
        src.MeasuredScanCodes[0] = 0xE05B;
        Assert.Null(PortableExporter.ExportKeyboard(src, Ten, null, new ExportOptions(), new PortableReport()).MeasuredScanCodes);
        var kb = PortableExporter.ExportKeyboard(src, Ten, null, new ExportOptions { IncludeMeasured = true }, new PortableReport());
        Assert.Equal("SC_E05B", kb.MeasuredScanCodes!["0"]);

        var same = ProjectService.CreateDefault(1, null, Ten, true);
        PortableImporter.Import(kb, same, Ten, null, null, new ImportOptions { MeasuredScanCodes = true }, false);
        Assert.Equal((ushort)0xE05B, same.MeasuredScanCodes[0]);

        var small = Small();
        var other = ProjectService.CreateDefault(2, null, small, true);
        var res = PortableImporter.Import(kb, other, small, null, null, new ImportOptions { MeasuredScanCodes = true }, false);
        Assert.Empty(other.MeasuredScanCodes);
        Assert.Contains(res.Report.Lines, l => l.Text.Contains("同じレイアウトのときだけ"));
    }

    // ---- 番号の付け替え ----

    [Theory]
    [InlineData("MACRO(3)", "MACRO(10)")]
    [InlineData("HLP(0x7)", "HLP(70)")]
    [InlineData("TAPDANCE(2)", "TAPDANCE(5)")]
    [InlineData("KC_A", "KC_A")]
    [InlineData("MT(MOD_LCTL,KC_A)", "MT(MOD_LCTL,KC_A)")]
    public void TokenIds_Rewrite(string token, string expected)
    {
        var m = new Dictionary<int, int> { [3] = 10 };
        var t = new Dictionary<int, int> { [2] = 5 };
        var h = new Dictionary<int, int> { [7] = 70 };
        Assert.Equal(expected, TokenIds.Rewrite(token, m, t, h));
    }

    // ---- 全体一括（保存先をまとめて） ----

    private void Store(uint uniq, KeymapProject p, HelperConfig? helper = null)
    {
        p.UniqId = uniq;
        p.LayoutPath = Ten.FilePath; // 同梱のレイアウトに頼らない
        ProjectService.Save(p, _dir);
        helper?.Save(HelperConfig.DevicePath(uniq, _dir));
    }

    [Fact]
    public void ExportAll_ThenImportAllIntoCleanStore_RestoresEverythingAndBacksUp()
    {
        Store(0xCDEC0001, Sample(), SampleHelper());
        var second = ProjectService.CreateDefault(0xCDEC0002, "HID\\VID_2", Ten, true);
        second.Layers[0][0] = "KC_F14";
        Store(0xCDEC0002, second);
        new TrayConfig { ImeLink = false, Helper = SampleHelper() }.Save(PortableService.TrayPath(_dir));

        var report = new PortableReport();
        var file = PortableService.ExportAll(_dir, new ExportOptions(), report);
        Assert.Equal(2, file.Keyboards.Count);
        Assert.False(file.Tray!.ImeLink);

        // 別の「パソコン」: 空の保存先に、デバイス 1 台だけ（UniqId が違う）。
        string other = Path.Combine(_dir, "other");
        Directory.CreateDirectory(other);
        var maps = PortableService.AutoMap(file, new[] { new PortableService.TargetDevice(0xCDEC0002, "HID\\VID_2", "kbd") });
        Assert.Null(maps[0].TargetUniqId);
        Assert.Equal(0xCDEC0002u, maps[1].TargetUniqId);

        var r2 = new PortableReport();
        var changed = PortableService.ImportAll(file, maps, new ImportOptions(), other, r2, _ => Ten);

        Assert.Equal(new[] { 0xCDEC0002u }, changed);
        Assert.False(r2.HasErrors, r2.ToString());
        var loaded = ProjectService.TryLoad(0xCDEC0002, other)!;
        Assert.Equal("KC_F14", loaded.Layers[0][0]);
        Assert.True(Directory.GetFiles(Path.Combine(other, "backup"), "*.kasane.json").Length == 1);
        Assert.False(TrayConfig.Load(PortableService.TrayPath(other)).ImeLink);
    }

    [Fact]
    public void ImportAll_AutoMapMatchesUniqIdFirstThenHardwareId()
    {
        var file = PortableExporter.NewFile(PortableScope.All);
        file.Keyboards.Add(new PortableKeyboard { UniqId = "0x00000001", HardwareId = "HID\\A" });
        file.Keyboards.Add(new PortableKeyboard { UniqId = "0x00000002", HardwareId = "HID\\B" });
        file.Keyboards.Add(new PortableKeyboard { UniqId = "0x00000003", HardwareId = "HID\\B" });
        var devices = new[]
        {
            new PortableService.TargetDevice(2, "HID\\B", "b"),
            new PortableService.TargetDevice(9, "HID\\A", "a"),
            new PortableService.TargetDevice(8, "HID\\B", "b2"),
        };

        var maps = PortableService.AutoMap(file, devices);

        Assert.Equal(9u, maps[0].TargetUniqId); // UniqId は無いが HardwareId が合う
        Assert.Equal(2u, maps[1].TargetUniqId); // UniqId が合う
        Assert.Equal(8u, maps[2].TargetUniqId); // 残った同じ HardwareId
    }

    [Fact]
    public void ImportAll_WithABrokenHelperJson_DoesNotOverwriteIt()
    {
        var p = ProjectService.CreateDefault(0xCDEC0001, null, Ten, true);
        Store(0xCDEC0001, p);
        string helperPath = HelperConfig.DevicePath(0xCDEC0001, _dir);
        File.WriteAllText(helperPath, "{ broken");
        var kb = PortableExporter.ExportKeyboard(Sample(), Ten, SampleHelper(), new ExportOptions(), new PortableReport());
        var file = PortableExporter.NewFile(PortableScope.All);
        file.Keyboards.Add(kb);

        var report = new PortableReport();
        PortableService.ImportAll(file, new[] { new PortableService.Mapping(0, 0xCDEC0001) }, new ImportOptions(), _dir, report);

        Assert.Equal("{ broken", File.ReadAllText(helperPath));
        Assert.Contains(report.Lines, l => l.Level == ReportLevel.Warning && l.Text.Contains("helper.json"));
    }
}
