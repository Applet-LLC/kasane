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
/// </summary>
public sealed class ImeLinkHost : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_FOCUS = 0x8005;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint threadId, uint eventTime);

    private readonly ImeLinkService _service;
    private readonly WinEventProc _winEventProc; // GCされないよう保持する
    private readonly IntPtr _foregroundHook;
    private readonly IntPtr _focusHook;
    private volatile bool _enabled;
    private volatile bool _sessionActive;
    private bool _disposed;

    public ImeLinkHost()
    {
        _enabled = TrayConfig.Load().ImeLink;
        _sessionActive = IsActiveConsoleSession();

        _service = new ImeLinkService(
            new ImeLinkController(new KbdLayerImeLayerDriver()),
            new Win32ImeSource(),
            () => _enabled,
            () => _sessionActive);

        _winEventProc = (_, _, _, _, _, _, _) => _service.Poke();
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        _focusHook = SetWinEventHook(EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS, IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        SystemEvents.SessionSwitch += OnSessionSwitch;

        _service.Start();
    }

    public ImeLinkStatus Status => _service.Status;

    /// <summary>tray.json を読み直す（エディタの設定画面での変更を、数秒で反映するため、トレイのタイマーから呼ぶ）。</summary>
    public void ReloadConfig() => _enabled = TrayConfig.Load().ImeLink;

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
        _service.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc proc,
        uint idProcess, uint idThread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
}
