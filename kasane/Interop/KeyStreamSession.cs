using System.Runtime.InteropServices;

namespace Kasane.Interop;

public enum KeyStreamState
{
    /// <summary>管理者権限が無い（ドライバに読み取り専用でしか開けない、またはIOCTLが拒否された）。</summary>
    AccessDenied,
    /// <summary>ドライバが WAIT_KEY_EVENT を知らない（古いドライバ）。</summary>
    Unsupported,
    /// <summary>ドライバに接続できない、待機の上限（4）を超えた、そのほかの失敗。</summary>
    Failed,
}

/// <summary>
/// ドライバの生キーイベント（WAIT_KEY_EVENT）を待ち続ける専用スレッド（2026-10-14）。設定画面の「入力確認」「同時押しを試す」が使う。
/// LL フック（WH_KEYBOARD_LL）は、キーボードレイアウト DLL の影響で、右 Shift や日本語キーの解放を取りこぼすため、ドライバから直接受ける。
///  - ハンドルは専用に開く（同期I/Oのハンドルは要求が直列化されるため、待機中にほかの操作が止まらないように）。
///  - 初回は「今より後」から受ける。止めるときは <c>CancelSynchronousIo</c> で保留中のIRPを取り消す。
///  - 失敗（権限・古いドライバ・上限・ほか）は <c>onState</c> で知らせて、スレッドを終える。呼び出し側は LL フックなどに切り替える。
///  - <c>onEvent</c> と <c>onState</c> は、このスレッドから呼ばれる（画面の操作は Dispatcher へ渡すこと）。
/// </summary>
public sealed class KeyStreamSession : IDisposable
{
    private const int ErrorOperationAborted = 995;
    private const uint ThreadTerminate = 0x0001;

    private readonly Action<KeyEvent> _onEvent;
    private readonly Action<KeyStreamState, string> _onState;
    private readonly Thread _thread;
    private volatile bool _stop;
    private int _nativeThreadId;
    private bool _disposed;

    public KeyStreamSession(Action<KeyEvent> onEvent, Action<KeyStreamState, string> onState)
    {
        _onEvent = onEvent;
        _onState = onState;
        _thread = new Thread(Loop) { IsBackground = true, Name = "kasane-key-stream" };
    }

    public void Start() => _thread.Start();

    private void Loop()
    {
        Volatile.Write(ref _nativeThreadId, (int)GetCurrentThreadId());
        KbdLayerClient? client = null;
        try
        {
            client = KbdLayerClient.TryOpen(out var error);
            if (client == null) { Report(KeyStreamState.Failed, $"ドライバに接続できません（{error}）"); return; }
            if (client.IsReadOnly) { Report(KeyStreamState.AccessDenied, "管理者権限が無いため、ドライバから直接キーを受け取れません"); return; }

            uint lastSeq = KbdLayerClient.KeySeqLatest;
            while (!_stop)
            {
                var batch = client.WaitKeyEvent(lastSeq);
                lastSeq = batch.Seq;
                foreach (var e in batch.Events)
                {
                    if (_stop) break;
                    try { _onEvent(e); }
                    catch (Exception) { /* 呼び出し側の失敗で、受信を止めない */ }
                }
            }
        }
        catch (KbdLayerException ex) when (ex.Win32Error == ErrorOperationAborted)
        {
            // 取り消した（終了）。
        }
        catch (KbdLayerException ex) when (ex.Win32Error == KbdLayerClient.ErrorAccessDenied)
        {
            Report(KeyStreamState.AccessDenied, "管理者権限が無いため、ドライバから直接キーを受け取れません");
        }
        catch (KbdLayerException ex) when (ex.Win32Error == KbdLayerClient.ErrorInvalidFunction)
        {
            Report(KeyStreamState.Unsupported, "このドライバはキーイベントの通知に対応していません（kbdlayer.sys を更新してください）");
        }
        catch (KbdLayerException ex) when (ex.Win32Error == KbdLayerClient.ErrorNoSystemResources)
        {
            Report(KeyStreamState.Failed, "同時にキーを監視できる数の上限に達しています（KeymapTool --watch-keys などを閉じてください）");
        }
        catch (Exception ex)
        {
            Report(KeyStreamState.Failed, ex.Message);
        }
        finally
        {
            client?.Dispose();
        }
    }

    private void Report(KeyStreamState state, string message)
    {
        if (_stop) return;
        try { _onState(state, message); }
        catch (Exception) { /* 呼び出し側の失敗は無視 */ }
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
        // 待機に入る直前だと取り消しが空振りするので、止まるまで繰り返す。
        for (int i = 0; i < 20 && _thread.IsAlive; i++)
        {
            CancelPending();
            _thread.Join(100);
        }
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelSynchronousIo(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
