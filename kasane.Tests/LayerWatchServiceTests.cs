using Kasane.Interop;
using Kasane.Tray;

namespace Kasane.Tests;

public class LayerWatchServiceTests
{
    private sealed class FakeSource : ILayerWatchSource
    {
        public readonly object Gate = new();
        public List<uint> Keyboards = new();
        public Exception? ListError;
        public Exception? WaitError; // Wait の最初の呼び出しで投げる
        public readonly Dictionary<uint, FakeHandle> Handles = new();
        public int OpenCount;

        public IReadOnlyList<uint> ListKeyboards()
        {
            lock (Gate) { if (ListError != null) throw ListError; return Keyboards.ToList(); }
        }

        public ILayerWatchHandle? Open(uint uniqId)
        {
            lock (Gate)
            {
                OpenCount++;
                var h = new FakeHandle(this);
                Handles[uniqId] = h;
                return h;
            }
        }
    }

    private sealed class FakeHandle : ILayerWatchHandle
    {
        private readonly FakeSource _source;
        private readonly SemaphoreSlim _signal = new(0);
        private LayerState _state = new(1, 0, 0, 0, 1, true, false);
        private volatile bool _cancelled, _removed;
        public bool Disposed;

        public FakeHandle(FakeSource source) { _source = source; }

        public void Change(uint effective)
        {
            lock (this) _state = _state with { Seq = _state.Seq + 1, EffectiveLayerMask = effective };
            _signal.Release();
        }

        public void Remove() { _removed = true; _signal.Release(); }

        public LayerState GetState() { lock (this) return _state; }

        public LayerState Wait(uint lastSeq)
        {
            if (_source.WaitError != null) throw _source.WaitError;
            while (true)
            {
                if (_cancelled) throw new KbdLayerException("wait", 995);
                if (_removed) throw new KbdLayerException("wait", KbdLayerClient.ErrorDeviceRemoved);
                lock (this) { if (_state.Seq != lastSeq) return _state; }
                _signal.Wait(50);
            }
        }

        public void Cancel() => _cancelled = true;
        public void Dispose() => Disposed = true;
    }

    private sealed class Recorder
    {
        public readonly object Gate = new();
        public IReadOnlyDictionary<uint, LayerState> Last = new Dictionary<uint, LayerState>();
        public void On(IReadOnlyDictionary<uint, LayerState> s) { lock (Gate) Last = s; }
        public IReadOnlyDictionary<uint, LayerState> Get() { lock (Gate) return Last; }
    }

    private static void WaitFor(Func<bool> cond)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!cond()) { Assert.True(DateTime.UtcNow < end, "タイムアウト"); Thread.Sleep(10); }
    }

    [Fact]
    public void ReportsInitialStateAndChanges()
    {
        var src = new FakeSource { Keyboards = { 1 } };
        var rec = new Recorder();
        using var svc = new LayerWatchService(src, () => true, rec.On);
        svc.Sync();
        WaitFor(() => rec.Get().ContainsKey(1));
        src.Handles[1].Change(0b11);
        WaitFor(() => rec.Get()[1].EffectiveLayerMask == 0b11);
        Assert.Equal(0b11u, svc.Snapshot()[1].EffectiveLayerMask);
    }

    [Fact]
    public void RemovedKeyboard_IsDroppedOnNextSync()
    {
        var src = new FakeSource { Keyboards = { 1, 2 } };
        var rec = new Recorder();
        using var svc = new LayerWatchService(src, () => true, rec.On);
        svc.Sync();
        WaitFor(() => rec.Get().Count == 2);
        lock (src.Gate) src.Keyboards.Remove(2);
        src.Handles[2].Remove();
        svc.Sync();
        WaitFor(() => rec.Get().Count == 1);
        Assert.True(rec.Get().ContainsKey(1));
        Assert.True(src.Handles[2].Disposed);
    }

    [Fact]
    public void NotWanted_StopsWatching()
    {
        var src = new FakeSource { Keyboards = { 1 } };
        var rec = new Recorder();
        bool wanted = true;
        using var svc = new LayerWatchService(src, () => wanted, rec.On);
        svc.Sync();
        WaitFor(() => rec.Get().Count == 1);
        wanted = false;
        svc.Sync();
        WaitFor(() => rec.Get().Count == 0);
        Assert.True(src.Handles[1].Disposed);
        wanted = true;
        svc.Sync();
        WaitFor(() => rec.Get().Count == 1);
    }

    [Fact]
    public void OldDriver_MarksUnsupported_AndStops()
    {
        var src = new FakeSource { Keyboards = { 1 }, WaitError = new KbdLayerException("wait", KbdLayerClient.ErrorInvalidFunction) };
        var rec = new Recorder();
        var logs = new List<string>();
        using var svc = new LayerWatchService(src, () => true, rec.On, logs.Add);
        svc.Sync();
        WaitFor(() => svc.Unsupported);
        int opened = src.OpenCount;
        svc.Sync();
        Assert.Equal(opened, src.OpenCount); // 以後は開き直さない
    }

    [Fact]
    public void DriverUnavailable_DoesNotThrow()
    {
        var src = new FakeSource { ListError = new InvalidOperationException("no driver") };
        using var svc = new LayerWatchService(src, () => true, _ => { });
        svc.Sync();
        Assert.Empty(svc.Snapshot());
    }

    [Fact]
    public void Dispose_CancelsWaiters()
    {
        var src = new FakeSource { Keyboards = { 1 } };
        var rec = new Recorder();
        var svc = new LayerWatchService(src, () => true, rec.On);
        svc.Sync();
        WaitFor(() => rec.Get().Count == 1);
        svc.Dispose();
        Assert.True(src.Handles[1].Disposed);
    }
}
