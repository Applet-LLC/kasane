using Kasane.Model;

namespace Kasane.Tests;

/// <summary>共有範囲タブに表示する、デバイスの識別方法（DeviceIdMode）の読み取り。</summary>
public class DeviceIdModeInfoTests
{
    [Theory]
    [InlineData(null, DeviceIdModeKind.InstancePath, 0)]   // 値が無いときはドライバと同じく 0
    [InlineData(0, DeviceIdModeKind.InstancePath, 0)]
    [InlineData(1, DeviceIdModeKind.DeviceId, 1)]
    [InlineData(2, DeviceIdModeKind.Unknown, 2)]
    [InlineData("1", DeviceIdModeKind.InstancePath, 0)]    // REG_DWORD 以外は無かったものとして 0
    public void FromRegistryValue_MapsLikeTheDriver(object? value, DeviceIdModeKind expected, int expectedRaw)
    {
        Assert.Equal(expected, DeviceIdModeInfo.FromRegistryValue(value, out int? raw));
        Assert.Equal(expectedRaw, raw);
    }
}
