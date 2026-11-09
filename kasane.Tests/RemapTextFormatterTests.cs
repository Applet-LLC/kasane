using Kasane.Interop;
using Kasane.Layout;
using Kasane.Model;
using Xunit;

namespace Kasane.Tests;

/// <summary>割り当ての一覧の文字（GUI 計画 2-2）。</summary>
public class RemapTextFormatterTests
{
    private static readonly KeyboardLayout Layout = LayoutFixture.TenKey;

    private static KeymapProject NewProject(int layers = 1)
    {
        var p = ProjectService.CreateDefault(0x55F70000, null, Layout, bundled: true);
        while (p.Layers.Count < layers) p.Layers.Add(Enumerable.Repeat("KC_TRNS", p.Layers[0].Count).ToList());
        return p;
    }

    private static string[] Lines(string text) => text.Split("\r\n");

    // 揃えのための空白をまとめて、1つの空白にする（中身の確かめ用。揃え方は AlignsArrowsAndColons で見る）。
    private static string Norm(string text) => System.Text.RegularExpressions.Regex.Replace(text, " {2,}", " ");

    [Fact]
    public void DefaultProject_ListsNothingChanged()
    {
        var text = Norm(RemapTextFormatter.Format(NewProject(2), Layout));
        Assert.Contains("## レイヤー0", text);
        Assert.Contains("(刻印のまま)", text);
        Assert.Contains("(すべて ▽)", text);
        Assert.DoesNotContain("key ", text);
        Assert.DoesNotContain("## コンボ", text);   // 定義が無い節は出さない
        Assert.Contains("55F70000", text);
    }

    [Fact]
    public void Layer0_ListsOnlyKeysChangedFromThePrintedKey()
    {
        var p = NewProject();
        p.Layers[0][0] = "KC_ESC";       // Num Lock → Esc
        p.Layers[0][1] = "KC_KP_SLASH";  // 刻印のまま（出さない）
        var lines = Lines(Norm(RemapTextFormatter.Format(p, Layout)));
        var keys = lines.Where(l => l.StartsWith("key ")).ToList();
        Assert.Single(keys);
        Assert.Equal($"key {RemapTextFormatter.KeyName(Layout.Slots.First(s => s.Index == 0))} -> Esc : KC_ESC", keys[0]); // 名前は刻印のキーコードの表示名
    }

    [Fact]
    public void UpperLayer_ListsNonTransparentKeys_WithReadableActions()
    {
        var p = NewProject(2);
        p.LayerNames = new List<string?> { null, "数字" };
        p.Layers[0][0] = "MO(1)";
        p.Layers[0][1] = "MT(MOD_LSFT,KC_A)";
        p.Layers[0][2] = "LT(1,KC_SPACE)";
        p.Layers[1][3] = "LCTL(LSFT(KC_T))";
        var text = Norm(RemapTextFormatter.Format(p, Layout));
        Assert.Contains("-> 押している間 レイヤー1「数字」 : MO(1)", text);
        Assert.Contains("-> タップ: A / 長押し: Shift (Mod-Tap) : MT(MOD_LSFT,KC_A)", text);
        Assert.Contains("-> タップ: Space / 長押し: レイヤー1「数字」 (Layer-Tap)", text);
        Assert.Contains("## レイヤー1「数字」", text);
        Assert.Contains("-> Ctrl+Shift+T : LCTL(LSFT(KC_T))", text);
    }

    [Fact]
    public void Definitions_AreListed_DisabledWithHash_FeatureOffNoted()
    {
        var p = NewProject();
        p.Combos.Add(new ComboDef { Id = 0, Slot1 = 0, Slot2 = 1, Output = "KC_ESC" });
        p.TapDances.Add(new TapDanceDef { Id = 2, SingleTap = "KC_Q", SingleHold = "KC_TAB" });
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 1, Mods = "MOD_LSFT", Key = "KC_BSPACE", Replacement = "KC_DELETE", Disabled = true });
        p.Macros.Add(new MacroDef { Id = 3, Keys = new() { "KC_H", "KC_I" } });
        p.MacroNames = new() { [3] = "挨拶" };
        p.EnabledFeatures = KbdLayerClient.FeatureAll & ~KbdLayerClient.FeatureCombo;

        var text = Norm(RemapTextFormatter.Format(p, Layout));
        Assert.Contains("## コンボ(機能が OFF。適用しても動きません)", text);
        string k0 = RemapTextFormatter.KeyName(Layout.Slots.First(s => s.Index == 0)), k1 = RemapTextFormatter.KeyName(Layout.Slots.First(s => s.Index == 1));
        Assert.Contains($"combo 0: {k0} + {k1} -> Esc : KC_ESC", text);
        Assert.Contains("tapdance 2: 1回 Q / 長押し Tab / 2回 － / 3回 －", text);
        Assert.Contains($"# override 1: Shift+{Kasane.Keycodes.KeycodeCatalog.ShortName("KC_BSPACE")} -> {Kasane.Keycodes.KeycodeCatalog.ShortName("KC_DELETE")} : MOD_LSFT + KC_BSPACE -> KC_DELETE", text);
        Assert.Contains("macro 3「挨拶」: H I", text);
    }

    [Fact]
    public void Helper_ActionsTriggersAndHlpNames()
    {
        var p = NewProject();
        p.Layers[0][0] = "HLP(1)";
        p.Layers[0][1] = "HLP(9)";
        var device = new HelperConfig { Actions = { new HelperAction { Id = 1, Name = "メモ帳", Action = new HelperActionBody { Type = HelperActionBody.Launch, Path = "notepad.exe" } } } };
        var global = new HelperConfig { Triggers = { new HelperTrigger { Key = "KC_F13", Mods = "MOD_LCTL", Action = new HelperActionBody { Type = HelperActionBody.PauseToggle } } } };

        var text = Norm(RemapTextFormatter.Format(p, Layout, device, global));
        Assert.Contains("-> ヘルパー動作 HLP(1) 「メモ帳」 : HLP(1)", text);
        Assert.Contains("-> ヘルパー動作 HLP(9)(動作がありません)", text);
        Assert.Contains("helper [このキーボード] HLP(1) メモ帳 — 起動: notepad.exe", text);
        Assert.Contains("helper trigger [共通] Ctrl+KC_F13", text);
    }

    [Fact]
    public void Helper_SequenceAndTextAreListedInFull_AndTriggersShowTheirName()
    {
        var p = NewProject();
        var seq = new HelperActionBody
        {
            Type = HelperActionBody.Sequence,
            Steps = new List<HelperStep> { new HelperStep { Key = "LCTL(KC_C)" }, new HelperStep { DelayMs = 50 }, new HelperStep { Text = "こんにちは\n" } },
            ReleaseSteps = new List<HelperStep> { new HelperStep { Up = "KC_A" } },
        };
        var long24 = new string('あ', 40);
        var device = new HelperConfig
        {
            Actions = { new HelperAction { Id = 2, Name = "手順", Action = seq },
                        new HelperAction { Id = 3, Action = new HelperActionBody { Type = HelperActionBody.TextInput, Text = long24 } } },
            Triggers = { new HelperTrigger { Name = "コピー", Key = "KC_F13", Mods = "MOD_LCTL", Action = seq } },
        };

        var text = Norm(RemapTextFormatter.Format(p, Layout, device, null));
        // 手順の中身が、番号だけでなく、1 行に出る
        Assert.Contains("helper [このキーボード] HLP(2) 手順 — 操作: key LCTL(KC_C); delay 50; text こんにちは\\n / 離したとき: up KC_A", text);
        // 文字列は、省略しない（設定の一覧の 1 行用は 24 文字まで）
        Assert.Contains(long24, text);
        Assert.DoesNotContain("操作 3 手順", text);
        // トリガーの名前
        Assert.Contains("helper trigger [このキーボード] コピー Ctrl+KC_F13 — 操作: key LCTL(KC_C)", text);
        // 設定の一覧の 1 行用は、これまでどおり短い
        Assert.Equal("操作 3 手順 + 離したとき 1 手順", HelperSummary.Body(seq));
    }

    [Fact]
    public void AppRulesAndImeLinkLayers_AreListed()
    {
        var p = NewProject(3);
        var device = new HelperConfig
        {
            AppLayerRules = { new AppLayerRule { Name = "コード", Enabled = true, Match = new AppMatch { Exe = "code.exe" }, Layers = { 1, 2 } } },
            ImeLayers = new ImeLayerLink { Layers = { 1, 2 } },
        };
        var text = Norm(RemapTextFormatter.Format(p, Layout, device, null));
        Assert.Contains("## アプリ別レイヤーと IME 連動", text);
        Assert.Contains("rule [このキーボード] [有効] コード exe=code.exe → レイヤー 1,2", text);
        Assert.Contains("ime-link [このキーボード] レイヤー 1,2（IME が ON のとき）", text);
    }

    [Fact]
    public void AllLayers_AreListed()
    {
        var p = NewProject(3);
        p.Layers[1][0] = "KC_A";
        p.Layers[2][1] = "KC_B";
        var text = Norm(RemapTextFormatter.Format(p, Layout));
        Assert.Contains("## レイヤー0", text);
        Assert.Contains("## レイヤー1", text);
        Assert.Contains("## レイヤー2", text);
        Assert.Contains("-> A : KC_A", text);
        Assert.Contains("-> B : KC_B", text);
    }

    [Fact]
    public void HelperTrigger_NameRoundTripsThroughJson()
    {
        var cfg = new HelperConfig { Triggers = { new HelperTrigger { Name = "メモ", Key = "KC_F14", Action = new HelperActionBody { Type = HelperActionBody.PauseToggle } } } };
        var back = HelperConfig.FromJson(cfg.ToJson())!;
        Assert.Equal("メモ", back.Triggers[0].Name);
        Assert.Equal("メモ", HelperSummary.CloneTrigger(back.Triggers[0]).Name);
    }

    [Fact]
    public void Nicola_IsSummarized()
    {
        var p = NewProject();
        p.Nicola = new NicolaDef { Layer = 1, Keys = { new NicolaKeyDef { Key = "KC_Q" } }, Enabled = false };
        var text = Norm(RemapTextFormatter.Format(p, Layout));
        Assert.Contains("# nicola レイヤー1 / 親指左 無変換 / 親指右 変換 / 文字キー 1 個 / ローマ字入力", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("zz")]
    [InlineData("0")]
    public void DumpText_BadUniqId_PrintsUsage(string? id)
    {
        var err = new StringWriter();
        Assert.Equal(1, Kasane.Cli.HeadlessCommands.RunDumpText(id, null, new StringWriter(), err, Path.GetTempPath()));
        Assert.Contains("--dump-text", err.ToString());
    }

    [Fact]
    public void DumpText_NoProject_IsAnError()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kasane-dump-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var err = new StringWriter();
            Assert.Equal(1, Kasane.Cli.HeadlessCommands.RunDumpText("0x12345678", null, new StringWriter(), err, dir));
            Assert.Contains("12345678", err.ToString());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ModdedKey_KeepsTheInnerNameAsWritten()
    {
        var p = NewProject();
        p.Layers[0][0] = "LALT(KC_ZKHK)"; // KC_ZKHK は KC_GRAVE と同じスキャンコード。表示は書いたままの 半/全 側
        string text = Norm(RemapTextFormatter.Format(p, Layout));
        Assert.Contains($"-> Alt+{Kasane.Keycodes.KeycodeCatalog.ShortName("KC_ZKHK")} : LALT(KC_ZKHK)", text);
    }

    [Fact]
    public void AlignsArrowsAndColons_CountingJapaneseAsDoubleWidth()
    {
        var p = NewProject();
        p.Layers[0][0] = "KC_ESC";
        p.Layers[0][1] = "MACRO(3)";
        p.Layers[0][2] = "LCTL(LSFT(KC_T))";
        p.MacroNames = new() { [3] = "挨拶" };                  // 右側に日本語が入る行
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 0, Mods = "MOD_LCTL", Key = "KC_1", Replacement = "MACRO(3)" });
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 1, Mods = "MOD_LSFT", Key = "KC_P", Replacement = "KC_B", Disabled = true });
        p.KeyOverrideNames = new() { [0] = "Ctrl-1" };
        var lines = Lines(RemapTextFormatter.Format(p, Layout));

        void SameColumns(IReadOnlyList<string> rows)
        {
            Assert.True(rows.Count >= 2);
            // 「->」と「 : 」の見た目の位置（日本語は2と数える）が、どの行も同じ。
            int Col(string l, string mark) => RemapTextFormatter.DisplayWidth(l[..l.IndexOf(mark, StringComparison.Ordinal)]);
            Assert.Single(rows.Select(l => Col(l, " -> ")).Distinct());
            Assert.Single(rows.Select(l => Col(l, " : ")).Distinct());
        }
        SameColumns(lines.Where(l => l.StartsWith("key ")).ToList());
        SameColumns(lines.Where(l => l.Contains("override ")).ToList()); // 無効の行（# 付き）も揃う
        Assert.All(lines, l => Assert.False(l.EndsWith(" "), $"末尾に空白: [{l}]"));
    }

    [Theory]
    [InlineData("abc", 3)]
    [InlineData("挨拶", 4)]
    [InlineData("「数字」", 8)]
    [InlineData("▽", 2)]
    [InlineData("Alt+半/全", 9)]  // Alt+ が 4、半・全 が 2 ずつ、/ が 1
    public void DisplayWidth_CountsWideCharactersAsTwo(string s, int expected) => Assert.Equal(expected, RemapTextFormatter.DisplayWidth(s));

    [Fact]
    public void AliasOfThePrintedKey_IsNotListed()
    {
        var p = NewProject();
        var slot = Layout.Slots.First(s => s.DefaultKeycode == "KC_KP_SLASH");
        p.Layers[0][slot.Index] = "KC_PSLS"; // 同じキーの別名
        Assert.DoesNotContain("key ", RemapTextFormatter.Format(p, Layout));
    }
}
