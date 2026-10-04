using Kasane.Interop;
using Kasane.Tray;

namespace Kasane.Tests;

/// <summary>トレイの「一時停止」（PauseController）。偽のドライバと記録で、実機なしに動きを確かめる。</summary>
public class PauseControllerTests
{
    private sealed class FakeDriver : IDriverAccess
    {
        public List<DeviceInfo> Devices = new();
        public bool Unavailable;
        public HashSet<uint> FailSetFor = new();
        public List<(uint Id, bool Enabled)> Calls = new();

        public List<DeviceInfo> ListDevices()
        {
            if (Unavailable) throw new InvalidOperationException("no driver");
            return Devices.ToList();
        }

        public void SetEnabled(uint uniqId, bool enabled)
        {
            if (Unavailable) throw new InvalidOperationException("no driver");
            if (FailSetFor.Contains(uniqId)) throw new KbdLayerException("SetEnabled", 5);
            Calls.Add((uniqId, enabled));
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

    private sealed class FakeStore : IPauseStateStore
    {
        public List<uint> Ids = new();
        public IReadOnlyList<uint> Load() => Ids.ToList();
        public void Save(IReadOnlyList<uint> uniqIds) => Ids = uniqIds.ToList();
        public void Clear() => Ids = new List<uint>();
    }

    private static (PauseController Controller, FakeDriver Driver, FakeStore Store) Make()
    {
        var driver = new FakeDriver();
        var store = new FakeStore();
        return (new PauseController(driver, store), driver, store);
    }

    [Fact]
    public void Refresh_Counts_Keyboards_With_Remapping_On()
    {
        var (c, d, _) = Make();
        d.Set(1, hasMap: true, enabled: true);
        d.Set(2, hasMap: true, enabled: false);
        d.Set(3, hasMap: false, enabled: false);
        var s = c.Refresh();
        Assert.Equal(TrayStatus.Running, s.Status);
        Assert.Equal(1, s.ActiveCount);
        Assert.Equal(3, s.DeviceCount);
        Assert.True(s.CanTogglePause);
    }

    [Fact]
    public void Refresh_Ignores_The_Offline_Virtual_Device()
    {
        var (c, d, _) = Make();
        d.Set(0, hasMap: true, enabled: true);
        var s = c.Refresh();
        Assert.Equal(0, s.DeviceCount);
        Assert.False(s.CanTogglePause);
    }

    [Fact]
    public void Refresh_Reports_Unavailable_Driver_Without_Throwing()
    {
        var (c, d, _) = Make();
        d.Unavailable = true;
        var s = c.Refresh();
        Assert.Equal(TrayStatus.DriverUnavailable, s.Status);
        Assert.False(s.CanTogglePause);
        Assert.Equal(TrayStatus.DriverUnavailable, c.Pause().Status);
        Assert.Equal(TrayStatus.DriverUnavailable, c.Resume().Status);
    }

    [Fact]
    public void Pause_Turns_Off_Only_The_Keyboards_That_Were_On_And_Remembers_Them()
    {
        var (c, d, store) = Make();
        d.Set(1, true, true);
        d.Set(2, true, true);
        d.Set(3, true, false);   // もともとOFF
        d.Set(4, false, false);  // マップ無し
        var s = c.Pause();
        Assert.Equal(TrayStatus.Paused, s.Status);
        Assert.Equal(0, s.ActiveCount);
        Assert.False(d.IsOn(1));
        Assert.False(d.IsOn(2));
        Assert.Equal(new uint[] { 1, 2 }, store.Ids.OrderBy(x => x));
        Assert.DoesNotContain(d.Calls, call => call.Id is 3 or 4);
        Assert.True(s.CanTogglePause); // 再開できる
    }

    [Fact]
    public void Resume_Restores_Only_What_Pause_Turned_Off()
    {
        var (c, d, store) = Make();
        d.Set(1, true, true);
        d.Set(2, true, true);
        d.Set(3, true, false);
        c.Pause();
        var s = c.Resume();
        Assert.Equal(TrayStatus.Running, s.Status);
        Assert.True(d.IsOn(1));
        Assert.True(d.IsOn(2));
        Assert.False(d.IsOn(3)); // もともとOFFのものはOFFのまま
        Assert.Empty(store.Ids);
        Assert.Equal(2, s.ActiveCount);
    }

    [Fact]
    public void Pause_With_Nothing_On_Does_Nothing()
    {
        var (c, d, store) = Make();
        d.Set(1, true, false);
        var s = c.Pause();
        Assert.Equal(TrayStatus.Running, s.Status);
        Assert.Empty(store.Ids);
        Assert.Empty(d.Calls);
        Assert.False(s.CanTogglePause);
    }

    [Fact]
    public void Pause_Does_Not_Record_A_Keyboard_It_Could_Not_Turn_Off()
    {
        var (c, d, store) = Make();
        d.Set(1, true, true);
        d.Set(2, true, true);
        d.FailSetFor.Add(2);
        c.Pause();
        Assert.Equal(new uint[] { 1 }, store.Ids);
        Assert.True(d.IsOn(2));
    }

    [Fact]
    public void A_Keyboard_Connected_During_Pause_Is_Turned_Off_And_Resumed_With_The_Others()
    {
        var (c, d, store) = Make();
        d.Set(1, true, true);
        c.Pause();
        d.Set(5, true, true); // 一時停止の後に接続された（ドライバが前回のONを復元した）
        var s = c.Refresh();
        Assert.Equal(TrayStatus.Paused, s.Status);
        Assert.False(d.IsOn(5));
        Assert.Contains(5u, store.Ids);
        c.Resume();
        Assert.True(d.IsOn(1));
        Assert.True(d.IsOn(5));
    }

    [Fact]
    public void Turning_A_Paused_Keyboard_On_Elsewhere_Ends_The_Pause()
    {
        var (c, d, store) = Make();
        d.Set(1, true, true);
        d.Set(2, true, true);
        c.Pause();
        d.Set(1, true, true); // エディタの「キーボードに適用（ON）」
        var s = c.Refresh();
        Assert.Equal(TrayStatus.Running, s.Status);
        Assert.Empty(store.Ids);
        Assert.True(d.IsOn(1));
        Assert.False(d.IsOn(2)); // 触らない（ユーザーが選んだ状態を尊重する）
    }

    [Fact]
    public void A_Paused_State_Survives_A_New_Controller_Instance()
    {
        // 「停止のまま終了」: トレイを終了しても記録は残り、次に起動したトレイが停止中として再開できる。
        var driver = new FakeDriver();
        var store = new FakeStore();
        driver.Set(1, true, true);
        new PauseController(driver, store).Pause();

        var second = new PauseController(driver, store);
        Assert.Equal(TrayStatus.Paused, second.Refresh().Status);
        Assert.Equal(TrayStatus.Running, second.Resume().Status);
        Assert.True(driver.IsOn(1));
    }

    [Fact]
    public void Resume_Skips_A_Keyboard_That_Is_No_Longer_Connected()
    {
        var (c, d, store) = Make();
        d.Set(1, true, true);
        d.Set(2, true, true);
        c.Pause();
        d.Devices.RemoveAll(x => x.UniqId == 2);
        var s = c.Resume();
        Assert.Equal(TrayStatus.Running, s.Status);
        Assert.True(d.IsOn(1));
        Assert.Empty(store.Ids);
    }

    [Fact]
    public void Resume_Keeps_Going_When_One_Keyboard_Fails()
    {
        var (c, d, _) = Make();
        d.Set(1, true, true);
        d.Set(2, true, true);
        c.Pause();
        d.FailSetFor.Add(1);
        c.Resume();
        Assert.False(d.IsOn(1));
        Assert.True(d.IsOn(2));
    }

    // ------------------------------------------------------------------
    // JsonPauseStateStore
    // ------------------------------------------------------------------

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"kasane-tray-test-{Guid.NewGuid():N}", "tray-state.json");

    [Fact]
    public void Store_RoundTrips_And_Removes_Duplicates()
    {
        var path = TempPath();
        try
        {
            var store = new JsonPauseStateStore(path);
            Assert.Empty(store.Load());
            store.Save(new uint[] { 0x55F70000, 0x05CF0000, 0x55F70000 });
            Assert.Equal(new uint[] { 0x55F70000, 0x05CF0000 }, store.Load());
            store.Clear();
            Assert.Empty(store.Load());
            store.Clear(); // 無くても例外にならない
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch (IOException) { } }
    }

    [Fact]
    public void Store_Treats_A_Corrupt_File_As_No_Record()
    {
        var path = TempPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ not json");
            Assert.Empty(new JsonPauseStateStore(path).Load());
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch (IOException) { } }
    }
}
