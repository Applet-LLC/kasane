namespace Kasane.Interop;

public sealed partial class KbdLayerClient
{
    private static readonly uint IOCTL_WAIT_LAYER_CHANGE = CtlCode(0x812, FILE_READ_ACCESS);

    /// <summary>キーボードが外された（STATUS_DEVICE_REMOVED）ときの Win32 エラー（ERROR_DEVICE_REMOVED）。</summary>
    public const int ErrorDeviceRemoved = 1617;

    /// <summary>ドライバ全体の保留の上限（64本）を超えたときの Win32 エラー（ERROR_NO_SYSTEM_RESOURCES）。</summary>
    public const int ErrorNoSystemResources = 1450;

    /// <summary>
    /// WAIT_LAYER_CHANGE: レイヤー状態の Seq が lastSeq と違うものになるまで待つ（同期。すでに違えば即座に戻る）。初回は
    /// <see cref="GetLayerState"/> の Seq を渡す。ほかのスレッドから <c>CancelSynchronousIo</c> で止めると
    /// KbdLayerException（ERROR_OPERATION_ABORTED = 995）、キーボードが外されると <see cref="ErrorDeviceRemoved"/>、
    /// ドライバが古いと <see cref="ErrorInvalidFunction"/>。このハンドルから SET はしないこと（所有者が変わる）。
    /// </summary>
    public LayerState WaitLayerChange(uint uniqId, uint lastSeq)
    {
        var input = Build(w => { w.Write(uniqId); w.Write(lastSeq); });
        return ParseLayerState(Ioctl(Kasane.Model.Loc.T("レイヤー変化の待機", "Waiting for layer changes"), IOCTL_WAIT_LAYER_CHANGE, input, LayerStateSize));
    }
}
