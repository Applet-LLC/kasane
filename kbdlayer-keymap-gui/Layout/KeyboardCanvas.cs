using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KbdLayerKeymapGui.Layout;

/// <summary>1キー分の見た目。</summary>
public sealed record KeyVisual(
    string MainText,
    Brush Background,
    Brush BorderBrush,
    double BorderThickness = 1,
    string? ToolTip = null,
    Brush? MainForeground = null);

/// <summary>
/// KeyboardLayoutをCanvas上にキーの矩形として描く。nodoka-gui（MainWindow.RenderKeyboard）と同じく
/// 1キー=固定ピクセル幅の格子に配置し、ISO Enterのような2矩形キーは2つ目の矩形も同じキーとして描く。
/// </summary>
public sealed class KeyboardCanvas : Canvas
{
    public const double Unit = 50;
    private const double Gap = 3;

    private KeyboardLayout? _layout;
    private readonly Dictionary<int, List<Border>> _keyBorders = new();

    public event Action<KeySlot>? KeyClicked;
    public event Action<KeySlot>? KeyRightClicked;

    public KeyboardLayout? Layout => _layout;

    public static readonly Brush DefaultKeyBrush = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF4));
    public static readonly Brush DefaultBorderBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));

    /// <summary>レイアウトを差し替え、visualFor で各キーの見た目を決めて描き直す。</summary>
    public void Render(KeyboardLayout? layout, Func<KeySlot, KeyVisual> visualFor)
    {
        _layout = layout;
        Children.Clear();
        _keyBorders.Clear();
        if (layout == null) { Width = 0; Height = 0; return; }

        Width = layout.Width * Unit + Gap;
        Height = layout.Height * Unit + Gap;

        foreach (var slot in layout.Slots)
        {
            var visual = visualFor(slot);
            var borders = new List<Border>();

            if (slot.W2 != null)
            {
                // 2つ目の矩形（テキスト無し）を先に描き、主矩形を上に重ねる。
                var second = MakeRect(slot, slot.X + slot.X2!.Value, slot.Y + slot.Y2!.Value, slot.W2.Value, slot.H2!.Value, visual);
                borders.Add(second);
            }
            var main = MakeRect(slot, slot.X, slot.Y, slot.W, slot.H, visual);
            main.Child = MakeContent(slot, visual);
            borders.Add(main);

            _keyBorders[slot.Index] = borders;
        }
    }

    /// <summary>1キーだけ見た目を更新する（全体を描き直さない）。</summary>
    public void Update(KeySlot slot, KeyVisual visual)
    {
        if (!_keyBorders.TryGetValue(slot.Index, out var borders)) return;
        foreach (var b in borders)
        {
            b.Background = visual.Background;
            b.BorderBrush = visual.BorderBrush;
            b.BorderThickness = new Thickness(visual.BorderThickness);
            b.ToolTip = visual.ToolTip;
        }
        borders[^1].Child = MakeContent(slot, visual);
    }

    public Rect GetKeyBounds(KeySlot slot) =>
        new(slot.X * Unit + Gap, slot.Y * Unit + Gap, slot.W * Unit - Gap, slot.H * Unit - Gap);

    private Border MakeRect(KeySlot slot, double x, double y, double w, double h, KeyVisual visual)
    {
        var b = new Border
        {
            Width = Math.Max(10, w * Unit - Gap),
            Height = Math.Max(10, h * Unit - Gap),
            Background = visual.Background,
            BorderBrush = visual.BorderBrush,
            BorderThickness = new Thickness(visual.BorderThickness),
            CornerRadius = new CornerRadius(4),
            ToolTip = visual.ToolTip,
            Cursor = Cursors.Hand,
        };
        SetLeft(b, x * Unit + Gap);
        SetTop(b, y * Unit + Gap);
        b.MouseLeftButtonUp += (_, e) => { KeyClicked?.Invoke(slot); e.Handled = true; };
        b.MouseRightButtonUp += (_, e) => { KeyRightClicked?.Invoke(slot); e.Handled = true; };
        Children.Add(b);
        return b;
    }

    // 日本語フォントだけだと「\」が「¥」の字形で描かれ、KC_BSLASHとKC_JYENが見分けられなくなるため、
    // 英数字はSegoe UIで描き、日本語だけYu Gothic UIへフォールバックさせる。
    private static readonly FontFamily KeyFont = new("Segoe UI, Yu Gothic UI");

    private static UIElement MakeContent(KeySlot slot, KeyVisual visual)
    {
        var grid = new Grid();
        grid.Children.Add(new TextBlock
        {
            Text = slot.PrintedLabel,
            FontFamily = KeyFont,
            FontSize = 9,
            Foreground = Brushes.Gray,
            Margin = new Thickness(3, 1, 3, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        grid.Children.Add(new TextBlock
        {
            Text = visual.MainText,
            FontFamily = KeyFont,
            FontSize = visual.MainText.Length > 6 ? 9 : 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = visual.MainForeground ?? Brushes.Black,
            Margin = new Thickness(2, 10, 2, 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        });
        return grid;
    }
}
