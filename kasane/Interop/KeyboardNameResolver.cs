using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Kasane.Model;
using Microsoft.Win32;

namespace Kasane.Interop;

/// <summary>
/// キーボードの Windows 上の名前（FriendlyName と ShellFriendlyName）を取る。取り方は mana の ARCHITECTURE.md 第2節を移した:
///  - FriendlyName: SPDRP_FRIENDLYNAME → SPDRP_DEVICEDESC → デバイスインスタンスID。
///  - ShellFriendlyName: Bluetooth の事前索引（ContainerId、BD アドレス）→ 親をたどった BTHENUM / BTHLE の FriendlyName →
///    親をたどった USB\VID_* の BusReportedDeviceDesc → 自身の BusReportedDeviceDesc。使えない名前（汎用・同じ名前）は飛ばす。
/// ドライバの UniqId（= デバイスインスタンスIDの16bitハッシュ）との結びつけは、ドライバが見る形のインスタンスIDを作ってハッシュで照合する
/// （<see cref="KeyboardNameRules.PickMatch"/>。docs/DEVICE-ID.md 第3.1節）。決まらなければ名前を出さない。
/// 実機で確かめたのは、取得できる範囲だけ（Bluetooth の経路は未確認）。失敗しても例外にせず、取れた分だけ返す。
/// </summary>
internal static class KeyboardNameResolver
{
    private const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum";

    /// <summary>接続中のキーボード（UniqId が0でないもの）の名前を取る。取れなかったキーボードは含まれない。</summary>
    public static Dictionary<uint, KeyboardNameInfo> Resolve(IEnumerable<DeviceInfo> devices)
    {
        var result = new Dictionary<uint, KeyboardNameInfo>();
        List<KeyboardNodeCandidate> candidates;
        try { candidates = Enumerate(); }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or System.Security.SecurityException or DllNotFoundException or EntryPointNotFoundException)
        {
            return result;
        }
        foreach (var d in devices.Where(d => d.UniqId != 0))
        {
            var c = KeyboardNameRules.PickMatch(candidates, d.UniqId, d.HardwareId);
            if (c != null) result[d.UniqId] = c.Names;
        }
        return result;
    }

    // ------------------------------------------------------------------
    // 列挙
    // ------------------------------------------------------------------

    private static List<KeyboardNodeCandidate> Enumerate()
    {
        var list = new List<KeyboardNodeCandidate>();
        var bt = LoadBluetoothNames();
        Guid keyboardClass = new("4D36E96B-E325-11CE-BFC1-08002BE10318");
        IntPtr set = SetupDiGetClassDevsW(ref keyboardClass, null, IntPtr.Zero, DIGCF_PRESENT);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref data)) break;

                string? instanceId = InstanceId(set, ref data);
                if (instanceId == null) continue;

                string friendly = RegistryProperty(set, ref data, SPDRP_FRIENDLYNAME)
                                  ?? RegistryProperty(set, ref data, SPDRP_DEVICEDESC)
                                  ?? instanceId;
                string? firstHardwareId = FirstOf(RegistryMultiSz(set, ref data, SPDRP_HARDWAREID));
                string? shell = ResolveShellName(data.DevInst, instanceId, friendly, bt);

                list.Add(new KeyboardNodeCandidate(DriverCase(instanceId), firstHardwareId, new KeyboardNameInfo(friendly, shell)));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    // ------------------------------------------------------------------
    // 製品寄りの名前
    // ------------------------------------------------------------------

    private sealed record BluetoothNames(Dictionary<Guid, string> ByContainerId, Dictionary<string, string> ByAddress);

    private static string? ResolveShellName(uint devInst, string instanceId, string friendly, BluetoothNames bt)
    {
        string? Useful(string? name) => KeyboardNameRules.IsUseful(name, friendly) ? name!.Trim() : null;

        // 1・2: Bluetooth の事前索引
        if (ContainerId(devInst) is Guid container && bt.ByContainerId.TryGetValue(container, out var byContainer) && Useful(byContainer) is { } a) return a;
        if (KeyboardNameRules.ExtractBdAddress(instanceId) is { } address && bt.ByAddress.TryGetValue(address, out var byAddress) && Useful(byAddress) is { } b) return b;

        // 3・4: 親をたどる（最大12段）
        string? usbName = null;
        uint node = devInst;
        for (int depth = 0; depth < 12; depth++)
        {
            if (CM_Get_Parent(out uint parent, node, 0) != 0) break;
            node = parent;
            string? id = DeviceId(node);
            if (id == null) continue;
            if (id.StartsWith(@"BTHENUM\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"BTHLE\", StringComparison.OrdinalIgnoreCase)
                || id.StartsWith(@"BTHLEDEVICE\", StringComparison.OrdinalIgnoreCase))
            {
                if (Useful(ReadEnumString(id, "FriendlyName")) is { } c) return c;
            }
            else if (usbName == null && id.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase))
            {
                usbName = Useful(BusReportedDeviceDesc(node))
                          ?? Useful(ReadEnumString(id, "FriendlyName"))
                          ?? Useful(DeviceDescText(ReadEnumString(id, "DeviceDesc")));
            }
        }
        if (usbName != null) return usbName;

        // 5: 自身の BusReportedDeviceDesc
        return Useful(BusReportedDeviceDesc(devInst));
    }

    // DeviceDesc は "@oem12.inf,%name%;名前" の形のことがある。";" の後ろが表示名。
    private static string? DeviceDescText(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        int i = s.LastIndexOf(';');
        return i >= 0 ? s[(i + 1)..] : s;
    }

    // HKLM\...\Enum\BTHENUM と BTHLE の各ノードの FriendlyName を、ContainerID と BD アドレスで引けるようにする。
    private static BluetoothNames LoadBluetoothNames()
    {
        var byContainer = new Dictionary<Guid, string>();
        var byAddress = new Dictionary<string, string>();
        foreach (var root in new[] { "BTHENUM", "BTHLE" })
        {
            try
            {
                using var rootKey = Registry.LocalMachine.OpenSubKey($@"{EnumRoot}\{root}");
                if (rootKey == null) continue;
                foreach (var hw in rootKey.GetSubKeyNames())
                {
                    using var hwKey = rootKey.OpenSubKey(hw);
                    if (hwKey == null) continue;
                    foreach (var inst in hwKey.GetSubKeyNames())
                    {
                        using var k = hwKey.OpenSubKey(inst);
                        if (k?.GetValue("FriendlyName") is not string name || !KeyboardNameRules.IsUseful(name, null)) continue;
                        if (k.GetValue("ContainerID") is string cid && Guid.TryParse(cid, out var guid)) byContainer.TryAdd(guid, name.Trim());
                        if (KeyboardNameRules.ExtractBdAddress($@"{root}\{hw}\{inst}") is { } address) byAddress.TryAdd(address, name.Trim());
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* 取れた分だけ使う */ }
        }
        return new BluetoothNames(byContainer, byAddress);
    }

    // ------------------------------------------------------------------
    // ドライバが見る形のデバイスインスタンスID
    // ------------------------------------------------------------------

    /// <summary>
    /// SetupAPI が返す形のインスタンスIDを、ドライバが見る形にする。docs/DEVICE-ID.md 第3.1節のとおり、レジストリ Enum のキー名が元の
    /// 大文字小文字のまま見えるので、それを使う。見つからなければ、最後の "\" の後ろを小文字にする（実機の5台では一致した）。
    /// </summary>
    internal static string DriverCase(string instanceId)
    {
        string[] parts = instanceId.Split('\\');
        if (parts.Length < 3) return instanceId;
        try
        {
            using var bus = Registry.LocalMachine.OpenSubKey($@"{EnumRoot}\{parts[0]}");
            string? bus0 = bus?.GetSubKeyNames().FirstOrDefault(n => n.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
            using var busKey = Registry.LocalMachine.OpenSubKey($@"{EnumRoot}\{bus0 ?? parts[0]}");
            string? hw = busKey?.GetSubKeyNames().FirstOrDefault(n => n.Equals(parts[1], StringComparison.OrdinalIgnoreCase));
            if (hw != null)
            {
                using var hwKey = busKey!.OpenSubKey(hw);
                string rest = string.Join('\\', parts.Skip(2));
                string? inst = hwKey?.GetSubKeyNames().FirstOrDefault(n => n.Equals(rest, StringComparison.OrdinalIgnoreCase));
                if (inst != null) return $@"{bus0 ?? parts[0]}\{hw}\{inst}";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* 下の既定へ */ }
        return $@"{parts[0]}\{parts[1]}\{string.Join('\\', parts.Skip(2)).ToLowerInvariant()}";
    }

    // ------------------------------------------------------------------
    // SetupAPI / CfgMgr32 の補助
    // ------------------------------------------------------------------

    private static string? ReadEnumString(string deviceId, string name)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"{EnumRoot}\{deviceId}");
            return k?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    private static string? InstanceId(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        var sb = new StringBuilder(512);
        return SetupDiGetDeviceInstanceIdW(set, ref data, sb, (uint)sb.Capacity, out _) ? sb.ToString() : null;
    }

    private static string? DeviceId(uint devInst)
    {
        var sb = new StringBuilder(512);
        return CM_Get_Device_IDW(devInst, sb, (uint)sb.Capacity, 0) == 0 ? sb.ToString() : null;
    }

    private static byte[]? RegistryBytes(IntPtr set, ref SP_DEVINFO_DATA data, uint property)
    {
        SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, null, 0, out uint required);
        if (required == 0 || required > 65536) return null;
        var buf = new byte[required];
        return SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buf, required, out _) ? buf : null;
    }

    private static string? RegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint property)
    {
        var buf = RegistryBytes(set, ref data, property);
        if (buf == null) return null;
        string s = Encoding.Unicode.GetString(buf).TrimEnd('\0');
        return s.Length == 0 ? null : s;
    }

    private static IReadOnlyList<string> RegistryMultiSz(IntPtr set, ref SP_DEVINFO_DATA data, uint property)
    {
        var buf = RegistryBytes(set, ref data, property);
        if (buf == null) return Array.Empty<string>();
        return Encoding.Unicode.GetString(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string? FirstOf(IReadOnlyList<string> list) => list.Count > 0 ? list[0] : null;

    private static string? BusReportedDeviceDesc(uint devInst)
    {
        var key = new DEVPROPKEY { fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), pid = 4 };
        uint size = 0;
        CM_Get_DevNode_PropertyW(devInst, ref key, out _, null, ref size, 0);
        if (size == 0 || size > 65536) return null;
        var buf = new byte[size];
        if (CM_Get_DevNode_PropertyW(devInst, ref key, out _, buf, ref size, 0) != 0) return null;
        string s = Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0');
        return s.Length == 0 ? null : s;
    }

    private static Guid? ContainerId(uint devInst)
    {
        var key = new DEVPROPKEY { fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), pid = 2 };
        var buf = new byte[16];
        uint size = 16;
        return CM_Get_DevNode_PropertyW(devInst, ref key, out _, buf, ref size, 0) == 0 && size == 16 ? new Guid(buf) : null;
    }

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint SPDRP_DEVICEDESC = 0x00000000;
    private const uint SPDRP_HARDWAREID = 0x00000001;
    private const uint SPDRP_FRIENDLYNAME = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder? buffer, uint size, out uint required);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, uint property, out uint regType, byte[]? buffer, uint size, out uint required);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint devInst, StringBuilder buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, byte[]? buffer, ref uint size, uint flags);
}
