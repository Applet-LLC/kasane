using Kasane.Views;

namespace Kasane.Tests;

/// <summary>マクロを選ぶ画面に出す1行（番号・名前・中身）と、入れるトークン。</summary>
public class MacroInfoTests
{
    [Fact]
    public void NamedMacro_ShowsIdNameAndContent() =>
        Assert.Equal("  7: 署名 — KC_H KC_I", new MacroInfo(7, "署名", "KC_H KC_I").ToString());

    [Fact]
    public void UnnamedMacro_ShowsIdAndContent() =>
        Assert.Equal("  0: KC_A", new MacroInfo(0, null, "KC_A").ToString());

    [Fact]
    public void BlankName_IsTreatedAsUnnamed() =>
        Assert.Equal("  1: KC_A", new MacroInfo(1, "  ", "KC_A").ToString());

    [Fact]
    public void EmptyContent_IsShownAsUnused() =>
        Assert.Equal(" 12: 後で作る — （未使用）", new MacroInfo(12, "後で作る", "").ToString());

    [Theory]
    [InlineData(0, "MACRO(0)")]
    [InlineData(127, "MACRO(127)")]
    public void Token_IsMacroCall(int id, string expected) => Assert.Equal(expected, new MacroInfo(id, null, "KC_A").Token);

    [Fact]
    public void Directory_WithoutProvider_IsEmpty()
    {
        var old = MacroDirectory.Provider;
        try
        {
            MacroDirectory.Provider = null;
            Assert.Empty(MacroDirectory.Items);
            MacroDirectory.Provider = () => new[] { new MacroInfo(3, null, "KC_B") };
            Assert.Single(MacroDirectory.Items);
        }
        finally { MacroDirectory.Provider = old; }
    }
}
