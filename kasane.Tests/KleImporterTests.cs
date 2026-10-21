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

    [Fact]
    public void RotatedKeys_CannotBeImported()
    {
        var r = KleImporter.Analyze("[[{\"r\":15,\"rx\":1,\"ry\":1},\"A\"]]");
        Assert.True(r.HasRotation);
        Assert.False(r.CanSave);
        Assert.Throws<FormatException>(() => KleImporter.Apply("[[{\"r\":15},\"A\"]]", new Dictionary<int, string> { [0] = "KC_A" }));
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
