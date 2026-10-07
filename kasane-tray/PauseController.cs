using Kasane.Interop;

namespace Kasane.Tray;

/// <summary>トレイが使うドライバの操作（テストでは偽物に差し替える）。失敗は例外で知らせる。</summary>
public interface IDriverAccess
{
    /// <summary>ドライバが認識しているキーボードの一覧（ドライバに接続できなければ例外）。</summary>
    List<DeviceInfo> ListDevices();
    /// <summary>キーボードのレイヤーマップのON/OFF（SET_LAYERMAP_ENABLED。読み取り側のIOCTLなので昇格は要らない）。</summary>
    void SetEnabled(uint uniqId, bool enabled);
}

/// <summary>一時停止で止めたキーボードの記録（トレイを終了しても残す）。</summary>
public interface IPauseStateStore
{
    IReadOnlyList<uint> Load();
    void Save(IReadOnlyList<uint> uniqIds);
    void Clear();
}

public enum TrayStatus
{
    /// <summary>ドライバに接続できない（未インストール・ライセンス無効等）。</summary>
    DriverUnavailable,
    /// <summary>通常。リマップが効いているキーボードが <see cref="TraySnapshot.ActiveCount"/> 台ある。</summary>
    Running,
    /// <summary>一時停止中（トレイが全キーボードをOFFにした状態）。</summary>
    Paused,
}

public sealed record TraySnapshot(TrayStatus Status, int ActiveCount, int DeviceCount)
{
    /// <summary>一時停止／再開のメニューを押せるか（止める相手がいるか、再開できる状態か）。</summary>
    public bool CanTogglePause => Status == TrayStatus.Paused || (Status == TrayStatus.Running && ActiveCount > 0);
}

/// <summary>
/// 「一時停止」の中身。接続中の全キーボードのレイヤーマップをOFF（素通し）にし、再開で、一時停止の前にONだった
/// キーボードだけをONに戻す。止めたキーボードの一覧は <see cref="IPauseStateStore"/> に残すので、
/// トレイを終了しても（停止のまま終了）、次に起動したトレイが停止中として扱い、再開できる。
/// ドライバはON/OFFを永続化するので、一時停止中にPCを再起動してもOFFのまま起動する。
/// </summary>
public sealed class PauseController
{
    private readonly IDriverAccess _driver;
    private readonly IPauseStateStore _store;

    public PauseController(IDriverAccess driver, IPauseStateStore store)
    {
        _driver = driver;
        _store = store;
    }

    private static bool IsDriverFailure(Exception ex) => ex is KbdLayerException or InvalidOperationException or IOException;

    private static List<DeviceInfo> Real(List<DeviceInfo> all) => all.Where(d => d.UniqId != 0).ToList();

    private static readonly TraySnapshot Unavailable = new(TrayStatus.DriverUnavailable, 0, 0);

    private static TraySnapshot Snapshot(TrayStatus status, List<DeviceInfo> devices) =>
        new(status, devices.Count(d => d.HasLayerMap && d.Enabled), devices.Count);

    /// <summary>
    /// 状態をドライバから読み直す（タイマーとメニューを開くときに呼ぶ）。一時停止中は、次の2つも行う。
    ///  - 止めたキーボードのどれかがONになっていたら、ユーザーが（エディタの適用などで）自分で再開したと見て、一時停止を解く。
    ///  - 一時停止の後に接続された（またはONになった）キーボードは、止める（ドライバは接続時に前回のON/OFFを復元するため）。
    /// </summary>
    public TraySnapshot Refresh()
    {
        List<DeviceInfo> devices;
        try { devices = Real(_driver.ListDevices()); }
        catch (Exception ex) when (IsDriverFailure(ex)) { return Unavailable; }

        var paused = _store.Load();
        if (paused.Count == 0) return Snapshot(TrayStatus.Running, devices);

        if (devices.Any(d => paused.Contains(d.UniqId) && d.HasLayerMap && d.Enabled))
        {
            _store.Clear();
            return Snapshot(TrayStatus.Running, devices);
        }

        var all = new List<uint>(paused);
        foreach (var d in devices.Where(d => d.HasLayerMap && d.Enabled && !paused.Contains(d.UniqId)))
        {
            try { _driver.SetEnabled(d.UniqId, false); all.Add(d.UniqId); }
            catch (Exception ex) when (IsDriverFailure(ex)) { /* 次の更新でやり直す */ }
        }
        if (all.Count != paused.Count)
        {
            _store.Save(all);
            // 止めた直後の状態を返すため、読み直す。
            try { devices = Real(_driver.ListDevices()); }
            catch (Exception ex) when (IsDriverFailure(ex)) { return Unavailable; }
        }
        return Snapshot(TrayStatus.Paused, devices);
    }

    /// <summary>ONのキーボードをすべてOFFにして、止めたものを記録する。止めたものが無ければ何もしない。</summary>
    public TraySnapshot Pause()
    {
        List<DeviceInfo> devices;
        try { devices = Real(_driver.ListDevices()); }
        catch (Exception ex) when (IsDriverFailure(ex)) { return Unavailable; }

        var stopped = new List<uint>();
        foreach (var d in devices.Where(d => d.HasLayerMap && d.Enabled))
        {
            try { _driver.SetEnabled(d.UniqId, false); stopped.Add(d.UniqId); }
            catch (Exception ex) when (IsDriverFailure(ex)) { /* 止められなかったものは記録しない（再開でも触らない） */ }
        }
        if (stopped.Count > 0) _store.Save(stopped);
        return Refresh();
    }

    /// <summary>一時停止の前にONだったキーボードを、ONに戻す（その後に外されたものは無視する）。</summary>
    public TraySnapshot Resume()
    {
        var paused = _store.Load();
        if (paused.Count > 0)
        {
            List<DeviceInfo> devices;
            try { devices = Real(_driver.ListDevices()); }
            catch (Exception ex) when (IsDriverFailure(ex)) { return Unavailable; }

            foreach (var d in devices.Where(d => paused.Contains(d.UniqId) && d.HasLayerMap && !d.Enabled))
            {
                try { _driver.SetEnabled(d.UniqId, true); }
                catch (Exception ex) when (IsDriverFailure(ex)) { /* 戻せなかったものは、エディタの適用で戻せる */ }
            }
            _store.Clear();
        }
        return Refresh();
    }
}
