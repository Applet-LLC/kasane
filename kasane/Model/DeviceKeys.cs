namespace Kasane.Model;

/// <summary>
/// ドライバ（kbdlayer.sys）が、キーボードに付ける識別キー（docs/DEVICE-ID.md）。ModelKey は HardwareId（先頭の1件）のハッシュで、
/// 同じ型番のキーボードで同じ値になる。ドライバの設定の保存先（LayerMaps\M_&lt;ModelKey&gt;）の名前になる。
/// </summary>
public static class DeviceKeys
{
    /// <summary>16bit FNV-1a（UTF-16 のユニットを下位バイト→上位バイトの順に処理）を、上位16bitに置いた32bit値。</summary>
    public static uint Fnv1aHash16Upper(string s)
    {
        ushort h = 0x9DC5;
        foreach (char ch in s)
        {
            h = (ushort)((h ^ (byte)(ch & 0xFF)) * 0x0193);
            h = (ushort)((h ^ (byte)(ch >> 8)) * 0x0193);
        }
        return (uint)h << 16;
    }

    /// <summary>HardwareId から ModelKey。無い・空なら null（保存先を決められない）。加工はしない（大文字小文字も、そのままハッシュする）。</summary>
    public static uint? ModelKey(string? hardwareId) =>
        string.IsNullOrEmpty(hardwareId) ? null : Fnv1aHash16Upper(hardwareId);

    /// <summary>ドライバの保存先のキー名（"M_" + ModelKey を16進8桁の大文字）。</summary>
    public static string ModelSubKeyName(uint modelKey) => $"M_{modelKey:X8}";
}
