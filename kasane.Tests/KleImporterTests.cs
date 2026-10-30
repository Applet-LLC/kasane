using System.Reflection;
using System.Text.Json.Nodes;
using Kasane.Keycodes;
using Kasane.Layout;

namespace Kasane.Tests;

public class KleImporterTests
{
    private static string BundledPath(string name) => Path.Combine(AppContext.BaseDirectory, "Layouts", $"kle-{name}.json");

    // 同梱のレイアウトから、凡例インデックス 4（キーコード）を消した「素のファイル」を作る。
    private static string Strip(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        foreach (var row in root.OfType<JsonArray>())
            for (int i = 0; i < row.Count; i++)
                if (row[i] is JsonValue v && v.TryGetValue<string>(out var s))
                {
                    var parts = s.Split('\n');
                    if (parts.Length > 4) row[i] = string.Join("\n", parts.Take(4)).TrimEnd('\n');
                }
        return root.ToJsonString();
    }

    [Theory]
    [InlineData("us104")]
    [InlineData("jp109")]
    [InlineData("10key")]
    public void BundledLayouts_WithKeycodesRemoved_AreRestoredFromTheLabels(string name)
    {
        var original = KeyboardLayout.Load(BundledPath(name));
        var result = KleImporter.Analyze(Strip(BundledPath(name)));
        Assert.Equal(original.Slots.Count, result.Keys.Count);
        var mismatches = result.Keys
            .Where(k => k.Keycode != original.Slots[k.Slot].DefaultKeycode)
            .Select(k => $"#{k.Slot + 1} {k.Label}: {k.Keycode ?? "(なし)"} (期待 {original.Slots[k.Slot].DefaultKeycode})")
            .ToList();
        Assert.True(mismatches.Count == 0, string.Join(" | ", mismatches));
        Assert.True(result.CanSave);
    }

    [Fact]
    public void JapaneseLayout_IsDetected_AndUsEnglishIsNot()
    {
        Assert.True(KleImporter.Analyze(Strip(BundledPath("jp109"))).IsJis);
        Assert.False(KleImporter.Analyze(Strip(BundledPath("us104"))).IsJis);
    }

    [Fact]
    public void Apply_WritesIndex4_AndRoundTripsThroughTheLayoutLoader()
    {
        string stripped = Strip(BundledPath("us104"));
        var result = KleImporter.Analyze(stripped);
        var map = result.Keys.Where(k => k.Keycode != null).ToDictionary(k => k.Slot, k => k.Keycode!);
        string written = KleImporter.Apply(stripped, map);

        string tmp = Path.Combine(Path.GetTempPath(), $"kle-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tmp, written);
            var layout = KeyboardLayout.Load(tmp);
            var original = KeyboardLayout.Load(BundledPath("us104"));
            Assert.Equal(original.Slots.Count, layout.Slots.Count);
            for (int i = 0; i < layout.Slots.Count; i++)
            {
                Assert.Equal(original.Slots[i].DefaultKeycode, layout.Slots[i].DefaultKeycode);
                Assert.Equal(original.Slots[i].X, layout.Slots[i].X);
            }
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Apply_KeepsOtherContent_AndRejectsUnknownKeycodes()
    {
        const string json = "[[{\"c\":\"#ff0000\",\"a\":7},\"Q\",\"W\"]]";
        string written = KleImporter.Apply(json, new Dictionary<int, string> { [0] = "KC_Q" });
        Assert.Contains("\"c\": \"#ff0000\"", written);
        Assert.Contains("\"a\": 7", written);
        Assert.Contains("Q\\n\\n\\n\\nKC_Q", written);
        Assert.Contains("\"W\"", written); // 触っていないキー
        Assert.Throws<FormatException>(() => KleImporter.Apply(json, new Dictionary<int, string> { [0] = "KC_NOPE" }));
    }

    [Fact]
    public void CenteredAlignment_AndShiftedLegends_AreRead()
    {
        var r = KleImporter.Analyze("[[{\"a\":7},\"Q\",\"1\",\"Esc\"]]");
        Assert.Equal(new[] { "KC_Q", "KC_1", "KC_ESC" }, r.Keys.Select(k => k.Keycode).ToArray());
        var us = KleImporter.Analyze("[[\"~\\n`\",\"!\\n1\",\"_\\n-\",\"|\\n\\\\\"]]");
        Assert.Equal(new[] { "KC_GRAVE", "KC_1", "KC_MINUS", "KC_BSLASH" }, us.Keys.Select(k => k.Keycode).ToArray());
    }

    [Fact]
    public void ModifierSide_IsDecidedByPosition()
    {
        var r = KleImporter.Analyze("[[\"Shift\",{\"x\":10},\"Shift\"],[\"Ctrl\",{\"x\":10},\"Ctrl\"]]");
        Assert.Equal(new[] { "KC_LSHIFT", "KC_RSHIFT", "KC_LCTRL", "KC_RCTRL" }, r.Keys.Select(k => k.Keycode).ToArray());
        Assert.All(r.Keys, k => Assert.Equal(KleKeyStatus.Inferred, k.Status));
    }

    [Fact]
    public void WideUnlabeledKey_IsTheSpaceBar_AndOthersStayUnassigned()
    {
        var r = KleImporter.Analyze("[[{\"w\":6.25},\"\",\"Fn\"]]");
        Assert.Equal("KC_SPACE", r.Keys[0].Keycode);
        Assert.Null(r.Keys[1].Keycode);
        Assert.Equal(KleKeyStatus.Unassigned, r.Keys[1].Status);
        Assert.Contains(r.Warnings, w => w.Contains("決まらなかった"));
    }

    private static KeyboardLayout LoadJson(string json)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"kle-test-{Guid.NewGuid():N}.json");
        try { File.WriteAllText(tmp, json); return KeyboardLayout.Load(tmp); }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void RotatedKeys_CanBeImported_AndAreReported()
    {
        var r = KleImporter.Analyze("[[{\"r\":15,\"rx\":1,\"ry\":1},\"A\"]]");
        Assert.True(r.HasRotation);
        Assert.True(r.CanSave);
        Assert.Contains(r.Warnings, w => w.Contains("回転したキー") || w.Contains("rotated"));
        Assert.Equal(15, r.Keys[0].Angle);
        Assert.Equal(1, r.Keys[0].OriginX);
        Assert.Equal(1, r.Keys[0].OriginY);
        string written = KleImporter.Apply("[[{\"r\":15,\"rx\":1,\"ry\":1},\"A\"]]", new Dictionary<int, string> { [0] = "KC_A" });
        Assert.Contains("\"r\": 15", written); // 回転の指定を壊さない
        Assert.Contains("\"rx\": 1", written);
        Assert.Contains("A\\n\\n\\n\\nKC_A", written);
        Assert.Equal("KC_A", LoadJson(written).Slots[0].DefaultKeycode);
    }

    [Fact]
    public void Rotation_FollowsTheKleSite_CursorMovesToTheOrigin_AndCarriesOver()
    {
        // rx/ry でカーソルが (rx, ry) に移る。次の行の頭は x = rx。r は次の指定まで続く。r:0 で戻っても rx は残る。
        var l = LoadJson("[[\"Z\"],[{\"r\":10,\"rx\":3,\"ry\":2},\"A\",\"B\"],[\"C\"],[{\"r\":0},\"D\"]]");
        Assert.Equal(5, l.Slots.Count);
        var (a, b, c, d) = (l.Slots[1], l.Slots[2], l.Slots[3], l.Slots[4]);
        Assert.Equal((3.0, 2.0, 10.0), (a.X, a.Y, a.Angle));
        Assert.Equal((4.0, 2.0), (b.X, b.Y));
        Assert.Equal((3.0, 3.0, 10.0), (c.X, c.Y, c.Angle));
        Assert.Equal((3.0, 4.0, 0.0), (d.X, d.Y, d.Angle));
        Assert.Equal((3.0, 2.0), (a.OriginX, a.OriginY));
        Assert.Equal(0, l.Slots[0].Angle);
    }

    [Fact]
    public void RotatedKey_CornersAndLayoutSize_MatchHandCalculation()
    {
        // (1,1) の 1u キーを、左上 (1,1) を中心に 90 度(時計回り)回す → 四隅は (1,1)(1,2)(0,2)(0,1)。外接矩形は x 0〜1、y 1〜2。
        var l = LoadJson("[[{\"r\":90,\"rx\":1,\"ry\":1},\"A\"]]");
        var bb = l.Slots[0].Bounds;
        Assert.Equal(0, bb.MinX, 9);
        Assert.Equal(1, bb.MinY, 9);
        Assert.Equal(1, bb.MaxX, 9);
        Assert.Equal(2, bb.MaxY, 9);
        Assert.Equal(1, l.Width, 9);
        Assert.Equal(2, l.Height, 9);
        var c = KeyGeometry.Corners(1, 1, 1, 1, 90, 1, 1);
        double[][] want = { new[] { 1.0, 1 }, new[] { 1.0, 2 }, new[] { 0.0, 2 }, new[] { 0.0, 1 } };
        for (int i = 0; i < 4; i++) { Assert.Equal(want[i][0], c[i].X, 9); Assert.Equal(want[i][1], c[i].Y, 9); }
    }

    [Fact]
    public void NegativeCoordinates_AreShiftedToTheOrigin_WithoutChangingSlotNumbers()
    {
        // 原点まわりに 45 度回すと、左の角が x = -0.7071 になる → 全体を右へずらす。
        var l = LoadJson("[[{\"r\":45,\"rx\":0,\"ry\":0},\"A\",\"B\"]]");
        double s = Math.Sqrt(0.5);
        Assert.Equal(2, l.Slots.Count);
        Assert.Equal(0, l.Slots.Min(k => k.Bounds.MinX), 9);
        Assert.Equal(0, l.Slots.Min(k => k.Bounds.MinY), 9);
        Assert.Equal(s, l.Slots[0].X, 9);
        Assert.Equal(s, l.Slots[0].OriginX, 9);
        Assert.Equal(new[] { 0, 1 }, l.Slots.Select(k => k.Index).ToArray());
    }

    [Fact]
    public void LoadAndAnalyze_NumberTheSlotsTheSame_ForARotatedLayout()
    {
        const string json = "[[\"Esc\",{\"x\":1},\"F1\"],[{\"r\":-20,\"rx\":6,\"ry\":2,\"x\":0.5},\"Y\",{\"d\":true},\"deco\",\"U\"],[\"H\",{\"w\":1.5},\"J\"],[{\"r\":0,\"rx\":0,\"ry\":5},\"Space\"]]";
        var layout = LoadJson(json);
        var result = KleImporter.Analyze(json);
        Assert.Equal(layout.Slots.Count, result.Keys.Count);
        for (int i = 0; i < layout.Slots.Count; i++)
        {
            Assert.Equal(layout.Slots[i].Index, result.Keys[i].Slot);
            Assert.Equal(layout.Slots[i].Angle, result.Keys[i].Angle);
        }
    }

    [Theory]
    [InlineData("us104")]
    [InlineData("jp109")]
    [InlineData("10key")]
    public void BundledLayouts_AreNotRotated_AndKeepTheirSize(string name)
    {
        var l = KeyboardLayout.Load(BundledPath(name));
        Assert.All(l.Slots, k => { Assert.Equal(0, k.Angle); Assert.True(k.X >= 0 && k.Y >= 0); });
        Assert.Equal(l.Slots.Max(k => Math.Max(k.X + k.W, k.X + (k.X2 ?? 0) + (k.W2 ?? 0))), l.Width);
        Assert.Equal(l.Slots.Max(k => Math.Max(k.Y + k.H, k.Y + (k.Y2 ?? 0) + (k.H2 ?? 0))), l.Height);
    }

    [Fact]
    public void SplitKeyboard_LeftRightModifiers_UseTheRotatedPosition()
    {
        // 左手側 (x 0〜) と右手側 (x 12〜) に Shift。右手側は回転しているが、回転後の位置で判定する。
        var r = KleImporter.Analyze("[[\"Shift\"],[{\"r\":10,\"rx\":12,\"ry\":0},\"Shift\"]]");
        Assert.Equal(new[] { "KC_LSHIFT", "KC_RSHIFT" }, r.Keys.Select(k => k.Keycode).ToArray());
    }

    [Fact]
    public void Decals_AreNotSlots_LikeTheLayoutLoader()
    {
        var r = KleImporter.Analyze("[[\"A\",{\"d\":true},\"decal\",\"B\"]]");
        Assert.Equal(2, r.Keys.Count);
        Assert.Equal(new[] { "KC_A", "KC_B" }, r.Keys.Select(k => k.Keycode).ToArray());
    }

    [Fact]
    public void ExistingKeycodes_AreKept()
    {
        var r = KleImporter.Analyze("[[\"Foo\\n\\n\\n\\nKC_F13\"]]");
        Assert.Equal("KC_F13", r.Keys[0].Keycode);
        Assert.Equal(KleKeyStatus.Confirmed, r.Keys[0].Status);
    }

    [Fact]
    public void NotKleJson_IsRejected()
    {
        Assert.Throws<FormatException>(() => KleImporter.Analyze("{\"a\":1}"));
        Assert.Throws<FormatException>(() => KleImporter.Analyze("not json"));
    }

    [Theory]
    [InlineData("my-layout", true)]
    [InlineData("US104", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData("-bad", false)]
    [InlineData("has space", false)]
    [InlineData("日本語", false)]
    [InlineData("../evil", false)]
    public void NameValidation(string name, bool ok) => Assert.Equal(ok, KleImporter.IsValidName(name));

    // 手元の素の KLE ファイルで確かめる（任意）。環境変数 KASANE_KLE_RAW_DIR にフォルダを指すと、その中の *.json を、すべて読めること。
    // さらに、ANSI_104.json・OADG_109A.json（keyboard_layouts のもの）があれば、同梱の us104・jp109 と同じキーコードになること。
    [Fact]
    public void RawFilesInTheFolderOfTheEnvironmentVariable_AreAnalyzed()
    {
        string? dir = Environment.GetEnvironmentVariable("KASANE_KLE_RAW_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        var problems = new List<string>();
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var r = KleImporter.Analyze(File.ReadAllText(file));
            string name = Path.GetFileName(file);
            string? bundled = name.Contains("ANSI_104", StringComparison.OrdinalIgnoreCase) ? "us104"
                            : name.Contains("OADG_109A", StringComparison.OrdinalIgnoreCase) ? "jp109" : null;
            if (bundled != null)
            {
                var original = KeyboardLayout.Load(BundledPath(bundled));
                if (original.Slots.Count != r.Keys.Count) { problems.Add($"{name}: キーの数 {r.Keys.Count}(同梱は {original.Slots.Count})"); continue; }
                foreach (var k in r.Keys.Where(k => k.Keycode != original.Slots[k.Slot].DefaultKeycode))
                    problems.Add($"{name} #{k.Slot + 1} {k.Label}: {k.Keycode ?? "(なし)"}(同梱は {original.Slots[k.Slot].DefaultKeycode})");
            }
            else if (r.Keys.Count == 0) problems.Add($"{name}: キーが 0 個");
        }
        Assert.True(problems.Count == 0, string.Join(" | ", problems));
    }

    [Fact]
    public void EveryKeycodeInTheTables_IsAValidName()
    {
        // 推定の表のキーコードが、キーコード表に無い名前にならないこと。
        var values = new List<string>();
        foreach (var f in typeof(KleImporter).GetFields(BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (f.GetValue(null) is Dictionary<string, string> d) values.AddRange(d.Values);
        }
        values.AddRange(new[] { "KC_LSHIFT", "KC_RSHIFT", "KC_LCTRL", "KC_RCTRL", "KC_LALT", "KC_RALT", "KC_LGUI", "KC_RGUI", "KC_SPACE", "KC_KP_ENTER",
            "KC_KP_DOT", "KC_KP_PLUS", "KC_KP_MINUS", "KC_KP_ASTERISK", "KC_KP_SLASH" });
        for (int i = 0; i <= 9; i++) values.Add($"KC_KP_{i}");
        for (int i = 1; i <= 24; i++) values.Add($"KC_F{i}");
        Assert.NotEmpty(values);
        var bad = values.Distinct().Where(v => !KeycodeTable.TryResolveBaseKey(v, out _)).ToList();
        Assert.True(bad.Count == 0, "キーコード表に無い名前: " + string.Join(", ", bad));
    }
}
