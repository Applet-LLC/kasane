using Kasane.Interop;

namespace Kasane.Tray;

/// <summary>自動で OFF にしたキーボードの記録（トレイを終了しても残す）。一時停止の記録（<see cref="IPauseStateStore"/>）とは別。</summary>
public interface IAutoOffStore
{
    IReadOnlyList<uint> Load();
    void Save(IReadOnlyList<uint> uniqIds);
    void Clear();
}

/// <summary>
/// 「トレイが動いている間だけリマップを有効にする」モード（TrayConfig.RemapOnlyWhileTrayRuns。既定はON）の中身。
/// ドライバは、永続化された Enabled で、ログオン画面や起動直後でもリマップを続ける。このモードでは、トレイが
/// 動いていない間・セッションがアクティブでない間（ロック・ユーザー切り替え・リモート切断）は、キーボードのリマップを OFF にし、
/// トレイが動いてセッションがアクティブになったら ON に戻す。ドライバは変えない（OFF は SET_LAYERMAP_ENABLED。読み取り側の IOCTL なので昇格は要らない）。
///
/// **このモードで OFF にしたキーボードの UniqId だけを記録する**。緊急解除や利用者が手で OFF にしたキーボードは、
/// 戻さない（緊急解除の安全網を壊さない）。一時停止（<see cref="PauseController"/>）とは別の記録を使う。
/// 一時停止中は何もしない（OFF のものを戻すのは、一時停止を解いたあとの周期）。
/// </summary>
public sealed class AutoOffController
{
    private readonly IDriverAccess _driver;
    private readonly IAutoOffStore _store;

    public AutoOffController(IDriverAccess driver, IAutoOffStore store)
    {
        _driver = driver;
        _store = store;
    }

    private static bool IsDriverFailure(Exception ex) => ex is KbdLayerException or InvalidOperationException or IOException;

    /// <summary>
    /// 周期ごと（トレイの起動直後を含む）に呼ぶ。モードがONでセッションが非アクティブなら、ONのキーボードをOFFにして記録する。
    /// そうでなければ（モードOFF・セッションがアクティブ）、記録したキーボードのうち今OFFのものをONに戻して、記録を消す。
    /// ドライバに接続できないときは何もせず、記録も残す（次の周期でやり直す）。
    /// </summary>
    public void Tick(bool modeOn, bool sessionActive, bool paused)
    {
        if (paused) return;
        if (modeOn && !sessionActive) TurnOff();
        else Restore();
    }

    /// <summary>
    /// トレイの終了・ログオフ・シャットダウンのとき。モードがONなら、ONのキーボードをOFFにして記録する
    /// （ドライバが OFF を永続化するので、次の起動のログオン画面でもOFFのまま）。何度呼んでもよい。
    /// 一時停止中でも呼ぶ（OFF のものは対象外なので、一時停止で止めたものを自分の記録に入れることはない）。
    /// </summary>
    public void Shutdown(bool modeOn)
    {
        if (modeOn) TurnOff();
    }

    private void TurnOff()
    {
        List<DeviceInfo> devices;
        try { devices = _driver.ListDevices().Where(d => d.UniqId != 0 && d.HasLayerMap && d.Enabled).ToList(); }
        catch (Exception ex) when (IsDriverFailure(ex)) { return; }
        if (devices.Count == 0) return;

        var recorded = new List<uint>(_store.Load());
        bool changed = false;
        foreach (var d in devices)
        {
            try
            {
                _driver.SetEnabled(d.UniqId, false);
                if (!recorded.Contains(d.UniqId)) recorded.Add(d.UniqId);
                changed = true;
            }
            catch (Exception ex) when (IsDriverFailure(ex)) { /* 次の周期でやり直す */ }
        }
        if (changed) _store.Save(recorded);
    }

    private void Restore()
    {
        var recorded = _store.Load();
        if (recorded.Count == 0) return;

        List<DeviceInfo> devices;
        try { devices = _driver.ListDevices(); }
        catch (Exception ex) when (IsDriverFailure(ex)) { return; }

        var failed = new List<uint>();
        foreach (var id in recorded)
        {
            var d = devices.FirstOrDefault(x => x.UniqId == id);
            if (d == null || !d.HasLayerMap || d.Enabled) continue; // 外された・マップが無い・すでにON
            try { _driver.SetEnabled(id, true); }
            catch (Exception ex) when (IsDriverFailure(ex)) { failed.Add(id); }
        }
        if (failed.Count == 0) _store.Clear();
        else _store.Save(failed); // 戻せなかったものだけ残して、次の周期でやり直す
    }
}
