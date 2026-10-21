using System.Runtime.InteropServices;
using Kasane.Interop;
using Kasane.Model;

namespace Kasane.Tray;

/// <summary>
/// ドライバのヘルパーイベント（CUSTOM = HLP(id) のキー）を待つ専用スレッド（P3、2026-10-08）。
///  - WAIT_HELPER_EVENT を同期で投げ続ける（保留中のIRPは、止めるときに CancelSynchronousIo で取り消す）。
///    ハンドルは、レイヤーの合成器（<see cref="AppLayerArbiter"/>）のものとは別。
///  - 待つのは <c>wanted()</c> が true の間だけ（HLP の動作が1つも無ければ待たない。ドライバの保留数には上限があるため）。
///    設定が変わったら <see cref="Poke"/> で見直す。
///  - 初回は「今より後」から受ける（<see cref="KbdLayerClient.HelperSeqLatest"/>）。押し出された分（Dropped）はログに残す。
///  - ドライバが古くて IOCTL を知らなければ（ERROR_INVALID_FUNCTION）、止めて <see cref="Unsupported"/> を true にする。
///  - 押下で古すぎるもの（<see cref="HelperEventTiming.StaleMs"/> より前。比べ方は <see cref="HelperEventTiming.IsStale"/>）は渡さない。解放は常に渡す（Hold の取りこぼしを防ぐ）。
/// </summary>
public sealed class HelperEventService : IDisposable
{
    private const int ErrorOperationAborted = 995;
    private const uint ThreadTerminate = 0x0001;

    private readonly Action<HelperEvent> _onEvent;
    private readonly Func<bool> _wanted;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stop;
    private volatile bool _unsupported;
    private int _nativeThreadId;
    private bool _disposed;

    public HelperEventService(Action<HelperEvent> onEvent, Func<bool> wanted)
    {
        _onEvent = onEvent;
        _wanted = wanted;
        _thread = new Thread(Loop) { IsBackground = true, Name = "kasane-helper-events" };
    }

    /// <summary>ドライバが WAIT_HELPER_EVENT を知らない（古いドライバ）。</summary>
    public bool Unsupported => _unsupported;

    public void Start() => _thread.Start();

    /// <summary>設定が変わった。待つかどうかを見直す（待っていて不要になったなら、取り消す）。</summary>
    public void Poke()
    {
        _wake.Set();
        if (!_wanted()) CancelPending();
    }

    private void Loop()
    {
        Volatile.Write(ref _nativeThreadId, (int)GetCurrentThreadId());
        while (!_stop && !_unsupported)
        {
            if (!_wanted())
            {
                _wake.WaitOne(1000);
                continue;
            }

            KbdLayerClient? client = null;
            try
            {
                client = KbdLayerClient.TryOpen(out _);
                if (client == null) { _wake.WaitOne(5000); continue; } // ドライバが無い・まだ開けない
                uint lastSeq = KbdLayerClient.HelperSeqLatest;
                while (!_stop && _wanted())
                {
                    var batch = client.WaitHelperEvent(lastSeq);
                    lastSeq = batch.Seq;
                    if (batch.Dropped > 0) TrayLog.Write(Loc.T($"HelperEvents: {batch.Dropped} 件が押し出されました", $"HelperEvents: {batch.Dropped} events were dropped"));
                    foreach (var e in batch.Events)
                    {
                        if (!e.IsBreak && HelperEventTiming.IsStale(Environment.TickCount64, e.TimeMs)) continue;
                        try { _onEvent(e); }
                        catch (Exception ex) { TrayLog.Write("HelperEvents: " + ex); }
                    }
                }
            }
            catch (KbdLayerException ex) when (ex.Win32Error == KbdLayerClient.ErrorInvalidFunction)
            {
                _unsupported = true;
                TrayLog.Write(Loc.T("HelperEvents: このドライバは WAIT_HELPER_EVENT に対応していません(HLP は使えません)", "HelperEvents: this driver does not support WAIT_HELPER_EVENT (HLP is unavailable)"));
            }
            catch (KbdLayerException ex) when (ex.Win32Error == ErrorOperationAborted)
            {
                // 取り消した（設定が変わった・終了）。見直す。
            }
            catch (Exception ex)
            {
                TrayLog.Write("HelperEvents: " + ex);
                _wake.WaitOne(2000);
            }
            finally
            {
                client?.Dispose();
            }
        }
    }

    // 待機中のIRPを取り消す（待っていなければ何もしない）。
    private void CancelPending()
    {
        int id = Volatile.Read(ref _nativeThreadId);
        if (id == 0) return;
        IntPtr h = OpenThread(ThreadTerminate, false, (uint)id);
        if (h == IntPtr.Zero) return;
        try { CancelSynchronousIo(h); }
        finally { CloseHandle(h); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop = true;
        _wake.Set();
        // 待機中に取り消しが間に合わない瞬間（これから待機に入るところ）があるので、止まるまで繰り返す。
        for (int i = 0; i < 20 && _thread.IsAlive; i++)
        {
            CancelPending();
            _thread.Join(100);
        }
        _wake.Dispose();
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelSynchronousIo(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
