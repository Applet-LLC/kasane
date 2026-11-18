using System.Globalization;
using Kasane.Model;
using Xunit;

namespace Kasane.Tests;

public class TextTemplateTests
{
    private static readonly DateTime Morning = new(2026, 11, 18, 9, 0, 5);   // 水曜
    private static readonly DateTime Evening = new(2026, 11, 18, 21, 5, 0);
    private static readonly CultureInfo Ja = CultureInfo.GetCultureInfo("ja-JP");
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    private static string X(string t, DateTime? now = null, CultureInfo? c = null, List<string>? errors = null) =>
        TextTemplate.Expand(t, now ?? Morning, c ?? Ja, errors);

    [Fact]
    public void NoPlaceholder_IsUnchanged()
    {
        Assert.Equal("abc", X("abc"));
        Assert.Equal("", X(""));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("if (x) { return; }")]
    [InlineData("{{}}")]
    [InlineData("{dates}")]
    [InlineData("{foo:bar}")]
    [InlineData("}")]
    public void OtherBraces_StayLiteral_WithoutErrors(string text)
    {
        var errors = new List<string>();
        Assert.Equal(text, X(text, errors: errors));
        Assert.Empty(errors);
    }

    [Fact]
    public void StandardFormats_UseCulture()
    {
        Assert.Equal(Morning.ToString("d", Ja), X("{date}"));
        Assert.Equal(Morning.ToString("d", Ja), X("{date:short}"));
        Assert.Equal(Morning.ToString("D", Ja), X("{date:long}"));
        Assert.Equal(Morning.ToString("t", Ja), X("{time}"));
        Assert.Equal(Morning.ToString("T", Ja), X("{time:long}"));
        Assert.Equal(Morning.ToString("D", En), X("{date:long}", c: En));
    }

    [Fact]
    public void Custom_Example_FromRequest()
    {
        Assert.Equal("2026-11-18 (水) 9:00am", X("{now:yyyy-MM-dd (ddd) h:mm}{ampm}"));
        Assert.Equal("2026-11-18 (水) 9:00pm", X("{now:yyyy-MM-dd (ddd) h:mm}{ampm}", Morning.AddHours(12)));
        Assert.Equal("2026-11-18 (Wed) 9:00 AM", X("{now:yyyy-MM-dd (ddd) h:mm} {AMPM}", c: En));
    }

    [Fact]
    public void Ampm_BoundaryAtNoonAndMidnight()
    {
        Assert.Equal("am", X("{ampm}", new DateTime(2026, 1, 1, 0, 0, 0)));
        Assert.Equal("am", X("{ampm}", new DateTime(2026, 1, 1, 11, 59, 59)));
        Assert.Equal("PM", X("{AMPM}", new DateTime(2026, 1, 1, 12, 0, 0)));
    }

    [Fact]
    public void PrefixAndSuffixText_IsKept()
    {
        Assert.Equal("作成日 " + Morning.ToString("d", Ja) + " です", X("作成日 {date} です"));
    }

    [Fact]
    public void SingleCharCustomFormat_NeedsPercent()
    {
        Assert.Equal("9", X("{now:%h}"));
    }

    [Fact]
    public void Escape_DoubleOpeningBrace_TypesPlaceholderLiterally()
    {
        var errors = new List<string>();
        Assert.Equal("{date}", X("{{date}", errors: errors));
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("{date:medium}")]
    [InlineData("{time:x}")]
    [InlineData("{now}")]
    [InlineData("{now:}")]
    [InlineData("{ampm:x}")]
    public void BadPlaceholder_ReportsError_AndKeepsOriginalText(string text)
    {
        var errors = new List<string>();
        Assert.Equal(text, X(text, errors: errors));
        Assert.Single(errors);
    }

    [Fact]
    public void Unclosed_ReportsError_AndKeepsOriginalText()
    {
        var errors = new List<string>();
        Assert.Equal("a{date", X("a{date", errors: errors));
        Assert.Single(errors);
    }

    [Fact]
    public void SameInstant_DateAndTimeAgree_AcrossMidnight()
    {
        var t = new DateTime(2026, 11, 18, 23, 59, 59, 900);
        Assert.Equal("2026-11-18 23:59", X("{now:yyyy-MM-dd} {now:HH:mm}", t));
    }

    [Fact]
    public void Validate_ReturnsErrors()
    {
        Assert.Empty(TextTemplate.Validate("{now:yyyy-MM-dd} {ampm}"));
        Assert.Single(TextTemplate.Validate("{date:medium}"));
    }

    [Fact]
    public void Compile_ExpandsTextStep_ToUnicodeOps()
    {
        var r = SequenceCompiler.Compile(new[] { new HelperStep { Text = "{now:yyyy}!" } }, Morning, Ja);
        Assert.Empty(r.Errors);
        var chars = r.Ops.Where(o => o.Kind == InputKind.Unicode && !o.Up).Select(o => (char)o.Value).ToArray();
        Assert.Equal("2026!", new string(chars));
    }

    [Fact]
    public void Compile_SingleStep_ErrorHasNoStepPrefix()
    {
        var seq = SequenceCompiler.Compile(new[] { new HelperStep { Text = "{now:}" } }, Morning, Ja);
        var text = SequenceCompiler.Compile(new[] { new HelperStep { Text = "{now:}" } }, Morning, Ja, singleStep: true);
        Assert.Single(seq.Errors);
        Assert.Single(text.Errors);
        Assert.NotEqual(seq.Errors[0], text.Errors[0]);
        Assert.EndsWith(text.Errors[0], seq.Errors[0]); // 「手順1: 」が付くかどうかだけの違い
    }

    [Fact]
    public void CurrentUserCulture_IsUsable_AfterClearingCache()
    {
        var c = TextTemplate.CurrentUserCulture();
        Assert.NotNull(c);
        Assert.False(string.IsNullOrEmpty(Morning.ToString("d", c)));
        Assert.Equal(c.Name, TextTemplate.CurrentUserCulture().Name);
        Assert.NotEqual("", c.Name); // 地域の名前が取れている(空は Invariant)
    }

    [Fact]
    public void Compile_PlainText_IsUnchanged()
    {
        var r = SequenceCompiler.Compile(new[] { new HelperStep { Text = "a{}b" } }, Morning, Ja);
        var chars = r.Ops.Where(o => o.Kind == InputKind.Unicode && !o.Up).Select(o => (char)o.Value).ToArray();
        Assert.Equal("a{}b", new string(chars));
    }
}
