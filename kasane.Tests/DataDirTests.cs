using Kasane.Model;

namespace Kasane.Tests;

/// <summary>環境変数 KASANE によるデータフォルダの上書き。</summary>
public class DataDirTests
{
    private static string Default => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "applet", "kasane");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\dir")]
    public void Resolve_UnsetEmptyOrRelative_UsesDefault(string? value) =>
        Assert.Equal(Default, HelperConfig.ResolveBaseDirectory(value));

    [Fact]
    public void Resolve_AbsolutePath_IsUsed() =>
        Assert.Equal(@"D:\kasane-data", HelperConfig.ResolveBaseDirectory(@"D:\kasane-data"));

    [Fact]
    public void Resolve_QuotedAndPadded_IsTrimmed() =>
        Assert.Equal(@"D:\kasane-data", HelperConfig.ResolveBaseDirectory("  \"D:\\kasane-data\" "));

    [Fact]
    public void TrayConfigAndProjectPath_FollowOverride()
    {
        Assert.Equal(Path.Combine(HelperConfig.BaseDirectory, "tray.json"), TrayConfig.DefaultPath);
        Assert.Equal(Path.Combine(@"D:\x", "0000ABCD", "project.json"), ProjectService.ProjectPath(0xABCD, @"D:\x"));
    }
}
