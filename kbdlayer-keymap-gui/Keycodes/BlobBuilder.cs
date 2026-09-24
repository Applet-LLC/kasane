using System.IO;
using KbdLayerKeymapGui.Interop;

namespace KbdLayerKeymapGui.Keycodes;

/// <summary>
/// レイヤーマップのblob（KBDLAYER_MAP_HEADER + KBDLAYER_KEY_ENTRY[]）を組み立てる。
/// driver/KeymapTool/BlobBuilder.cpp の移植。
/// </summary>
public static class BlobBuilder
{
    public sealed record KeyInput(ushort SourceScanCode, IReadOnlyList<string> TokensPerLayer);

    public static byte[] Build(int layerCount, IReadOnlyList<KeyInput> keys)
    {
        if (layerCount < 1 || layerCount > KbdLayerClient.MaxLayers)
            throw new InvalidOperationException($"レイヤー数 {layerCount} は1〜{KbdLayerClient.MaxLayers}の範囲外です");
        if (keys.Count < 1 || keys.Count > KbdLayerClient.MaxKeys)
            throw new InvalidOperationException($"キー数 {keys.Count} は1〜{KbdLayerClient.MaxKeys}の範囲外です");

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            w.Write(0u);                 // Header（予約）
            w.Write((uint)layerCount);
            w.Write((uint)keys.Count);
            foreach (var key in keys)
            {
                w.Write(key.SourceScanCode);
                for (int L = 0; L < KbdLayerClient.MaxLayers; L++)
                {
                    KeyAction a = default; // 使わないレイヤー分はゼロ埋め（C++版のZeroMemoryと同じ）
                    if (L < layerCount)
                    {
                        string token = key.TokensPerLayer[L];
                        if (!KeycodeTable.TryResolve(token, out a, out var error))
                            throw new InvalidOperationException($"レイヤー{L}のキーコード「{token}」: {error}");
                    }
                    w.Write(a.Type);
                    w.Write(a.Param1);
                    w.Write(a.Param2);
                }
            }
        }
        return ms.ToArray();
    }
}
