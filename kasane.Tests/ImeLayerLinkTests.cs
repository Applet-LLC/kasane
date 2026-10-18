using Kasane.Interop;
using Kasane.Model;
using Kasane.Tray;

namespace Kasane.Tests;

/// <summary>NICOLAとは無関係の、IMEのON/OFFとレイヤーだけの連動（2026-10-19）。偽のドライバと偽の時計で確かめる。</summary>
public class ImeLayerLinkTests
{
    private sealed class FakeDriver : IImeLayerDriver
    {
        public List<NicolaTarget> Targets = new();
        public Dictionary<(uint Id, int Layer), bool> Bits = new();
        public IReadOnlyList<NicolaTarget> FindTargets() => Targets.ToList();
        public bool IsAppLayerOn(uint uniqId, int layer) => Bits.TryGetValue((uniqId, layer), out var on) && on;
        public void SetAppLayer(uint uniqId, int layer, bool on) => Bits[(uniqId, layer)] = on;
        public void Release() => Bits.Clear();
        public bool Bit(uint id, int layer) => Bits.TryGetValue((id, layer), out var on) && on;
    }

    private static readonly ImeState Kana = new(true, true, ImeConversion.Native);
    private static readonly ImeState Alnum = new(true, true, 0);   // IMEはONだが半角英数
    private static readonly ImeState Off = new(true, false, 0);

    private long _now = 10_000;
    private FakeDriver _driver = null!;

    private ImeLinkController Make(params NicolaTarget[] targets)
    {
        _driver = new FakeDriver { Targets = targets.ToList() };
        return new ImeLinkController(_driver, () => _now);
    }

    private static NicolaTarget Layer(uint id, int layer, bool kanaOnly = false) =>
        new(id, layer, MapEnabled: true, IsNicola: false, KanaOnly: kanaOnly);

    [Fact]
    public void ImeOn_RaisesTheLayer_AndOffLowersIt_EvenInAlnumMode()
    {
        var c = Make(Layer(1, 2));
        c.Tick(Alnum, enabled: true, sessionActive: true);
        Assert.True(_driver.Bit(1, 2)); // 半角英数でも、IME が ON なら立てる

        _now += 100;
        c.Tick(Off, true, true);
        Assert.False(_driver.Bit(1, 2));
    }

    [Fact]
    public void KanaOnly_IgnoresAlnumMode()
    {
        var c = Make(Layer(1, 2, kanaOnly: true));
        c.Tick(Alnum, true, true);
        Assert.False(_driver.Bit(1, 2));

        _now += 100;
        c.Tick(Kana, true, true);
        Assert.True(_driver.Bit(1, 2));
    }

    [Fact]
    public void SeveralLayers_AreRaisedTogether()
    {
        var c = Make(Layer(1, 1), Layer(1, 3));
        c.Tick(Kana, true, true);
        Assert.True(_driver.Bit(1, 1));
        Assert.True(_driver.Bit(1, 3));
    }

    [Fact]
    public void SameLayerFromNicolaAndLayerLink_IsOr()
    {
        // NICOLA（かな入力のとき）と、レイヤーだけの連動（IME ON のとき）が同じレイヤーを指す: 半角英数では、後者だけが立てたい。
        var c = Make(new NicolaTarget(1, 2), Layer(1, 2));
        c.Tick(Alnum, true, true);
        Assert.True(_driver.Bit(1, 2));

        _now += 100;
        c.Tick(Off, true, true);
        Assert.False(_driver.Bit(1, 2));
    }

    [Fact]
    public void Status_CountsOnlyNicolaTargets_ForTheTooltip()
    {
        var onlyLayer = Make(Layer(1, 2));
        var status = onlyLayer.Tick(Kana, true, true);
        Assert.False(status.NicolaOn);
        Assert.False(status.HasNicola);
        Assert.Equal(1, status.TargetCount);

        var both = Make(new NicolaTarget(1, 2), Layer(2, 4));
        var s2 = both.Tick(Kana, true, true);
        Assert.True(s2.NicolaOn);
        Assert.True(s2.HasNicola);
        Assert.Equal(2, s2.TargetCount);
    }

    [Fact]
    public void DecideOpen_UsesOpenOnly_AndKeepsPreviousWhenUnknown()
    {
        Assert.True(ImeLinkController.DecideOpen(Alnum, previous: false));
        Assert.False(ImeLinkController.DecideOpen(Off, previous: true));
        Assert.True(ImeLinkController.DecideOpen(ImeState.Unknown, previous: true));
    }

    // ---- 設定 ----

    [Fact]
    public void Config_DevicePrecedesGlobal_AndDeviceCanDisable()
    {
        var global = new HelperConfig { ImeLayers = new ImeLayerLink { Layers = { 1 } } };
        var perDevice = new Dictionary<uint, HelperConfig>
        {
            [0xA0000] = new HelperConfig { ImeLayers = new ImeLayerLink { Enabled = false } },
            [0xB0000] = new HelperConfig { ImeLayers = new ImeLayerLink { Layers = { 5 }, Mode = ImeLayerLink.ModeKana } },
            [0xC0000] = new HelperConfig(),
        };
        var set = new HelperConfigSet(global, perDevice);

        Assert.False(set.ImeLinkFor(0xA0000)!.IsActive);            // このキーボードだけ連動しない
        Assert.True(set.ImeLinkFor(0xB0000)!.KanaOnly);              // キーボードごとの設定が先
        Assert.Equal(new[] { 5 }, set.ImeLinkFor(0xB0000)!.Layers);
        Assert.Equal(new[] { 1 }, set.ImeLinkFor(0xC0000)!.Layers);  // 設定が無いキーボードは、共通に従う
        Assert.Equal(new[] { 1 }, set.ImeLinkFor(0xD0000)!.Layers);  // 知らないキーボードも共通
        Assert.True(set.HasAnyImeLayerLink);
    }

    [Fact]
    public void Config_NothingActive_WhenEmptyOrDisabled()
    {
        Assert.False(HelperConfigSet.Empty.HasAnyImeLayerLink);
        var set = new HelperConfigSet(new HelperConfig { ImeLayers = new ImeLayerLink { Enabled = true } }, new Dictionary<uint, HelperConfig>()); // レイヤーなし
        Assert.False(set.HasAnyImeLayerLink);
    }

    [Fact]
    public void Config_RoundTripsThroughJson_AndCountsAsNonEmpty()
    {
        var c = new HelperConfig { ImeLayers = new ImeLayerLink { Layers = { 2, 3 }, Mode = ImeLayerLink.ModeKana } };
        Assert.False(HelperSettingsModel.IsEmpty(c));
        var back = HelperConfig.FromJson(c.ToJson())!;
        Assert.Equal(new[] { 2, 3 }, back.ImeLayers!.Layers);
        Assert.True(back.ImeLayers.KanaOnly);
        Assert.Null(new HelperConfig().ImeLayers);
        Assert.True(HelperSettingsModel.IsEmpty(new HelperConfig()));
    }

    [Fact]
    public void NicolaImeLink_IsOffByDefault_AndTheChoiceIsKept()
    {
        Assert.False(new TrayConfig().ImeLink); // 既定はオフ（以前はオン）

        string path = Path.Combine(Path.GetTempPath(), "kasane-imelink-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            new TrayConfig { ImeLink = true }.Save(path);
            Assert.True(TrayConfig.Load(path).ImeLink);   // 利用者がオンにしたら、保存されて残る
            Assert.False(TrayConfig.Load(path + ".none").ImeLink); // ファイルが無ければオフ
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Validation_NeedsAtLeastOneLayerWhenEnabled()
    {
        var tray = new TrayConfig { Helper = new HelperConfig { ImeLayers = new ImeLayerLink { Enabled = true } } };
        var model = new HelperSettingsModel(0, tray);
        Assert.Contains(model.Validate(), e => e.Contains("IME連動") && e.Contains("1つ以上"));
    }
}

/// <summary>NICOLAのIME連動を、キーボードごと／全キーボード共通で設定できる（2026-10-19）。</summary>
public class NicolaLinkScopeTests
{
    private static HelperConfigSet Set(Dictionary<uint, HelperConfig> perDevice) => new(null, perDevice);

    [Fact]
    public void DeviceSettingPrecedesGlobal()
    {
        var set = Set(new()
        {
            [1] = new HelperConfig { NicolaImeLink = true },
            [2] = new HelperConfig { NicolaImeLink = false },
            [3] = new HelperConfig(),
        });

        Assert.True(set.NicolaLinkFor(1, global: false));   // このキーボードだけ連動する
        Assert.False(set.NicolaLinkFor(2, global: true));   // このキーボードだけ連動しない
        Assert.True(set.NicolaLinkFor(3, global: true));    // 設定が無ければ共通に従う
        Assert.False(set.NicolaLinkFor(3, global: false));
        Assert.True(set.NicolaLinkFor(99, global: true));   // 知らないキーボードも共通
    }

    [Theory]
    [InlineData(false, false, false)]  // 共通オフ、キーボードごとの設定なし: 見ない
    [InlineData(true, false, true)]    // 共通オン
    [InlineData(false, true, true)]    // 共通オフでも、あるキーボードがオン
    public void AnyNicolaLink_DecidesWhetherToWatchTheIme(bool global, bool deviceOn, bool expected)
    {
        var set = Set(deviceOn ? new() { [1] = new HelperConfig { NicolaImeLink = true } } : new());
        Assert.Equal(expected, set.AnyNicolaLink(global));
    }

    [Fact]
    public void SettingIsSavedOnlyWhenSet_AndKeepsTheFileNonEmpty()
    {
        Assert.DoesNotContain("NicolaImeLink", new HelperConfig().ToJson());
        Assert.True(HelperSettingsModel.IsEmpty(new HelperConfig()));

        var c = new HelperConfig { NicolaImeLink = false }; // 「連動しない」も、選んだ設定として残す
        Assert.False(HelperSettingsModel.IsEmpty(c));
        var back = HelperConfig.FromJson(c.ToJson())!;
        Assert.False(back.NicolaImeLink);
    }
}
