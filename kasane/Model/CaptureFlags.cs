namespace Kasane.Model;

/// <summary>
/// キャリブレーションの CAPTURE_NEXT_KEY に、疑似Shift（E0 2A / E0 36）の捕捉を許すフラグを付けるかどうか。
/// 右ShiftをE0 36で出す一部のキーボードを実測するためのもの（docs/KNOWN-ISSUES.md の 3）。
/// PrintScreen や NumLock 状態でのナビゲーションキーは E0 2A / E0 36 を前置きするので、
/// Shift のスロット以外では付けない（付けると、前置きのほうを先に捕まえてしまう）。
/// </summary>
public static class CaptureFlags
{
    /// <summary>左Shift(0x2A) または 右Shift(0x36) のスロットか（既定のスキャンコードで判定）。</summary>
    public static bool WantsFakeShift(ushort? defaultScanCode) =>
        defaultScanCode is 0x002A or 0x0036;
}
