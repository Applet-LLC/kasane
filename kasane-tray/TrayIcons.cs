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

        IntPtr handle = result.GetHicon();
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
