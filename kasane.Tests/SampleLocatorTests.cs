using Kasane.Model;
using Kasane.Model.Portable;

namespace Kasane.Tests;

public class SampleLocatorTests
{
    [Fact]
    public void Find_InstalledLayout_UsesSamplesBesideTheEditorFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), "kasane-sl-" + Guid.NewGuid().ToString("N"));
        try
        {
            string gui = Path.Combine(root, "keymap-gui");
            string dir = Path.Combine(root, "samples", "emacs");
            Directory.CreateDirectory(gui);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, SampleLocator.EmacsFile), "{}");

            Assert.Equal(Path.Combine(dir, SampleLocator.EmacsFile), SampleLocator.FindEmacs(gui));
            Assert.Null(SampleLocator.Find(gui, "nothing", "x.json"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Find_DevelopmentLayout_FindsTheRepositorySample()
    {
        // テストの実行ファイルは、リポジトリの下（bin\...）にある。
        string? path = SampleLocator.FindEmacs(AppContext.BaseDirectory);
        Assert.NotNull(path);
        Assert.True(PortableIO.TryRead(path!, out var file, out _));
        Assert.NotNull(file);
    }
}
