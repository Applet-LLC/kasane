using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KasaneLicenseSvc;

/// <summary>
/// kbdlayer ドライバにライセンスの再検証を依頼する（IOCTL_KBDLAYER_REVALIDATE_LICENSE。
/// driver/common/kbdlayer_ioctl.h）。LicenseToken を書いた・消した直後と、サービスの起動時に使う。
///
/// ドライバは起動直後の検証に失敗することがあり、そのときは自分でも再試行する（暫定有効の期間中）。
/// このIOCTLは、サービスがトークンを書き換えたことを、再起動やキーボードの抜き差しなしに反映させるためのもの。
/// ドライバ未導入・IOCTL の失敗は無視する（ログだけ残す）。
/// </summary>
public static class DriverRevalidator
{
    /// <summary>結果を確定として扱う。失敗なら、暫定有効の途中でも即座に無効にする（トークンを書いた・消した直後）。</summary>
    public const uint ModeDefinitive = 0;

    /// <summary>成功なら有効にするが、失敗しても状態を変えない（サービスの起動時。ここでの失敗でドライバ自身の再試行を潰さない）。</summary>
    public const uint ModeSoft = 1;

    private const string ControlDevicePath = @"\\.\KbdLayerControl";
    private const uint FILE_DEVICE_KBDLAYER = 0x00008020;
    private const uint FILE_WRITE_ACCESS = 0x0002;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;

    // CTL_CODE(FILE_DEVICE_KBDLAYER, 0x820, METHOD_BUFFERED, FILE_WRITE_ACCESS)
    private const uint IoctlRevalidateLicense = (FILE_DEVICE_KBDLAYER << 16) | (FILE_WRITE_ACCESS << 14) | (0x820u << 2);

    /// <summary>
    /// 再検証を依頼する。戻り値はドライバのライセンス状態（0=無効、1=暫定有効、2=有効）。
    /// 依頼できなかったときは null。
    /// </summary>
    public static uint? Revalidate(uint mode)
    {
        try
        {
            using var handle = CreateFileW(ControlDevicePath, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero,
                OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                DebugTrace.Log($"DriverRevalidator: cannot open control device: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                return null;
            }

            var input = BitConverter.GetBytes(mode);
            var output = new byte[4];
            if (!DeviceIoControl(handle, IoctlRevalidateLicense, input, (uint)input.Length,
                    output, (uint)output.Length, out uint returned, IntPtr.Zero) || returned < 4)
            {
                DebugTrace.Log($"DriverRevalidator: IOCTL failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                return null;
            }

            uint state = BitConverter.ToUInt32(output, 0);
            DebugTrace.Log($"DriverRevalidator: mode={mode} -> driver license state={state}");
            return state;
        }
        catch (Exception ex)
        {
            DebugTrace.Log($"DriverRevalidator: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode,
        byte[]? inBuffer, uint inBufferSize, byte[]? outBuffer, uint outBufferSize,
        out uint bytesReturned, IntPtr overlapped);
}
