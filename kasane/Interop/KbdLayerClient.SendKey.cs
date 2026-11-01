using Loc = Kasane.Model.Loc;

namespace Kasane.Interop;

public sealed partial class KbdLayerClient
{
    // 2026-11-01。1.01 以降のドライバが処理する（1.00 までのドライバは ErrorInvalidFunction を返す）。
    // 任意のキーの注入になるので FILE_WRITE_ACCESS（管理者・SYSTEMだけ。昇格していないと読み取り専用で開くので使えない）。
    private static readonly uint IOCTL_SEND_KEY = CtlCode(0x821, FILE_WRITE_ACCESS);

    /// <summary>
    /// 修飾キー（KBDLAYER_MOD_*）を押したまま、キー（Enc16。上位が E0 なら拡張キー）を1回押して離す。リマップなどは通さない。
    /// ドライバが古い・Release 構成だと KbdLayerException（<see cref="ErrorInvalidFunction"/>）。
    /// </summary>
    public void SendKey(uint uniqId, ushort scanCode, byte mods) =>
        Ioctl(Loc.T("キーの送出", "Sending a key"), IOCTL_SEND_KEY, Build(w =>
        {
            w.Write(uniqId);
            w.Write(scanCode);
            w.Write(mods);
            w.Write((byte)0); // Flags（予約）
        }), 0);
}
