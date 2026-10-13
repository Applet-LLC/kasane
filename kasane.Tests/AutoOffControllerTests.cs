using Kasane.Interop;
using Kasane.Model;
using Kasane.Tray;

namespace Kasane.Tests;

/// <summary>トレイの「動いている間だけリマップを有効にする」モード（AutoOffController）。偽のドライバと記録で、実機なしに確かめる。</summary>
public class AutoOffControllerTests
{
    private sealed class FakeDriver : IDriverAccess
    {
        public List<DeviceInfo> Devices = new();
        public bool Unavailable;
        public HashSet<uint> FailSetFor = new();

        public List<DeviceInfo> ListDevices()
        {
            if (Unavailable) throw new InvalidOperationException("no driver");
            return Devices.ToList();
        }

        public void SetEnabled(uint uniqId, bool enabled)
        {
            if (Unavailable) throw new InvalidOperationException("no driver");
            if (FailSetFor.Contains(uniqId)) throw new KbdLayerException("SetEnabled", 5);
            int i = Devices.FindIndex(d => d.UniqId == uniqId);
            Devices[i] = Devices[i] with { Enabled = enabled };
        }

        public void Set(uint id, bool hasMap, bool enabled)
        {
            var d = new DeviceInfo(id, hasMap, enabled, 0, $"HID\\TEST_{id}");
            int i = Devices.FindIndex(x => x.UniqId == id);
            if (i >= 0) Devices[i] = d; else Devices.Add(d);
        }

        public bool IsOn(uint id) => Devices.First(d => d.UniqId == id).Enabled;
    }

    private sealed class FakeStore : IAutoOffStore
    {
        public List<uint> Ids = new();
        public IReadOnlyList<uint> Load() => Ids.ToList();
        public void Save(IReadOnlyList<uint> uniqIds) => Ids = uniqIds.ToList();
        public void Clear() => Ids = new List<uint>();
    }

    private static (AutoOffController C, FakeDriver D, FakeStore S) Make()
    {
        var d = new FakeDriver();
        var s = new FakeStore();
        return (new AutoOffController(d, s), d, s);
    }

    [Fact]
    public void Shutdown_WithModeOn_TurnsOnKeyboardsOffAndRecordsThem()
    {
        var (c, d, s) = Make();
        d.Set(1, true, true);
        d.Set(2, true, true);
        c.Shutdown(modeOn: true);
        Assert.False(d.IsOn(1));
        Assert.False(d.IsOn(2));
        Assert.Equal(new uint[] { 1, 2 }, s.Ids.OrderBy(x => x));
    }

    [Fact]
    public void Shutdown_WithModeOff_DoesNothing()
    {
        var (c, d, s) = Make();
        d.Set(1, true, true);
        c.Shutdown(modeOn: false);
        Assert.True(d.IsOn(1));
        Assert.Empty(s.Ids);
    }

    [Fact]
    public void Shutdown_DoesNotRecordKeyboardsThatWereAlreadyOff()
    {
        // 緊急解除や手でOFFにしたものは、自分の記録に入れない（起動時に勝手にONに戻さない）。
        var (c, d, s) = Make();
        d.Set(1, true, true);
        d.Set(2, true, false);
        d.Set(3, false, false); // マップが無い
        c.Shutdown(modeOn: true);
        Assert.Equal(new uint[] { 1 }, s.Ids);
        Assert.False(d.IsOn(2));
    }

    [Fact]
    public void Shutdown_CanBeCalledTwice()
    {
        var (c, d, s) = Make();
        d.Set(1, true, true);
        c.Shutdown(true);
        c.Shutdown(true);
        Assert.Equal(new uint[] { 1 }, s.Ids);
    }

    [Fact]
    public void Tick_AtStartup_RestoresOnlyTheRecordedKeyboards()
    {
        var (c, d, s) = Make();
        d.Set(1, true, false); // 前回のトレイが OFF にしたもの
        d.Set(2, true, false); // 緊急解除などで OFF になっているもの
        s.Ids = new List<uint> { 1 };
        c.Tick(modeOn: true, sessionActive: true, paused: false);
        Assert.True(d.IsOn(1));
        Assert.False(d.IsOn(2));
        Assert.Empty(s.Ids);
    }

    [Fact]
    public void Tick_WhenSessionInactive_TurnsOffAndLaterRestores()
    {
        var (c, d, s) = Make();
        d.Set(1, true, true);
        c.Tick(true, sessionActive: false, paused: false);
        Assert.False(d.IsOn(1));
        Assert.Equal(new uint[] { 1 }, s.Ids);
        c.Tick(true, sessionActive: true, paused: false);
        Assert.True(d.IsOn(1));
        Assert.Empty(s.Ids);
    }

    [Fact]
    public void Tick_WhenModeOff_RestoresWhatWasRecorded()
    {
        var (c, d, s) = Make();
        d.Set(1, true, false);
        s.Ids = new List<uint> { 1 };
        c.Tick(modeOn: false, sessionActive: true, paused: false);
        Assert.True(d.IsOn(1));
        Assert.Empty(s.Ids);
    }

    [Fact]
    public void Tick_WhenPaused_DoesNothing()
    {
        var (c, d, s) = Make();
        d.Set(1, true, false);
        s.Ids = new List<uint> { 1 };
        c.Tick(true, sessionActive: true, paused: true);
        Assert.False(d.IsOn(1));
        Assert.Equal(new uint[] { 1 }, s.Ids); // 記録は残し、一時停止を解いたあとの周期で戻す
    }

    [Fact]
    public void Tick_DoesNotTurnOnAKeyboardTheUserAlreadyTurnedOnOrRemoved()
    {
        var (c, d, s) = Make();
        d.Set(1, true, true);  // 記録されているが、すでにON
        s.Ids = new List<uint> { 1, 9 }; // 9 は外されたキーボード
        c.Tick(true, true, false);
        Assert.True(d.IsOn(1));
        Assert.Empty(s.Ids);
    }

    [Fact]
    public void DriverUnavailable_KeepsTheRecordAndRetries()
    {
        var (c, d, s) = Make();
        d.Set(1, true, false);
        s.Ids = new List<uint> { 1 };
        d.Unavailable = true;
        c.Tick(true, true, false);
        Assert.Equal(new uint[] { 1 }, s.Ids);
        d.Unavailable = false;
        c.Tick(true, true, false);
        Assert.True(d.IsOn(1));
        Assert.Empty(s.Ids);
    }

    [Fact]
    public void RestoreFailure_KeepsOnlyTheFailedOnes()
    {
        var (c, d, s) = Make();
        d.Set(1, true, false);
        d.Set(2, true, false);
        s.Ids = new List<uint> { 1, 2 };
        d.FailSetFor.Add(2);
        c.Tick(true, true, false);
        Assert.True(d.IsOn(1));
        Assert.False(d.IsOn(2));
        Assert.Equal(new uint[] { 2 }, s.Ids);
    }

    [Fact]
    public void TurnOffFailure_RecordsOnlyTheOnesTurnedOff()
    {
        var (c, d, s) = Make();
        d.Set(1, true, true);
        d.Set(2, true, true);
        d.FailSetFor.Add(2);
        c.Shutdown(true);
        Assert.Equal(new uint[] { 1 }, s.Ids);
        Assert.True(d.IsOn(2));
    }

    [Fact]
    public void TrayConfig_DefaultsToRemapOnlyWhileTrayRuns()
    {
        Assert.True(new TrayConfig().RemapOnlyWhileTrayRuns);
        // tray.json が無い・読めないときの既定も同じ。
        Assert.True(TrayConfig.Load(Path.Combine(Path.GetTempPath(), "kasane-no-such-" + Guid.NewGuid().ToString("N"), "tray.json")).RemapOnlyWhileTrayRuns);
    }

    [Fact]
    public void TrayConfig_RoundTripsTheFlag()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kasane-autooff-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(dir, "tray.json");
            var c = new TrayConfig { RemapOnlyWhileTrayRuns = false };
            c.Save(path);
            Assert.False(TrayConfig.Load(path).RemapOnlyWhileTrayRuns);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void JsonStore_RoundTripsAndToleratesBrokenFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kasane-autooffstore-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(dir, "tray-autooff.json");
            var store = new JsonAutoOffStore(path);
            Assert.Empty(store.Load());
            store.Save(new uint[] { 3, 3, 7 });
            Assert.Equal(new uint[] { 3, 7 }, store.Load());
            File.WriteAllText(path, "{ broken");
            Assert.Empty(store.Load());
            store.Clear();
            Assert.Empty(store.Load());
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
