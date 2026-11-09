using Kasane.Interop;
using Kasane.Layout;
using Kasane.Model;
using Xunit;

namespace Kasane.Tests;

public class KeyMarksTests
{
    private static readonly KeyboardLayout Layout = LayoutFixture.TenKey;

    private static KeymapProject NewProject() => ProjectService.CreateDefault(0, null, Layout, bundled: true);

    private static KeySlot Slot(int index) => Layout.Slots.First(s => s.Index == index);

    private static KeyMarks.HelperLookup Lookup() => KeyMarks.HelperLookup.From(
        new HelperConfig
        {
            Actions = { new HelperAction { Id = 4, Name = "メモ帳", Action = new HelperActionBody { Type = HelperActionBody.Launch, Path = "notepad.exe" } } },
            Triggers = { new HelperTrigger { Name = "停止", Key = "KC_F13", Mods = "MOD_LCTL", Action = new HelperActionBody { Type = HelperActionBody.PauseToggle } } },
        },
        new HelperConfig { Actions = { new HelperAction { Id = 4, Name = "共通の4", Action = new HelperActionBody { Type = HelperActionBody.PauseToggle } } } });

    [Fact]
    public void HlpAssignment_GetsH_WithTheActionDescription()
    {
        var p = NewProject();
        p.Layers[0][0] = "HLP(4)";
        p.Layers[0][1] = "HLP(9)";
        var m = KeyMarks.For(p, Layout, 0, Slot(0), Lookup());
        Assert.Equal("H", m.Marks);
        Assert.Contains("メモ帳", m.ToolTip);                 // このキーボード用が先
        Assert.DoesNotContain("共通の4", m.ToolTip);
        var missing = KeyMarks.For(p, Layout, 0, Slot(1), Lookup());
        Assert.Equal("H", missing.Marks);
        Assert.Contains("動作がありません", missing.ToolTip);
        Assert.Equal("H", KeyMarks.For(p, Layout, 0, Slot(0)).Marks);   // ヘルパーの情報なしでも H
    }

    [Fact]
    public void TriggerKey_GetsT_AndOnlyWithTheMatchingKey()
    {
        var p = NewProject();
        p.Layers[0][0] = "KC_F13";
        p.Layers[0][1] = "LCTL(KC_F13)";
        p.Layers[0][2] = "KC_F14";
        var m = KeyMarks.For(p, Layout, 0, Slot(0), Lookup());
        Assert.Equal("T", m.Marks);
        Assert.Contains("停止", m.ToolTip);
        Assert.Contains("Ctrl+KC_F13", m.ToolTip);
        Assert.Equal("T", KeyMarks.For(p, Layout, 0, Slot(1), Lookup()).Marks);   // 修飾付きでも、出力キーが同じ
        Assert.Equal("", KeyMarks.For(p, Layout, 0, Slot(2), Lookup()).Marks);
        Assert.Equal("", KeyMarks.For(p, Layout, 0, Slot(0)).Marks);               // ヘルパーの情報なしでは付けない
    }

    [Fact]
    public void Marks_AreOrdered_MDCRHT()
    {
        var p = NewProject();
        p.Layers[0][0] = "HLP(4)";
        p.Combos.Add(new ComboDef { Id = 0, Slot1 = 0, Slot2 = 1, Output = "KC_ESC" });
        Assert.Equal("CH", KeyMarks.For(p, Layout, 0, Slot(0), Lookup()).Marks);
    }

    [Fact]
    public void PlainKey_HasNoMarks()
    {
        var p = NewProject();
        Assert.Equal("", KeyMarks.For(p, Layout, 0, Slot(0)).Marks);
    }

    [Fact]
    public void MacroAndTapDanceAssignments_GetMAndD()
    {
        var p = NewProject();
        p.Layers[0][0] = "MACRO(2)";
        p.Layers[0][1] = "TAPDANCE(3)";
        p.MacroNames = new() { [2] = "挨拶" };

        var m = KeyMarks.For(p, Layout, 0, Slot(0));
        Assert.Equal("M", m.Marks);
        Assert.Contains("マクロ2「挨拶」", m.ToolTip);
        Assert.Equal("D", KeyMarks.For(p, Layout, 0, Slot(1)).Marks);
    }

    [Fact]
    public void Combo_MarksBothKeys_OnEveryLayer()
    {
        var p = NewProject();
        p.Layers.Add(Enumerable.Repeat("KC_TRNS", p.Layers[0].Count).ToList()); // レイヤー1（全キー透過）
        p.Combos.Add(new ComboDef { Id = 1, Slot1 = 0, Slot2 = 1, Output = "KC_ESC" });

        foreach (int layer in new[] { 0, 1 })
        {
            Assert.Equal("C", KeyMarks.For(p, Layout, layer, Slot(0)).Marks);
            Assert.Equal("C", KeyMarks.For(p, Layout, layer, Slot(1)).Marks);
            Assert.Equal("", KeyMarks.For(p, Layout, layer, Slot(2)).Marks);
        }
        Assert.Contains("KC_ESC", KeyMarks.For(p, Layout, 0, Slot(0)).ToolTip);
    }

    [Fact]
    public void KeyOverride_MarksKeysThatOutputTheTrigger_OnlyOnLayersThatDoSo()
    {
        var p = NewProject();
        p.Layers[0][0] = "KC_A";
        p.Layers.Add(Enumerable.Repeat("KC_TRNS", p.Layers[0].Count).ToList());
        p.Layers[1][2] = "KC_A";      // レイヤー1では、キー2も A を出す
        p.Layers[1][0] = "KC_B";      // レイヤー1では、キー0は B に変わる
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 3, Mods = "MOD_LSFT", Key = "KC_A", Replacement = "KC_Z" });

        var onL0 = KeyMarks.For(p, Layout, 0, Slot(0));
        Assert.Equal("R", onL0.Marks);
        Assert.Contains("キー置換3", onL0.ToolTip);
        Assert.Contains("KC_Z", onL0.ToolTip);

        Assert.Equal("", KeyMarks.For(p, Layout, 1, Slot(0)).Marks);   // レイヤー1のキー0は B
        Assert.Equal("R", KeyMarks.For(p, Layout, 1, Slot(2)).Marks);  // レイヤー1のキー2は A
    }

    [Fact]
    public void TransparentKey_InheritsTheLowerLayersAssignment()
    {
        var p = NewProject();
        p.Layers[0][0] = "KC_A";
        p.Layers.Add(Enumerable.Repeat("KC_TRNS", p.Layers[0].Count).ToList());
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 0, Mods = "MOD_LCTL", Key = "KC_A", Replacement = "KC_B" });
        Assert.Equal("KC_A", KeyMarks.EffectiveToken(p, 1, Slot(0)));
        Assert.Equal("R", KeyMarks.For(p, Layout, 1, Slot(0)).Marks);
    }

    [Fact]
    public void ModdedKeyAssignment_MatchesOverrideByOutputKey()
    {
        var p = NewProject();
        p.Layers[0][0] = "LALT(KC_ZKHK)";    // Caps Lock を IME のオン・オフにする割り当て
        p.Layers[0][1] = "LALT(KC_A)";
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 0, Mods = "MOD_LSFT", Key = "KC_ZKHK", Replacement = "KC_CAPSLOCK" });

        var m = KeyMarks.For(p, Layout, 0, Slot(0));
        Assert.Equal("R", m.Marks);
        Assert.Contains("キー置換0", m.ToolTip);
        Assert.Equal("", KeyMarks.For(p, Layout, 0, Slot(1)).Marks);   // 出力キーが違う
    }

    [Fact]
    public void MarksAreOrderedMDCR()
    {
        var p = NewProject();
        p.Layers[0][0] = "MACRO(1)";
        p.Combos.Add(new ComboDef { Id = 0, Slot1 = 0, Slot2 = 1, Output = "KC_ESC" });
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 0, Mods = "MOD_LSFT", Key = "MACRO(1)", Replacement = "KC_B" }); // 同じトークンに当たる（印の並びの確認用）
        Assert.Equal("MCR", KeyMarks.For(p, Layout, 0, Slot(0)).Marks);
    }

    // ---- Find（割り当て画面・右クリックの「開く」。GUI 計画 1-2） ----

    [Fact]
    public void Find_Combo_EitherMember_IncludesDisabled()
    {
        var p = NewProject();
        p.Combos.Add(new ComboDef { Id = 4, Slot1 = 1, Slot2 = 0, Output = "KC_ESC" });
        p.Combos.Add(new ComboDef { Id = 2, Slot1 = 0, Slot2 = 2, Output = "KC_TAB", Disabled = true });

        var r = KeyMarks.Find(p, 0, Slot(0));
        Assert.Equal(new[] { 2, 4 }, r.Combos.Select(c => c.Id));       // 番号の順。無効も返す
        Assert.Equal(new[] { 4 }, KeyMarks.Find(p, 0, Slot(1)).Combos.Select(c => c.Id)); // キー2 の側でも
        Assert.Equal("C", KeyMarks.For(p, Layout, 0, Slot(0)).Marks);   // 印は有効なものだけ（従来どおり）
        Assert.DoesNotContain("KC_TAB", KeyMarks.For(p, Layout, 0, Slot(0)).ToolTip);
        Assert.Equal("", KeyMarks.For(p, Layout, 0, Slot(2)).Marks);    // 無効のコンボだけのキーは印なし
        Assert.Single(KeyMarks.Find(p, 0, Slot(2)).Combos);              // でも Find は返す
    }

    [Fact]
    public void Find_KeyOverride_MatchesLikeFor_IncludesDisabled()
    {
        var p = NewProject();
        p.Layers[0][0] = "LALT(KC_ZKHK)";
        p.Layers.Add(Enumerable.Repeat("KC_TRNS", p.Layers[0].Count).ToList());
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 1, Mods = "MOD_LSFT", Key = "KC_ZKHK", Replacement = "KC_CAPSLOCK" });
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 0, Mods = "MOD_LCTL", Key = "KC_ZKHK", Replacement = "KC_A", Disabled = true });

        var r = KeyMarks.Find(p, 1, Slot(0)); // 透過のレイヤー1でも、下の LALT(KC_ZKHK) の出力キーで照合
        Assert.Equal("LALT(KC_ZKHK)", r.Token);
        Assert.Equal(new[] { 0, 1 }, r.KeyOverrides.Select(k => k.Id));
        Assert.Equal("R", KeyMarks.For(p, Layout, 1, Slot(0)).Marks);
    }

    [Fact]
    public void Find_MacroAndTapDance()
    {
        var p = NewProject();
        p.Layers[0][0] = "MACRO(7)";
        p.Layers[0][1] = "TAPDANCE(3)";
        Assert.Equal(7, KeyMarks.Find(p, 0, Slot(0)).MacroId);
        Assert.Null(KeyMarks.Find(p, 0, Slot(0)).TapDanceId);
        Assert.Equal(3, KeyMarks.Find(p, 0, Slot(1)).TapDanceId);
        Assert.Null(KeyMarks.Find(p, 0, Slot(2)).MacroId);
    }

    [Theory]
    [InlineData("KC_A", "KC_A")]
    [InlineData("LALT(KC_ZKHK)", "KC_ZKHK")]   // 修飾付きは出力キー
    [InlineData("MO(1)", null)]
    [InlineData("MACRO(2)", null)]
    [InlineData("KC_NO", null)]
    [InlineData("MT(MOD_LSFT,KC_A)", null)]
    public void OverrideKeyFor(string token, string? expected) => Assert.Equal(expected, KeyMarks.OverrideKeyFor(token));

    [Theory]
    [InlineData("KC_A", "KC_A")]
    [InlineData("LCTL(KC_C)", "LCTL(KC_C)")]   // タップダンスの1回タップには修飾付きも入る
    [InlineData("MO(1)", null)]
    [InlineData("KC_NO", null)]
    [InlineData("KC_TRNS", null)]
    public void KeyLikeToken(string token, string? expected) => Assert.Equal(expected, KeyMarks.KeyLikeToken(token));

    [Theory]
    [InlineData(0, true, "マクロ...")]
    [InlineData(2, true, "マクロ(2)...")]
    [InlineData(2, false, "マクロ(2・OFF)...")]
    [InlineData(0, false, "マクロ...")] // 定義が無ければ、OFF でも出さない
    public void ButtonText(int count, bool on, string expected) => Assert.Equal(expected, KeyMarks.ButtonText("マクロ", count, on));

    [Fact]
    public void FeatureOn_NullMeansAllOn()
    {
        var p = NewProject();
        Assert.True(KeyMarks.FeatureOn(p, KbdLayerClient.FeatureCombo));
        p.EnabledFeatures = KbdLayerClient.FeatureAll & ~KbdLayerClient.FeatureCombo;
        Assert.False(KeyMarks.FeatureOn(p, KbdLayerClient.FeatureCombo));
        Assert.True(KeyMarks.FeatureOn(p, KbdLayerClient.FeatureMacro));
    }
}
