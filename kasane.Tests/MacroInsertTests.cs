using Kasane.Views;
using Xunit;

namespace Kasane.Tests;

/// <summary>マクロ・タップダンスの編集画面の「入れる」: まだ入れていないかの判定（閉じるときの確認と、ボタンの強調に使う）。</summary>
public class MacroInsertTests
{
    [Theory]
    [InlineData("MACRO(3)", "", false, true)]                // 欄が空: まだ入れていない
    [InlineData("MACRO(3)", "KC_A", false, true)]            // 別の内容が入っている: まだ入れていない
    [InlineData("MACRO(3)", "MACRO(3)", false, false)]       // すでに入っている
    [InlineData("MACRO(3)", "macro(3)", false, false)]       // 大文字小文字は区別しない
    [InlineData("MACRO(3)", "MACRO(30)", false, true)]       // 番号の前方一致は、入っていると見ない
    [InlineData("MACRO(3)", "KC_A MACRO(3) KC_B", false, false)]   // 他のキーといっしょでも、入っている
    [InlineData("TAPDANCE(2)", "", false, false)]            // TAPDANCE(n) を入れられない欄: 入れ先とは見ない（確認しない）
    [InlineData("TAPDANCE(2)", "", true, true)]              // キー割り当て画面のキーコード欄: まだ入れていない
    [InlineData("TAPDANCE(2)", "TAPDANCE(2)", true, false)]
    public void NeedsInsert(string token, string targetText, bool acceptsTapDance, bool expected) =>
        Assert.Equal(expected, MacroInsert.NeedsInsert(token, targetText, acceptsTapDance));

    [Fact]
    public void NeedsInsert_WithoutATarget_IsFalse() => Assert.False(MacroInsert.NeedsInsert("MACRO(1)"));
}
