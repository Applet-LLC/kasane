using System.Text.RegularExpressions;

namespace Kasane.Model;

/// <summary>Windows が持つキーボードの名前。FriendlyName はデバイスマネージャーの名前、Shell は製品寄りの名前（無ければ null）。</summary>
public sealed record KeyboardNameInfo(string FriendlyName, string? ShellFriendlyName);

/// <summary>
/// 列挙したキーボードのデバイスノード1つ。<see cref="InstanceIdForHash"/> は、ドライバが見る形のデバイスインスタンスID
/// （前半がOSの元の大文字小文字、後半のインスタンス部分が小文字。docs/DEVICE-ID.md 第3.1節）で、UniqId との照合に使う。
/// </summary>
public sealed record KeyboardNodeCandidate(string InstanceIdForHash, string? FirstHardwareId, KeyboardNameInfo Names);

/// <summary>
/// キーボード名の取得の、画面やOSに依存しない部分（使える名前かの判定、Bluetooth アドレスの取り出し、UniqId との照合）。
/// OSを呼ぶ部分は <c>Kasane.Interop.KeyboardNameResolver</c>。取得の方法は mana の ARCHITECTURE.md 第2節を移した。
/// </summary>
public static class KeyboardNameRules
{
    /// <summary>名前として役に立たない汎用の名前（比較は大文字小文字を区別しない）。</summary>
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "HID Keyboard Device", "HID キーボード デバイス", "HID-compliant keyboard",
        "Bluetooth HID Device", "Bluetooth HID デバイス",
        "USB Input Device", "USB 入力デバイス",
        "Standard PS/2 Keyboard", "標準 PS/2 キーボード",
        "Keyboard Device", "キーボード デバイス",
    };

    /// <summary>空でなく、デバイスマネージャーの名前と違い、汎用の名前でもない。</summary>
    public static bool IsUseful(string? name, string? deviceManagerName)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        name = name.Trim();
        if (deviceManagerName != null && string.Equals(name, deviceManagerName.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        return !GenericNames.Contains(name);
    }

    private static readonly Regex BdAddress = new(@"(?:DEV_|BluetoothDevice_|&|_)([0-9A-F]{12})(?=[_\\&]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// デバイスインスタンスIDから、Bluetooth のアドレス（16進12桁、大文字）を取り出す。
    /// 例: <c>BTHENUM\DEV_C2F2FFDA05E8\7&amp;1234&amp;0&amp;BLUETOOTHDEVICE_C2F2FFDA05E8</c>、<c>HID\{...}_VID&amp;...\9&amp;1&amp;0&amp;C2F2FFDA05E8_C00000000</c>。
    /// 見つからなければ null。
    /// </summary>
    public static string? ExtractBdAddress(string? instanceId)
    {
        if (string.IsNullOrEmpty(instanceId)) return null;
        var m = BdAddress.Match(instanceId);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>
    /// UniqId（= デバイスインスタンスIDの16bitハッシュ。<see cref="DeviceKeys.Fnv1aHash16Upper"/>）に合うデバイスノードを選ぶ。
    /// 16bit なので衝突しうる。複数あれば、HardwareId の先頭が一致するものに絞る。1つに決まらなければ null
    /// （誤った名前を出さないため）。
    /// </summary>
    public static KeyboardNodeCandidate? PickMatch(IReadOnlyList<KeyboardNodeCandidate> candidates, uint uniqId, string? hardwareId)
    {
        if (uniqId == 0) return null;
        var hits = candidates.Where(c => DeviceKeys.Fnv1aHash16Upper(c.InstanceIdForHash) == uniqId).ToList();
        if (hits.Count == 1) return hits[0];
        if (hits.Count == 0 || string.IsNullOrEmpty(hardwareId)) return null;
        var narrowed = hits.Where(c => string.Equals(c.FirstHardwareId, hardwareId, StringComparison.Ordinal)).ToList();
        return narrowed.Count == 1 ? narrowed[0] : null;
    }

    /// <summary>
    /// 画面に出すキーボードの表示名: UniqId、利用者がつけた名前、FriendlyName、ShellFriendlyName、HardwareId の順（無いものは飛ばす）。
    /// UniqId を先頭に保つ（ImportExportWindow が前提にしている）。同じ文字列が続くときは1つにする。
    /// </summary>
    public static string DisplayName(uint uniqId, string? userName, KeyboardNameInfo? names, string? hardwareId)
    {
        var parts = new List<string> { $"{uniqId:X8}" };
        void Add(string? s)
        {
            s = s?.Trim();
            if (!string.IsNullOrEmpty(s) && !parts.Contains(s, StringComparer.OrdinalIgnoreCase)) parts.Add(s);
        }
        Add(userName);
        Add(names?.FriendlyName);
        Add(names?.ShellFriendlyName);
        Add(hardwareId);
        return string.Join("  ", parts);
    }
}
