using System.Runtime.InteropServices;
using Kasane.Interop;

namespace Kasane.Tray;

/// <summary>1台ぶんの待機用ハンドル（テストでは偽物に差し替える）。</summary>
public interface ILayerWatchHandle : IDisposable
{
    LayerState GetState();
    /// <summary>Seq が lastSeq と違うものになるまで待つ。<see cref="Cancel"/> されると KbdLayerException(995)。</summary>
    LayerState Wait(uint lastSeq);
    /// <summary>待機中なら取り消す（どのスレッドからでもよい。待機に入る直前でも効くよう、繰り返してよい）。</summary>
    void Cancel();
}

public interface ILayerWatchSource
{
    /// <summary>レイヤーマップを持つキーボードの UniqId（ドライバに接続できなければ例外）。</summary>
    IReadOnlyList<uint> ListKeyboards();
    /// <summary>待機専用のハンドルを開く（ドライバが開けなければ null）。ほかの用途のハンドルとは共有しない。</summary>
    ILayerWatchHandle? Open(uint uniqId);
}

/// <summary>
/// レイヤー状態の変化を待つ（B-1、WAIT_LAYER_CHANGE）。キーボード1台につき待機スレッドを1本持ち、状態が変わるたびに
/// <c>onChanged</c> を呼ぶ（待機スレッドから。UI スレッドへは受け側で渡す）。
///  - 台数の増減は <see cref="Sync"/>（数秒ごとに呼ぶ）で追う。外されたキーボードのスレッドは終わるので、次の Sync で片付ける。
///  - <c>wanted()</c> が false の間は待たない（ドライバ全体の保留は64本まで。IME連動などと共有）。
///  - ドライバが古ければ（ERROR_INVALID_FUNCTION）<see cref="Unsupported"/> にして、以後は何もしない。
///  - 保留の上限（1450）などの失敗は静かに諦め、次の Sync でやり直す。
/// </summary>
public sealed class LayerWatchService : IDisposable
{
    private const int ErrorOperationAborted = 995;

    private readonly ILayerWatchSource _source;
    private readonly Func<bool> _wanted;
    private readonly Action<IReadOnlyDictionary<uint, LayerState>> _onChanged;
    private readonly Action<string> _log;
    private readonly object _lock = new();
    private readonly Dictionary<uint, Watcher> _watchers = new();
    private readonly Dictionary<uint, LayerState> _states = new();
    private volatile bool _unsupported;
    private bool _disposed;

    public LayerWatchService(ILayerWatchSource source, Func<bool> wanted, Action<IReadOnlyDictionary<uint, LayerState>> onChanged, Action<string>? log = null)
    {
        _log = log ?? (_ => { });
        _source = source;
        _wanted = wanted;
        _onChanged = onChanged;
    }

    /// <summary>ドライバが WAIT_LAYER_CHANGE を知らない（古いドライバ）。</summary>
    public bool Unsupported => _unsupported;

    /// <summary>今の状態のコピー。</summary>
    public IReadOnlyDictionary<uint, LayerState> Snapshot()
    {
        lock (_lock) return new Dictionary<uint, LayerState>(_states);
    }

    /// <summary>キーボードの増減と、待つかどうかを見直す（UI スレッドのタイマーから）。</summary>
    public void Sync()
    {
        if (_disposed || _unsupported) return;

        IReadOnlyList<uint> keyboards = Array.Empty<uint>();
        if (_wanted())
        {
            try { keyboards = _source.ListKeyboards(); }
            catch (Exception ex) when (ex is KbdLayerException or InvalidOperationException or IOException) { /* ドライバに接続できない。待機は止める */ }
        }

        List<Watcher> stop;
        lock (_lock)
        {
            // 終わったもの（外された・失敗した）と、もう要らないものを外す。
            stop = _watchers.Where(p => p.Value.Finished || !keyboards.Contains(p.Key)).Select(p => p.Value).ToList();
            foreach (var w in stop) _watchers.Remove(w.UniqId);
        }
        foreach (var w in stop) w.Stop();
        bool removed = false;
        lock (_lock)
        {
            foreach (var w in stop) removed |= _states.Remove(w.UniqId);
        }
        if (removed) Raise();

        foreach (uint id in keyboards)
        {
            lock (_lock) { if (_watchers.ContainsKey(id)) continue; }
            var watcher = new Watcher(this, id);
            lock (_lock) _watchers[id] = watcher;
            watcher.Start();
        }
    }

    private void Raise()
    {
        IReadOnlyDictionary<uint, LayerState> copy;
        lock (_lock) copy = new Dictionary<uint, LayerState>(_states);
        try { _onChanged(copy); }
        catch (Exception ex) { _log("LayerWatch: " + ex); }
    }

    private void SetState(uint uniqId, LayerState state)
    {
        lock (_lock)
        {
            if (_disposed || !_watchers.ContainsKey(uniqId)) return; // 片付け中のものの通知は捨てる
            _states[uniqId] = state;
        }
        Raise();
    }

    public void Dispose()
    {
        List<Watcher> all;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            all = _watchers.Values.ToList();
            _watchers.Clear();
            _states.Clear();
        }
        foreach (var w in all) w.Stop();
    }

    private sealed class Watcher
    {
        private readonly LayerWatchService _owner;
        private readonly Thread _thread;
        private volatile bool _stop;
        private volatile bool _finished;
        private volatile ILayerWatchHandle? _handle;

        public uint UniqId { get; }
        public bool Finished => _finished;

        public Watcher(LayerWatchService owner, uint uniqId)
        {
            _owner = owner;
            UniqId = uniqId;
            _thread = new Thread(Loop) { IsBackground = true, Name = "kasane-layer-watch-" + uniqId.ToString("X8") };
        }

        public void Start() => _thread.Start();

        public void Stop()
        {
            _stop = true;
            // 待機に入る直前だと取り消しが空振りするので、止まるまで繰り返す。
            for (int i = 0; i < 20 && _thread.IsAlive; i++)
            {
                try { _handle?.Cancel(); } catch (Exception) { /* 閉じた後 */ }
                _thread.Join(100);
            }
        }

        private void Loop()
        {
            try
            {
                using var handle = _owner._source.Open(UniqId);
                if (handle == null) return; // 開けない。次の Sync でやり直す
                _handle = handle;
                var state = handle.GetState();
                _owner.SetState(UniqId, state);
                while (!_stop)
                {
                    state = handle.Wait(state.Seq);
                    _owner.SetState(UniqId, state);
                }
            }
            catch (KbdLayerException ex) when (ex.Win32Error == KbdLayerClient.ErrorInvalidFunction)
            {
                _owner._unsupported = true;
                _owner._log("LayerWatch: このドライバは WAIT_LAYER_CHANGE に対応していません（レイヤー表示は使えません）");
            }
            catch (KbdLayerException ex) when (ex.Win32Error is ErrorOperationAborted or KbdLayerClient.ErrorDeviceRemoved or KbdLayerClient.ErrorNoSystemResources)
            {
                // 取り消した・外された・保留の上限。静かに終える（次の Sync で、まだ要るならやり直す）。
            }
            catch (Exception ex)
            {
                _owner._log($"LayerWatch({UniqId:X8}): {ex}");
            }
            finally
            {
                _handle = null;
                _finished = true;
            }
        }
    }
}

/// <summary>実際のドライバを使う <see cref="ILayerWatchSource"/>。</summary>
public sealed class KbdLayerWatchSource : ILayerWatchSource
{
    public IReadOnlyList<uint> ListKeyboards()
    {
        using var client = KbdLayerClient.TryOpen(out string? error)
            ?? throw new InvalidOperationException(error ?? "ドライバを開けません");
        return client.ListDevices().Where(d => d.UniqId != 0 && d.HasLayerMap).Select(d => d.UniqId).ToList();
    }

    public ILayerWatchHandle? Open(uint uniqId)
    {
        var client = KbdLayerClient.TryOpen(out _);
        return client == null ? null : new Handle(client, uniqId);
    }

    private sealed class Handle : ILayerWatchHandle
    {
        private const uint ThreadTerminate = 0x0001;
        private readonly KbdLayerClient _client;
        private readonly uint _uniqId;
        private int _waitingThread;

        public Handle(KbdLayerClient client, uint uniqId) { _client = client; _uniqId = uniqId; }

        public LayerState GetState() => _client.GetLayerState(_uniqId);

        public LayerState Wait(uint lastSeq)
        {
            Volatile.Write(ref _waitingThread, (int)GetCurrentThreadId());
            return _client.WaitLayerChange(_uniqId, lastSeq);
        }

        public void Cancel()
        {
            int id = Volatile.Read(ref _waitingThread);
            if (id == 0) return;
            IntPtr h = OpenThread(ThreadTerminate, false, (uint)id);
            if (h == IntPtr.Zero) return;
            try { CancelSynchronousIo(h); }
            finally { CloseHandle(h); }
        }

        public void Dispose() => _client.Dispose();

        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelSynchronousIo(IntPtr thread);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
