using Kasane.Interop;
using Kasane.Tray;

namespace Kasane.Tests;

/// <summary>IME連動（ImeLinkController）。偽のドライバと偽の時計で、実機・IMEなしに動きを確かめる。</summary>
public class ImeLinkControllerTests
{
    private sealed class FakeDriver : IImeLayerDriver
    {
        public List<NicolaTarget> Targets = new();
        public Dictionary<(uint Id, int Layer), bool> Bits = new();
        public int SetCalls, ReleaseCalls, FindCalls;
        public bool FailNext, FailFind;

        public IReadOnlyList<NicolaTarget> FindTargets()
        {
            FindCalls++;
            if (FailFind) throw new KbdLayerException("find", 6);
            return Targets.ToList();
        }

        public bool IsAppLayerOn(uint uniqId, int layer)
        {
            if (FailNext) { FailNext = false; throw new KbdLayerException("get", 6); }
            return Bits.TryGetValue((uniqId, layer), out var on) && on;
        }

        public void SetAppLayer(uint uniqId, int layer, bool on) { SetCalls++; Bits[(uniqId, layer)] = on; }

        public void Release() { ReleaseCalls++; Bits.Clear(); } // ハンドルを閉じるとアプリ管理レイヤーは0に戻る

        public bool Bit(uint id, int layer) => Bits.TryGetValue((id, layer), out var on) && on;
    }

    private static readonly ImeState Kana = new(true, true, ImeConversion.Native);
    private static readonly ImeState KanaFullShape = new(true, true, ImeConversion.Native | ImeConversion.FullShape);
    private static readonly ImeState Katakana = new(true, true, ImeConversion.Native | ImeConversion.Katakana);
    private static readonly ImeState Alnum = new(true, true, 0);      // IMEはONだが半角英数
    private static readonly ImeState Off = new(true, false, 0);

    private long _now;
    private FakeDriver _driver = null!;
    private ImeLinkController Make(params NicolaTarget[] targets)
    {
        _now = 10_000;
        _driver = new FakeDriver { Targets = targets.ToList() };
        return new ImeLinkController(_driver, () => _now);
    }

    // ------------------------------------------------------------------
    // 判定
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(true, true, 1u, true)]    // かな
    [InlineData(true, true, 9u, true)]    // かな・全角
    [InlineData(true, true, 3u, true)]    // カタカナ
    [InlineData(true, true, 0u, false)]   // IME ON でも半角英数
    [InlineData(true, false, 0u, false)]  // IME OFF
    [InlineData(true, false, 1u, false)]  // OFF（モードのビットが残っていても）
    public void Decide_Uses_Open_And_Native_Mode(bool known, bool open, uint mode, bool expected)
    {
        Assert.Equal(expected, ImeLinkController.Decide(new ImeState(known, open, mode), previous: !expected));
    }

    [Fact]
    public void Decide_Keeps_The_Previous_Value_When_The_State_Is_Unknown()
    {
        Assert.True(ImeLinkController.Decide(ImeState.Unknown, previous: true));
        Assert.False(ImeLinkController.Decide(ImeState.Unknown, previous: false));
    }

    [Fact]
    public void KanaInput_Property_Matches()
    {
        Assert.True(Kana.IsKanaInput);
        Assert.True(KanaFullShape.IsKanaInput);
        Assert.True(Katakana.IsKanaInput);
        Assert.False(Alnum.IsKanaInput);
        Assert.False(Off.IsKanaInput);
        Assert.False(ImeState.Unknown.IsKanaInput);
    }

    // ------------------------------------------------------------------
    // コントローラ
    // ------------------------------------------------------------------

    [Fact]
    public void Kana_Mode_Raises_The_Nicola_Layer_And_Off_Lowers_It()
    {
        var c = Make(new NicolaTarget(1, 1));
        var s = c.Tick(Kana, enabled: true, sessionActive: true);
        Assert.True(_driver.Bit(1, 1));
        Assert.True(s.Linked);
        Assert.True(s.NicolaOn);
        Assert.Equal(1, s.TargetCount);

        s = c.Tick(Off, true, true);
        Assert.False(_driver.Bit(1, 1));
        Assert.False(s.NicolaOn);
    }

    [Fact]
    public void Uses_The_Layer_Of_Each_Keyboards_Nicola_Setting()
    {
        var c = Make(new NicolaTarget(1, 1), new NicolaTarget(2, 3));
        c.Tick(Kana, true, true);
        Assert.True(_driver.Bit(1, 1));
        Assert.True(_driver.Bit(2, 3));
        Assert.False(_driver.Bit(1, 3));
    }

    [Fact]
    public void Does_Not_Touch_The_Driver_Again_While_Nothing_Changes()
    {
        var c = Make(new NicolaTarget(1, 1));
        c.Tick(Kana, true, true);
        int calls = _driver.SetCalls;
        for (int i = 0; i < 5; i++) { _now += 100; c.Tick(Kana, true, true); }
        Assert.Equal(calls, _driver.SetCalls);
    }

    [Fact]
    public void Repairs_A_Lost_App_Layer_At_The_Next_Verification()
    {
        // キーボードを接続し直すと、ドライバ側のアプリ管理レイヤーは失われる。
        var c = Make(new NicolaTarget(1, 1));
        c.Tick(Kana, true, true);
        _driver.Bits.Clear();
        _now += ImeLinkController.VerifyIntervalMs;
        c.Tick(Kana, true, true);
        Assert.True(_driver.Bit(1, 1));
    }

    [Fact]
    public void Keeps_The_Previous_State_While_The_Ime_State_Is_Unknown()
    {
        var c = Make(new NicolaTarget(1, 1));
        c.Tick(Kana, true, true);
        _now += 200;
        var s = c.Tick(ImeState.Unknown, true, true); // 管理者のウィンドウ等
        Assert.True(_driver.Bit(1, 1));
        Assert.True(s.NicolaOn);
        _now += 200;
        c.Tick(Off, true, true);
        _now += 200;
        c.Tick(ImeState.Unknown, true, true);
        Assert.False(_driver.Bit(1, 1));
    }

    [Fact]
    public void Disabling_Gives_The_Handle_Back_And_Reenabling_Raises_The_Layer_Again()
    {
        var c = Make(new NicolaTarget(1, 1));
        c.Tick(Kana, true, true);
        var s = c.Tick(Kana, enabled: false, sessionActive: true);
        Assert.Equal(1, _driver.ReleaseCalls);
        Assert.False(_driver.Bit(1, 1));
        Assert.False(s.Linked);

        _now += 100;
        c.Tick(Kana, true, true);
        Assert.True(_driver.Bit(1, 1));
    }

    [Fact]
    public void An_Inactive_Session_Releases_The_Layer()
    {
        var c = Make(new NicolaTarget(1, 1));
        c.Tick(Kana, true, true);
        c.Tick(Kana, enabled: true, sessionActive: false); // ロック・別ユーザー
        Assert.Equal(1, _driver.ReleaseCalls);
        Assert.False(_driver.Bit(1, 1));
        c.Tick(Kana, true, true); // 戻ってきたら、また立てる
        Assert.True(_driver.Bit(1, 1));
    }

    [Fact]
    public void Without_Targets_It_Holds_No_Handle()
    {
        var c = Make();
        var s = c.Tick(Kana, true, true);
        Assert.Equal(0, _driver.SetCalls);
        Assert.Equal(0, _driver.ReleaseCalls);
        Assert.True(s.Linked);
        Assert.Equal(0, s.TargetCount);
        Assert.False(s.NicolaOn);
    }

    [Fact]
    public void A_Keyboard_That_Gets_Nicola_Later_Is_Picked_Up_At_The_Next_Refresh()
    {
        var c = Make();
        c.Tick(Kana, true, true);
        _driver.Targets.Add(new NicolaTarget(7, 1));
        _now += ImeLinkController.TargetRefreshIntervalMs;
        c.Tick(Kana, true, true);
        Assert.True(_driver.Bit(7, 1));
    }

    [Fact]
    public void When_The_Last_Target_Goes_Away_The_Handle_Is_Released()
    {
        var c = Make(new NicolaTarget(1, 1));
        c.Tick(Kana, true, true);
        _driver.Targets.Clear();
        _now += ImeLinkController.TargetRefreshIntervalMs;
        c.Tick(Kana, true, true);
        Assert.Equal(1, _driver.ReleaseCalls);
    }

    [Fact]
    public void A_Driver_Failure_Does_Not_Throw_And_Is_Retried()
    {
        var c = Make(new NicolaTarget(1, 1));
        _driver.FailNext = true;
        var ex = Record.Exception(() => c.Tick(Kana, true, true));
        Assert.Null(ex);
        Assert.False(_driver.Bit(1, 1));

        _now += 100;
        c.Tick(Kana, true, true);
        Assert.True(_driver.Bit(1, 1));
    }

    // ------------------------------------------------------------------
    // 対象から外れたキーボード・一覧の取得失敗・レイヤーマップOFF
    // ------------------------------------------------------------------

    [Fact]
    public void A_Keyboard_That_Loses_Nicola_Gets_Its_Layer_Lowered_While_Others_Stay()
    {
        // 2台とも対象のとき、1台のNICOLAをクリアしても、その台のレイヤーが残っていた（ハンドルは、もう1台のために開いたまま）。
        var c = Make(new NicolaTarget(1, 1), new NicolaTarget(2, 1));
        c.Tick(Kana, true, true);
        Assert.True(_driver.Bit(1, 1));
        Assert.True(_driver.Bit(2, 1));

        _driver.Targets.RemoveAll(t => t.UniqId == 1); // --clear-nicola 1
        _now += ImeLinkController.TargetRefreshIntervalMs;
        var s = c.Tick(Kana, true, true);

        Assert.False(_driver.Bit(1, 1));
        Assert.True(_driver.Bit(2, 1));
        Assert.Equal(0, _driver.ReleaseCalls);
        Assert.Equal(1, s.TargetCount);
    }

    [Fact]
    public void Changing_The_Nicola_Layer_Lowers_The_Old_Layer()
    {
        var c = Make(new NicolaTarget(1, 1), new NicolaTarget(2, 1));
        c.Tick(Kana, true, true);
        _driver.Targets[0] = new NicolaTarget(1, 3);
        _now += ImeLinkController.TargetRefreshIntervalMs;
        c.Tick(Kana, true, true);
        Assert.False(_driver.Bit(1, 1));
        Assert.True(_driver.Bit(1, 3));
    }

    [Fact]
    public void A_Dropped_Keyboard_That_Is_Gone_Does_Not_Break_The_Tick()
    {
        var c = Make(new NicolaTarget(1, 1), new NicolaTarget(2, 1));
        c.Tick(Kana, true, true);
        _driver.Targets.RemoveAll(t => t.UniqId == 1);
        _driver.FailNext = true; // 下ろす操作（IsAppLayerOn/SetAppLayer）が失敗しても、例外にしない
        _now += ImeLinkController.TargetRefreshIntervalMs;
        var ex = Record.Exception(() => c.Tick(Kana, true, true));
        Assert.Null(ex);
    }

    [Fact]
    public void A_Transient_Failure_To_List_Keyboards_Keeps_The_Layers_And_The_Handle()
    {
        var c = Make(new NicolaTarget(1, 1));
        c.Tick(Kana, true, true);
        _driver.FailFind = true;
        _now += ImeLinkController.TargetRefreshIntervalMs;
        var s = c.Tick(Kana, true, true);

        Assert.Equal(0, _driver.ReleaseCalls);
        Assert.True(_driver.Bit(1, 1));
        Assert.Equal(1, s.TargetCount);
        Assert.True(s.NicolaOn);
    }

    [Fact]
    public void Nicola_Is_Reported_On_Only_While_A_Keyboard_Has_Its_Layer_Map_On()
    {
        // 緊急解除・一時停止のキーボードは、レイヤーを立てても入力は変わらない。表示は「入力中」にしない。
        var c = Make(new NicolaTarget(1, 1, MapEnabled: false));
        var s = c.Tick(Kana, true, true);
        Assert.True(_driver.Bit(1, 1));   // レイヤーは立てておく（マップがONになった瞬間から効く）
        Assert.False(s.NicolaOn);
        Assert.Equal(1, s.TargetCount);

        _driver.Targets[0] = new NicolaTarget(1, 1, MapEnabled: true);
        _now += ImeLinkController.TargetRefreshIntervalMs;
        s = c.Tick(Kana, true, true);
        Assert.True(s.NicolaOn);
    }

    [Fact]
    public void Shutdown_Releases_The_Handle()
    {
        var c = Make(new NicolaTarget(1, 1));
        c.Tick(Kana, true, true);
        c.Shutdown();
        Assert.Equal(1, _driver.ReleaseCalls);
        Assert.False(_driver.Bit(1, 1));
    }
}
