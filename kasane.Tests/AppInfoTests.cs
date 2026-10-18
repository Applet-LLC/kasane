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

/// <summary>バージョン情報に出す、ビルド日時・著作権・サイト（2026-10-19）。</summary>
public class AppInfoAboutTests
{
    [Fact]
    public void ParseBuildDate_ReadsUtcIsoAndConvertsToLocal()
    {
        var local = AppInfo.ParseBuildDate("2026-10-18T06:31:26Z");
        Assert.NotNull(local);
        Assert.Equal(new DateTime(2026, 10, 18, 6, 31, 26, DateTimeKind.Utc).ToLocalTime(), local);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    public void ParseBuildDate_ReturnsNullWhenUnreadable(string? text) => Assert.Null(AppInfo.ParseBuildDate(text));

    [Fact]
    public void AboutTexts()
    {
        Assert.Equal("Copyright 2026 Applet LLC", AppInfo.Copyright);
        Assert.Equal("https://appletllc.com/", AppInfo.Website);
        Assert.NotEmpty(AppInfo.BuildTimeText); // テストの実行ファイルの更新日時、または「?」
    }
}
