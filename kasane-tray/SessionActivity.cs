namespace Kasane.Tray;

/// <summary>Windows のセッションの接続状態（<c>WTS_CONNECTSTATE_CLASS</c>）。</summary>
public enum WtsConnectState
{
    Active = 0,
    Connected = 1,
    ConnectQuery = 2,
    Shadow = 3,
    Disconnected = 4,
    Idle = 5,
    Listen = 6,
    Reset = 7,
    Down = 8,
    Init = 9,
}

/// <summary>
/// このトレイのセッションが「いま利用者が操作しているセッション」か。副作用のない判定にして、実機なしで試験できるようにしてある。
///
/// 実機のコンソールか、リモートデスクトップ（RDP）かは問わない。**画面につながっている（接続状態が Active）**こと、
/// ロック画面でないこと、入力を受けるデスクトップに触れることを見る。ユーザーの切り替えで裏に回ったセッション、
/// RDP を切断したセッション（接続状態が Disconnected など）は、操作中ではない。
/// 以前は「アクティブなコンソールのセッションか」を見ていたため、RDP のセッションが、ロックされていなくても常に非アクティブになり、
/// 「動いている間だけリマップ」がキーボードを OFF にしていた（2026-10-22）。
/// </summary>
public static class SessionActivity
{
    /// <param name="connectState">自分のセッションの接続状態。取得できなかったときは null（動作を止めたままにしないため、操作中とみなす）。</param>
    /// <param name="locked">セッションがロックされている（WTSSessionInfoEx の SessionFlags）。</param>
    /// <param name="desktopDenied">入力を受けるデスクトップに触れない（ロック画面・UAC の画面の間）。</param>
    /// <param name="lockApp">前面が LockApp.exe（ロック画面）。</param>
    public static bool IsActive(WtsConnectState? connectState, bool locked, bool desktopDenied, bool lockApp) =>
        (connectState == null || connectState == WtsConnectState.Active) && !locked && !desktopDenied && !lockApp;
}
