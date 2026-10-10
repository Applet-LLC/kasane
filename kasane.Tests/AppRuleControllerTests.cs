using Kasane.Interop;
using Kasane.Model;
using Kasane.Tray;

namespace Kasane.Tests;

/// <summary>前面のアプリの規則（AppRuleController と AppMatch）。偽のドライバと偽の時計で確かめる。</summary>
public class AppRuleControllerTests
{
    private const uint Kb1 = 0xCDEC0000;
    private const uint Kb2 = 0x55F70000;

    private sealed class FakeDevices : IDriverAccess
    {
        public List<DeviceInfo> Devices = new();
        public List<(uint Id, bool Enabled)> SetCalls = new();
        public Func<uint, bool, bool>? OnSet;       // SetEnabled の直前に呼ぶ（順序の確認用）
        public bool FailNextOff;

        public List<DeviceInfo> ListDevices() => Devices.ToList();

        public void SetEnabled(uint uniqId, bool enabled)
        {
            OnSet?.Invoke(uniqId, enabled);
            if (!enabled && FailNextOff) { FailNextOff = false; throw new KbdLayerException("set", 6); }
            SetCalls.Add((uniqId, enabled));
            int i = Devices.FindIndex(d => d.UniqId == uniqId);
            if (i >= 0) Devices[i] = Devices[i] with { Enabled = enabled };
        }

        public bool Enabled(uint id) => Devices.First(d => d.UniqId == id).Enabled;
    }

    private sealed class FakeStore : IMapOffStore
    {
        public List<uint> Saved = new();
        public IReadOnlyList<uint> Load() => Saved.ToList();
        public void Save(IReadOnlyList<uint> uniqIds) => Saved = uniqIds.ToList();
    }

    private static DeviceInfo Device(uint id, bool enabled = true) => new(id, true, enabled, 1, "HID\\VID_04FE");

    private static ForegroundWindowInfo Window(string exePath, string title = "", string cls = "Win", string focusClass = "Edit") =>
        new(exePath, cls, title, $"{exePath}:{cls}:{focusClass}", $"{exePath}:{title}:");

    private static HelperConfigSet Rules(params AppLayerRule[] global) =>
        new(new HelperConfig { AppLayerRules = global.ToList() }, new Dictionary<uint, HelperConfig>());

    private long _now;
    private AppLayerArbiterTests.FakeLayerDriver _layers = null!;
    private FakeDevices _devices = null!;
    private FakeStore _store = null!;

    private AppRuleController Make(params DeviceInfo[] devices)
    {
        _now = 10_000;
        _layers = new AppLayerArbiterTests.FakeLayerDriver();
        _devices = new FakeDevices { Devices = devices.ToList() };
        _store = new FakeStore();
        return new AppRuleController(new AppLayerArbiter(_layers), _devices, _store, () => _now);
    }

    // デバウンスを越えて決めさせる。
    private void Settle(AppRuleController c, ForegroundWindowInfo? fg, HelperConfigSet cfg, bool session = true, bool paused = false)
    {
        c.Tick(fg, cfg, session, paused);
        _now += AppRuleController.DebounceMs;
        c.Tick(fg, cfg, session, paused);
    }

    // ------------------------------------------------------------------
    // 照合（AppMatch）
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("code.exe", @"C:\Apps\VS Code\Code.exe", true)]          // ファイル名、大文字小文字を区別しない
    [InlineData("notepad.exe", @"C:\Apps\VS Code\Code.exe", false)]
    [InlineData(@"C:\Apps\VS Code\Code.exe", @"c:\apps\vs code\code.exe", true)] // 「\」を含めばフルパス
    public void Match_Exe(string exe, string actualPath, bool expected)
    {
        var m = new AppMatch { Exe = exe };
        Assert.Equal(expected, m.IsMatch(Window(actualPath)));
    }

    [Fact]
    public void Match_AllSpecifiedFieldsMustMatch()
    {
        var m = new AppMatch { Exe = "chrome.exe", TitleRegex = "YouTube" };
        Assert.True(m.IsMatch(Window(@"C:\x\chrome.exe", "Music - YouTube - Google Chrome")));
        Assert.False(m.IsMatch(Window(@"C:\x\chrome.exe", "Mail - Google Chrome")));
        Assert.False(m.IsMatch(Window(@"C:\x\msedge.exe", "Music - YouTube")));
    }

    [Fact]
    public void Match_NodokaStrings_AndOr()
    {
        // のどかの window EditControl /:(Edit|TEdit|RichEdit(20[AW])?)$/
        var cls = new AppMatch { NodokaClass = ":(Edit|TEdit|RichEdit(20[AW])?)$" };
        Assert.True(cls.IsMatch(Window(@"C:\Windows\notepad.exe", focusClass: "Edit")));
        Assert.False(cls.IsMatch(Window(@"C:\Windows\notepad.exe", focusClass: "RichEditD2DPT")));

        var and = new AppMatch { NodokaClass = @"notepad\.exe", NodokaTitle = "Secret" };
        var or = new AppMatch { NodokaClass = @"notepad\.exe", NodokaTitle = "Secret", NodokaOr = true };
        var w = Window(@"C:\Windows\notepad.exe", "memo");
        Assert.False(and.IsMatch(w));
        Assert.True(or.IsMatch(w));
    }

    [Fact]
    public void Match_EmptyOrInvalid_NeverMatches()
    {
        Assert.False(new AppMatch().IsMatch(Window(@"C:\x\a.exe")));
        var bad = new AppMatch { TitleRegex = "([" };
        Assert.Equal(nameof(AppMatch.TitleRegex), bad.InvalidRegexField);
        Assert.False(bad.IsMatch(Window(@"C:\x\a.exe", "([")));
    }

    // ------------------------------------------------------------------
    // 決定（DecideFor）
    // ------------------------------------------------------------------

    [Fact]
    public void Decide_FirstMatchWins_DeviceRulesBeforeGlobal_ThenDefault()
    {
        var global = new HelperConfig
        {
            AppLayerRules =
            {
                new AppLayerRule { Name = "g-code", Match = { Exe = "code.exe" }, Layers = { 2 } },
                new AppLayerRule { Name = "g-default", Default = true, Layers = { 5 } },
            },
        };
        var device = new HelperConfig
        {
            AppLayerRules = { new AppLayerRule { Name = "d-code", Match = { Exe = "code.exe" }, Layers = { 3 } } },
        };
        var set = new HelperConfigSet(global, new Dictionary<uint, HelperConfig> { [Kb1] = device });

        Assert.Equal("d-code", AppRuleController.DecideFor(Kb1, Window(@"C:\x\code.exe"), set).RuleName);
        Assert.Equal("g-code", AppRuleController.DecideFor(Kb2, Window(@"C:\x\code.exe"), set).RuleName);
        Assert.Equal("g-default", AppRuleController.DecideFor(Kb1, Window(@"C:\x\other.exe"), set).RuleName);
    }

    [Fact]
    public void Decide_DisabledRulesAndOutOfRangeLayersAreIgnored()
    {
        var set = Rules(
            new AppLayerRule { Enabled = false, Match = { Exe = "a.exe" }, Layers = { 1 } },
            new AppLayerRule { Match = { Exe = "a.exe" }, Layers = { 2, 99, -1, 2 } });
        var d = AppRuleController.DecideFor(Kb1, Window(@"C:\x\a.exe"), set);
        Assert.Equal(new[] { 2 }, d.Layers);
    }

    // ------------------------------------------------------------------
    // Tick: レイヤー
    // ------------------------------------------------------------------

    [Fact]
    public void Tick_RaisesLayerForMatchingApp_AfterDebounce_AndLowersWhenLeaving()
    {
        var c = Make(Device(Kb1));
        var cfg = Rules(new AppLayerRule { Match = { Exe = "code.exe" }, Layers = { 3 } });

        c.Tick(Window(@"C:\x\code.exe"), cfg, true, false);
        Assert.False(_layers.Bit(Kb1, 3)); // まだ決めない（Alt+Tab 中のちらつきを抑える）
        Assert.NotNull(c.PendingDueInMs);

        _now += AppRuleController.DebounceMs;
        c.Tick(Window(@"C:\x\code.exe"), cfg, true, false);
        Assert.True(_layers.Bit(Kb1, 3));

        Settle(c, Window(@"C:\x\other.exe"), cfg);
        Assert.False(_layers.Bit(Kb1, 3));
        Assert.Equal(1, _layers.ReleaseCalls);
    }

    [Fact]
    public void Tick_FlickerShorterThanDebounce_DoesNotApply()
    {
        var c = Make(Device(Kb1));
        var cfg = Rules(new AppLayerRule { Match = { Exe = "code.exe" }, Layers = { 3 } });

        c.Tick(Window(@"C:\x\code.exe"), cfg, true, false);
        _now += 50;
        c.Tick(Window(@"C:\x\other.exe"), cfg, true, false); // 元に戻った（決めることは無い）
        _now += AppRuleController.DebounceMs;
        c.Tick(Window(@"C:\x\other.exe"), cfg, true, false);
        Assert.Equal(0, _layers.SetCalls);
    }

    [Fact]
    public void Tick_NullForeground_KeepsDecision()
    {
        var c = Make(Device(Kb1));
        var cfg = Rules(new AppLayerRule { Match = { Exe = "code.exe" }, Layers = { 3 } });
        Settle(c, Window(@"C:\x\code.exe"), cfg);
        Settle(c, null, cfg); // シェルのウィンドウ・トレイ自身など
        Assert.True(_layers.Bit(Kb1, 3));
    }

    [Fact]
    public void Tick_SessionInactiveOrPaused_LowersLayers()
    {
        var c = Make(Device(Kb1));
        var cfg = Rules(new AppLayerRule { Match = { Exe = "code.exe" }, Layers = { 3 } });
        Settle(c, Window(@"C:\x\code.exe"), cfg);

        c.Tick(Window(@"C:\x\code.exe"), cfg, sessionActive: false, paused: false);
        Assert.False(_layers.Bit(Kb1, 3));

        Settle(c, Window(@"C:\x\code.exe"), cfg);
        Assert.True(_layers.Bit(Kb1, 3));

        c.Tick(Window(@"C:\x\code.exe"), cfg, sessionActive: true, paused: true);
        Assert.False(_layers.Bit(Kb1, 3));
    }

    [Fact]
    public void Tick_RulesRemoved_LowersLayers()
    {
        var c = Make(Device(Kb1));
        var cfg = Rules(new AppLayerRule { Match = { Exe = "code.exe" }, Layers = { 3 } });
        Settle(c, Window(@"C:\x\code.exe"), cfg);
        c.Tick(Window(@"C:\x\code.exe"), HelperConfigSet.Empty, true, false);
        Assert.False(_layers.Bit(Kb1, 3));
    }

    [Fact]
    public void Tick_OnlyKeyboardsWithLayerMap()
    {
        var c = Make(Device(Kb1), new DeviceInfo(Kb2, false, false, 0, "ACPI"));
        var cfg = Rules(new AppLayerRule { Match = { Exe = "code.exe" }, Layers = { 3 } });
        Settle(c, Window(@"C:\x\code.exe"), cfg);
        Assert.True(_layers.Bit(Kb1, 3));
        Assert.False(_layers.Bit(Kb2, 3));
    }

    // ------------------------------------------------------------------
    // Tick: リマップの OFF
    // ------------------------------------------------------------------

    [Fact]
    public void MapOff_TurnsOffWhileFront_AndRestoresAfter()
    {
        var c = Make(Device(Kb1));
        var cfg = Rules(new AppLayerRule { Match = { Exe = "game.exe" }, MapOff = true });

        Settle(c, Window(@"C:\g\game.exe"), cfg);
        Assert.False(_devices.Enabled(Kb1));
        Assert.Equal(new[] { Kb1 }, _store.Saved);

        Settle(c, Window(@"C:\x\other.exe"), cfg);
        Assert.True(_devices.Enabled(Kb1));
        Assert.Empty(_store.Saved);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(250)]
    [InlineData(500)]
    public void MapOff_ReturningQuicklyToTheWindow_TurnsOffAgain(int gapMs)
    {
        // P2 の所見1: 離れて 1 秒以内に戻ると、控えの一覧が古くて「ユーザーが止めた」と取り違え、OFF にならなかった。
        var c = Make(Device(Kb1));
        var cfg = Rules(new AppLayerRule { Match = { Exe = "game.exe" }, MapOff = true });

        Settle(c, Window(@"C:\g\game.exe"), cfg);
        _now += AppRuleController.DeviceRefreshIntervalMs + 200;
        c.Tick(Window(@"C:\g\game.exe"), cfg, true, false); // 一覧が「OFF」で更新される
        Settle(c, Window(@"C:\x\other.exe"), cfg);
        Assert.True(_devices.Enabled(Kb1));

        _now += gapMs; // 一覧はまだ更新されない（古い「OFF」のまま）
        Settle(c, Window(@"C:\g\game.exe"), cfg);
        Assert.False(_devices.Enabled(Kb1));
        Assert.Contains(Kb1, c.MapOffDevices);
    }

    [Fact]
    public void MapOff_RapidBackAndForth_EndsInTheStateOfTheFrontWindow()
    {
        var c = Make(Device(Kb1));
        var cfg = Rules(new AppLayerRule { Match = { Exe = "game.exe" }, MapOff = true });
        for (int i = 0; i < 6; i++)
        {
            Settle(c, Window(@"C:\g\game.exe"), cfg);
            Assert.False(_devices.Enabled(Kb1));
            Settle(c, Window(@"C:\x\other.exe"), cfg);
            Assert.True(_devices.Enabled(Kb1));
        }
        Assert.Empty(_store.Saved);
    }

    [Fact]
    public void MapOff_RecordIsWrittenBeforeTheKeyboardIsTurnedOff()
    {
        // P2.1 の所見: OFF にした直後に落ちると、記録が無く戻せなかった。OFF にする時点で、記録がすでにある。
        var c = Make(Device(Kb1));
        var cfg = Rules(new AppLayerRule { Match = { Exe = "game.exe" }, MapOff = true });
        bool recordedAtOff = false;
        _devices.OnSet = (id, enabled) => { if (!enabled) recordedAtOff = _store.Saved.Contains(id); return true; };

        Settle(c, Window(@"C:\g\game.exe"), cfg);
        Assert.True(recordedAtOff);
        Assert.False(_devices.Enabled(Kb1));
    }

    [Fact]
    public void MapOff_FailedTurnOff_LeavesNoRecord()
    {
        var c = Make(Device(Kb1));
        var cfg = Rules(new AppLayerRule { Match = { Exe = "game.exe" }, MapOff = true });
        _devices.FailNextOff = true;
        Settle(c, Window(@"C:\g\game.exe"), cfg);
        Assert.True(_devices.Enabled(Kb1));
        Assert.Empty(_store.Saved);
        Assert.Empty(c.MapOffDevices);
    }

    [Fact]
    public void MapOff_DoesNotTouchKeyboardsAlreadyOff()
    {
        var c = Make(Device(Kb1, enabled: false));
        var cfg = Rules(new AppLayerRule { Match = { Exe = "game.exe" }, MapOff = true });
        Settle(c, Window(@"C:\g\game.exe"), cfg);
        Settle(c, Window(@"C:\x\other.exe"), cfg);
        Assert.Empty(_devices.SetCalls); // ユーザーが OFF にしていたものは、戻すときも ON にしない
    }

    [Fact]
    public void MapOff_PausedKeepsRecord_ResumeThenLeaveRestores()
    {
        var c = Make(Device(Kb1));
        var cfg = Rules(new AppLayerRule { Match = { Exe = "game.exe" }, MapOff = true });
        Settle(c, Window(@"C:\g\game.exe"), cfg);
        Assert.False(_devices.Enabled(Kb1));

        c.Tick(Window(@"C:\g\game.exe"), cfg, true, paused: true);
        Assert.Single(_devices.SetCalls); // 一時停止中は ON/OFF に触らない
        Assert.Contains(Kb1, c.MapOffDevices);

        Settle(c, Window(@"C:\g\game.exe"), cfg); // 再開。まだゲームが前面なので OFF のまま
        Assert.False(_devices.Enabled(Kb1));
        Assert.Single(_devices.SetCalls);

        Settle(c, Window(@"C:\x\other.exe"), cfg);
        Assert.True(_devices.Enabled(Kb1));
    }

    [Fact]
    public void MapOff_SessionInactiveRestores()
    {
        var c = Make(Device(Kb1));
        var cfg = Rules(new AppLayerRule { Match = { Exe = "game.exe" }, MapOff = true });
        Settle(c, Window(@"C:\g\game.exe"), cfg);
        c.Tick(Window(@"C:\g\game.exe"), cfg, sessionActive: false, paused: false);
        Assert.True(_devices.Enabled(Kb1));
    }

    [Fact]
    public void MapOff_LeftOverFromCrash_IsRestoredOnFirstTick()
    {
        _now = 10_000;
        _layers = new AppLayerArbiterTests.FakeLayerDriver();
        _devices = new FakeDevices { Devices = { Device(Kb1, enabled: false) } };
        _store = new FakeStore { Saved = { Kb1 } }; // 前のトレイが OFF にしたまま落ちた
        var c = new AppRuleController(new AppLayerArbiter(_layers), _devices, _store, () => _now);

        c.Tick(Window(@"C:\x\other.exe"), Rules(new AppLayerRule { Match = { Exe = "game.exe" }, MapOff = true }), true, false);
        Assert.True(_devices.Enabled(Kb1));
        Assert.Empty(_store.Saved);
    }

    [Fact]
    public void Shutdown_RestoresAndLowers()
    {
        var c = Make(Device(Kb1), Device(Kb2));
        var cfg = new HelperConfigSet(null, new Dictionary<uint, HelperConfig>
        {
            [Kb1] = new() { AppLayerRules = { new AppLayerRule { Match = { Exe = "game.exe" }, MapOff = true } } },
            [Kb2] = new() { AppLayerRules = { new AppLayerRule { Match = { Exe = "game.exe" }, Layers = { 4 } } } },
        });
        Settle(c, Window(@"C:\g\game.exe"), cfg);
        Assert.False(_devices.Enabled(Kb1));
        Assert.True(_layers.Bit(Kb2, 4));

        c.Shutdown();
        Assert.True(_devices.Enabled(Kb1));
        Assert.False(_layers.Bit(Kb2, 4));
    }
}
