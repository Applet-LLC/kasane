using Microsoft.Win32;

namespace Kasane.Model;

/// <summary>キーボードの識別方法（インストール時に選ぶ DeviceIdMode）。</summary>
public enum DeviceIdModeKind
{
    /// <summary>デバイスインスタンスパス（0。既定）。接続先（USBポートなど）ごとに別のキーボードとして扱う。</summary>
    InstancePath,

    /// <summary>デバイスID（1）。デバイス固有の ID（型番）で区別する。ポートを替えても同じキーボード。</summary>
    DeviceId,

    /// <summary>読めない、または 0・1 以外の値。</summary>
    Unknown,
}

/// <summary>
/// ドライバが読む HKLM\SYSTEM\CurrentControlSet\Services\kbdlayer\Parameters\DeviceIdMode（REG_DWORD）を、画面に表示するために読む。
/// 通常権限でも読める。値が無いときは、ドライバと同じく 0（デバイスインスタンスパス）。変更はインストーラーで行う（docs/DEVICE-ID.md）。
/// </summary>
public static class DeviceIdModeInfo
{
    private const string ParametersKey = @"SYSTEM\CurrentControlSet\Services\kbdlayer\Parameters";
    private const string ValueName = "DeviceIdMode";

    public static DeviceIdModeKind Read(out int? rawValue)
    {
        rawValue = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ParametersKey);
            if (key == null) return DeviceIdModeKind.Unknown; // ドライバが入っていない
            return FromRegistryValue(key.GetValue(ValueName), out rawValue);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return DeviceIdModeKind.Unknown;
        }
    }

    /// <summary>レジストリの値（無ければ null）から種類を決める。無い・REG_DWORD でないときは、ドライバと同じく 0 の扱い。</summary>
    public static DeviceIdModeKind FromRegistryValue(object? value, out int? rawValue)
    {
        rawValue = value is int i ? i : 0;
        return rawValue switch
        {
            0 => DeviceIdModeKind.InstancePath,
            1 => DeviceIdModeKind.DeviceId,
            _ => DeviceIdModeKind.Unknown,
        };
    }
}
