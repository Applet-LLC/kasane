using Kasane.Interop;

namespace Kasane.Tray;

/// <summary>
/// 実際のドライバ（\\.\KbdLayerControl）を操作する。ハンドルは操作のたびに開いて閉じる（常駐中に開きっぱなしにして、
/// ドライバの更新・アンインストールを妨げないため）。昇格していないと読み取り専用で開くが、一覧とON/OFF
/// （SET_LAYERMAP_ENABLED）は読み取り側のIOCTLなので、そのまま使える。
/// </summary>
public sealed class KbdLayerDriverAccess : IDriverAccess
{
    private static KbdLayerClient Open() =>
        KbdLayerClient.TryOpen(out var error) ?? throw new InvalidOperationException(error ?? Kasane.Model.Loc.T("ドライバに接続できません", "Can't connect to the driver"));

    public List<DeviceInfo> ListDevices()
    {
        using var client = Open();
        return client.ListDevices();
    }

    public void SetEnabled(uint uniqId, bool enabled)
    {
        using var client = Open();
        client.SetEnabled(uniqId, enabled);
    }
}
