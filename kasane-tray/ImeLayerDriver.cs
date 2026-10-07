using Kasane.Interop;

namespace Kasane.Tray;

/// <summary>
/// 実際のドライバに対するIME連動の操作。アプリ管理レイヤーは「最後に設定したハンドル」が所有し、そのハンドルを閉じると
/// 0に戻るので、レイヤーを立てている間は、ハンドルを開いたままにする（<see cref="Release"/> で閉じる）。
/// 対象の一覧（NICOLAを設定したキーボード）を調べるときは、別に短く開いて閉じる（設定側のハンドルの所有を変えず、
/// 対象が無いときにハンドルを持たないため）。
/// </summary>
public sealed class KbdLayerImeLayerDriver : IImeLayerDriver
{
    private KbdLayerClient? _held;

    private static KbdLayerClient Open() =>
        KbdLayerClient.TryOpen(out var error) ?? throw new InvalidOperationException(error ?? "ドライバに接続できません");

    public IReadOnlyList<NicolaTarget> FindTargets()
    {
        using var client = Open();
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

    public bool IsAppLayerOn(uint uniqId, int layer) =>
        Run(c => (c.GetLayerState(uniqId).AppLayerMask & (1u << layer)) != 0);

    public void SetAppLayer(uint uniqId, int layer, bool on) =>
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
