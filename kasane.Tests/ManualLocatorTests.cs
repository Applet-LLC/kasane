using Kasane.Model;

namespace Kasane.Tests;

/// <summary>マニュアルの場所の探し方（ManualLocator）。インストール先と、開発時の並びを、一時フォルダで確かめる。</summary>
public class ManualLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kasane-manual-" + Guid.NewGuid().ToString("N"));

    public ManualLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* 後始末の失敗は無視 */ }
    }

    private string MakeFile(params string[] parts)
    {
        string path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<html></html>");
        return path;
    }

    [Theory]
    [InlineData("keymap-gui")]   // エディタ
    [InlineData("tray")]         // トレイ
    public void Installed_FindsManualNextToTheExeFolder(string exeFolder)
    {
        string index = MakeFile("manual", "ja", "index.html");
        string baseDir = Path.Combine(_root, exeFolder);
        Directory.CreateDirectory(baseDir);

        Assert.Equal(index, ManualLocator.Find(baseDir));
    }

    [Fact]
    public void Development_FindsDocsManualFromTheSolutionFolder()
    {
        MakeFile("kasane.sln");
        string index = MakeFile("docs", "manual", "ja", "index.html");
        string baseDir = Path.Combine(_root, "kasane", "bin", "Debug", "net8.0-windows", "win-x64");
        Directory.CreateDirectory(baseDir);

        Assert.Equal(index, ManualLocator.Find(baseDir));
    }

    [Fact]
    public void Installed_WinsOverDevelopmentLayout()
    {
        MakeFile("kasane.sln");
        MakeFile("docs", "manual", "ja", "index.html");
        string installed = MakeFile("kasane", "manual", "ja", "index.html");
        string baseDir = Path.Combine(_root, "kasane", "tray");
        Directory.CreateDirectory(baseDir);

        Assert.Equal(installed, ManualLocator.Find(baseDir));
    }

    [Fact]
    public void English_PrefersTheEnglishManual()
    {
        MakeFile("manual", "ja", "index.html");
        string en = MakeFile("manual", "en", "index.html");
        string baseDir = Path.Combine(_root, "tray");
        Directory.CreateDirectory(baseDir);

        Assert.Equal(en, ManualLocator.Find(baseDir, english: true));
        Assert.Equal(Path.Combine(_root, "manual", "ja", "index.html"), ManualLocator.Find(baseDir, english: false));
    }

    [Fact]
    public void English_FallsBackToJapanese_WhenThereIsNoEnglishManual()
    {
        string ja = MakeFile("manual", "ja", "index.html");
        string baseDir = Path.Combine(_root, "tray");
        Directory.CreateDirectory(baseDir);

        Assert.Equal(ja, ManualLocator.Find(baseDir, english: true));
    }

    [Fact]
    public void FindPage_UsesTheEnglishChapter_AndFallsBackToJapaneseForUntranslatedOnes()
    {
        MakeFile("manual", "ja", "index.html");
        string jaSettings = MakeFile("manual", "ja", "13-settings.html");
        MakeFile("manual", "ja", "05-editor-basics.html");
        MakeFile("manual", "en", "index.html");
        string enEditor = MakeFile("manual", "en", "05-editor-basics.html");
        string baseDir = Path.Combine(_root, "keymap-gui");
        Directory.CreateDirectory(baseDir);

        Assert.Equal(enEditor, ManualLocator.FindPage(baseDir, "05-editor-basics.html", english: true));
        Assert.Equal(jaSettings, ManualLocator.FindPage(baseDir, "13-settings.html", english: true)); // 英語版に無い章
        Assert.Equal(Path.Combine(_root, "manual", "ja", "05-editor-basics.html"), ManualLocator.FindPage(baseDir, "05-editor-basics.html", english: false));
    }

    [Fact]
    public void FindPage_MissingChapter_OpensTheIndex()
    {
        MakeFile("manual", "ja", "index.html");
        string en = MakeFile("manual", "en", "index.html");
        string baseDir = Path.Combine(_root, "tray");
        Directory.CreateDirectory(baseDir);

        Assert.Equal(en, ManualLocator.FindPage(baseDir, "99-none.html", english: true));
    }

    [Fact]
    public void Missing_ReturnsNull_AndOpenReportsAnError()
    {
        string baseDir = Path.Combine(_root, "tray");
        Directory.CreateDirectory(baseDir);

        Assert.Null(ManualLocator.Find(baseDir));
        Assert.NotNull(ManualLocator.Open(baseDir)); // 開かずに、理由を返す
    }
}
