using Kasane.Tray;

namespace Kasane.Tests;

/// <summary>
/// トレイの「操作中のセッションか」の判定（SessionActivity）。コンソールでも RDP でも、画面につながっていてロックでなければ操作中。
/// 以前は「アクティブなコンソールのセッションか」で判定していたため、RDP のセッションが常に非アクティブになった（2026-10-22）。
/// </summary>
public class SessionActivityTests
{
    [Fact]
    public void Active_NotLocked_IsActive_RegardlessOfConsoleOrRdp()
    {
        // 接続状態だけで判定する。コンソールか RDP かは、判定に入っていない。
        Assert.True(SessionActivity.IsActive(WtsConnectState.Active, locked: false, desktopDenied: false, lockApp: false));
    }

    [Theory]
    [InlineData(WtsConnectState.Disconnected)] // RDP の切断、ユーザー切り替えで裏に回った
    [InlineData(WtsConnectState.Connected)]
    [InlineData(WtsConnectState.ConnectQuery)]
    [InlineData(WtsConnectState.Idle)]
    [InlineData(WtsConnectState.Down)]
    [InlineData(WtsConnectState.Shadow)]
    public void NotActiveConnectState_IsNotActive(WtsConnectState state)
    {
        Assert.False(SessionActivity.IsActive(state, locked: false, desktopDenied: false, lockApp: false));
    }

    [Theory]
    [InlineData(true, false, false)]   // セッションがロックされている
    [InlineData(false, true, false)]   // 入力デスクトップに触れない（ロック画面・UAC）
    [InlineData(false, false, true)]   // 前面が LockApp.exe
    [InlineData(true, true, true)]
    public void Locked_IsNotActive_EvenWhenConnected(bool locked, bool desktopDenied, bool lockApp)
    {
        Assert.False(SessionActivity.IsActive(WtsConnectState.Active, locked, desktopDenied, lockApp));
    }

    [Fact]
    public void UnknownConnectState_DoesNotStallTheTray()
    {
        // 接続状態を取れなかったときは、動作を止めたままにしないため、操作中とみなす（ロックの判定は残る）。
        Assert.True(SessionActivity.IsActive(null, locked: false, desktopDenied: false, lockApp: false));
        Assert.False(SessionActivity.IsActive(null, locked: true, desktopDenied: false, lockApp: false));
    }
}
