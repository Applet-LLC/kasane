using Kasane.Interop;
using Xunit;

namespace Kasane.Tests;

/// <summary>WAIT_KEY_EVENT の応答（KBDLAYER_WAIT_KEY_EVENT_OUTPUT + KBDLAYER_KEY_EVENT[]）の読み取り。</summary>
public class KeyEventParseTests
{
    private static byte[] Build(uint seq, uint dropped, params (uint Seq, uint Uniq, ushort Scan, byte IsBreak, uint Time)[] events)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(seq);
        w.Write((uint)events.Length);
        w.Write(dropped);
        w.Write(0u);
        foreach (var e in events)
        {
            w.Write(e.Seq);
            w.Write(e.Uniq);
            w.Write(e.Scan);
            w.Write(e.IsBreak);
            w.Write((byte)0);
            w.Write(e.Time);
        }
        return ms.ToArray();
    }

    [Fact]
    public void Parses_HeaderAndEvents()
    {
        var data = Build(7, 3,
            (6, 0x55F70000, 0x0036, 0, 1000),
            (7, 0x55F70000, 0xE11D, 1, 1090));
        var batch = KbdLayerClient.ParseKeyEvents(data);
        Assert.Equal(7u, batch.Seq);
        Assert.Equal(3u, batch.Dropped);
        Assert.Equal(2, batch.Events.Count);
        Assert.Equal(new KeyEvent(6, 0x55F70000, 0x0036, false, 1000), batch.Events[0]);
        Assert.Equal(new KeyEvent(7, 0x55F70000, 0xE11D, true, 1090), batch.Events[1]);
    }

    [Fact]
    public void TruncatedPayload_ReturnsOnlyWholeEvents()
    {
        var data = Build(2, 0, (1, 1, 0x001E, 0, 1), (2, 1, 0x001E, 1, 2));
        var cut = data[..(data.Length - 5)]; // 2件目の途中で切れている
        var batch = KbdLayerClient.ParseKeyEvents(cut);
        Assert.Single(batch.Events);
    }

    [Fact]
    public void EmptyBatch_IsAllowed()
    {
        var batch = KbdLayerClient.ParseKeyEvents(Build(5, 0));
        Assert.Empty(batch.Events);
        Assert.Equal(5u, batch.Seq);
    }

    [Fact]
    public void TooShort_Throws()
    {
        Assert.Throws<InvalidDataException>(() => KbdLayerClient.ParseKeyEvents(new byte[8]));
    }
}
