using Kasane.Model;

namespace Kasane.Tests;

/// <summary>手順のテキスト表記（設定画面の入力欄）。</summary>
public class StepTextTests
{
    private static List<HelperStep> Parse(string text)
    {
        Assert.True(StepText.TryParse(text, out var steps, out var errors), string.Join("; ", errors));
        return steps;
    }

    [Fact]
    public void Parse_EveryCommand()
    {
        var steps = Parse("""
            # コメントと空行は無視する

            key LCTL(KC_C)
            down KC_A
            up KC_A
            text こんにちは\nさようなら\t\\
            delay 50
            mouse left
            mouse RIGHT down
            mouse x1 up
            move 10 -5
            moveto 100 200
            wheel -120
            hwheel 120
            """);

        Assert.Equal(12, steps.Count);
        Assert.Equal("LCTL(KC_C)", steps[0].Key);
        Assert.Equal("KC_A", steps[1].Down);
        Assert.Equal("KC_A", steps[2].Up);
        Assert.Equal("こんにちは\nさようなら\t\\", steps[3].Text);
        Assert.Equal(50, steps[4].DelayMs);
        Assert.Equal("left", steps[5].Mouse);
        Assert.Null(steps[5].MouseAction);
        Assert.Equal(("right", "down"), (steps[6].Mouse, steps[6].MouseAction));
        Assert.Equal(("x1", "up"), (steps[7].Mouse, steps[7].MouseAction));
        Assert.Equal(10, steps[8].MoveX);
        Assert.Equal(-5, steps[8].MoveY);
        Assert.False(steps[8].Absolute);
        Assert.Equal(100, steps[9].MoveX);
        Assert.Equal(200, steps[9].MoveY);
        Assert.True(steps[9].Absolute);
        Assert.Equal(-120, steps[10].Wheel);
        Assert.Equal(120, steps[11].HWheel);
    }

    [Fact]
    public void Parse_TextKeepsInnerAndTrailingSpaces_AndUnknownEscapes()
    {
        var steps = Parse("text  a b  \ntext C:\\temp\\x");
        Assert.Equal(" a b  ", steps[0].Text);          // 命令の後ろの空白1つだけが区切り
        Assert.Equal("C:\temp\\x", steps[1].Text);      // 入力の \t は Tab になり、知らない書き方の \x はそのまま残る
    }

    [Theory]
    [InlineData("key", "key にキーコード")]
    [InlineData("key NOT_A_KEY", "キーとして使えません")]
    [InlineData("key MO(1)", "キーとして使えません")]
    [InlineData("text", "文字列がありません")]
    [InlineData("delay abc", "delay は")]
    [InlineData("delay -1", "delay は")]
    [InlineData("delay 20000", "delay は")]
    [InlineData("mouse", "mouse は")]
    [InlineData("mouse thumb", "mouse は")]
    [InlineData("mouse left smash", "click / down / up")]
    [InlineData("move 1", "整数を2つ")]
    [InlineData("moveto a b", "整数を2つ")]
    [InlineData("wheel 0", "0 以外")]
    [InlineData("teleport 1", "命令ではありません")]
    public void Parse_Errors_AreReportedWithLineNumbers(string line, string expected)
    {
        Assert.False(StepText.TryParse("key KC_A\n" + line, out var steps, out var errors));
        Assert.Single(errors);
        Assert.StartsWith("2行目:", errors[0]);
        Assert.Contains(expected, errors[0]);
        Assert.Single(steps); // 誤りの行は飛ばし、正しい行は残る
    }

    [Fact]
    public void Parse_EmptyAndNull_AreEmptySteps()
    {
        Assert.True(StepText.TryParse(null, out var a, out _));
        Assert.True(StepText.TryParse("  \r\n\r\n# only comment", out var b, out _));
        Assert.Empty(a);
        Assert.Empty(b);
    }

    [Fact]
    public void Parse_AcceptsCrLf()
    {
        Assert.Equal(2, Parse("key KC_A\r\nkey KC_B\r\n").Count);
    }

    [Fact]
    public void Format_ThenParse_GivesTheSameInputs()
    {
        var original = new List<HelperStep>
        {
            new() { Key = "LCTL(KC_C)" },
            new() { Down = "LSFT(KC_A)" },
            new() { DelayMs = 20 },
            new() { Up = "LSFT(KC_A)", Key = "KC_B" },                        // 1つの手順に複数の項目（実行する順に複数行になる）
            new() { Text = "こんにちは\n\t\\ 😀" },
            new() { Mouse = "Left", MouseAction = "Down" },
            new() { MoveX = 3, MoveY = -4 },
            new() { MoveX = 100, MoveY = 200, Absolute = true },
            new() { Wheel = 240, HWheel = -120 },
            new() { Mouse = "X2" },
        };

        string text = StepText.Format(original);
        var back = Parse(text);

        var a = SequenceCompiler.Compile(original);
        var b = SequenceCompiler.Compile(back);
        Assert.Empty(a.Errors);
        Assert.Empty(b.Errors);
        Assert.Equal(a.Ops, b.Ops);
        Assert.Equal(text, StepText.Format(back)); // 書き出しは安定している
    }

    [Fact]
    public void Format_OfNull_IsEmpty() => Assert.Equal("", StepText.Format(null));
}
