using Kasane.Model;

namespace Kasane.Tests;

/// <summary>画面に出すバージョン（主.副の2桁）。</summary>
public class AppInfoTests
{
    [Theory]
    [InlineData("1.0.0", "1.00")]
    [InlineData("1.0.1", "1.00")]
    [InlineData("1.2.0", "1.02")]
    [InlineData("1.12.3", "1.12")]
    [InlineData("2.0.0.0", "2.00")]
    public void FormatsMajorAndTwoDigitMinor(string version, string expected) =>
        Assert.Equal(expected, AppInfo.FormatVersion(Version.Parse(version)));

    [Fact]
    public void UnknownVersionIsQuestionMark() => Assert.Equal("?", AppInfo.FormatVersion(null));

    [Fact]
    public void DisplayNameIsKana() => Assert.Equal("かさね", AppInfo.DisplayName);
}
