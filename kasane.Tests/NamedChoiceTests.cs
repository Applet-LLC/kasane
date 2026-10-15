using Kasane.Model;
using Kasane.Views;

namespace Kasane.Tests;

/// <summary>キーの割り当てで、レイヤー・マクロ・タップダンスを「番号: 名前」で選べること。名前の無いものは番号だけ。</summary>
public class NamedChoiceTests
{
    [Fact]
    public void UnnamedShowsNumberOnly() => Assert.Equal("3", new NamedChoice(3, null).ToString());

    [Fact]
    public void NamedShowsNumberAndName() => Assert.Equal("3: 撮影", new NamedChoice(3, "撮影").ToString());

    [Fact]
    public void BlankNameIsUnnamed() => Assert.Equal("3", new NamedChoice(3, "  ").ToString());

    [Fact]
    public void NoProjectGivesNumbersOnly()
    {
        Assert.All(NamedChoice.Layers(null), c => Assert.Null(c.Name));
        Assert.All(NamedChoice.Macros(null), c => Assert.Null(c.Name));
    }

    [Fact]
    public void NamesComeFromProject()
    {
        var project = new KeymapProject
        {
            MacroNames = new() { [2] = "署名" },
            TapDanceNames = new() { [0] = "Esc/半角" },
        };
        Assert.Equal("2: 署名", NamedChoice.Macros(project)[2].ToString());
        Assert.Equal("1", NamedChoice.Macros(project)[1].ToString());
        Assert.Equal("0: Esc/半角", NamedChoice.TapDances(project)[0].ToString());
    }
}

/// <summary>キーのツールチップに出す、指す先の名前。</summary>
public class TargetNameNoteTests
{
    private static KeymapProject Project() => new()
    {
        Layers = new() { new(), new() },
        LayerNames = new() { null, "数字" },
        MacroNames = new() { [2] = "署名" },
        TapDanceNames = new() { [0] = "Esc/半角" },
    };

    [Theory]
    [InlineData("MO(1)", "レイヤー1「数字」")]
    [InlineData("LT(1,KC_A)", "レイヤー1「数字」")]
    [InlineData("MACRO(2)", "マクロ2「署名」")]
    [InlineData("TAPDANCE(0)", "タップダンス0「Esc/半角」")]
    [InlineData("MO(0)", null)]
    [InlineData("MACRO(3)", null)]
    [InlineData("KC_A", null)]
    public void Note(string token, string? expected)
    {
        Assert.True(Kasane.Keycodes.KeycodeTable.TryResolve(token, out var action));
        Assert.Equal(expected, ProjectService.TargetNameNote(Project(), action));
    }
}
