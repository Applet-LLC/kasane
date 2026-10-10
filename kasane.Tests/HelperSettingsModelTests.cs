using System.IO;
using Kasane.Model;

namespace Kasane.Tests;

/// <summary>設定画面のヘルパー設定のモデル（保存先、Id の割り当て、検証、壊れたファイルの扱い）。</summary>
public sealed class HelperSettingsModelTests : IDisposable
{
    private const uint Kb1 = 0xCDEC0000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kasane-test-" + Guid.NewGuid().ToString("N"));
    private string TrayPath => Path.Combine(_dir, "tray.json");
    private string DevicePath => HelperConfig.DevicePath(Kb1, _dir);

    public HelperSettingsModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private HelperSettingsModel Make(uint uniqId = Kb1, TrayConfig? tray = null) =>
        new(uniqId, tray ?? TrayConfig.Load(TrayPath), _dir, TrayPath);

    private static HelperAction Launch(int id, string path = "notepad.exe") =>
        new() { Id = id, Name = "n" + id, Action = new HelperActionBody { Type = HelperActionBody.Launch, Path = path } };

    [Fact]
    public void NothingSaved_NothingWritten()
    {
        var m = Make();
        Assert.False(m.IsDirty());
        Assert.True(m.TrySave(out var error));
        Assert.Null(error);
        Assert.False(File.Exists(DevicePath));
        Assert.False(File.Exists(TrayPath));
    }

    [Fact]
    public void DeviceScope_IsWrittenToHelperJson_AndReadBack()
    {
        var m = Make();
        m.Device.Actions.Add(Launch(1));
        Assert.True(m.IsDirty(HelperScope.Device));
        Assert.False(m.IsDirty(HelperScope.Global));

        Assert.True(m.TrySave(out _));
        Assert.True(File.Exists(DevicePath));
        Assert.False(File.Exists(DevicePath + ".tmp"));
        Assert.False(m.IsDirty());

        var again = Make();
        Assert.Equal("notepad.exe", again.Device.Actions.Single().Action.Path);
        Assert.Empty(again.Global.Actions);
    }

    [Fact]
    public void GlobalScope_GoesIntoTrayJson_AndKeepsImeLink()
    {
        var tray = new TrayConfig { ImeLink = false };
        var m = Make(tray: tray);
        m.Global.Triggers.Add(new HelperTrigger { Key = "KC_F13", Action = new HelperActionBody { Type = HelperActionBody.PauseToggle } });
        Assert.True(m.TrySave(out _));

        var loaded = TrayConfig.Load(TrayPath);
        Assert.False(loaded.ImeLink);
        Assert.Equal("KC_F13", loaded.Helper!.Triggers.Single().Key);
        Assert.False(File.Exists(DevicePath));
    }

    [Fact]
    public void EmptiedGlobal_IsRemovedFromTrayJson()
    {
        var tray = new TrayConfig { Helper = new HelperConfig { Actions = { Launch(5) } } };
        tray.Save(TrayPath);
        var m = Make();
        m.Global.Actions.Clear();
        Assert.True(m.TrySave(out _));
        Assert.Null(TrayConfig.Load(TrayPath).Helper);
    }

    [Fact]
    public void EmptiedDevice_StillOverwritesAnExistingFile_ButNeverCreatesOne()
    {
        var m = Make();
        m.Device.Actions.Add(Launch(1));
        m.Device.Actions.Clear();
        Assert.False(m.IsDirty(HelperScope.Device)); // 変えていないのと同じ
        Assert.True(m.TrySave(out _));
        Assert.False(File.Exists(DevicePath));

        new HelperConfig { Actions = { Launch(1) } }.Save(DevicePath);
        var m2 = Make();
        m2.Device.Actions.Clear();
        Assert.True(m2.TrySave(out _));
        Assert.Empty(HelperConfig.Load(DevicePath)!.Actions);
    }

    [Fact]
    public void BrokenHelperJson_IsReportedAndNeverOverwritten()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DevicePath)!);
        File.WriteAllText(DevicePath, "{ this is not json");
        var m = Make();

        Assert.NotNull(m.DeviceLoadError);
        Assert.False(m.DeviceWritable);
        m.Device.Actions.Add(Launch(1));
        Assert.True(m.TrySave(out _));
        Assert.Equal("{ this is not json", File.ReadAllText(DevicePath));
    }

    [Fact]
    public void NoKeyboard_OnlyGlobalIsEditable()
    {
        var m = Make(uniqId: 0);
        Assert.False(m.HasDevice);
        Assert.False(m.DeviceWritable);
        Assert.Null(m.DevicePath);
        m.Device.Actions.Add(Launch(1));
        m.Global.Actions.Add(Launch(2));
        Assert.True(m.TrySave(out _));
        Assert.NotNull(TrayConfig.Load(TrayPath).Helper);
        Assert.Empty(Directory.GetDirectories(_dir));
    }

    [Fact]
    public void NextFreeActionId_SkipsIdsUsedInEitherScope()
    {
        var m = Make();
        Assert.Equal(1, m.NextFreeActionId());
        m.Device.Actions.Add(Launch(1));
        m.Global.Actions.Add(Launch(2));
        m.Global.Actions.Add(Launch(4));
        Assert.Equal(3, m.NextFreeActionId());
    }

    [Fact]
    public void NextFreeActionId_IsMinusOneWhenFull()
    {
        var m = Make();
        for (int i = 1; i <= HelperSettingsModel.MaxActionId; i++) m.Global.Actions.Add(new HelperAction { Id = i });
        Assert.Equal(-1, m.NextFreeActionId());
    }

    [Fact]
    public void SaveFailure_IsReturnedNotThrown()
    {
        var m = Make();
        Directory.CreateDirectory(Path.GetDirectoryName(DevicePath)!);
        Directory.CreateDirectory(DevicePath); // ファイルの場所にフォルダがあって書けない
        m.Device.Actions.Add(Launch(1));
        Assert.False(m.TrySave(out var error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.False(File.Exists(DevicePath + ".tmp")); // 失敗しても、一時ファイルを残さない
    }

    [Fact]
    public void Json_OmitsDefaults_ButKeepsEnabledFalseAndRepeatTrue()
    {
        var cfg = new HelperConfig
        {
            Actions = { new HelperAction { Id = 1, Action = new HelperActionBody { Type = HelperActionBody.Sequence, Repeat = true, Steps = new() { new HelperStep { Key = "KC_A" } } } } },
            AppLayerRules = { new AppLayerRule { Enabled = false, Match = { Exe = "x.exe" } } },
        };
        string json = cfg.ToJson();
        Assert.DoesNotContain("Absolute", json);
        Assert.DoesNotContain("MapOff", json);
        Assert.DoesNotContain("NodokaOr", json);
        Assert.Contains("\"Enabled\": false", json);   // 既定が true の項目は、false を省かない（省くと true に戻ってしまう）
        var back = HelperConfig.FromJson(json)!;
        Assert.True(back.Actions[0].Action.Repeat);
        Assert.False(back.AppLayerRules[0].Enabled);
        Assert.False(back.AppLayerRules[0].MapOff);
    }

    [Fact]
    public void TrayJson_OmitsNulls()
    {
        var tray = new TrayConfig { Helper = new HelperConfig { Actions = { Launch(1) } } };
        tray.Save(TrayPath);
        string json = File.ReadAllText(TrayPath);
        Assert.DoesNotContain("null", json);
        Assert.Equal("notepad.exe", TrayConfig.Load(TrayPath).Helper!.Actions.Single().Action.Path);
    }

    // ------------------------------------------------------------------
    // 検証
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_AcceptsAGoodConfig()
    {
        var m = Make();
        m.Device.Actions.Add(Launch(1));
        m.Device.Actions.Add(new HelperAction
        {
            Id = 2,
            Action = new HelperActionBody
            {
                Type = HelperActionBody.Sequence,
                Steps = new() { new HelperStep { Key = "LCTL(KC_C)" }, new HelperStep { Text = "x" } },
            },
        });
        m.Global.Triggers.Add(new HelperTrigger { Key = "KC_F13", Mods = "MOD_LSFT", Action = new HelperActionBody { Type = HelperActionBody.PauseToggle } });
        m.Global.AppLayerRules.Add(new AppLayerRule { Match = { Exe = "code.exe" }, Layers = { 3 } });
        m.Global.AppLayerRules.Add(new AppLayerRule { Default = true });
        Assert.Empty(m.Validate());
    }

    [Fact]
    public void Validate_ReportsEachKindOfMistake()
    {
        var m = Make();
        m.Device.Actions.Add(Launch(1, path: ""));                                                    // Path が空
        m.Device.Actions.Add(Launch(1));                                                              // Id の重複
        m.Device.Actions.Add(new HelperAction { Id = 70000, Action = new HelperActionBody { Type = HelperActionBody.PauseToggle } });
        m.Device.Actions.Add(new HelperAction { Id = 3, Action = new HelperActionBody { Type = HelperActionBody.Sequence } });   // 手順なし
        m.Device.Actions.Add(new HelperAction { Id = 4, Action = new HelperActionBody { Type = HelperActionBody.AppLayer, Layers = new() { 20 }, LayerOp = "Spin" } });
        m.Device.Actions.Add(new HelperAction { Id = 5, Action = new HelperActionBody { Type = "Teleport" } });
        m.Global.Triggers.Add(new HelperTrigger { Key = "KC_NOPE", Action = new HelperActionBody { Type = HelperActionBody.PauseToggle } });
        m.Global.Triggers.Add(new HelperTrigger { Key = "KC_F13", Mods = "BOGUS", Action = new HelperActionBody { Type = HelperActionBody.PauseToggle } });
        m.Global.AppLayerRules.Add(new AppLayerRule());                                               // 条件なし
        m.Global.AppLayerRules.Add(new AppLayerRule { Match = { TitleRegex = "([" } });               // 正規表現が不正

        var errors = m.Validate();
        Assert.Contains(errors, e => e.Contains("Path が空"));
        Assert.Contains(errors, e => e.Contains("Id が重複"));
        Assert.Contains(errors, e => e.Contains("Id は 0〜65535"));
        Assert.Contains(errors, e => e.Contains("手順がありません"));
        Assert.Contains(errors, e => e.Contains("レイヤーは 0〜15"));
        Assert.Contains(errors, e => e.Contains("LayerOp"));
        Assert.Contains(errors, e => e.Contains("未対応"));
        Assert.Contains(errors, e => e.Contains("KC_NOPE"));
        Assert.Contains(errors, e => e.Contains("BOGUS"));
        Assert.Contains(errors, e => e.Contains("照合条件がありません"));
        Assert.Contains(errors, e => e.Contains("TitleRegex"));
    }

    [Fact]
    public void Validate_DuplicateTriggers_AndStepErrors()
    {
        var m = Make();
        m.Global.Triggers.Add(new HelperTrigger { Key = "KC_F13", Mods = "MOD_LCTL", Action = new HelperActionBody { Type = HelperActionBody.PauseToggle } });
        m.Global.Triggers.Add(new HelperTrigger { Key = "KC_F13", Mods = "MOD_RCTL", Action = new HelperActionBody { Type = HelperActionBody.PauseToggle } }); // 左右は区別しないので重複
        m.Global.Actions.Add(new HelperAction
        {
            Id = 9,
            Action = new HelperActionBody { Type = HelperActionBody.Sequence, Steps = new() { new HelperStep { Key = "BOGUS" } }, ReleaseSteps = new() { new HelperStep { Up = "BOGUS2" } } },
        });
        var errors = m.Validate();
        Assert.Contains(errors, e => e.Contains("重複"));
        Assert.Contains(errors, e => e.Contains("BOGUS'"));
        Assert.Contains(errors, e => e.Contains("離したとき") && e.Contains("BOGUS2"));
    }

    [Fact]
    public void Validate_SkipsAnUnwritableDeviceScope()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DevicePath)!);
        File.WriteAllText(DevicePath, "nope");
        var m = Make();
        m.Device.Actions.Add(Launch(1, path: ""));
        Assert.Empty(m.Validate()); // 保存しない分は、誤りとして数えない
    }

    [Fact]
    public void TryLoad_DistinguishesMissingFromBroken()
    {
        string path = Path.Combine(_dir, "x", "helper.json");
        Assert.True(HelperConfig.TryLoad(path, out var missing, out var e1));
        Assert.Null(missing);
        Assert.Null(e1);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ bad");
        Assert.False(HelperConfig.TryLoad(path, out var broken, out var e2));
        Assert.Null(broken);
        Assert.False(string.IsNullOrEmpty(e2));
        Assert.Null(HelperConfig.Load(path)); // 従来の Load は、どちらも null

        File.WriteAllText(path, "null");
        Assert.False(HelperConfig.TryLoad(path, out _, out var e3));
        Assert.NotNull(e3);
    }

    [Fact]
    public void ConfigSetLoad_LogsUnreadableFiles()
    {
        string path = HelperConfig.DevicePath(Kb1, _dir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ bad");
        var log = new List<string>();
        var set = HelperConfigSet.Load(null, _dir, log.Add);
        Assert.Empty(set.PerDevice);
        Assert.Single(log);
        Assert.Contains("helper.json", log[0]);
    }
}

/// <summary>設定画面の一覧の1行の説明と、項目の複製。</summary>
public class HelperSummaryTests
{
    [Fact]
    public void Summaries_AreReadable()
    {
        Assert.Equal("起動: notepad.exe a.txt", HelperSummary.Body(new HelperActionBody { Type = HelperActionBody.Launch, Path = "notepad.exe", Args = "a.txt" }));
        Assert.Equal("開く: https://example.com", HelperSummary.Body(new HelperActionBody { Type = HelperActionBody.Open, Path = "https://example.com" }));
        Assert.Equal("操作 2 手順 + 離したとき 1 手順（繰り返す）", HelperSummary.Body(new HelperActionBody
        {
            Type = HelperActionBody.Sequence, Repeat = true,
            Steps = new() { new HelperStep(), new HelperStep() }, ReleaseSteps = new() { new HelperStep() },
        }));
        Assert.Equal("レイヤー 2,3 を Hold", HelperSummary.Body(new HelperActionBody { Type = HelperActionBody.AppLayer, Layers = new() { 2, 3 }, LayerOp = "Hold" }));
        Assert.Equal("一時停止・再開", HelperSummary.Body(new HelperActionBody { Type = HelperActionBody.PauseToggle }));
        Assert.Contains("不明", HelperSummary.Body(new HelperActionBody { Type = "X" }));

        Assert.Equal("HLP(3)  メモ帳  — 起動: notepad.exe", HelperSummary.Action(new HelperAction
        {
            Id = 3, Name = "メモ帳", Action = new HelperActionBody { Type = HelperActionBody.Launch, Path = "notepad.exe" },
        }));
        Assert.Equal("Ctrl+Shift+KC_F13  — 一時停止・再開", HelperSummary.Trigger(new HelperTrigger
        {
            Key = "KC_F13", Mods = "MOD_LCTL|MOD_RSFT", Action = new HelperActionBody { Type = HelperActionBody.PauseToggle },
        }));
    }

    [Fact]
    public void RuleSummary_ShowsEnabledConditionAndEffect()
    {
        var r = new AppLayerRule { Name = "ゲーム", Match = { Exe = "game.exe", TitleRegex = "Level" }, MapOff = true, Layers = { 2 } };
        Assert.Equal("[有効] ゲーム  exe=game.exe かつ title~Level → レイヤー 2、リマップOFF", HelperSummary.Rule(r));
        Assert.Equal("[無効] （既定） → 何もしない", HelperSummary.Rule(new AppLayerRule { Default = true, Enabled = false }));
        Assert.Contains("条件なし", HelperSummary.Rule(new AppLayerRule()));
    }

    [Fact]
    public void Clones_AreIndependentCopies()
    {
        var a = new HelperAction { Id = 1, Name = "a", Action = new HelperActionBody { Type = HelperActionBody.AppLayer, Layers = new() { 1 } } };
        var copy = HelperSummary.CloneAction(a);
        copy.Id = 2;
        copy.Action.Layers!.Add(5);
        Assert.Equal(1, a.Id);
        Assert.Equal(new[] { 1 }, a.Action.Layers);

        var rule = new AppLayerRule { Match = { Exe = "x.exe" }, Layers = { 1 } };
        var rc = HelperSummary.CloneRule(rule);
        rc.Match.Exe = "y.exe";
        Assert.Equal("x.exe", rule.Match.Exe);

        var t = new HelperTrigger { Key = "KC_F13", Action = new HelperActionBody { Type = HelperActionBody.PauseToggle } };
        Assert.Equal("KC_F13", HelperSummary.CloneTrigger(t).Key);
    }
}
