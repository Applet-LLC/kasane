using Loc = Kasane.Model.Loc;

namespace Kasane.Interop;

public sealed partial class KbdLayerClient
{
    // 2026-11-03。マークモード（MARK）の解除。前面のウィンドウが変わったときに、トレイが呼ぶ。
    // キー入力は読めず、できるのは選択範囲を広げる動作を止めることだけなので、昇格していなくても呼べる
    // （FILE_READ_ACCESS）。マークの機能が入る前のドライバは ErrorInvalidFunction を返す。
    private static readonly uint IOCTL_CLEAR_MARK = CtlCode(0x822, FILE_READ_ACCESS);

    /// <summary>全キーボードのマークモードを終わらせる。ドライバが古いと KbdLayerException（<see cref="ErrorInvalidFunction"/>）。</summary>
    public void ClearMark() =>
        Ioctl(Loc.T("マークモードの解除", "Clearing mark mode"), IOCTL_CLEAR_MARK, null, 0);
}
