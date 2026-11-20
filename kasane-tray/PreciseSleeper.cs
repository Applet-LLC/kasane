using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Kasane.Tray;

/// <summary>
/// 1 ms 前後の精度で待つ（2026-11-20）。<c>WaitHandle.WaitOne(ms)</c> は、Windows の既定のタイマーの刻み（約 15.6 ms）に丸められる
/// （実測: 1〜15 ms の待ちが約 15.6 ms、30 ms の待ちが約 31 ms、35 ms の待ちが約 47 ms）。文字入力の間隔の揺らぎ（1〜10 ms）が
/// 意味を持つよう、高分解能のタイマー（CREATE_WAITABLE_TIMER_HIGH_RESOLUTION。Windows 10 1803 以降）で待つ。
/// 作れなければ（古い Windows）、<c>WaitOne</c> に戻る。<c>timeBeginPeriod</c> は使わない（Windows 11 では、ウィンドウの無いプロセスの要求は
/// 無視されることがあり、システム全体の電力にも響くため）。1 つのスレッドから使うこと。
/// </summary>
internal sealed class PreciseSleeper : IDisposable
{
    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2, TIMER_ALL_ACCESS = 0x1F0003;

    private readonly WaitHandle _cancel;
    private readonly ManualResetEvent? _timer;

    /// <param name="cancel">この待ちを打ち切るハンドル（止めるよう頼まれたとき、立つ）。</param>
    public PreciseSleeper(WaitHandle cancel)
    {
        _cancel = cancel;
        try
        {
            IntPtr h = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            if (h != IntPtr.Zero) _timer = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(h, ownsHandle: true) };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { /* WaitOne に戻る */ }
    }

    /// <summary>高分解能のタイマーを使えるか（診断用）。</summary>
    public bool IsHighResolution => _timer != null;

    /// <summary>待つ。打ち切られたら false。</summary>
    public bool Sleep(int milliseconds)
    {
        if (milliseconds <= 0) return !_cancel.WaitOne(0);
        if (_timer == null) return !_cancel.WaitOne(milliseconds);
        long due = -(long)milliseconds * 10_000; // 100 ns 単位。負の値は相対時間
        if (!SetWaitableTimer(_timer.SafeWaitHandle.DangerousGetHandle(), ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            return !_cancel.WaitOne(milliseconds);
        return WaitHandle.WaitAny(new[] { (WaitHandle)_timer, _cancel }) == 0;
    }

    public void Dispose() => _timer?.Dispose();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, [MarshalAs(UnmanagedType.Bool)] bool resume);
}
