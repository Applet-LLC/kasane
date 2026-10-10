using Kasane.Interop;
using Kasane.Tray;

namespace Kasane.Tests;

/// <summary>
/// アプリ管理レイヤーの合成器（AppLayerArbiter）。IME連動と前面のアプリの規則が、同じハンドルで、お互いのビットを
/// 消さずに立てられることを、偽のドライバで確かめる。
/// </summary>
public class AppLayerArbiterTests
{
    internal sealed class FakeLayerDriver : ILayerStateDriver
    {
        public readonly Dictionary<(uint Id, int Layer), bool> Bits = new();
        public int SetCalls, ReleaseCalls;
        public bool FailNextSet;

        public bool IsOn(uint uniqId, int layer) => Bits.TryGetValue((uniqId, layer), out var on) && on;

        public void Set(uint uniqId, int layer, bool on)
        {
            if (FailNextSet) { FailNextSet = false; Release(); throw new KbdLayerException("set", 6); } // 実装は失敗でハンドルを閉じる
            SetCalls++;
            Bits[(uniqId, layer)] = on;
        }

        public void Release() { ReleaseCalls++; Bits.Clear(); } // ハンドルを閉じるとアプリ管理レイヤーは0に戻る

        public bool Bit(uint id, int layer) => IsOn(id, layer);
    }

    private const uint Kb = 0xCDEC0000;

    [Fact]
    public void SameBitFromTwoSources_StaysOnUntilBothRelease()
    {
        var driver = new FakeLayerDriver();
        var arbiter = new AppLayerArbiter(driver);

        arbiter.SetBit("ime", Kb, 1, true);
        arbiter.SetSource("rule", new[] { (Kb, 1) });
        Assert.True(driver.Bit(Kb, 1));

        arbiter.SetBit("ime", Kb, 1, false);
        Assert.True(driver.Bit(Kb, 1)); // rule がまだ立てたい
        Assert.Equal(0, driver.ReleaseCalls);

        arbiter.ClearSource("rule");
        Assert.False(driver.Bit(Kb, 1));
        Assert.Equal(1, driver.ReleaseCalls); // 全部空になって、ハンドルを閉じた
        Assert.False(arbiter.Holding);
    }

    [Fact]
    public void DifferentBits_AreIndependent()
    {
        var driver = new FakeLayerDriver();
        var arbiter = new AppLayerArbiter(driver);

        arbiter.SetBit("ime", Kb, 1, true);
        arbiter.SetSource("rule", new[] { (Kb, 3) });
        Assert.True(driver.Bit(Kb, 1));
        Assert.True(driver.Bit(Kb, 3));

        arbiter.ClearSource("ime"); // IME 連動の Release
        Assert.False(driver.Bit(Kb, 1));
        Assert.True(driver.Bit(Kb, 3));
        Assert.Equal(0, driver.ReleaseCalls); // rule が残っているので、ハンドルは閉じない
        Assert.True(arbiter.Holding);
    }

    [Fact]
    public void ReleaseOnlyWhenEverySourceIsEmpty()
    {
        var driver = new FakeLayerDriver();
        var arbiter = new AppLayerArbiter(driver);

        arbiter.SetSource("rule", new[] { (Kb, 2) });
        arbiter.SetSource("rule", new[] { (Kb, 4) }); // 置き換え: 2 を下ろし、4 を立てる
        Assert.False(driver.Bit(Kb, 2));
        Assert.True(driver.Bit(Kb, 4));
        Assert.Equal(0, driver.ReleaseCalls);

        arbiter.SetSource("rule", Array.Empty<(uint, int)>());
        Assert.Equal(1, driver.ReleaseCalls);

        // 何も立てていないときに空にしても、閉じ直さない。
        arbiter.ClearSource("ime");
        Assert.Equal(1, driver.ReleaseCalls);
    }

    [Fact]
    public void ReconcileOnlyTouchesTheDifference()
    {
        var driver = new FakeLayerDriver();
        var arbiter = new AppLayerArbiter(driver);

        arbiter.SetSource("rule", new[] { (Kb, 3) });
        int calls = driver.SetCalls;
        arbiter.SetSource("rule", new[] { (Kb, 3) });
        arbiter.Reconcile();
        Assert.Equal(calls, driver.SetCalls);
    }

    [Fact]
    public void Verify_RestoresBitsLostByReconnect()
    {
        var driver = new FakeLayerDriver();
        var arbiter = new AppLayerArbiter(driver);
        arbiter.SetSource("rule", new[] { (Kb, 3) });

        driver.Bits.Clear(); // キーボードを接続し直して、アプリ管理レイヤーが消えた
        arbiter.Verify();
        Assert.True(driver.Bit(Kb, 3));
    }

    [Fact]
    public void SetBitOn_ReassertsEvenIfAlreadyRecorded()
    {
        // IME 連動は「実際の状態が違う」ときに SetAppLayer を呼ぶ。記録の上では立ててあっても、実際に消えていれば立て直す。
        var driver = new FakeLayerDriver();
        var arbiter = new AppLayerArbiter(driver);
        arbiter.SetBit("ime", Kb, 1, true);
        driver.Bits.Clear();
        arbiter.SetBit("ime", Kb, 1, true);
        Assert.True(driver.Bit(Kb, 1));
    }

    [Fact]
    public void DriverFailure_ForgetsAppliedBits_AndNextReconcileRestoresAll()
    {
        var driver = new FakeLayerDriver();
        var arbiter = new AppLayerArbiter(driver);
        arbiter.SetBit("ime", Kb, 1, true);

        driver.FailNextSet = true; // 失敗でハンドルが閉じ、ime のビットも消える
        Assert.Throws<KbdLayerException>(() => arbiter.SetSource("rule", new[] { (Kb, 3) }));
        Assert.False(driver.Bit(Kb, 1));

        arbiter.Reconcile();
        Assert.True(driver.Bit(Kb, 1));
        Assert.True(driver.Bit(Kb, 3));
    }

    [Fact]
    public void ImeLinkController_OnTheArbiter_KeepsRuleBits()
    {
        // IME 連動（既存の ImeLinkController）を、アダプター越しに合成器へ載せたときの動き。
        // IME が NICOLA のレイヤー 1 を立てたり下ろしたりしても、規則が立てたレイヤー 3 は残る。
        var driver = new FakeLayerDriver();
        var arbiter = new AppLayerArbiter(driver);
        var ime = new ArbiterImeDriver(arbiter, new NicolaTarget(Kb, 1));
        long now = 10_000;
        var controller = new ImeLinkController(ime, () => now);
        var kana = new ImeState(true, true, ImeConversion.Native);
        var off = new ImeState(true, false, 0);

        arbiter.SetSource("rule", new[] { (Kb, 3) });
        controller.Tick(kana, enabled: true, sessionActive: true);
        Assert.True(driver.Bit(Kb, 1));
        Assert.True(driver.Bit(Kb, 3));

        now += 50;
        controller.Tick(off, enabled: true, sessionActive: true);
        Assert.False(driver.Bit(Kb, 1));
        Assert.True(driver.Bit(Kb, 3));

        controller.Shutdown(); // IME 連動を止める（Release）
        Assert.True(driver.Bit(Kb, 3));
        Assert.Equal(0, driver.ReleaseCalls);
    }

    // ImeLayerSource と同じ橋渡し（FindTargets だけは、実ドライバの代わりに固定の対象を返す）。
    private sealed class ArbiterImeDriver : IImeLayerDriver
    {
        private readonly AppLayerArbiter _arbiter;
        private readonly NicolaTarget[] _targets;
        public ArbiterImeDriver(AppLayerArbiter arbiter, params NicolaTarget[] targets) { _arbiter = arbiter; _targets = targets; }
        public IReadOnlyList<NicolaTarget> FindTargets() => _targets;
        public bool IsAppLayerOn(uint uniqId, int layer) => _arbiter.IsOn(uniqId, layer);
        public void SetAppLayer(uint uniqId, int layer, bool on) => _arbiter.SetBit("ime", uniqId, layer, on);
        public void Release() => _arbiter.ClearSource("ime");
    }
}
