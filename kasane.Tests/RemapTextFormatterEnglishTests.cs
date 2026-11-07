using Kasane.Layout;
using Kasane.Model;
using Xunit;

namespace Kasane.Tests;

/// <summary>
/// 表示言語を切り替えるテスト（<see cref="Loc"/> はプロセス全体で1つ）。ほかのテストと同時に走ると、そちらの文言が英語になるので、
/// 並行実行しないコレクションに入れる。終わったら日本語に戻す。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LanguageSwitchingCollection
{
    public const string Name = "Language switching";
}

[Collection(LanguageSwitchingCollection.Name)]
public class RemapTextFormatterEnglishTests
{
    private static readonly KeyboardLayout Layout = LayoutFixture.TenKey;

    [Fact]
    public void English_HasNoJapaneseBrackets_OrJapaneseText()
    {
        var p = ProjectService.CreateDefault(0x55F70000, null, Layout, bundled: true);
        p.KeyboardName = "Work";
        p.Layers.Add(Enumerable.Repeat("KC_TRNS", p.Layers[0].Count).ToList());
        p.LayerNames = new List<string?> { null, "Numbers" };
        p.Layers[0][0] = "MACRO(3)";
        p.Layers[0][1] = "HLP(1)";
        p.Layers[0][2] = "LT(1,KC_SPACE)";
        p.Macros.Add(new MacroDef { Id = 3, Keys = new() { "KC_H", "KC_I" } });
        p.MacroNames = new() { [3] = "hello" };
        p.TapDances.Add(new TapDanceDef { Id = 0, SingleTap = "KC_Q" });   // 空の欄がある
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 1, Mods = "MOD_LCTL", Key = "KC_1", Replacement = "MACRO(3)" });
        p.KeyOverrideNames = new() { [1] = "Ctrl-1" };
        var device = new HelperConfig { Actions = { new HelperAction { Id = 1, Name = "Notepad", Action = new HelperActionBody { Type = HelperActionBody.Launch, Path = "notepad.exe" } } } };

        string text;
        try
        {
            Loc.Initialize(UiLanguage.English);
            text = RemapTextFormatter.Format(p, Layout, device, null, RemapTextFormatter.KeyboardLabel(p));
        }
        finally { Loc.Initialize(UiLanguage.Japanese); }

        Assert.DoesNotContain("「", text);
        Assert.DoesNotContain("」", text);
        // かな・漢字・全角の記号が残っていない（▽ は言語に関係しない記号なので除く）。
        Assert.DoesNotMatch("[　-ヿ一-鿿＀-￯]", text);
        Assert.Contains("# Keyboard: 55F70000 \"Work\"", text);
        Assert.Contains("## Layer 1 \"Numbers\"", text);
        Assert.Contains("override 1 \"Ctrl-1\": Ctrl+1", text);
        Assert.Contains("Macro 3 \"hello\"", text);
        Assert.Contains("helper action HLP(1) \"Notepad\"", text);
        Assert.Contains("tap Q / hold - / 2 taps - / 3 taps -", text);
    }
}
