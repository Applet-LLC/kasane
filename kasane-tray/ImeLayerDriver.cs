using Kasane.Interop;

namespace Kasane.Tray;

/// <summary>
/// 実際のドライバに対するアプリ管理レイヤーの操作。アプリ管理レイヤーは「最後に設定したハンドル」が所有し、そのハンドルを
/// 閉じると0に戻るので、レイヤーを立てている間は、ハンドルを開いたままにする（<see cref="Release"/> で閉じる）。
/// トレイの中では <see cref="AppLayerArbiter"/> だけがこれを使う（IME 連動と前面のアプリの規則が、同じハンドルを使うため）。
/// </summary>
public sealed class KbdLayerAppLayerDriver : ILayerStateDriver
{
    private KbdLayerClient? _held;

    internal static KbdLayerClient Open() =>
        KbdLayerClient.TryOpen(out var error) ?? throw new InvalidOperationException(error ?? "ドライバに接続できません");

    public bool IsOn(uint uniqId, int layer) =>
        Run(c => (c.GetLayerState(uniqId).AppLayerMask & (1u << layer)) != 0);

    public void Set(uint uniqId, int layer, bool on) =>
        Run(c => c.SetLayerState(uniqId, on ? KbdLayerClient.LayerOpOn : KbdLayerClient.LayerOpOff,
                                 (uint)layer, 0, KbdLayerClient.LayerFlagNoNicolaKeys));

    public void Release()
    {
        _held?.Dispose();
        _held = null;
    }

    /// <summary>失敗したらハンドルを捨てる（ドライバの入れ替え・再接続で無効になっていても、次の周期で開き直す）。</summary>
    private T Run<T>(Func<KbdLayerClient, T> action)
    {
        try
        {
            _held ??= Open();
            return action(_held);
        }
        catch
        {
            Release();
            throw;
        }
    }
}

/// <summary>
/// IME 連動（<see cref="ImeLinkController"/>）から見たドライバ。レイヤーの操作は <see cref="AppLayerArbiter"/> の
/// "ime" の入力源への希望として渡す（前面のアプリの規則が立てたレイヤーを、IME 連動が下ろしてしまわないように）。
///  - <see cref="IsAppLayerOn"/>: 実際のドライバの状態（再接続で消えたときの見直しは、今までどおりに働く）。
///  - <see cref="SetAppLayer"/>: "ime" の希望を変える。
///  - <see cref="Release"/>: "ime" の希望を全部消す（ほかの入力源が何も立てていなければ、合成器がハンドルを閉じる）。
/// 対象の一覧（NICOLAを設定したキーボード）を調べるときは、別に短く開いて閉じる（合成器のハンドルの所有を変えず、
/// 対象が無いときにハンドルを持たないため）。
/// </summary>
public sealed class ImeLayerSource : IImeLayerDriver
{
    public const string SourceName = "ime";

    private readonly AppLayerArbiter _arbiter;

    public ImeLayerSource(AppLayerArbiter arbiter)
    {
        _arbiter = arbiter;
    }

    public IReadOnlyList<NicolaTarget> FindTargets()
    {
        using var client = KbdLayerAppLayerDriver.Open();
        var targets = new List<NicolaTarget>();
        foreach (var d in client.ListDevices())
        {
            if (d.UniqId == 0 || !d.HasLayerMap) continue;
            var blob = client.GetNicola(d.UniqId);
            // KBDLAYER_NICOLA_HEADER: Version(0) Enabled(4) Layer(8)
            if (blob.Length < KbdLayerClient.NicolaHeaderSize) continue;
            bool enabled = BitConverter.ToUInt32(blob, 4) != 0;
            uint layer = BitConverter.ToUInt32(blob, 8);
            if (enabled && layer < KbdLayerClient.MaxLayers) targets.Add(new NicolaTarget(d.UniqId, (int)layer, d.Enabled));
        }
        return targets;
    }

    public bool IsAppLayerOn(uint uniqId, int layer) => _arbiter.IsOn(uniqId, layer);

    public void SetAppLayer(uint uniqId, int layer, bool on) => _arbiter.SetBit(SourceName, uniqId, layer, on);

    public void Release() => _arbiter.ClearSource(SourceName);
}
