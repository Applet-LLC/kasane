using System.Text.Json.Nodes;
using Loc = Kasane.Model.Loc;

namespace Kasane.Layout;

/// <summary>軸に平行な矩形（レイアウト座標。単位は u）。</summary>
public readonly record struct KeyBox(double MinX, double MinY, double MaxX, double MaxY)
{
    public KeyBox Union(KeyBox o) => new(Math.Min(MinX, o.MinX), Math.Min(MinY, o.MinY), Math.Max(MaxX, o.MaxX), Math.Max(MaxY, o.MaxY));
}

/// <summary>キー矩形の回転（KLE の r / rx / ry）の幾何。角度は度、時計回り。</summary>
public static class KeyGeometry
{
    /// <summary>矩形 (x, y, w, h) を (ox, oy) のまわりに angle 度回した四隅（左上・右上・右下・左下の順）。angle が 0 なら、計算を通さず元の値を返す。</summary>
    public static (double X, double Y)[] Corners(double x, double y, double w, double h, double angle, double ox, double oy)
    {
        var p = new (double X, double Y)[] { (x, y), (x + w, y), (x + w, y + h), (x, y + h) };
        if (angle == 0) return p;
        double rad = angle * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
        for (int i = 0; i < p.Length; i++)
        {
            double dx = p[i].X - ox, dy = p[i].Y - oy;
            p[i] = (ox + dx * cos - dy * sin, oy + dx * sin + dy * cos);
        }
        return p;
    }

    /// <summary>回転後の四隅を囲む、軸に平行な矩形。</summary>
    public static KeyBox Bounds(double x, double y, double w, double h, double angle, double ox, double oy)
    {
        var c = Corners(x, y, w, h, angle, ox, oy);
        return new KeyBox(c.Min(q => q.X), c.Min(q => q.Y), c.Max(q => q.X), c.Max(q => q.Y));
    }

    /// <summary>回転後の矩形の中心。</summary>
    public static (double X, double Y) Center(double x, double y, double w, double h, double angle, double ox, double oy)
    {
        double cx = x + w / 2, cy = y + h / 2;
        if (angle == 0) return (cx, cy);
        double rad = angle * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
        double dx = cx - ox, dy = cy - oy;
        return (ox + dx * cos - dy * sin, oy + dx * sin + dy * cos);
    }
}

/// <summary>KLE の生データを平らに走査したときの、1キー分。</summary>
internal sealed class WalkedKey
{
    public int Slot;
    public JsonArray Row = null!;
    public int ItemIndex;
    public string Raw = "";
    public double X, Y, W = 1, H = 1;
    // 2つ目の矩形（ISO Enter 等）。x2/y2 はキーの左上からの相対位置。無ければ null。
    public double? X2, Y2, W2, H2;
    public int Align = 4;
    // 回転（度）と、その中心（レイアウト座標）。回転なしは 0。
    public double Angle, OriginX, OriginY;

    /// <summary>回転後のキーの中心。</summary>
    public (double X, double Y) Center => KeyGeometry.Center(X, Y, W, H, Angle, OriginX, OriginY);

    /// <summary>回転後の中心から逆算した左上。位置による推定（左右の修飾キー、テンキー）用。回転なしなら X, Y そのもの。</summary>
    public double PosX => Angle == 0 ? X : Center.X - W / 2;
    public double PosY => Angle == 0 ? Y : Center.Y - H / 2;

    /// <summary>主矩形と2つ目の矩形をあわせた、回転後の外接矩形。</summary>
    public KeyBox Bounds
    {
        get
        {
            var b = KeyGeometry.Bounds(X, Y, W, H, Angle, OriginX, OriginY);
            if (W2 != null) b = b.Union(KeyGeometry.Bounds(X + X2!.Value, Y + Y2!.Value, W2.Value, H2!.Value, Angle, OriginX, OriginY));
            return b;
        }
    }
}

/// <summary>
/// KLE の生データ（行の配列）を、キーを平らにした順に走査する。<see cref="KeyboardLayout.Load"/> と <see cref="KleImporter"/> が
/// 同じ規則でスロット番号を振るため、走査はここ一か所に置く。規則は keyboard-layout-editor.com の serial.js に合わせる:
///  - x / y はカーソルを動かし、w / h / x2 / y2 / w2 / h2 は直後の1キーだけに効く。d:true の直後の文字列はキーではない。
///  - r は回転角、rx / ry は回転の中心。rx を指定すると x が rx に、ry を指定すると y が ry に移る。r / rx / ry は次の指定まで以降のキーに続く。
///  - 行が終わると y + 1、x = rx（回転の中心の x）。
/// </summary>
internal static class KleWalker
{
    public static List<WalkedKey> Walk(JsonNode? root)
    {
        if (root is not JsonArray rows) throw new FormatException(Loc.T("KLE の JSON は、行の配列である必要があります(Raw data のまま)。", "The KLE JSON must be an array of rows (use the Raw data as is)."));
        var keys = new List<WalkedKey>();
        double cy = 0, angle = 0, ox = 0, oy = 0;
        int align = 4;
        foreach (var rowNode in rows)
        {
            if (rowNode is not JsonArray row) continue; // 先頭のメタデータ
            double cx = ox, w = 1, h = 1;
            double? x2 = null, y2 = null, w2 = null, h2 = null;
            bool decal = false;
            for (int i = 0; i < row.Count; i++)
            {
                var item = row[i];
                if (item is JsonObject o)
                {
                    // 回転は、位置（x / y）より先に読む（rx / ry がカーソルを動かしてから、x / y を足すため）。
                    if (Num(o, "r") is double r) angle = r;
                    if (Num(o, "rx") is double rx) { ox = rx; cx = rx; }
                    if (Num(o, "ry") is double ry) { oy = ry; cy = ry; }
                    if (Num(o, "x") is double x) cx += x;
                    if (Num(o, "y") is double y) cy += y;
                    if (Num(o, "w") is double ww) w = ww;
                    if (Num(o, "h") is double hh) h = hh;
                    if (Num(o, "x2") is double xx2) x2 = xx2;
                    if (Num(o, "y2") is double yy2) y2 = yy2;
                    if (Num(o, "w2") is double ww2) w2 = ww2;
                    if (Num(o, "h2") is double hh2) h2 = hh2;
                    if (Num(o, "a") is double a) align = Math.Clamp((int)a, 0, 7);
                    if (o.TryGetPropertyValue("d", out var d) && d is JsonValue dv && dv.TryGetValue<bool>(out var db) && db) decal = true;
                    continue;
                }
                if (item is not JsonValue v || !v.TryGetValue<string>(out var s)) continue;
                if (!decal)
                {
                    bool hasSecond = w2 != null || h2 != null || x2 != null || y2 != null;
                    keys.Add(new WalkedKey
                    {
                        Slot = keys.Count, Row = row, ItemIndex = i, Raw = s ?? "",
                        X = cx, Y = cy, W = w, H = h,
                        X2 = hasSecond ? x2 ?? 0 : null,
                        Y2 = hasSecond ? y2 ?? 0 : null,
                        W2 = hasSecond ? w2 ?? w : null,
                        H2 = hasSecond ? h2 ?? h : null,
                        Align = align,
                        Angle = angle, OriginX = ox, OriginY = oy,
                    });
                }
                cx += w;
                w = 1; h = 1;
                x2 = y2 = w2 = h2 = null;
                decal = false;
            }
            cy += 1;
        }
        return keys;
    }

    private static double? Num(JsonObject o, string name) =>
        o.TryGetPropertyValue(name, out var n) && n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
}
