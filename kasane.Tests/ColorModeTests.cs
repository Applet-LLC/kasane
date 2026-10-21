using System.IO;
using System.Text.Json;
using Kasane.Model;
using Xunit;

namespace Kasane.Tests;

public class ColorModeTests
{
    [Theory]
    [InlineData(null, ColorMode.Light)]
    [InlineData("", ColorMode.Light)]
    [InlineData("Light", ColorMode.Light)]
    [InlineData("Dark", ColorMode.Dark)]
    [InlineData("  dark ", ColorMode.Dark)]
    [InlineData("unknown", ColorMode.Light)]
    [InlineData("7", ColorMode.Light)] // 数字は名前ではない（範囲外の値を作らない）
    public void Parse(string? text, ColorMode expected) => Assert.Equal(expected, ColorModes.Parse(text));

    [Fact]
    public void TrayConfig_DefaultIsLight()
    {
        var c = new TrayConfig();
        Assert.Equal("Light", c.ColorModeName);
        Assert.Equal(ColorMode.Light, c.ColorModeValue);
    }

    [Fact]
    public void TrayConfig_SaveAndLoad_KeepsColorMode_UnderTheColorModeKey_AndNotTheDerivedProperty()
    {
        string path = Path.Combine(Path.GetTempPath(), "kasane-test-color-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            new TrayConfig { ColorModeName = "Dark" }.Save(path);
            Assert.Equal(ColorMode.Dark, TrayConfig.Load(path).ColorModeValue);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("Dark", doc.RootElement.GetProperty("ColorMode").GetString());
            Assert.False(doc.RootElement.TryGetProperty("ColorModeValue", out _));
            Assert.False(doc.RootElement.TryGetProperty("ColorModeName", out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TrayConfig_OldFileWithoutColorMode_IsLight()
    {
        string path = Path.Combine(Path.GetTempPath(), "kasane-test-color-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{ \"ImeLink\": true }");
            Assert.Equal(ColorMode.Light, TrayConfig.Load(path).ColorModeValue);
        }
        finally { File.Delete(path); }
    }
}

public class ColorModeStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kasane-colorstore-" + Guid.NewGuid().ToString("N"));

    public ColorModeStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Write(string json)
    {
        string path = Path.Combine(_dir, "tray.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Read_Dark() => Assert.Equal(ColorMode.Dark, ColorModeStore.Read(Write("{ \"ImeLink\": true, \"ColorMode\": \"Dark\" }")));

    [Theory]
    [InlineData("{ }")]
    [InlineData("{ \"ColorMode\": 5 }")]       // 文字列ではない
    [InlineData("{ \"ColorMode\": \"Purple\" }")]
    [InlineData("[ 1, 2 ]")]                    // オブジェクトではない
    [InlineData("not json")]
    public void Read_FallsBackToLight_WhenMissingOrInvalid(string json) =>
        Assert.Equal(ColorMode.Light, ColorModeStore.Read(Write(json)));

    [Fact]
    public void Read_MissingFile_IsLight() => Assert.Equal(ColorMode.Light, ColorModeStore.Read(Path.Combine(_dir, "none.json")));

    [Fact]
    public void Read_AgreesWithTrayConfig()
    {
        string path = Path.Combine(_dir, "tray.json");
        new TrayConfig { ColorModeName = "Dark" }.Save(path);
        Assert.Equal(TrayConfig.Load(path).ColorModeValue, ColorModeStore.Read(path));
    }
}
