using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kasane.Keycodes;

namespace Kasane.Layout;

/// <summary>
/// KLEレイアウト上の1キー（スロット）。Index はKLE配列を平坦化した順番で、
/// driver/KeymapTool/samples/test-keymap-*.json のキー順・--calibrate-simple の番号と一致する。
/// </summary>
public sealed class KeySlot
{
    public int Index { get; init; }
    public string RawLabel { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
    public double W { get; init; } = 1;
    public double H { get; init; } = 1;
    // ISO Enter等の2つ目の矩形（x2/y2はキーの左上からの相対位置）。無ければnull。
    public double? X2 { get; init; }
    public double? Y2 { get; init; }
    public double? W2 { get; init; }
    public double? H2 { get; init; }
    // 回転（KLEのr/rx/ry）。Angleは度（時計回り）、OriginX/Yは回転の中心（レイアウト座標）。回転なしは0。
    public double Angle { get; init; }
    public double OriginX { get; init; }
    public double OriginY { get; init; }

    /// <summary>主矩形と2つ目の矩形をあわせた、回転後の外接矩形（レイアウト座標）。</summary>
    public KeyBox Bounds
    {
        get
        {
            var b = KeyGeometry.Bounds(X, Y, W, H, Angle, OriginX, OriginY);
            if (W2 != null) b = b.Union(KeyGeometry.Bounds(X + (X2 ?? 0), Y + (Y2 ?? 0), W2.Value, H2 ?? H, Angle, OriginX, OriginY));
            return b;
        }
    }

    /// <summary>KLE凡例インデックス4に埋め込まれた既定キーコード（例: KC_CAPSLOCK）。</summary>
    public string? DefaultKeycode { get; init; }

    public string[] Legends => RawLabel.Split('\n');

    /// <summary>キートップの刻印（凡例0と1。例: "~" と "`"）。</summary>
    public string PrintedLabel
    {
        get
        {
            var l = Legends;
            string a = l.Length > 0 ? l[0] : "";
            string b = l.Length > 1 ? l[1] : "";
            // KLEのテンキー表記（"_7"等）の先頭の_は見た目用なので外す。
            a = a.TrimStart('_');
            b = b.TrimStart('_');
            return b.Length > 0 ? $"{a} {b}" : a;
        }
    }

    /// <summary>
    /// 変更していないキーのキートップに大きく出す文字: 刻印の下段（シフト無しの文字。例: "~\n^" なら "^"）、
    /// 無ければ上段。キーコード名（US配列基準）ではなく刻印を出すことで、JIS配列でも見たままになる。
    /// </summary>
    public string MainLegend
    {
        get
        {
            var l = Legends;
            string rawUpper = l.Length > 0 ? l[0] : "";
            string lower = l.Length > 1 ? l[1].TrimStart('_') : "";
            string upper = rawUpper.TrimStart('_');
            // 同梱KLEではテンキーの上段が "_7" のように_付き（下段はHome等のNumLock OFF時の機能）。
            // テンキーは数字の方を出す。
            if (rawUpper.StartsWith('_')) return upper;
            return lower.Length > 0 ? lower : upper;
        }
    }

    /// <summary>既定キーコードから引いた標準スキャンコード（無ければnull）。</summary>
    public ushort? DefaultScanCode =>
        DefaultKeycode != null && KeycodeTable.TryResolveBaseKey(DefaultKeycode, out var sc) && sc != 0 ? sc : null;

    public string Description => $"#{Index + 1} {PrintedLabel}" + (DefaultKeycode != null ? $" ({DefaultKeycode})" : "");
}

public sealed class KeyboardLayout
{
    public string Name { get; }
    public string FilePath { get; }
    public IReadOnlyList<KeySlot> Slots { get; }
    public double Width { get; }
    public double Height { get; }

    private KeyboardLayout(string name, string path, List<KeySlot> slots)
    {
        Name = name;
        FilePath = path;
        Slots = slots;
        Width = slots.Count == 0 ? 0 : slots.Max(s => s.Bounds.MaxX);
        Height = slots.Count == 0 ? 0 : slots.Max(s => s.Bounds.MaxY);
    }

    /// <summary>同梱の（exe隣の）Layouts\*.json と、利用者が「KLEから追加」で保存したものを、あわせて列挙する（<see cref="LayoutFolders.EnumerateAll"/>）。</summary>
    public static IEnumerable<string> EnumerateLayoutFiles() => LayoutFolders.EnumerateAll();

    /// <summary>exe隣の Layouts\*.json（同梱のもの）を列挙する（ファイル名から "kle-" を除いたものをレイアウト名とする）。</summary>
    public static IEnumerable<string> EnumerateBundledLayoutFiles()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Layouts");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "kle-*.json").OrderBy(f => f) : Enumerable.Empty<string>();
    }

    public static string NameFromPath(string path)
    {
        var n = Path.GetFileNameWithoutExtension(path);
        return n.StartsWith("kle-", StringComparison.OrdinalIgnoreCase) ? n[4..].ToUpperInvariant() : n;
    }

    /// <summary>
    /// KLEの生データ（keyboard-layout-editor.com の「Raw data」をJSON配列にしたもの）を読む。
    /// 走査の規則は <see cref="KleWalker"/>（行の中では x/y がカーソルを動かし、w/h/x2/y2/w2/h2 は直後の1キーだけに効く。
    /// 回転 r/rx/ry は次の指定まで続く。d:true（装飾）の直後の文字列はキーではない）。
    /// 回転後に座標が負になるキーがあるときは、全体を原点へずらす（スロット番号は変わらない）。
    /// </summary>
    public static KeyboardLayout Load(string path)
    {
        var walked = KleWalker.Walk(JsonNode.Parse(File.ReadAllText(path)));
        double minX = walked.Count == 0 ? 0 : walked.Min(k => k.Bounds.MinX);
        double minY = walked.Count == 0 ? 0 : walked.Min(k => k.Bounds.MinY);
        double dx = minX < 0 ? -minX : 0, dy = minY < 0 ? -minY : 0;

        var slots = new List<KeySlot>();
        foreach (var k in walked)
        {
            var legends = k.Raw.Split('\n');
            string? kc = legends.Length > 4 && legends[4].Trim().Length > 0 ? legends[4].Trim() : null;
            slots.Add(new KeySlot
            {
                Index = k.Slot,
                RawLabel = k.Raw,
                X = k.X + dx, Y = k.Y + dy, W = k.W, H = k.H,
                X2 = k.X2, Y2 = k.Y2, W2 = k.W2, H2 = k.H2,
                Angle = k.Angle,
                OriginX = k.OriginX + dx, OriginY = k.OriginY + dy,
                DefaultKeycode = kc,
            });
        }
        return new KeyboardLayout(NameFromPath(path), path, slots);
    }
}
