using System.Diagnostics;
using Kasane.Tray;
using Xunit;

namespace Kasane.Tests;

/// <summary>1 ms 前後の精度の待ち（<see cref="PreciseSleeper"/>。2026-11-20）。</summary>
public class PreciseSleeperTests
{
    [Fact]
    public void Sleep_ReturnsTrue_WhenNotCancelled_AndWaitsAtLeastTheTime()
    {
        using var cancel = new ManualResetEvent(false);
        using var s = new PreciseSleeper(cancel);
        var sw = Stopwatch.StartNew();
        Assert.True(s.Sleep(20));
        Assert.True(sw.ElapsedMilliseconds >= 18, $"{sw.ElapsedMilliseconds} ms");
        Assert.True(s.Sleep(0));
    }

    [Fact]
    public void Sleep_ReturnsFalse_WhenAlreadyCancelled_AndWhenCancelledWhileWaiting()
    {
        using var cancel = new ManualResetEvent(false);
        using var s = new PreciseSleeper(cancel);
        var sw = Stopwatch.StartNew();
        // スレッドプールは、ほかのテストが並行して動くと、始まるのが遅れることがある。専用のスレッドで立てる。
        var t = new Thread(() => { Thread.Sleep(30); cancel.Set(); });
        t.Start();
        Assert.False(s.Sleep(5000));
        Assert.True(sw.ElapsedMilliseconds < 4000, $"{sw.ElapsedMilliseconds} ms");
        t.Join();
        Assert.False(s.Sleep(10));
        Assert.False(s.Sleep(0));
    }

    // 高分解能のタイマーが使える環境では、WaitOne のように約 15.6 ms に丸められない。
    [Fact]
    public void ShortWaits_AreNotRoundedUpToTheDefaultTimerTick()
    {
        using var cancel = new ManualResetEvent(false);
        using var s = new PreciseSleeper(cancel);
        if (!s.IsHighResolution) return; // 古い Windows
        var d = new List<double>();
        for (int i = 0; i < 20; i++)
        {
            var sw = Stopwatch.StartNew();
            s.Sleep(3);
            d.Add(sw.Elapsed.TotalMilliseconds);
        }
        d.Sort();
        // 他のテストが並行して動くと、遅れる回がある。いちばん短かった回で見る（既定のタイマーでは、3 ms の待ちは約 15 ms 未満にならない）。
        Assert.True(d[0] < 10, $"min {d[0]:F1} ms");
    }
}
