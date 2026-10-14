using System.IO;
namespace Kasane.Interop;

/// <summary>KBDLAYER_KEY_EVENT（生の物理キーの押下・解放）。ScanCode は Enc16（拡張キーは 0xE0xx、Pause は 0xE11D）。</summary>
public sealed record KeyEvent(uint Seq, uint UniqId, ushort ScanCode, bool IsBreak, uint TimeMs);

/// <summary>WAIT_KEY_EVENT の1回分の結果。Seq は次の LastSeq に渡す。Dropped は、読み出しが追いつかず押し出された件数。</summary>
public sealed record KeyEventBatch(uint Seq, uint Dropped, IReadOnlyList<KeyEvent> Events);

public sealed partial class KbdLayerClient
{
    // キー入力が丸見えになる（キーロガーになりうる）ので FILE_WRITE_ACCESS（管理者のみ）。
    private static readonly uint IOCTL_WAIT_KEY_EVENT = CtlCode(0x81F, FILE_WRITE_ACCESS);

    /// <summary>WAIT_KEY_EVENT の LastSeq に渡すと、「今より後のイベント」を待つ。</summary>
    public const uint KeySeqLatest = 0xFFFFFFFF;

    /// <summary>管理者権限が無いときの Win32 エラー（ERROR_ACCESS_DENIED）。</summary>
    public const int ErrorAccessDenied = 5;

    private const int KeyOutputHeaderSize = 16;   // KBDLAYER_WAIT_KEY_EVENT_OUTPUT
    private const int KeyEventSize = 16;          // KBDLAYER_KEY_EVENT
    private const int KeyEventsPerCall = 64;

    /// <summary>
    /// 生の物理キーの押下・解放を待つ（同期。イベントが来るまで戻らない）。管理者権限が要る。ほかのスレッドから
    /// <c>CancelSynchronousIo</c> で止めると KbdLayerException（ERROR_OPERATION_ABORTED = 995）になる。
    /// 権限が無いと ErrorAccessDenied、ドライバが古いと ErrorInvalidFunction、待機の上限を超えると ErrorNoSystemResources。
    /// </summary>
    public KeyEventBatch WaitKeyEvent(uint lastSeq)
    {
        var input = Build(w => { w.Write(lastSeq); w.Write(0u); });
        var data = Ioctl("キーイベントの待機", IOCTL_WAIT_KEY_EVENT, input, KeyOutputHeaderSize + KeyEventSize * KeyEventsPerCall);
        return ParseKeyEvents(data);
    }

    public static KeyEventBatch ParseKeyEvents(byte[] data)
    {
        if (data.Length < KeyOutputHeaderSize) throw new InvalidDataException("キーイベントの応答が短すぎます");
        using var r = new BinaryReader(new MemoryStream(data));
        uint seq = r.ReadUInt32();
        uint count = r.ReadUInt32();
        uint dropped = r.ReadUInt32();
        r.ReadUInt32(); // Reserved
        long available = (data.Length - KeyOutputHeaderSize) / KeyEventSize;
        var events = new List<KeyEvent>();
        for (long i = 0; i < Math.Min(count, available); i++)
        {
            uint eSeq = r.ReadUInt32();
            uint uniq = r.ReadUInt32();
            ushort scan = r.ReadUInt16();
            byte isBreak = r.ReadByte();
            r.ReadByte(); // Reserved
            uint time = r.ReadUInt32();
            events.Add(new KeyEvent(eSeq, uniq, scan, isBreak != 0, time));
        }
        return new KeyEventBatch(seq, dropped, events);
    }
}
