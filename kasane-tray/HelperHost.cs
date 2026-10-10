using Kasane.Interop;
using Kasane.Model;

namespace Kasane.Tray;

/// <summary>
/// ヘルパー動作（P3、2026-10-08）をトレイに載せる部分。次の3つをつなぐ。
///  - ドライバのヘルパーイベント（CUSTOM = HLP(id) のキー。<see cref="HelperEventService"/>）→ helper.json の Actions を引く。
///  - F13〜F24 などのキー（<see cref="KeyHookService"/>。helper.json の Triggers が1つでもあるときだけフックを張る）→ Triggers を引く。
///  - どちらも <see cref="ActionRunner"/>（専用スレッド）で実行する。
/// 実行してよいのは、自分のセッションがアクティブなコンソールのときだけ。一時停止中は、一時停止の切り替え（PauseToggle）だけ許す
/// （ホットキーで再開できるように）。設定が変わったら <see cref="OnConfigChanged"/> を呼ぶ。
/// </summary>
public sealed class HelperHost : IDisposable
{
    private readonly ImeLinkHost _ime;
    private readonly ActionRunner _runner;
    private readonly HelperEventService _events;
    private readonly KeyHookService _hook;
    private volatile TriggerTable _table = TriggerTable.Empty;
    private bool _disposed;

    /// <param name="togglePause">一時停止・再開の切り替え（どのスレッドから呼ばれてもよい。UI スレッドへ渡すのは呼び出し側）。</param>
    public HelperHost(ImeLinkHost ime, Action togglePause)
    {
        _ime = ime;
        _runner = new ActionRunner(Allow, ime.RequestLayer, togglePause);
        _events = new HelperEventService(OnHelperEvent, () => _ime.SessionActive && _ime.Store.Current.HasAnyActions);
        _hook = new KeyHookService(() => _table, Allow, _runner.Enqueue);
        _ime.StateChanged += OnStateChanged;

        _table = TriggerTable.Build(_ime.Store.Current);
        LogTableErrors();
        _hook.Start();
        _events.Start();
        _events.Poke();
    }

    /// <summary>ドライバがヘルパーイベントに対応していない（HLP は使えない）。</summary>
    public bool HelperEventsUnsupported => _events.Unsupported;

    /// <summary>キーフックを張っているか（診断用）。</summary>
    public bool HookInstalled => _hook.Installed;

    /// <summary>ヘルパー設定が読み直された。トリガーの表を作り直し、フックと待機を見直す。</summary>
    public void OnConfigChanged()
    {
        _table = TriggerTable.Build(_ime.Store.Current);
        LogTableErrors();
        _hook.Update();
        _events.Poke();
    }

    private void LogTableErrors()
    {
        foreach (var e in _table.Errors) TrayLog.Write("Helper: " + e);
    }

    private void OnStateChanged() => _events.Poke();

    // 実行してよいか。
    private bool Allow(HelperActionBody body) =>
        _ime.SessionActive && (!_ime.IsPaused || body.Type == HelperActionBody.PauseToggle);

    private void OnHelperEvent(HelperEvent e)
    {
        var body = HelperActionResolver.Resolve(_ime.Store.Current, e.UniqId, e.CustomId);
        if (body == null) return;
        if (e.IsBreak ? !ActionExecutor.NeedsRelease(body) : !Allow(body)) return;
        _runner.Enqueue(new ActionRequest(body, e.UniqId, (ushort)(e.UniqId >> 16), e.IsBreak, e.IsRepeat));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ime.StateChanged -= OnStateChanged;
        _hook.Dispose();    // 先に入口を止める
        _events.Dispose();
        _runner.Dispose();
    }
}
