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

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint threadId, uint eventTime);

    private readonly ImeLinkService _service;
    private readonly WinEventProc _winEventProc; // GCされないよう保持する
    private readonly IntPtr _foregroundHook;
    private readonly IntPtr _focusHook;
    private readonly WinEventProc _nameChangeProc;
    private readonly IntPtr _nameChangeHook;
    private readonly AppLayerArbiter _arbiter;
    private readonly AppRuleTick _ruleTick;
    private volatile bool _enabled;
    private volatile bool _sessionActive;
    private bool _disposed;

    public ImeLinkHost()
    {
        var config = TrayConfig.Load();
        _enabled = config.ImeLink;
        _sessionActive = IsActiveConsoleSession();

        _arbiter = new AppLayerArbiter(new KbdLayerAppLayerDriver());
        _ruleTick = new AppRuleTick(
            new AppRuleController(_arbiter, new KbdLayerDriverAccess(), new JsonMapOffStore()),
            new ForegroundInfo());
        _ruleTick.Reload(config);

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

    /// <summary>tray.json と helper.json を読み直す（エディタの設定画面での変更を、数秒で反映するため、トレイのタイマーから呼ぶ）。</summary>
    public void ReloadConfig()
    {
        var config = TrayConfig.Load();
        _enabled = config.ImeLink;
        if (_ruleTick.Reload(config)) _service.Poke();
    }

    /// <summary>一時停止中か（トレイのタイマーから伝える）。一時停止中は、前面のアプリの規則を止める。</summary>
    public bool Paused
    {
        set { if (_ruleTick.Paused != value) { _ruleTick.Paused = value; _service.Poke(); } }
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
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
            case SessionSwitchReason.SessionLogon:
                _sessionActive = IsActiveConsoleSession();
                break;
        }
        _service.Poke();
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
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
}

/// <summary>
/// 前面のアプリの規則を、IME連動のスレッドで動かす部分。前面のウィンドウを読み、設定（UI スレッドが読み直して差し替える）と
/// 一時停止の状態を添えて <see cref="AppRuleController"/> に渡す。
/// </summary>
internal sealed class AppRuleTick : ILinkedTick
{
    private readonly AppRuleController _controller;
    private readonly ForegroundInfo _foreground;
    private HelperConfigSet _config = HelperConfigSet.Empty;
    private string _configStamp = "";
    private volatile bool _paused;

    public AppRuleTick(AppRuleController controller, ForegroundInfo foreground)
    {
        _controller = controller;
        _foreground = foreground;
    }

    public bool Paused { get => _paused; set => _paused = value; }

    public bool HasRules => Volatile.Read(ref _config).HasAnyRules;

    /// <summary>
    /// 設定を読み直す（UI スレッドから）。helper.json の更新時刻と tray.json の Helper が変わったときだけ作り直す。
    /// 変わったら true。
    /// </summary>
    public bool Reload(TrayConfig config)
    {
        string stamp = Stamp(config);
        if (stamp == _configStamp) return false;
        _configStamp = stamp;
        Volatile.Write(ref _config, HelperConfigSet.Load(config.Helper));
        return true;
    }

    // 各 helper.json の更新時刻と大きさ、共通の分の JSON をつないだもの（読み直すかどうかの判定だけに使う）。
    private static string Stamp(TrayConfig config)
    {
        var sb = new System.Text.StringBuilder(config.Helper?.ToJson() ?? "");
        try
        {
            string dir = HelperConfig.BaseDirectory;
            if (Directory.Exists(dir))
            {
                foreach (var path in Directory.EnumerateFiles(dir, HelperConfig.FileName, SearchOption.AllDirectories).OrderBy(p => p))
                {
                    var info = new FileInfo(path);
                    sb.Append('|').Append(path).Append('@').Append(info.LastWriteTimeUtc.Ticks).Append(':').Append(info.Length);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return sb.ToString();
    }

    public void Tick(bool sessionActive)
    {
        var config = Volatile.Read(ref _config);
        // 規則が無ければ、前面のウィンドウも読まない（何も決めていなければ、何もしない）。
        var foreground = config.HasAnyRules ? _foreground.Read() : null;
        _controller.Tick(foreground, config, sessionActive, _paused);
    }

    public long? DueInMs => _controller.PendingDueInMs;

    public void Shutdown() => _controller.Shutdown();
}
