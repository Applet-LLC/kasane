using Kasane.Model;
using Xunit;

namespace Kasane.Tests;

public class StepInsertTests
{
    [Fact]
    public void EmptyBox_IsJustTheLine()
    {
        var (text, caret) = StepInsert.InsertLine("", 0, "key KC_A");
        Assert.Equal("key KC_A", text);
        Assert.Equal(8, caret);
    }

    [Fact]
    public void CaretOnLastLineWithoutNewline_AppendsAfterANewline()
    {
        var (text, caret) = StepInsert.InsertLine("key KC_A", 3, "key KC_B");
        Assert.Equal("key KC_A\r\nkey KC_B", text);
        Assert.Equal(text.Length, caret);
    }

    [Fact]
    public void CaretOnFirstLine_InsertsAfterThatLine()
    {
        var (text, caret) = StepInsert.InsertLine("key KC_A\r\nkey KC_C", 4, "key KC_B");
        Assert.Equal("key KC_A\r\nkey KC_B\r\nkey KC_C", text);
        Assert.Equal("key KC_A\r\nkey KC_B".Length, caret);
    }

    [Fact]
    public void CaretAtLineStart_InsertsAfterThatLineNotBefore()
    {
        // 2行目の行頭（直前が改行）: 2行目の次に入る。
        var (text, _) = StepInsert.InsertLine("key KC_A\r\nkey KC_C", 10, "key KC_B");
        Assert.Equal("key KC_A\r\nkey KC_C\r\nkey KC_B", text);
    }

    [Fact]
    public void CaretAtEndOfLine_BeforeItsNewline_StaysOnThatLine()
    {
        var (text, _) = StepInsert.InsertLine("key KC_A\r\nkey KC_C", 8, "key KC_B");
        Assert.Equal("key KC_A\r\nkey KC_B\r\nkey KC_C", text);
    }

    [Fact]
    public void TextEndingWithNewline_DoesNotAddABlankLine()
    {
        var (text, _) = StepInsert.InsertLine("key KC_A\r\n", 10, "mouse left");
        Assert.Equal("key KC_A\r\nmouse left", text);
    }

    [Fact]
    public void FollowsTheExistingNewlineStyle()
    {
        var (text, _) = StepInsert.InsertLine("key KC_A\nkey KC_C", 2, "key KC_B");
        Assert.Equal("key KC_A\nkey KC_B\nkey KC_C", text);
    }

    [Fact]
    public void OutOfRangeCaret_IsTreatedAsTheEnd()
    {
        var (text, _) = StepInsert.InsertLine("key KC_A", 999, "key KC_B");
        Assert.Equal("key KC_A\r\nkey KC_B", text);
        var (text2, _) = StepInsert.InsertLine("key KC_A", -1, "key KC_B");
        Assert.Equal("key KC_A\r\nkey KC_B", text2);
    }

    [Fact]
    public void InsertedLinesAreAcceptedByTheStepParser()
    {
        var (text, _) = StepInsert.InsertLine("", 0, "key KC_A");
        (text, _) = StepInsert.InsertLine(text, text.Length, "mouse left down");
        (text, _) = StepInsert.InsertLine(text, text.Length, "wheel 120");
        Assert.True(StepText.TryParse(text, out _, out var errors), string.Join(";", errors));
    }
}
