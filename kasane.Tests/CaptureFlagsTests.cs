using Kasane.Interop;
using Kasane.Model;
using Xunit;

namespace Kasane.Tests;

/// <summary>疑似Shift（E0 36）捕捉フラグ: Shift のスロットだけに付け、旧ドライバとの互換のため、不要なときは8バイトのまま送る。</summary>
public class CaptureFlagsTests
{
    [Theory]
    [InlineData((ushort)0x002A, true)]
    [InlineData((ushort)0x0036, true)]
    [InlineData((ushort)0xE037, false)] // PrintScreen
    [InlineData((ushort)0x001E, false)]
    public void WantsFakeShift_OnlyForShiftSlots(ushort sc, bool expected) =>
        Assert.Equal(expected, CaptureFlags.WantsFakeShift(sc));

    [Fact]
    public void WantsFakeShift_NoDefault_IsFalse() => Assert.False(CaptureFlags.WantsFakeShift(null));

    [Fact]
    public void Request_WithoutFlag_IsLegacy8Bytes()
    {
        var data = KbdLayerClient.BuildCaptureRequest(0x55F70000, 500, false);
        Assert.Equal(8, data.Length);
        Assert.Equal(500u, BitConverter.ToUInt32(data, 4));
    }

    [Fact]
    public void Request_WithFlag_Has12BytesAndFlag()
    {
        var data = KbdLayerClient.BuildCaptureRequest(0x55F70000, 500, true);
        Assert.Equal(12, data.Length);
        Assert.Equal(KbdLayerClient.CaptureFlagAllowFakeShift, BitConverter.ToUInt32(data, 8));
    }
}
