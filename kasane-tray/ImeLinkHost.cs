using System.Diagnostics;
using System.Runtime.InteropServices;
using Kasane.Model;
using Microsoft.Win32;

namespace Kasane.Tray;

/// <summary>
/// IME連動をトレイに載せる部分（UIスレッド側）。
///  - 前面のウィンドウ・フォーカスが変わったら、すぐにIMEの状態を読み直す（WinEventフック）。
///  - 自分のセッションがアクティブなコンソールのときだけ連動する（ロック・ユーザー切り替え・リモート接続中は、
///    レイヤーを戻す。アプリ管理レイヤーはキーボード単位で、全セッションで共有されるため、ユーザー同士で取り合わない）。
///  - 連動のON/OFFは、エディタの設定画面が書く tray.json（<see cref="TrayConfig"/>）から読む。
///  - 前面のアプリの規則（<see cref="AppRuleController"/>、2026-10-08）も、同じスレッド・同じフック・同じセッションの判定で動かす。
///    IME連動とアプリ管理レイヤーのハンドルを共有するため、両方とも <see cref="AppLayerArbiter"/> を通して立てる。
///    規則は helper.json（キーボードごと）と tray.json の Helper（共通）から読む。タイトルの変化でも決め直す
///    （EVENT_OBJECT_NAMECHANGE。前面のウィンドウのものだけ）。
/// </summary>
public sealed class ImeLinkHost : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_FOCUS = 0x8005;
    private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    private const int OBJID_WINDOW = 0;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;
    private const int ERROR_ACCESS_DENIED = 5;

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint threadId, uint eventTime);

    private readonly ImeLinkService _service;
    private readonly WinEventProc _winEventProc; // GCされないよう保持する
    private readonly IntPtr _foregroundHook;
    private readonly IntPtr _focusHook;
    private readonly WinEventProc _nameChangeProc;
    private readonly IntPtr _nameChangeHook;
    private readonly AppLayerArbiter _arbiter;
    private readonly AppRuleTick _ruleTick;
    private readonly ActionLayerController _actions;
    private volatile bool _enabled;
    private volatile bool _sessionActive;
    private bool _disposed;

    public ImeLinkHost()
    {
        var config = TrayConfig.Load();
        _enabled = config.ImeLink;
        _sessionActive = IsActiveConsoleSession() && InputDesktopAccessible();

        _arbiter = new AppLayerArbiter(new KbdLayerAppLayerDriver());
        _actions = new ActionLayerController(_arbiter, new KbdLayerDriverAccess());
        _ruleTick = new AppRuleTick(
            new AppRuleController(_arbiter, new KbdLayerDriverAccess(), new JsonMapOffStore()),
            _actions,
            new ForegroundInfo(),
            Store);
        Store.Reload(config);

        _service = new ImeLinkService(
            new ImeLinkController(new ImeLayerSource(_arbiter)),
            new Win32ImeSource(),
            () => _enabled,
            () => _sessionActive,
            _ruleTick);

        _winEventProc = (_, _, _, _, _, _, _) => _service.Poke();
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        _focusHook = SetWinEventHook(EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS, IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        // タイトルの変化（ブラウザのタブの切り替えなど）。前面のウィンドウ自身のものだけで起こす（頻繁に来るので、規則が無ければ見ない）。
        _nameChangeProc = (_, _, hwnd, idObject, _, _, _) =>
        {
            if (idObject == OBJID_WINDOW && _ruleTick.HasRules && hwnd == GetForegroundWindow()) _service.Poke();
        };
        _nameChangeHook = SetWinEventHook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE, IntPtr.Zero, _nameChangeProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        SystemEvents.SessionSwitch += OnSessionSwitch;

        _service.Start();
    }

    public ImeLinkStatus Status => _service.Status;

    /// <summary>読み込んだヘルパー設定（前面のアプリの規則・HLP の動作・キーフックのトリガー）。</summary>
    public HelperConfigStore Store { get; } = new();

    /// <summary>
    /// 自分のセッションがアクティブなコンソールで、ロック画面でもないか。イベントで追う値（<c>_sessionActive</c>）に加えて、
    /// 入力デスクトップに触れるかをその場で確かめる（150ms だけ覚える）。SessionLock の通知は 0.2〜3.4 秒遅れることがあり、
    /// その間にドライバのイベント（ロック画面でも出る）で動作が実行されるのを防ぐ。
    /// </summary>
    public bool SessionActive => _sessionActive && DesktopAccessibleNow();

    private long _desktopCheckedAt = long.MinValue;
    private volatile bool _desktopAccessible = true;

    private bool DesktopAccessibleNow()
    {
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _desktopCheckedAt) < 150) return _desktopAccessible;
        _desktopAccessible = InputDesktopAccessible(); // 競合しても、どちらも正しい値を入れるだけ
        Volatile.Write(ref _desktopCheckedAt, now);
        return _desktopAccessible;
    }

    /// <summary>一時停止中か。</summary>
    public bool IsPaused => _ruleTick.Paused;

    /// <summary>セッションの状態か一時停止の状態が変わった（キーフックを見直すため）。UI スレッドで起こる。</summary>
    public event Action? StateChanged;

    /// <summary>ヘルパー動作（AppLayer）のレイヤー操作を頼む（どのスレッドからでもよい）。反映は IME 連動のスレッド。</summary>
    public void RequestLayer(LayerRequest request)
    {
        _actions.Request(request);
        _service.Poke();
    }

    /// <summary>tray.json と helper.json を読み直す（エディタの設定画面での変更を、数秒で反映するため、トレイのタイマーから呼ぶ）。ヘルパー設定が変わったら true。</summary>
    public bool ReloadConfig()
    {
        var config = TrayConfig.Load();
        _enabled = config.ImeLink;
        if (!Store.Reload(config)) return false;
        _service.Poke();
        return true;
    }

    /// <summary>一時停止中か（トレイのタイマーから伝える）。一時停止中は、前面のアプリの規則を止める。</summary>
    public bool Paused
    {
        set { if (_ruleTick.Paused != value) { _ruleTick.Paused = value; _service.Poke(); StateChanged?.Invoke(); } }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
            case SessionSwitchReason.SessionLogoff:
                _sessionActive = false;
                break;
            case SessionSwitchReason.SessionUnlock:
                _sessionActive = IsActiveConsoleSession();
                break;
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
            case SessionSwitchReason.SessionLogon:
                // ユーザーを切り替えて戻ったときは、ロック画面が出たまま接続される（SessionLock は来ない）。
                // ロック画面の間は動かさず、SessionUnlock で戻す。
                _sessionActive = IsActiveConsoleSession() && InputDesktopAccessible();
                break;
        }
        _service.Poke();
        StateChanged?.Invoke();
    }

    // 入力を受けるデスクトップに触れるか。ロック画面・UAC の画面（Winlogon のデスクトップ）の間は、通常のユーザーには
    // アクセス拒否になる。ほかのエラーは「触れる」とみなす（判定できないときに、動作を止めたままにしないため）。
    private static bool InputDesktopAccessible()
    {
        IntPtr desktop = OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP);
        if (desktop != IntPtr.Zero)
        {
            CloseDesktop(desktop);
            return true;
        }
        return Marshal.GetLastWin32Error() != ERROR_ACCESS_DENIED;
    }

    private static bool IsActiveConsoleSession() =>
        ProcessIdToSessionId((uint)Environment.ProcessId, out uint mine) && mine == WTSGetActiveConsoleSessionId();

    public void Dispose()
    {
        if (_disposed) return; // 終了処理（ExitTray）と、後始末（Dispose）の両方から呼ばれる
        _disposed = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
        if (_focusHook != IntPtr.Zero) UnhookWinEvent(_focusHook);
        if (_nameChangeHook != IntPtr.Zero) UnhookWinEvent(_nameChangeHook);
        _service.Dispose();
        _arbiter.Shutdown(); // 念のため（通常は、上で両方の入力源が空になり、ハンドルは閉じている）
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc proc,
        uint idProcess, uint idThread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
}

/// <summary>
/// 前面のアプリの規則とヘルパー動作のレイヤー操作（AppLayer）を、IME連動のスレッドで動かす部分。前面のウィンドウを読み、設定
/// （<see cref="HelperConfigStore"/>。UI スレッドが読み直して差し替える）と一時停止の状態を添えて <see cref="AppRuleController"/> に渡す。
/// </summary>
internal sealed class AppRuleTick : ILinkedTick
{
    private readonly AppRuleController _controller;
    private readonly ActionLayerController _actions;
    private readonly ForegroundInfo _foreground;
    private readonly HelperConfigStore _store;
    private volatile bool _paused;

    public AppRuleTick(AppRuleController controller, ActionLayerController actions, ForegroundInfo foreground, HelperConfigStore store)
    {
        _controller = controller;
        _actions = actions;
        _foreground = foreground;
        _store = store;
    }

    public bool Paused { get => _paused; set => _paused = value; }

    public bool HasRules => _store.Current.HasAnyRules;

    public void Tick(bool sessionActive)
    {
        var config = _store.Current;
        // 規則が無ければ、前面のウィンドウも読まない（何も決めていなければ、何もしない）。
        var foreground = config.HasAnyRules ? _foreground.Read() : null;
        _controller.Tick(foreground, config, sessionActive, _paused);
        _actions.Tick(sessionActive && !_paused);
    }

    public long? DueInMs
    {
        get
        {
            long? rule = _controller.PendingDueInMs, action = _actions.DueInMs;
            if (rule == null) return action;
            return action == null ? rule : Math.Min(rule.Value, action.Value);
        }
    }

    public void Shutdown()
    {
        _controller.Shutdown();
        _actions.Shutdown();
    }
}
