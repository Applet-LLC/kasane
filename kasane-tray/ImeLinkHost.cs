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
    private volatile bool _enabled;     // NICOLAのIME連動、またはレイヤーだけの連動のどちらかが有効（IMEの状態を見る）
    private volatile bool _nicolaLink;  // tray.json の ImeLink（全キーボード共通の、NICOLAの IME 連動。キーボードごとの設定が先）
    private bool _disposed;

    // IME の状態の取得元（IMM32 と、天狼のフック経由の TSF）。フックは、TSF を使う設定で、連動が有効なときだけ入れる。
    private readonly TsfSiriusImeSource _tsfSource;
    private readonly ImeSourceSelector _imeSource;
    private SiriusHook? _siriusHook;
    private Kasane.Model.ImeSourceMode _imeSourceMode = Kasane.Model.ImeSourceMode.Imm32;
    private bool _siriusFailed; // 入れられなかった（DLL が無いなど）。設定が変わるまで、数秒ごとに試し直さない

    public ImeLinkHost()
    {
        var config = TrayConfig.Load();
        _nicolaLink = config.ImeLink;

        _arbiter = new AppLayerArbiter(new KbdLayerAppLayerDriver());
        _actions = new ActionLayerController(_arbiter, new KbdLayerDriverAccess());
        _ruleTick = new AppRuleTick(
            new AppRuleController(_arbiter, new KbdLayerDriverAccess(), new JsonMapOffStore()),
            _actions,
            new ForegroundInfo(),
            Store);
        Store.Reload(config);
        _enabled = Store.Current.AnyNicolaLink(_nicolaLink) || Store.Current.HasAnyImeLayerLink;

        var imm = new Win32ImeSource();
        _tsfSource = new TsfSiriusImeSource(imm);
        _imeSource = new ImeSourceSelector(imm, _tsfSource, TrayLog.Write, imm.ForegroundLabel);
        ApplyImeSource(config);

        _service = new ImeLinkService(
            new ImeLinkController(new ImeLayerSource(_arbiter, () => Store.Current, uniqId => Store.Current.NicolaLinkFor(uniqId, _nicolaLink))),
            _imeSource,
            () => _enabled,
            () => SessionActive,
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
    /// 自分のセッションがアクティブなコンソールで、ロック画面でもないか。**イベントに頼らず、その場で確かめる**（150ms だけ覚える）。
    /// SessionSwitch の通知は、遅れる・届かない・順序が入れ替わることがあり（P4 の試験で、起動から時間のたったトレイで
    /// ロックが反映されなかった／ロック解除が先に処理されて24分間止まったままだった）、イベントで持った状態は信用できない。
    /// ロックの判断は、(1) セッションの状態（WTSSessionInfoEx の SessionFlags）、(2) 入力デスクトップに触れるか、
    /// (3) 前面が LockApp.exe（ロック画面）か、のどれかが「ロック」なら、ロックとする。
    /// </summary>
    public bool SessionActive => SessionActiveNow();

    private long _activeCheckedAt;
    private volatile bool _activeCache;
    private volatile bool _activeCached;

    private readonly object _activeLock = new();

    private bool SessionActiveNow()
    {
        long now = Environment.TickCount64;
        if (_activeCached && unchecked(now - Volatile.Read(ref _activeCheckedAt)) < 150) return _activeCache;
        lock (_activeLock) return RecheckSessionActive(now);
    }

    private bool RecheckSessionActive(long now)
    {
        // 別のスレッドが先に確かめ終えていたら、それを使う（切り替わりの記録が、二重にならないように）。
        if (_activeCached && unchecked(now - Volatile.Read(ref _activeCheckedAt)) < 150) return _activeCache;
        bool console = IsActiveConsoleSession(), wts = WtsSessionLocked(), desktop = !InputDesktopAccessible(), lockApp = LockAppInFront();
        bool active = console && !wts && !desktop && !lockApp;
        if (!_activeCached || active != _activeCache)
            TrayLog.Write($"Session: active={active} (console={console}, wtsLocked={wts}, desktopDenied={desktop}, lockApp={lockApp})"); // 切り替わりだけ記録する（原因の切り分け用）
        _activeCache = active; // 競合しても、どちらも今の正しい値を入れるだけ
        Volatile.Write(ref _activeCheckedAt, now);
        _activeCached = true;
        return active;
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
        _nicolaLink = config.ImeLink;
        bool reloaded = Store.Reload(config);
        _enabled = Store.Current.AnyNicolaLink(_nicolaLink) || Store.Current.HasAnyImeLayerLink;
        ApplyImeSource(config);
        if (!reloaded) return false;
        _service.Poke();
        return true;
    }

    /// <summary>
    /// 設定の IME の取得方法に合わせて、取得元の順序を切り替え、天狼のフックを入れる／外す（UI スレッド）。
    /// フックは、TSF を使う設定で、IME 連動が有効なときだけ入れる（<c>Imm32</c> を選ぶと、全プロセスへの注入をしない）。
    /// 入れられなくても（DLL が無い・読めない）、IMM32 だけで動き続ける。
    /// </summary>
    private void ApplyImeSource(TrayConfig config)
    {
        var mode = config.ImeSourceMode;
        if (mode != _imeSourceMode) _siriusFailed = false; // 設定を変えたら、入れ直しを試す
        _imeSourceMode = mode;
        _imeSource.Mode = mode;

        bool wantHook = mode.UsesTsf() && _enabled && !_siriusFailed;
        if (wantHook && _siriusHook == null)
        {
            _siriusHook = SiriusHook.TryInstall(out string error);
            if (_siriusHook != null)
            {
                _tsfSource.Attach(_siriusHook);
                TrayLog.Write(Loc.T($"Sirius: フックを入れました({mode})", $"Sirius: hook installed ({mode})"));
            }
            else
            {
                _siriusFailed = true;
                TrayLog.Write(Loc.T("Sirius: TSF は使えません(IMM32 だけで動きます): ", "Sirius: TSF is unavailable (running on IMM32 only): ") + error);
            }
        }
        else if (!(mode.UsesTsf() && _enabled) && _siriusHook != null)
        {
            ReleaseSiriusHook();
        }
    }

    // 読むのをやめてから外す（読んでいる最中に共有メモリを外すと、アクセス違反になる）。
    private void ReleaseSiriusHook()
    {
        var hook = _siriusHook;
        if (hook == null) return;
        _siriusHook = null;
        _tsfSource.Detach();
        hook.Dispose();
        TrayLog.Write(Loc.T("Sirius: フックを外しました", "Sirius: hook removed"));
    }

    /// <summary>一時停止中か（トレイのタイマーから伝える）。一時停止中は、前面のアプリの規則を止める。</summary>
    public bool Paused
    {
        set { if (_ruleTick.Paused != value) { _ruleTick.Paused = value; _service.Poke(); StateChanged?.Invoke(); } }
    }

    // セッションの変化は、状態を持つのではなく、「今すぐ確かめ直す」きっかけにする。
    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        _activeCached = false;
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

    // セッションがロックされているか。Windows 8 以降は SessionFlags が WTS_SESSIONSTATE_LOCK(0) / UNLOCK(1)。
    // 構造体のずれに備えて、SessionId の位置で配置を確かめてから読む。
    private static bool WtsSessionLocked()
    {
        const int WTSSessionInfoEx = 25;
        if (!WTSQuerySessionInformationW(IntPtr.Zero, 0xFFFFFFFF, WTSSessionInfoEx, out IntPtr buffer, out uint bytes) || buffer == IntPtr.Zero)
            return false;
        try
        {
            if (bytes < 24 || Marshal.ReadInt32(buffer, 0) != 1) return false; // Level 1
            if (!ProcessIdToSessionId((uint)Environment.ProcessId, out uint mine)) return false;
            int dataOffset = Marshal.ReadInt32(buffer, 4) == (int)mine ? 4 : Marshal.ReadInt32(buffer, 8) == (int)mine ? 8 : -1;
            if (dataOffset < 0 || bytes < dataOffset + 12) return false;
            return Marshal.ReadInt32(buffer, dataOffset + 8) == 0;
        }
        finally { WTSFreeMemory(buffer); }
    }

    // 前面のウィンドウが、ロック画面（LockApp.exe）か。
    private static bool LockAppInFront()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        IntPtr process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == IntPtr.Zero) return false;
        try
        {
            var sb = new System.Text.StringBuilder(512);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(process, 0, sb, ref size) &&
                   sb.ToString().EndsWith("\\LockApp.exe", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseHandle(process); }
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
        ReleaseSiriusHook(); // 読むスレッドを止めてから外す
        _arbiter.Shutdown(); // 念のため（通常は、上で両方の入力源が空になり、ハンドルは閉じている）
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc proc,
        uint idProcess, uint idThread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, System.Text.StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytesReturned);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
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
