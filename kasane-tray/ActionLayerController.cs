using System.Collections.Concurrent;
using Kasane.Interop;
using Kasane.Model;

namespace Kasane.Tray;

/// <summary>
/// ヘルパー動作（AppLayer）が立てるアプリ管理レイヤー（P3、2026-10-08）。アプリ管理レイヤーのハンドルは IME 連動のスレッドだけで
/// 使うので、動作の実行スレッドは <see cref="Request"/> で依頼を積み、そのスレッドの <see cref="Tick"/> が反映する。
/// 入力源の名前は "action"（<see cref="AppLayerArbiter"/> が、IME 連動・前面のアプリの規則と OR で合わせる）。
///  - On / Off / Toggle は、そのレイヤーを立てる / 下ろす / 反転する。Hold は、動作の側が押下で On、解放で Off を依頼する。
///  - 一時停止中・セッションが非アクティブのときは、全部下ろす（依頼も捨てる）。
///  - 立てている間は、定期的に実際の状態を確かめて立て直す（キーボードの再接続でアプリ管理レイヤーが消えた場合）。
/// </summary>
public sealed class ActionLayerController
{
    public const string SourceName = "action";
    public const long VerifyIntervalMs = 1000;

    private readonly AppLayerArbiter _arbiter;
    private readonly IDriverAccess _devices;
    private readonly Func<long> _nowMs;
    private readonly ConcurrentQueue<LayerRequest> _queue = new();
    private readonly HashSet<(uint UniqId, int Layer)> _held = new();
    private long _lastVerify = long.MinValue;

    public ActionLayerController(AppLayerArbiter arbiter, IDriverAccess devices, Func<long>? nowMs = null)
    {
        _arbiter = arbiter;
        _devices = devices;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>依頼を積む（どのスレッドからでもよい）。反映は <see cref="Tick"/>。</summary>
    public void Request(LayerRequest request) => _queue.Enqueue(request);

    public bool HasPending => !_queue.IsEmpty;

    /// <summary>今立てているビット（テストと診断用）。</summary>
    public IReadOnlyCollection<(uint UniqId, int Layer)> Held => _held;

    /// <summary>依頼を反映する。active は「セッションがアクティブで、一時停止中でない」こと。</summary>
    public void Tick(bool active)
    {
        if (!active)
        {
            while (_queue.TryDequeue(out _)) { }
            if (_held.Count > 0)
            {
                _held.Clear();
                try { _arbiter.ClearSource(SourceName); }
                catch (Exception ex) when (IsDriverFailure(ex)) { }
            }
            return;
        }

        bool changed = false;
        while (_queue.TryDequeue(out var request))
        {
            foreach (uint id in Targets(request))
            {
                foreach (int layer in request.Layers)
                {
                    var bit = (id, layer);
                    switch (request.Op)
                    {
                        case LayerOp.On or LayerOp.Hold: changed |= _held.Add(bit); break;
                        case LayerOp.Off: changed |= _held.Remove(bit); break;
                        case LayerOp.Toggle: changed = true; if (!_held.Remove(bit)) _held.Add(bit); break;
                    }
                }
            }
        }

        long now = _nowMs();
        try
        {
            if (changed) _arbiter.SetSource(SourceName, _held);
            else if (_held.Count > 0 && (now - _lastVerify >= VerifyIntervalMs || _lastVerify == long.MinValue)) _arbiter.Verify();
        }
        catch (Exception ex) when (IsDriverFailure(ex)) { _lastVerify = long.MinValue; return; }
        if (changed || _held.Count > 0) _lastVerify = now;
    }

    /// <summary>次に Tick を呼んでほしいまでの時間。依頼があればすぐ、立てている間は確かめる間隔、なければ null。</summary>
    public long? DueInMs => HasPending ? 0 : _held.Count > 0 ? VerifyIntervalMs : null;

    public void Shutdown()
    {
        while (_queue.TryDequeue(out _)) { }
        _held.Clear();
        try { _arbiter.ClearSource(SourceName); }
        catch (Exception ex) when (IsDriverFailure(ex)) { }
    }

    // 対象のキーボード: UniqId が分かればそれ。分からなければ、ハッシュ（上位16bit）が合うキーボード。
    // それも分からなければ、レイヤーマップのある全キーボード。
    private IEnumerable<uint> Targets(LayerRequest r)
    {
        if (r.UniqId != 0) return new[] { r.UniqId };
        try
        {
            var devices = _devices.ListDevices().Where(d => d.UniqId != 0 && d.HasLayerMap);
            if (r.DeviceHash16 != 0) devices = devices.Where(d => (ushort)(d.UniqId >> 16) == r.DeviceHash16);
            return devices.Select(d => d.UniqId).ToList();
        }
        catch (Exception ex) when (IsDriverFailure(ex)) { return Array.Empty<uint>(); }
    }

    private static bool IsDriverFailure(Exception ex) => ex is KbdLayerException or InvalidOperationException or IOException;
}
