using Kasane.Model;
using Kasane.Tray;
using Xunit;

namespace Kasane.Tests;

/// <summary>トレイが動作ごとに文字の入力の速さを決める（<see cref="TextTypingResolver"/>。2026-11-20）。</summary>
public class TextTypingResolverTests
{
    private const uint Kb = 0x1234ABCD;

    private static ActionRequest Hlp(uint uniqId) => new(new HelperActionBody { Type = "Text", Text = "a" }, uniqId, (ushort)(uniqId >> 16), false, false);
    private static ActionRequest Trigger(ushort hash16) => new(new HelperActionBody { Type = "Text", Text = "a" }, 0, hash16, false, false);

    private sealed class Driver
    {
        public int ListCalls, StateCalls;
        public uint Mask;
        public bool Fail;
        public IReadOnlyList<uint> List() { ListCalls++; if (Fail) throw new InvalidOperationException("no driver"); return new[] { 0x55550001u, Kb }; }
        public uint State(uint id) { StateCalls++; if (Fail) throw new InvalidOperationException("no driver"); Assert.Equal(Kb, id); return Mask; }
    }

    private static (TextTypingResolver, Driver, List<string>) Make(HelperConfigSet set)
    {
        var d = new Driver();
        var log = new List<string>();
        return (new TextTypingResolver(() => set, d.List, d.State, log.Add), d, log);
    }

    private static HelperConfigSet WithLayers() => new(
        new HelperConfig { TextTyping = new TextTypingConfig { CharMs = 5, Layers = new() { new TextTypingLayer { Layer = 4, CharMs = 40 } } } },
        new Dictionary<uint, HelperConfig>());

    [Fact]
    public void GlobalOnly_DoesNotTouchDriver()
    {
        var (r, d, _) = Make(new HelperConfigSet(new HelperConfig { TextTyping = new TextTypingConfig { CharMs = 7 } }, new Dictionary<uint, HelperConfig>()));
        Assert.Equal(7, r.Resolve(Trigger((ushort)(Kb >> 16))).CharMs);
        Assert.Equal(0, d.ListCalls + d.StateCalls);
    }

    [Fact]
    public void LayerOverride_ReadsActiveLayers_ForHlpKeyboard()
    {
        var (r, d, _) = Make(WithLayers());
        d.Mask = 1u << 4;
        Assert.Equal(40, r.Resolve(Hlp(Kb)).CharMs);
        d.Mask = 1;
        Assert.Equal(5, r.Resolve(Hlp(Kb)).CharMs);
        Assert.Equal(0, d.ListCalls); // UniqId が分かっているので、一覧は読まない
    }

    [Fact]
    public void Trigger_FindsKeyboardByHash()
    {
        var (r, d, _) = Make(WithLayers());
        d.Mask = 1u << 4;
        Assert.Equal(40, r.Resolve(Trigger((ushort)(Kb >> 16))).CharMs);
        Assert.Equal(1, d.ListCalls);
    }

    [Fact]
    public void UnknownKeyboard_UsesDefaultsWithoutLayers()
    {
        var (r, d, _) = Make(WithLayers());
        Assert.Equal(5, r.Resolve(Trigger(0x9999)).CharMs);
        Assert.Equal(0, d.StateCalls);
    }

    [Fact]
    public void DriverFailure_FallsBack_AndLogsOncePerReason()
    {
        var (r, d, log) = Make(WithLayers());
        d.Fail = true;
        Assert.Equal(5, r.Resolve(Hlp(Kb)).CharMs);
        Assert.Equal(5, r.Resolve(Hlp(Kb)).CharMs);
        Assert.Single(log);
    }

    [Fact]
    public void DeviceDefault_IsUsedForTriggerKeyboard()
    {
        var set = new HelperConfigSet(null, new Dictionary<uint, HelperConfig> { [Kb] = new HelperConfig { TextTyping = new TextTypingConfig { CharMs = 12 } } });
        var (r, d, _) = Make(set);
        Assert.Equal(12, r.Resolve(Trigger((ushort)(Kb >> 16))).CharMs);
        Assert.Equal(0, d.StateCalls); // レイヤーの上書きが無ければ、レイヤーは読まない
    }
}
