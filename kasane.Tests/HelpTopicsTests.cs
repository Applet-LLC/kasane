using Kasane.Model;
using Xunit;

namespace Kasane.Tests;

/// <summary>
/// 画面の「?」と F1 が開くマニュアルの節（2026-11-08、UI の見直し）。対応表のすべての章とアンカーが、作り直したマニュアル（docs/manual/ja）に
/// 実際にあること（リンク切れを防ぐ）と、節へ飛ぶ中継の HTML の中身。
/// </summary>
public class HelpTopicsTests
{
    private static string ManualDir()
    {
        string? index = ManualLocator.Find(AppContext.BaseDirectory); // 開発時は kasane.sln の隣の docs\manual\ja\index.html
        Assert.NotNull(index);
        return Path.GetDirectoryName(index)!;
    }

    public static IEnumerable<object[]> Topics() => HelpTopics.All.Select(kv => new object[] { kv.Key });

    [Theory]
    [MemberData(nameof(Topics))]
    public void EverySection_ExistsInTheBuiltManual(string topic)
    {
        var s = HelpTopics.All[topic];
        string page = Path.Combine(ManualDir(), s.Page);
        Assert.True(File.Exists(page), $"{topic}: {s.Page} がありません");
        if (s.Anchor == null) return;
        string html = File.ReadAllText(page);
        int count = System.Text.RegularExpressions.Regex.Matches(html, $"id=\"{System.Text.RegularExpressions.Regex.Escape(s.Anchor)}\"").Count;
        Assert.True(count == 1, $"{topic}: {s.Page} の id=\"{s.Anchor}\" が {count} 個（1 個であること。docs/manual/build-manual.ps1 で作り直したか）");
    }

    /// <summary>英語版（docs/manual/en）は、章ごとに少しずつ訳している。訳した章は、日本語版と同じアンカー（<c>&lt;a id="…"&gt;&lt;/a&gt;</c>）を、すべて持つこと。</summary>
    [Fact]
    public void EnglishPages_HaveTheSameAnchorsAsJapanese()
    {
        string ja = ManualDir();
        string en = Path.Combine(Path.GetDirectoryName(ja)!, "en");
        if (!Directory.Exists(en)) return;
        var anchor = new System.Text.RegularExpressions.Regex("<a id=\"([^\"]+)\"></a>");
        foreach (string enPage in Directory.GetFiles(en, "*.html"))
        {
            string jaPage = Path.Combine(ja, Path.GetFileName(enPage));
            Assert.True(File.Exists(jaPage), $"{Path.GetFileName(enPage)}: 日本語版にない章");
            var jaIds = anchor.Matches(File.ReadAllText(jaPage)).Select(m => m.Groups[1].Value).OrderBy(x => x).ToArray();
            var enIds = anchor.Matches(File.ReadAllText(enPage)).Select(m => m.Groups[1].Value).OrderBy(x => x).ToArray();
            Assert.True(jaIds.SequenceEqual(enIds), $"{Path.GetFileName(enPage)}: アンカーが日本語版と違う（日本語版: {string.Join(",", jaIds)} / 英語版: {string.Join(",", enIds)}）");
        }
    }

    [Fact]
    public void UnknownTopic_FallsBackToTheEditor()
    {
        Assert.Equal(HelpTopics.All[HelpTopics.Editor], HelpTopics.For("no-such-topic"));
    }

    [Fact]
    public void SectionUrl_EncodesSpacesAndJapanese()
    {
        string url = ManualLocator.SectionUrl(@"C:\Program Files\かさね\manual\ja\08-key-assign.html", "combo-keyoverride");
        Assert.StartsWith("file:///C:/Program%20Files/", url);
        Assert.Contains("%E3%81%8B%E3%81%95%E3%81%AD", url); // 「かさね」
        Assert.EndsWith("/manual/ja/08-key-assign.html#combo-keyoverride", url);
    }

    [Fact]
    public void RelayHtml_RefreshesToTheSection()
    {
        string html = ManualLocator.BuildRelayHtml(@"C:\a b\08-key-assign.html", "mod-tap");
        Assert.Contains("<meta http-equiv=\"refresh\" content=\"0;url=file:///C:/a%20b/08-key-assign.html#mod-tap\">", html);
        Assert.Contains("<a href=\"file:///C:/a%20b/08-key-assign.html#mod-tap\">", html);
        Assert.DoesNotContain("<script", html);
    }
}
