using Kasane.Keycodes;
using Kasane.Views;

namespace Kasane.Tests;

/// <summary>「キーを押して入力」: WM_KEYDOWN の lParam からキーコード名を作る部分。</summary>
public class KeycodeCaptureTests
{
    private static long LParam(int scan, bool extended) => ((long)scan << 16) | (extended ? 1L << 24 : 0);

    [Theory]
    [InlineData(0x1E, false, 0x001E)] // A
    [InlineData(0x1D, false, 0x001D)] // 左Ctrl
    [InlineData(0x1D, true, 0xE01D)]  // 右Ctrl
    [InlineData(0x1C, true, 0xE01C)]  // テンキーの Enter（普通の Enter と区別できる）
    [InlineData(0x1C, false, 0x001C)] // Enter
    [InlineData(0x7B, false, 0x007B)] // 無変換（JIS）
    [InlineData(0x79, false, 0x0079)] // 変換（JIS）
    public void TokenFromKeyMessage_RoundTripsToTheSameScanCode(int scan, bool extended, int expected)
    {
        string? token = KeycodeCapture.TokenFromKeyMessage(LParam(scan, extended));
        Assert.NotNull(token);
        Assert.True(KeycodeTable.TryResolveBaseKey(token!, out ushort sc));
        Assert.Equal((ushort)expected, sc);
    }

    [Fact]
    public void TokenFromKeyMessage_UsesKeyNames() =>
        Assert.Equal("KC_A", KeycodeCapture.TokenFromKeyMessage(LParam(0x1E, false)));

    [Fact]
    public void TokenFromKeyMessage_IgnoresFakeShiftOfNumLockOff() =>
        Assert.Null(KeycodeCapture.TokenFromKeyMessage(LParam(0x2A, true)));

    [Fact]
    public void TokenFromKeyMessage_ZeroScanCode_IsIgnored() =>
        Assert.Null(KeycodeCapture.TokenFromKeyMessage(LParam(0, false)));
}
