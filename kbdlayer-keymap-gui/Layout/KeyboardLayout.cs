using System.IO;
using System.Text.Json;
using KbdLayerKeymapGui.Keycodes;

namespace KbdLayerKeymapGui.Layout;

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
        Width = slots.Count == 0 ? 0 : slots.Max(s => Math.Max(s.X + s.W, s.X + (s.X2 ?? 0) + (s.W2 ?? 0)));
        Height = slots.Count == 0 ? 0 : slots.Max(s => Math.Max(s.Y + s.H, s.Y + (s.Y2 ?? 0) + (s.H2 ?? 0)));
    }

    /// <summary>exe隣の Layouts\*.json を列挙する（ファイル名から "kle-" を除いたものをレイアウト名とする）。</summary>
    public static IEnumerable<string> EnumerateBundledLayoutFiles()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Layouts");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").OrderBy(f => f) : Enumerable.Empty<string>();
    }

    public static string NameFromPath(string path)
    {
        var n = Path.GetFileNameWithoutExtension(path);
        return n.StartsWith("kle-", StringComparison.OrdinalIgnoreCase) ? n[4..].ToUpperInvariant() : n;
    }

    /// <summary>
    /// KLEの生データ（keyboard-layout-editor.com の「Raw data」をJSON配列にしたもの）を読む。
    /// 標準的なKLEの配置規則に従う: 行の中では x/y がカーソルを動かし、w/h/x2/y2/w2/h2 は
    /// 直後の1キーだけに効く。行が終わると y+1、x=0。回転（r/rx/ry）は非対応。
    /// d:true（装飾）の直後の文字列はキーとして扱わない。
    /// </summary>
    public static KeyboardLayout Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var slots = new List<KeySlot>();
        double cy = 0;

        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array) continue; // 先頭のメタデータオブジェクト等
            double cx = 0;
            double w = 1, h = 1;
            double? x2 = null, y2 = null, w2 = null, h2 = null;
            bool decal = false;

            foreach (var item in row.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    if (item.TryGetProperty("x", out var v)) cx += v.GetDouble();
                    if (item.TryGetProperty("y", out v)) cy += v.GetDouble();
                    if (item.TryGetProperty("w", out v)) w = v.GetDouble();
                    if (item.TryGetProperty("h", out v)) h = v.GetDouble();
                    if (item.TryGetProperty("x2", out v)) x2 = v.GetDouble();
                    if (item.TryGetProperty("y2", out v)) y2 = v.GetDouble();
                    if (item.TryGetProperty("w2", out v)) w2 = v.GetDouble();
                    if (item.TryGetProperty("h2", out v)) h2 = v.GetDouble();
                    if (item.TryGetProperty("d", out v) && v.ValueKind == JsonValueKind.True) decal = true;
                    continue;
                }
                if (item.ValueKind != JsonValueKind.String) continue;

                if (!decal)
                {
                    string label = item.GetString() ?? "";
                    var legends = label.Split('\n');
                    string? kc = legends.Length > 4 && legends[4].Trim().Length > 0 ? legends[4].Trim() : null;
                    bool hasSecond = w2 != null || h2 != null || x2 != null || y2 != null;
                    slots.Add(new KeySlot
                    {
                        Index = slots.Count,
                        RawLabel = label,
                        X = cx, Y = cy, W = w, H = h,
                        X2 = hasSecond ? x2 ?? 0 : null,
                        Y2 = hasSecond ? y2 ?? 0 : null,
                        W2 = hasSecond ? w2 ?? w : null,
                        H2 = hasSecond ? h2 ?? h : null,
                        DefaultKeycode = kc,
                    });
                }
                cx += w;
                w = 1; h = 1;
                x2 = y2 = w2 = h2 = null;
                decal = false;
            }
            cy += 1;
        }

        return new KeyboardLayout(NameFromPath(path), path, slots);
    }
}
