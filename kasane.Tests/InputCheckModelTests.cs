using Kasane.Model;
using Xunit;

namespace Kasane.Tests;

public class InputCheckModelTests
{
    private static InputCheckModel Make() => new(new Dictionary<ushort, int>
    {
        [0x001E] = 0,   // A
        [0x0030] = 1,   // B
        [0xE01D] = 2,   // 右Ctrl
    });

    [Fact]
    public void Press_MarksDownAndChecked_ReleaseClearsOnlyDown()
    {
        var m = Make();
        var r = m.OnKey(0x001E, true);
        Assert.Equal(0, r.Slot);
        Assert.True(r.Changed);
        Assert.True(m.IsDown(0));
        Assert.True(m.IsChecked(0));

        r = m.OnKey(0x001E, false);
        Assert.Equal(0, r.Slot);
        Assert.True(r.Changed);
        Assert.False(m.IsDown(0));
        Assert.True(m.IsChecked(0)); // 一度押したキーは、離しても確認済み
        Assert.Equal(1, m.CheckedCount);
    }

    [Fact]
    public void AutoRepeat_DoesNotChange()
    {
        var m = Make();
        m.OnKey(0x001E, true);
        Assert.False(m.OnKey(0x001E, true).Changed);
    }

    [Fact]
    public void ReleaseWithoutPress_DoesNotChange()
    {
        var m = Make();
        Assert.False(m.OnKey(0x001E, false).Changed);
        Assert.Equal(0, m.CheckedCount);
    }

    [Fact]
    public void UnknownScanCode_HasNoSlotAndChangesNothing()
    {
        var m = Make();
        var r = m.OnKey(0x0099, true);
        Assert.Null(r.Slot);
        Assert.False(r.Changed);
        Assert.Equal(0x0099, r.Scan);
        Assert.Equal(0, m.CheckedCount);
    }

    [Fact]
    public void ExtendedKeysAreDistinctFromPlainOnes()
    {
        var m = Make();
        Assert.Null(m.OnKey(0x001D, true).Slot);      // 左Ctrl は対応が無い
        Assert.Equal(2, m.OnKey(0xE01D, true).Slot);  // 右Ctrl
    }

    [Fact]
    public void ReleaseAll_ClearsDownAndReturnsThem_ButKeepsChecked()
    {
        var m = Make();
        m.OnKey(0x001E, true);
        m.OnKey(0x0030, true);
        var released = m.ReleaseAll();
        Assert.Equal(new[] { 0, 1 }, released.OrderBy(x => x));
        Assert.False(m.IsDown(0));
        Assert.Equal(2, m.CheckedCount);
    }

    [Fact]
    public void ResetChecked_ClearsCheckedAndReturnsThem()
    {
        var m = Make();
        m.OnKey(0x001E, true);
        m.OnKey(0x001E, false);
        var was = m.ResetChecked();
        Assert.Equal(new[] { 0 }, was);
        Assert.Equal(0, m.CheckedCount);
    }

    [Fact]
    public void SetMap_ChangesWhichKeyAScanCodeBelongsTo_KeepingState()
    {
        var m = Make();
        m.OnKey(0x001E, true);
        m.SetMap(new Dictionary<ushort, int> { [0x0099] = 0 }); // 実測で、スロット0のスキャンコードが変わった
        Assert.True(m.IsChecked(0));
        Assert.Null(m.OnKey(0x001E, true).Slot);
        Assert.Equal(0, m.OnKey(0x0099, true).Slot);
    }
}
