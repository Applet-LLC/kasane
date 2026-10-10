using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Kasane.Tray;

/// <summary>トレイのアイコン。通常は kasane のアイコン、一時停止中はそれを灰色にして「一時停止」の縦棒を重ね、ドライバに接続できないときは灰色のみ。</summary>
public static class TrayIcons
{
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon Load()
    {
        var asm = Assembly.GetExecutingAssembly();
        string name = asm.GetManifestResourceNames().First(n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        // 複数サイズを含むicoから、トレイのサイズ（高DPIなら大きいもの）に近い画像が選ばれる。
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    /// <summary>灰色にしたアイコン。<paramref name="pauseMarks"/> なら右下に「一時停止」の縦棒2本を描く。</summary>
    public static Icon Gray(Icon source, bool pauseMarks)
    {
        using var bmp = source.ToBitmap();
        using var result = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(result))
        {
            var gray = new ColorMatrix(new[]
            {
                new[] { 0.30f, 0.30f, 0.30f, 0f, 0f },
                new[] { 0.59f, 0.59f, 0.59f, 0f, 0f },
                new[] { 0.11f, 0.11f, 0.11f, 0f, 0f },
                new[] { 0f, 0f, 0f, 1f, 0f },
                new[] { 0f, 0f, 0f, 0f, 1f },
            });
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(gray);
            g.DrawImage(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, attributes);

            if (pauseMarks)
            {
                // 右下の約半分の大きさに、縦棒2本（白地に濃い縁で、どの背景でも見える）。
                int w = Math.Max(2, bmp.Width / 6), h = bmp.Height / 2;
                int x = bmp.Width - 2 * w - w / 2 - 2, y = bmp.Height - h - 1;
                g.SmoothingMode = SmoothingMode.None;
                using var outline = new SolidBrush(Color.FromArgb(40, 40, 40));
                using var fill = new SolidBrush(Color.White);
                foreach (int bx in new[] { x, x + w + w / 2 })
                {
                    g.FillRectangle(outline, bx - 1, y - 1, w + 2, h + 2);
                    g.FillRectangle(fill, bx, y, w, h);
                }
            }
        }

        return ToIcon(result);
    }

    // 3x5 ドットの文字（行ごとに3ビット、左が上位）。フォントの描画は16pxでは「9」と「0」が潰れて同じに見えるため、ドットを直接置く。
    private static readonly Dictionary<char, int[]> Glyphs = new()
    {
        ['0'] = new[] { 7, 5, 5, 5, 7 }, ['1'] = new[] { 2, 6, 2, 2, 7 }, ['2'] = new[] { 7, 1, 7, 4, 7 },
        ['3'] = new[] { 7, 1, 7, 1, 7 }, ['4'] = new[] { 5, 5, 7, 1, 1 }, ['5'] = new[] { 7, 4, 7, 1, 7 },
        ['6'] = new[] { 7, 4, 7, 5, 7 }, ['7'] = new[] { 7, 1, 1, 1, 1 }, ['8'] = new[] { 7, 5, 7, 5, 7 },
        ['9'] = new[] { 7, 5, 7, 1, 7 }, ['A'] = new[] { 7, 5, 7, 5, 5 }, ['B'] = new[] { 6, 5, 6, 5, 6 },
        ['C'] = new[] { 7, 4, 4, 4, 7 }, ['D'] = new[] { 6, 5, 5, 5, 6 }, ['E'] = new[] { 7, 4, 7, 4, 7 },
        ['F'] = new[] { 7, 4, 7, 4, 4 },
    };

    /// <summary>基のアイコンの右下に、レイヤー番号の1文字（0〜9・A〜F）を重ねる（濃い地に白いドットで、どの背景でも見える）。呼び出し側が Dispose する。</summary>
    public static Icon WithBadge(Icon source, char badge)
    {
        using var bmp = source.ToBitmap();
        using var result = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(result))
        {
            g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            int scale = 2 * Math.Max(1, bmp.Width / 16);      // 16px で2、32px で4（アイコンの大半を隠してよい。読みやすさを優先）
            int side = 7 * scale;                              // 3x5 の文字の周りに1ドットの余白
            int x0 = bmp.Width - side, y0 = bmp.Height - side;
            using (var back = new SolidBrush(Color.FromArgb(30, 30, 30))) g.FillRectangle(back, x0, y0, side, side);
            if (Glyphs.TryGetValue(char.ToUpperInvariant(badge), out var rows))
            {
                for (int r = 0; r < 5; r++)
                    for (int c = 0; c < 3; c++)
                        if ((rows[r] & (4 >> c)) != 0)
                            g.FillRectangle(Brushes.White, x0 + (1 + c) * scale, y0 + (1 + r) * scale, scale, scale);
            }
        }
        return ToIcon(result);
    }

    private static Icon ToIcon(Bitmap bitmap)
    {
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone(); // ハンドルは破棄するので、複製を返す
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
