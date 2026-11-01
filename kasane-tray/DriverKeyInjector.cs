using Kasane.Interop;
using Kasane.Model;

namespace Kasane.Tray;

/// <summary>
/// ドライバ経由でキーを送る（IOCTL_KBDLAYER_SEND_KEY。1.01 以降のドライバが持つ）。SendInput が管理者のウィンドウへ
/// 確実に届かないことへの対策で、使えないときは false を返す。呼び出し側は SendInput へ戻すこと。
///  - 使えない理由は <see cref="Status"/>。1.00 までのドライバ（IOCTL なし）、昇格していないトレイ（書き込み権限なし）、ドライバなし。
///  - 使えないと分かったら、一定時間は試さない（ドライバの入れ替え・昇格の変更に追従するため、時間が経ったら試し直す）。
///  - 状態が変わったときだけ、ログに1行出す。
/// 1つのスレッド（ActionRunner のスレッド）から順に呼ぶこと。
/// </summary>
internal sealed class DriverKeyInjector
{
    public enum InjectorStatus { Unknown, Available, NoDriver, ReadOnly, Unsupported, NoKeyboard, Failed }

    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(60);

    private readonly Func<DateTime> _now;
    private DateTime _retryAt = DateTime.MinValue;

    public InjectorStatus Status { get; private set; } = InjectorStatus.Unknown;

    public DriverKeyInjector(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    /// <summary>修飾キー（KBDLAYER_MOD_*）を押したまま、キー（Enc16）を1回押して離す。送れたら true。</summary>
    public bool TrySend(ushort scanCode, byte mods)
    {
        if (Status is not (InjectorStatus.Unknown or InjectorStatus.Available) && _now() < _retryAt) return false;

        KbdLayerClient? client = null;
        try
        {
            client = KbdLayerClient.TryOpen(out _);
            if (client == null) return Fail(InjectorStatus.NoDriver, Loc.T("ドライバを開けません", "Can't open the driver"));
            if (client.IsReadOnly) return Fail(InjectorStatus.ReadOnly, Loc.T("昇格していないため、ドライバへ書き込めません", "The driver can't be written to because the tray is not elevated"));

            var device = client.ListDevices().FirstOrDefault(d => d.UniqId != 0);
            if (device == null) return Fail(InjectorStatus.NoKeyboard, Loc.T("送出先のキーボードがありません", "There is no keyboard to send to"));

            client.SendKey(device.UniqId, scanCode, mods);
            Set(InjectorStatus.Available, Loc.T($"ドライバ経由でキーを送りました(キーボード {device.UniqId:X8})", $"Sent a key through the driver (keyboard {device.UniqId:X8})"));
            return true;
        }
        catch (KbdLayerException ex) when (ex.Win32Error == KbdLayerClient.ErrorInvalidFunction)
        {
            return Fail(InjectorStatus.Unsupported, Loc.T("このドライバはキー送出に対応していません(1.00 までのドライバです)", "This driver does not support sending keys (version 1.00 or earlier)"));
        }
        catch (Exception ex)
        {
            return Fail(InjectorStatus.Failed, ex.Message);
        }
        finally
        {
            client?.Dispose();
        }
    }

    private bool Fail(InjectorStatus status, string reason)
    {
        _retryAt = _now() + RetryAfter;
        Set(status, reason);
        return false;
    }

    private void Set(InjectorStatus status, string reason)
    {
        if (status == Status) return;
        Status = status;
        TrayLog.Write("DriverKey: " + reason);
    }
}
