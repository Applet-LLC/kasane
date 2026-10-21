using System.IO;
namespace Kasane.Interop;

/// <summary>KBDLAYER_HELPER_EVENT（CUSTOM のキーの押下・オートリピート・解放）。</summary>
public sealed record HelperEvent(uint Seq, uint UniqId, ushort CustomId, bool IsBreak, bool IsRepeat, byte Modifiers, uint TimeMs);

/// <summary>WAIT_HELPER_EVENT の1回分の結果。Seq は次の LastSeq に渡す。Dropped は、読み出しが追いつかず押し出された件数。</summary>
public sealed record HelperEventBatch(uint Seq, uint Dropped, IReadOnlyList<HelperEvent> Events);

public sealed partial class KbdLayerClient
{
    private static readonly uint IOCTL_WAIT_HELPER_EVENT = CtlCode(0x81E, FILE_READ_ACCESS);

    /// <summary>WAIT_HELPER_EVENT の LastSeq に渡すと、「今より後のイベント」を待つ。</summary>
    public const uint HelperSeqLatest = 0xFFFFFFFF;

    /// <summary>ドライバが WAIT_HELPER_EVENT を知らないときの Win32 エラー（ERROR_INVALID_FUNCTION）。</summary>
    public const int ErrorInvalidFunction = 1;

    private const int HelperOutputHeaderSize = 16;   // KBDLAYER_WAIT_HELPER_EVENT_OUTPUT
    private const int HelperEventSize = 20;          // KBDLAYER_HELPER_EVENT
    private const int HelperEventsPerCall = 16;

    /// <summary>
    /// ヘルパーイベントを待つ（同期。イベントが来るまで戻らない）。ほかのスレッドから <c>CancelSynchronousIo</c> で止めると
    /// KbdLayerException（ERROR_OPERATION_ABORTED = 995）になる。ドライバが古いと ErrorInvalidFunction。
    /// </summary>
    public HelperEventBatch WaitHelperEvent(uint lastSeq)
    {
        var input = Build(w => { w.Write(lastSeq); w.Write(0u); });
        var data = Ioctl(Kasane.Model.Loc.T("ヘルパーイベントの待機", "waiting for helper events"), IOCTL_WAIT_HELPER_EVENT, input, HelperOutputHeaderSize + HelperEventSize * HelperEventsPerCall);
        return ParseHelperEvents(data);
    }

    public static HelperEventBatch ParseHelperEvents(byte[] data)
    {
        if (data.Length < HelperOutputHeaderSize) throw new InvalidDataException(Kasane.Model.Loc.T("ヘルパーイベントの応答が短すぎます", "The helper event response is too short"));
        using var r = new BinaryReader(new MemoryStream(data));
        uint seq = r.ReadUInt32();
        uint count = r.ReadUInt32();
        uint dropped = r.ReadUInt32();
        r.ReadUInt32(); // Reserved
        long available = (data.Length - HelperOutputHeaderSize) / HelperEventSize;
        var events = new List<HelperEvent>();
        for (long i = 0; i < Math.Min(count, available); i++)
        {
            uint eSeq = r.ReadUInt32();
            uint uniq = r.ReadUInt32();
            ushort customId = r.ReadUInt16();
            byte isBreak = r.ReadByte();
            byte flags = r.ReadByte();
            byte mods = r.ReadByte();
            r.ReadBytes(3); // Reserved
            uint time = r.ReadUInt32();
            events.Add(new HelperEvent(eSeq, uniq, customId, isBreak != 0, (flags & 0x01) != 0, mods, time));
        }
        return new HelperEventBatch(seq, dropped, events);
    }
}
