using Kasane.Keycodes;
using Kasane.Layout;

namespace Kasane.Tests;

/// <summary>
/// 同梱のレイアウト（kasane/Layouts/kle-*.json。インストール先の Layouts\ に入る）すべてに共通の点検。
/// ファイルを足したときに、読み込めない・キーコードが決まらないものが入らないようにする。
/// </summary>
public class BundledLayoutsTests
{
    public static IEnumerable<object[]> Files() =>
        KeyboardLayout.EnumerateBundledLayoutFiles().Select(f => new object[] { Path.GetFileName(f) });

    private static string PathOf(string fileName) => Path.Combine(AppContext.BaseDirectory, "Layouts", fileName);

    [Fact]
    public void BundledLayouts_IncludeTheThinkPadJapaneseKeyboard()
    {
        Assert.Contains(KeyboardLayout.EnumerateBundledLayoutFiles(), f => Path.GetFileName(f) == "kle-Thinkpad_JP.json");
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void EveryKey_HasAKnownDefaultKeycode_AndNoKeycodeIsUsedTwice(string fileName)
    {
        var layout = KeyboardLayout.Load(PathOf(fileName));
        Assert.NotEmpty(layout.Slots);
        var unknown = layout.Slots
            .Where(s => string.IsNullOrEmpty(s.DefaultKeycode) || !KeycodeTable.TryResolve(s.DefaultKeycode, out _))
            .Select(s => $"#{s.Index + 1} {s.PrintedLabel}: {s.DefaultKeycode}")
            .ToList();
        Assert.True(unknown.Count == 0, fileName + ": " + string.Join(" | ", unknown));
        var dup = layout.Slots.GroupBy(s => s.DefaultKeycode).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dup.Count == 0, fileName + ": キーコードが重なっている: " + string.Join(", ", dup));
    }
}

/// <summary>キートップに出す短い名前（日本語の表示）。</summary>
public class ShortNameTests
{
    [Fact]
    public void RoKey_IsShownAsUnderscore_NotAsKana()
    {
        // 半角の「\」は、Yu Gothic UI で「¥」に見え、KC_JYEN と区別できない。そのため、「ろ」ではなく「_」にしている。
        Kasane.Model.Loc.Initialize(Kasane.Model.UiLanguage.Japanese);
        Assert.Equal("_", Kasane.Keycodes.KeycodeCatalog.ShortName("KC_RO"));
        Assert.NotEqual(Kasane.Keycodes.KeycodeCatalog.ShortName("KC_JYEN"), Kasane.Keycodes.KeycodeCatalog.ShortName("KC_RO"));
    }
}
