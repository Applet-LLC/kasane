using System.IO;
using Kasane.Interop;
using Kasane.Model;

namespace Kasane.Keycodes;

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
            throw new InvalidOperationException(Loc.T($"レイヤー数 {layerCount} は1〜{KbdLayerClient.MaxLayers}の範囲外です", $"The layer count {layerCount} is outside the range 1 to {KbdLayerClient.MaxLayers}"));
        if (keys.Count < 1 || keys.Count > KbdLayerClient.MaxKeys)
            throw new InvalidOperationException(Loc.T($"キー数 {keys.Count} は1〜{KbdLayerClient.MaxKeys}の範囲外です", $"The key count {keys.Count} is outside the range 1 to {KbdLayerClient.MaxKeys}"));

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms))
        {
            w.Write(0u);                 // Header（予約）
            w.Write((uint)keys.Count);   // LayerCountは廃止（レイヤー数は常にMaxLayers）
            foreach (var key in keys)
            {
                w.Write(key.SourceScanCode);
                for (int L = 0; L < KbdLayerClient.MaxLayers; L++)
                {
                    // 使わないレイヤー分はTRANSPARENT（下のレイヤーへフォールスルー）で埋める
                    // （BlobBuilder.cppと同じ）。NONEだと、ヘルパーアプリが未定義の上位レイヤーを
                    // 動的にONにしたときにキーが効かなくなる。
                    KeyAction a = KeyAction.Transparent;
                    if (L < layerCount)
                    {
                        string token = key.TokensPerLayer[L];
                        if (!KeycodeTable.TryResolve(token, out a, out var error))
                            throw new InvalidOperationException(Loc.T($"レイヤー{L}のキーコード「{token}」: {error}", $"Layer {L} keycode \"{token}\": {error}"));
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
