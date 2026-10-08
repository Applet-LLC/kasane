using Kasane.Interop;
using Kasane.Model;
using Kasane.Tray;

namespace Kasane.Tests;

/// <summary>ヘルパー動作（P3）の、Windows に依存しない部分: 手順の展開、トリガーの表、HLP の引き当て、実行、レイヤー操作。</summary>
public class HelperActionsTests
{
    private const uint Kb1 = 0xCDEC0000;
    private const uint Kb2 = 0x55F70000;

    // ------------------------------------------------------------------
    // SequenceCompiler
    // ------------------------------------------------------------------

    private static CompileResult Compile(params HelperStep[] steps) => SequenceCompiler.Compile(steps);

    [Fact]
    public void Compile_PlainKey_IsDownThenUp()
    {
        var r = Compile(new HelperStep { Key = "KC_A" });
        Assert.Empty(r.Errors);
        Assert.Equal(new[]
        {
            new InputOp(InputKind.Key, 0x1E),
            new InputOp(InputKind.Key, 0x1E, Up: true),
        }, r.Ops);
    }

    [Fact]
    public void Compile_ModdedKey_PressesModifiersAroundTheKey()
    {
        var r = Compile(new HelperStep { Key = "LCTL(KC_C)" });
        Assert.Empty(r.Errors);
        Assert.Equal(new[]
        {
            new InputOp(InputKind.Key, 0x1D),
            new InputOp(InputKind.Key, 0x2E),
            new InputOp(InputKind.Key, 0x2E, Up: true),
            new InputOp(InputKind.Key, 0x1D, Up: true),
        }, r.Ops);
    }

    [Fact]
    public void Compile_DownThenUp_KeepsTheKeyHeldBetween()
    {
        var r = Compile(new HelperStep { Down = "LSFT(KC_A)" }, new HelperStep { DelayMs = 20 }, new HelperStep { Up = "LSFT(KC_A)" });
        Assert.Empty(r.Errors);
        Assert.Equal(new[]
        {
            new InputOp(InputKind.Key, 0x2A),
            new InputOp(InputKind.Key, 0x1E),
            new InputOp(InputKind.Delay, 20),
            new InputOp(InputKind.Key, 0x1E, Up: true),
            new InputOp(InputKind.Key, 0x2A, Up: true),
        }, r.Ops);
    }

    [Fact]
    public void Compile_Text_SendsUnicode_NewlineAsEnter_TabAsTab()
    {
        var r = Compile(new HelperStep { Text = "あ\r\n\tb" });
        Assert.Empty(r.Errors);
        Assert.Equal(new[]
        {
            new InputOp(InputKind.Unicode, 'あ'), new InputOp(InputKind.Unicode, 'あ', Up: true),
            new InputOp(InputKind.Key, 0x1C), new InputOp(InputKind.Key, 0x1C, Up: true),
            new InputOp(InputKind.Key, 0x0F), new InputOp(InputKind.Key, 0x0F, Up: true),
            new InputOp(InputKind.Unicode, 'b'), new InputOp(InputKind.Unicode, 'b', Up: true),
        }, r.Ops);
    }

    [Fact]
    public void Compile_RawScanCode_AndExtendedKey()
    {
        var r = Compile(new HelperStep { Key = "SC_E06A" });
        Assert.Empty(r.Errors);
        Assert.Equal(0xE06A, r.Ops[0].Value);
    }

    [Fact]
    public void Compile_Delay_IsClamped_AndTotalIsLimited()
    {
        var r = Compile(new HelperStep { DelayMs = 99_999 });
        Assert.Single(r.Errors);
        Assert.Equal(new InputOp(InputKind.Delay, SequenceCompiler.MaxDelayMs), r.Ops[0]);

        var many = Enumerable.Range(0, 10).Select(_ => new HelperStep { DelayMs = 10_000 }).ToArray();
        var limited = SequenceCompiler.Compile(many);
        Assert.NotEmpty(limited.Errors);
        Assert.True(limited.Ops.Sum(o => o.Value) <= SequenceCompiler.MaxTotalDelayMs);
    }

    [Fact]
    public void Compile_Mouse_ButtonsMoveAndWheel()
    {
        var r = Compile(
            new HelperStep { Mouse = "Left" },
            new HelperStep { Mouse = "X1", MouseAction = "Down" },
            new HelperStep { MoveX = 10, MoveY = -5 },
            new HelperStep { MoveX = 100, MoveY = 200, Absolute = true },
            new HelperStep { Wheel = -120 },
            new HelperStep { HWheel = 120 });
        Assert.Empty(r.Errors);
        Assert.Equal(new[]
        {
            new InputOp(InputKind.MouseButton, HelperMouseButtons.Left),
            new InputOp(InputKind.MouseButton, HelperMouseButtons.Left, Up: true),
            new InputOp(InputKind.MouseButton, HelperMouseButtons.X1),
            new InputOp(InputKind.MouseMove, 10, -5),
            new InputOp(InputKind.MouseMove, 100, 200, Flag: true),
            new InputOp(InputKind.Wheel, -120),
            new InputOp(InputKind.HWheel, 120),
        }, r.Ops);
    }

    [Theory]
    [InlineData("NOT_A_KEY")]
    [InlineData("MO(1)")]       // キーではない
    [InlineData("HLP(3)")]
    public void Compile_UnresolvableKey_IsReported_AndSkipped(string key)
    {
        var r = Compile(new HelperStep { Key = key }, new HelperStep { Key = "KC_B" });
        Assert.Single(r.Errors);
        Assert.Equal(2, r.Ops.Count); // KC_B の分だけ
    }

    [Fact]
    public void Compile_BadMouseAndAbsoluteWithoutBothAxes_AreReported()
    {
        var r = Compile(new HelperStep { Mouse = "Thumb" }, new HelperStep { Mouse = "Left", MouseAction = "Smash" },
                        new HelperStep { MoveX = 5, Absolute = true });
        Assert.Equal(3, r.Errors.Count);
        Assert.Empty(r.Ops);
    }

    // ------------------------------------------------------------------
    // TriggerTable / HelperActionResolver
    // ------------------------------------------------------------------

    private static HelperActionBody Launch(string path) => new() { Type = HelperActionBody.Launch, Path = path };

    private static HelperConfigSet Config(HelperConfig? global, params (uint Id, HelperConfig Cfg)[] devices) =>
        new(global, devices.ToDictionary(d => d.Id, d => d.Cfg));

    [Fact]
    public void Trigger_MatchesKeyAndModifiersExactly()
    {
        var table = TriggerTable.Build(Config(new HelperConfig
        {
            Triggers =
            {
                new HelperTrigger { Key = "KC_F13", Action = Launch("a.exe") },
                new HelperTrigger { Key = "KC_F13", Mods = "MOD_LSFT", Action = Launch("b.exe") },
            },
        }));
        Assert.Empty(table.Errors);
        Assert.Equal("a.exe", table.Lookup(0x64, 0, 0)!.Path);
        Assert.Equal("b.exe", table.Lookup(0x64, TriggerTable.ModShift, 0)!.Path);
        Assert.Null(table.Lookup(0x64, TriggerTable.ModCtrl, 0));
        Assert.Null(table.Lookup(0x64, TriggerTable.ModShift | TriggerTable.ModCtrl, 0));
        Assert.Null(table.Lookup(0x65, 0, 0));
    }

    [Theory]
    [InlineData("MOD_LCTL", 1)]
    [InlineData("MOD_RCTL", 1)]       // 左右は区別しない
    [InlineData("MOD_LSFT|MOD_RALT", 6)]
    [InlineData("ctrl|shift", 3)]
    [InlineData("WIN", 8)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void Trigger_ParseMods(string? text, int expected)
    {
        Assert.True(TriggerTable.TryParseMods(text, out byte mods));
        Assert.Equal(expected, mods);
    }

    [Fact]
    public void Trigger_ParseMods_RejectsUnknownNames()
    {
        Assert.False(TriggerTable.TryParseMods("MOD_LSFT|BOGUS", out _));
    }

    [Fact]
    public void Trigger_DeviceBeforeGlobal_ByExtraInfoHash()
    {
        var global = new HelperConfig { Triggers = { new HelperTrigger { Key = "KC_F13", Action = Launch("global.exe") } } };
        var dev1 = new HelperConfig { Triggers = { new HelperTrigger { Key = "KC_F13", Action = Launch("kb1.exe") } } };
        var table = TriggerTable.Build(Config(global, (Kb1, dev1)));

        Assert.Equal("kb1.exe", table.Lookup(0x64, 0, (ushort)(Kb1 >> 16))!.Path);
        Assert.Equal("global.exe", table.Lookup(0x64, 0, (ushort)(Kb2 >> 16))!.Path); // 別のキーボードは共通へ
        Assert.Equal("global.exe", table.Lookup(0x64, 0, 0)!.Path);                    // 不明（ドライバを通らない入力）は共通だけ
    }

    [Fact]
    public void Trigger_DeviceOnlyTrigger_DoesNotFireForUnknownOrOtherKeyboards()
    {
        var dev1 = new HelperConfig { Triggers = { new HelperTrigger { Key = "KC_F14", Action = Launch("kb1.exe") } } };
        var table = TriggerTable.Build(Config(null, (Kb1, dev1)));
        Assert.Null(table.Lookup(0x65, 0, 0));
        Assert.Null(table.Lookup(0x65, 0, (ushort)(Kb2 >> 16)));
        Assert.NotNull(table.Lookup(0x65, 0, (ushort)(Kb1 >> 16)));
    }

    [Fact]
    public void Trigger_BadEntries_AreReported_AndGoodOnesKept()
    {
        var table = TriggerTable.Build(Config(new HelperConfig
        {
            Triggers =
            {
                new HelperTrigger { Key = "KC_NOPE", Action = Launch("x") },
                new HelperTrigger { Key = "KC_F15", Mods = "MOD_BOGUS", Action = Launch("y") },
                new HelperTrigger { Key = "SC_E06A", Action = Launch("z.exe") },
            },
        }));
        Assert.Equal(2, table.Errors.Count);
        Assert.Equal("z.exe", table.Lookup(0xE06A, 0, 0)!.Path);
        Assert.False(table.IsEmpty);
    }

    [Fact]
    public void Trigger_EmptyConfig_IsEmpty()
    {
        Assert.True(TriggerTable.Build(HelperConfigSet.Empty).IsEmpty);
        Assert.True(TriggerTable.Empty.IsEmpty);
    }

    [Fact]
    public void Resolver_DeviceActionsBeforeGlobal_ById()
    {
        var global = new HelperConfig { Actions = { new HelperAction { Id = 1, Action = Launch("g1") }, new HelperAction { Id = 2, Action = Launch("g2") } } };
        var dev = new HelperConfig { Actions = { new HelperAction { Id = 1, Action = Launch("d1") } } };
        var cfg = Config(global, (Kb1, dev));

        Assert.Equal("d1", HelperActionResolver.Resolve(cfg, Kb1, 1)!.Path);
        Assert.Equal("g2", HelperActionResolver.Resolve(cfg, Kb1, 2)!.Path);
        Assert.Equal("g1", HelperActionResolver.Resolve(cfg, Kb2, 1)!.Path);
        Assert.Null(HelperActionResolver.Resolve(cfg, Kb1, 9));
    }

    [Fact]
    public void ConfigSet_HasAnyActionsAndTriggers()
    {
        Assert.False(HelperConfigSet.Empty.HasAnyActions);
        Assert.False(HelperConfigSet.Empty.HasAnyTriggers);
        var cfg = Config(null, (Kb1, new HelperConfig { Actions = { new HelperAction { Id = 1 } } }));
        Assert.True(cfg.HasAnyActions);
        Assert.False(cfg.HasAnyTriggers);
        Assert.True(Config(new HelperConfig { Triggers = { new HelperTrigger { Key = "KC_F13" } } }).HasAnyTriggers);
    }

    [Fact]
    public void HelperJson_RoundTrips_AndReadsHandWrittenSteps()
    {
        const string json = """
        {
          // コメントと末尾のカンマも読める
          "Actions": [
            { "Id": 3, "Name": "貼り付け", "Action": { "Type": "Sequence", "Repeat": true,
              "Steps": [ { "Key": "LCTL(KC_V)" }, { "DelayMs": 50 }, { "Text": "こんにちは" }, ],
              "ReleaseSteps": [ { "Mouse": "Left", "MouseAction": "Up" } ] } }
          ],
          "Triggers": [ { "Key": "KC_F13", "Mods": "MOD_LSFT", "Action": { "Type": "AppLayer", "Layers": [2], "LayerOp": "Hold" } } ],
        }
        """;
        var cfg = HelperConfig.FromJson(json)!;
        var body = cfg.Actions.Single().Action;
        Assert.Equal(HelperActionBody.Sequence, body.Type);
        Assert.True(body.Repeat);
        Assert.Equal(3, body.Steps!.Count);
        Assert.Equal("LCTL(KC_V)", body.Steps[0].Key);
        Assert.Equal(50, body.Steps[1].DelayMs);
        Assert.Equal("こんにちは", body.Steps[2].Text);
        Assert.Equal("Up", body.ReleaseSteps!.Single().MouseAction);
        Assert.Equal(new[] { 2 }, cfg.Triggers.Single().Action.Layers);

        var again = HelperConfig.FromJson(cfg.ToJson())!;
        Assert.Equal(cfg.ToJson(), again.ToJson());
    }

    // ------------------------------------------------------------------
    // ActionExecutor
    // ------------------------------------------------------------------

    private sealed class FakeEnv : IActionEnvironment
    {
        public List<string> Log = new();
        public List<InputOp> Sent = new();
        public List<int> Sleeps = new();
        public List<(string Path, string? Args, string? WorkDir)> Shells = new();
        public List<LayerRequest> Layers = new();
        public int PauseToggles;
        public bool SleepResult = true;

        public void Send(InputOp op) => Sent.Add(op);
        public bool Sleep(int milliseconds) { Sleeps.Add(milliseconds); return SleepResult; }
        public void Shell(string path, string? args, string? workDir) => Shells.Add((path, args, workDir));
        public void TogglePause() => PauseToggles++;
        public void Layer(LayerRequest request) => Layers.Add(request);
        void IActionEnvironment.Log(string message) => Log.Add(message);
    }

    private static ActionRequest Press(HelperActionBody b, bool repeat = false) => new(b, Kb1, (ushort)(Kb1 >> 16), false, repeat);
    private static ActionRequest Release(HelperActionBody b) => new(b, Kb1, (ushort)(Kb1 >> 16), true, false);

    [Fact]
    public void Launch_RunsOnPress_ExpandsEnvironment_IgnoresReleaseAndRepeat()
    {
        var env = new FakeEnv();
        var ex = new ActionExecutor(env);
        var body = new HelperActionBody { Type = HelperActionBody.Launch, Path = "%SystemRoot%\\notepad.exe", Args = "a.txt", WorkDir = "C:\\w" };

        ex.Execute(Press(body));
        ex.Execute(Press(body, repeat: true));
        ex.Execute(Release(body));

        var shell = Assert.Single(env.Shells);
        Assert.EndsWith("notepad.exe", shell.Path);
        Assert.DoesNotContain("%", shell.Path);
        Assert.Equal("a.txt", shell.Args);
        Assert.Equal("C:\\w", shell.WorkDir);
    }

    [Fact]
    public void Open_PassesNoArguments_AndEmptyPathIsLogged()
    {
        var env = new FakeEnv();
        var ex = new ActionExecutor(env);
        ex.Execute(Press(new HelperActionBody { Type = HelperActionBody.Open, Path = "https://example.com", Args = "ignored" }));
        Assert.Null(env.Shells.Single().Args);

        ex.Execute(Press(new HelperActionBody { Type = HelperActionBody.Open, Path = " " }));
        Assert.Single(env.Shells);
        Assert.Single(env.Log);
    }

    [Fact]
    public void Sequence_RunsStepsOnPress_AndReleaseStepsOnRelease()
    {
        var env = new FakeEnv();
        var ex = new ActionExecutor(env);
        var body = new HelperActionBody
        {
            Type = HelperActionBody.Sequence,
            Steps = new() { new HelperStep { Mouse = "Left", MouseAction = "Down" }, new HelperStep { DelayMs = 30 }, new HelperStep { Key = "KC_A" } },
            ReleaseSteps = new() { new HelperStep { Mouse = "Left", MouseAction = "Up" } },
        };

        ex.Execute(Press(body));
        Assert.Equal(new[] { 30 }, env.Sleeps);
        Assert.Equal(3, env.Sent.Count);
        Assert.Equal(new InputOp(InputKind.MouseButton, HelperMouseButtons.Left), env.Sent[0]);

        env.Sent.Clear();
        ex.Execute(Release(body));
        Assert.Equal(new[] { new InputOp(InputKind.MouseButton, HelperMouseButtons.Left, Up: true) }, env.Sent);
    }

    [Fact]
    public void Sequence_RepeatOnlyWhenAsked()
    {
        var env = new FakeEnv();
        var ex = new ActionExecutor(env);
        var once = new HelperActionBody { Type = HelperActionBody.Sequence, Steps = new() { new HelperStep { Key = "KC_A" } } };
        var repeating = new HelperActionBody { Type = HelperActionBody.Sequence, Repeat = true, Steps = once.Steps };

        ex.Execute(Press(once, repeat: true));
        Assert.Empty(env.Sent);
        ex.Execute(Press(repeating, repeat: true));
        Assert.Equal(2, env.Sent.Count);
    }

    [Fact]
    public void Sequence_StopsWhenSleepIsInterrupted()
    {
        var env = new FakeEnv { SleepResult = false };
        var ex = new ActionExecutor(env);
        var body = new HelperActionBody
        {
            Type = HelperActionBody.Sequence,
            Steps = new() { new HelperStep { Key = "KC_A" }, new HelperStep { DelayMs = 100 }, new HelperStep { Key = "KC_B" } },
        };
        ex.Execute(Press(body));
        Assert.Equal(2, env.Sent.Count); // KC_A の分だけ。待ちで打ち切り
    }

    [Fact]
    public void Sequence_LogsCompileErrors_ButRunsTheRest()
    {
        var env = new FakeEnv();
        new ActionExecutor(env).Execute(Press(new HelperActionBody
        {
            Type = HelperActionBody.Sequence,
            Steps = new() { new HelperStep { Key = "BOGUS" }, new HelperStep { Key = "KC_A" } },
        }));
        Assert.Single(env.Log);
        Assert.Equal(2, env.Sent.Count);
    }

    [Fact]
    public void AppLayer_OnOffToggle_AreRequestedOnPressOnly()
    {
        var env = new FakeEnv();
        var ex = new ActionExecutor(env);
        foreach (var (op, expected) in new[] { ("On", LayerOp.On), ("Off", LayerOp.Off), ("Toggle", LayerOp.Toggle), (null, LayerOp.On) })
        {
            env.Layers.Clear();
            var body = new HelperActionBody { Type = HelperActionBody.AppLayer, Layers = new() { 2, 2, 20, -1 }, LayerOp = op };
            ex.Execute(Press(body));
            ex.Execute(Release(body));
            var req = Assert.Single(env.Layers);
            Assert.Equal(expected, req.Op);
            Assert.Equal(new[] { 2 }, req.Layers);       // 範囲外と重複は除く
            Assert.Equal(Kb1, req.UniqId);
            Assert.Equal((ushort)(Kb1 >> 16), req.DeviceHash16);
        }
    }

    [Fact]
    public void AppLayer_Hold_OnWhilePressed_OffOnRelease_IgnoresRepeat()
    {
        var env = new FakeEnv();
        var ex = new ActionExecutor(env);
        var body = new HelperActionBody { Type = HelperActionBody.AppLayer, Layers = new() { 3 }, LayerOp = "hold", Repeat = true };

        ex.Execute(Press(body));
        ex.Execute(Press(body, repeat: true));
        ex.Execute(Release(body));

        Assert.Equal(new[] { LayerOp.On, LayerOp.Off }, env.Layers.Select(l => l.Op));
    }

    [Fact]
    public void AppLayer_WithoutLayers_IsLogged()
    {
        var env = new FakeEnv();
        new ActionExecutor(env).Execute(Press(new HelperActionBody { Type = HelperActionBody.AppLayer, Layers = new() }));
        Assert.Empty(env.Layers);
        Assert.Single(env.Log);
    }

    [Fact]
    public void PauseToggle_OnPressOnly_AndUnknownTypeIsLogged()
    {
        var env = new FakeEnv();
        var ex = new ActionExecutor(env);
        var pause = new HelperActionBody { Type = HelperActionBody.PauseToggle };
        ex.Execute(Press(pause));
        ex.Execute(Release(pause));
        Assert.Equal(1, env.PauseToggles);

        ex.Execute(Press(new HelperActionBody { Type = "Teleport" }));
        Assert.Single(env.Log);
    }

    // ------------------------------------------------------------------
    // ActionLayerController
    // ------------------------------------------------------------------

    private sealed class FakeDevices : IDriverAccess
    {
        public List<DeviceInfo> Devices = new();
        public List<DeviceInfo> ListDevices() => Devices.ToList();
        public void SetEnabled(uint uniqId, bool enabled) { }
    }

    private static DeviceInfo Device(uint id, bool hasMap = true) => new(id, hasMap, true, 1, "HID\\VID_04FE");

    private long _now = 10_000;
    private AppLayerArbiterTests.FakeLayerDriver _driver = null!;
    private AppLayerArbiter _arbiter = null!;
    private FakeDevices _devices = null!;

    private ActionLayerController MakeLayers(params DeviceInfo[] devices)
    {
        _now = 10_000;
        _driver = new AppLayerArbiterTests.FakeLayerDriver();
        _arbiter = new AppLayerArbiter(_driver);
        _devices = new FakeDevices { Devices = devices.ToList() };
        return new ActionLayerController(_arbiter, _devices, () => _now);
    }

    private static LayerRequest Req(uint id, LayerOp op, params int[] layers) => new(id, (ushort)(id >> 16), layers, op);

    [Fact]
    public void Layers_On_Off_Toggle()
    {
        var c = MakeLayers(Device(Kb1));
        c.Request(Req(Kb1, LayerOp.On, 2));
        c.Tick(true);
        Assert.True(_driver.Bit(Kb1, 2));

        c.Request(Req(Kb1, LayerOp.Toggle, 2));
        c.Tick(true);
        Assert.False(_driver.Bit(Kb1, 2));
        Assert.False(_arbiter.Holding); // 何も立てていないので、ハンドルを手放す

        c.Request(Req(Kb1, LayerOp.Toggle, 2));
        c.Request(Req(Kb1, LayerOp.On, 3));
        c.Tick(true);
        Assert.True(_driver.Bit(Kb1, 2));
        Assert.True(_driver.Bit(Kb1, 3));

        c.Request(Req(Kb1, LayerOp.Off, 2));
        c.Tick(true);
        Assert.False(_driver.Bit(Kb1, 2));
        Assert.True(_driver.Bit(Kb1, 3));
    }

    [Fact]
    public void Layers_CoexistWithOtherSources()
    {
        var c = MakeLayers(Device(Kb1));
        _arbiter.SetBit("ime", Kb1, 1, true);
        _arbiter.SetSource("rule", new[] { (Kb1, 3) });

        c.Request(Req(Kb1, LayerOp.On, 3));
        c.Tick(true);
        c.Request(Req(Kb1, LayerOp.Off, 3));
        c.Tick(true);

        Assert.True(_driver.Bit(Kb1, 1));   // IME 連動の分は残る
        Assert.True(_driver.Bit(Kb1, 3));   // 前面のアプリの規則の分も残る
    }

    [Fact]
    public void Layers_UnknownKeyboard_TargetsByHashOrAll()
    {
        var c = MakeLayers(Device(Kb1), Device(Kb2), Device(0x11110000, hasMap: false));

        c.Request(new LayerRequest(0, (ushort)(Kb2 >> 16), new[] { 4 }, LayerOp.On)); // ハッシュだけ
        c.Tick(true);
        Assert.True(_driver.Bit(Kb2, 4));
        Assert.False(_driver.Bit(Kb1, 4));

        c.Request(new LayerRequest(0, 0, new[] { 5 }, LayerOp.On)); // 何も分からない = レイヤーマップのある全キーボード
        c.Tick(true);
        Assert.True(_driver.Bit(Kb1, 5));
        Assert.True(_driver.Bit(Kb2, 5));
        Assert.False(_driver.Bit(0x11110000, 5));
    }

    [Fact]
    public void Layers_Inactive_ClearsHeldAndDropsRequests()
    {
        var c = MakeLayers(Device(Kb1));
        c.Request(Req(Kb1, LayerOp.On, 2));
        c.Tick(true);
        Assert.True(_driver.Bit(Kb1, 2));

        c.Request(Req(Kb1, LayerOp.On, 3));
        c.Tick(false); // ロック・一時停止
        Assert.False(_driver.Bit(Kb1, 2));
        Assert.False(_driver.Bit(Kb1, 3));
        Assert.Empty(c.Held);

        c.Tick(true);  // 解除されても、捨てた依頼は戻らない
        Assert.False(_driver.Bit(Kb1, 3));
    }

    [Fact]
    public void Layers_VerifyRestoresBitsLostByReconnect_OnTheInterval()
    {
        var c = MakeLayers(Device(Kb1));
        c.Request(Req(Kb1, LayerOp.On, 2));
        c.Tick(true);

        _driver.Bits.Clear(); // キーボードの再接続で消えた
        c.Tick(true);
        Assert.False(_driver.Bit(Kb1, 2)); // まだ間隔内

        _now += ActionLayerController.VerifyIntervalMs;
        c.Tick(true);
        Assert.True(_driver.Bit(Kb1, 2));
    }

    [Fact]
    public void Layers_DueInMs_ReflectsPendingAndHeld()
    {
        var c = MakeLayers(Device(Kb1));
        Assert.Null(c.DueInMs);
        c.Request(Req(Kb1, LayerOp.On, 2));
        Assert.Equal(0L, c.DueInMs);
        c.Tick(true);
        Assert.Equal(ActionLayerController.VerifyIntervalMs, c.DueInMs);
        c.Shutdown();
        Assert.Null(c.DueInMs);
        Assert.False(_driver.Bit(Kb1, 2));
    }
}
