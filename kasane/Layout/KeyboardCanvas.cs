using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Kasane.Layout;

/// <summary>1キー分の見た目。</summary>
public sealed record KeyVisual(
    string MainText,
    Brush Background,
    Brush BorderBrush,
    double BorderThickness = 1,
    string? ToolTip = null,
    Brush? MainForeground = null,
    string? Marks = null); // 右下の印（M マクロ・D タップダンス・C コンボ・R キー置換。KeyMarks）

/// <summary>
/// キー面の矩形（主矩形）。BorderはUI Automationの要素として公開されないので、Invoke（クリックと同じ）
/// できるボタンとして公開するAutomationPeerを持たせる（UI Automationの試験でキーの割り当てを操作するため）。
/// </summary>
public sealed class KeyFace : Border
{
    private readonly Action _invoke;

    public KeyFace(Action invoke) => _invoke = invoke;

    internal void Invoke() => _invoke();

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
        new KeyFaceAutomationPeer(this);
}

internal sealed class KeyFaceAutomationPeer :
    System.Windows.Automation.Peers.FrameworkElementAutomationPeer,
    System.Windows.Automation.Provider.IInvokeProvider
{
    public KeyFaceAutomationPeer(KeyFace owner) : base(owner) { }

    protected override string GetClassNameCore() => nameof(KeyFace);

    protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() =>
        System.Windows.Automation.Peers.AutomationControlType.Button;

    public override object? GetPattern(System.Windows.Automation.Peers.PatternInterface patternInterface) =>
        patternInterface == System.Windows.Automation.Peers.PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

    // クリックと同じ処理（キーの割り当てダイアログをShowDialogで開く）を同期で呼ぶと、ダイアログを閉じるまで
    // UI Automation側のInvokeが戻らない。WPFのButtonと同じく、Dispatcher経由で後から実行してすぐ戻る。
    public void Invoke() => Owner.Dispatcher.BeginInvoke(new Action(((KeyFace)Owner).Invoke));
}

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

    // 色のモード（ライト／ダーク）で変わる（AppTheme.Palette）。
    public static Brush DefaultKeyBrush => Kasane.Views.AppTheme.Palette.KeyDefault;
    public static Brush DefaultBorderBrush => Kasane.Views.AppTheme.Palette.KeyBorder;

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
                var second = MakeRect(slot, slot.X + slot.X2!.Value, slot.Y + slot.Y2!.Value, slot.W2.Value, slot.H2!.Value, visual, isMain: false);
                borders.Add(second);
            }
            var main = MakeRect(slot, slot.X, slot.Y, slot.W, slot.H, visual, isMain: true);
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

    /// <summary>キー（回転していれば回転後）を囲む、軸に平行な矩形（キャンバス座標）。</summary>
    public Rect GetKeyBounds(KeySlot slot)
    {
        var b = slot.Bounds;
        return new(b.MinX * Unit + Gap, b.MinY * Unit + Gap, (b.MaxX - b.MinX) * Unit - Gap, (b.MaxY - b.MinY) * Unit - Gap);
    }

    private Border MakeRect(KeySlot slot, double x, double y, double w, double h, KeyVisual visual, bool isMain)
    {
        // 主矩形はUI Automationから操作できるようにする（AutomationId "Key_<スロット番号>"、Invokeでクリックと同じ）。
        // 2つ目の矩形（ISO Enter等）は見た目だけなので、普通のBorderのまま。
        Border b = isMain ? new KeyFace(() => KeyClicked?.Invoke(slot)) : new Border();
        if (isMain)
        {
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, $"Key_{slot.Index}");
            System.Windows.Automation.AutomationProperties.SetName(b, slot.Description);
        }
        b.Width = Math.Max(10, w * Unit - Gap);
        b.Height = Math.Max(10, h * Unit - Gap);
        b.Background = visual.Background;
        b.BorderBrush = visual.BorderBrush;
        b.BorderThickness = new Thickness(visual.BorderThickness);
        b.CornerRadius = new CornerRadius(4);
        b.ToolTip = visual.ToolTip;
        b.Cursor = Cursors.Hand;
        SetLeft(b, x * Unit + Gap);
        SetTop(b, y * Unit + Gap);
        // 回転は、回転の中心（レイアウト座標）をこの矩形の座標に直して掛ける。ヒットテストも回転に従う。
        if (slot.Angle != 0)
            b.RenderTransform = new RotateTransform(slot.Angle, (slot.OriginX - x) * Unit, (slot.OriginY - y) * Unit);
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
            Foreground = visual.MainForeground ?? Kasane.Views.AppTheme.Palette.KeyText,
            Margin = new Thickness(2, 10, 2, 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        });
        if (!string.IsNullOrEmpty(visual.Marks))
        {
            grid.Children.Add(new Border
            {
                Background = MarkBrush,
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(2, 0, 2, 0),
                Margin = new Thickness(0, 0, 2, 2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false, // クリック・ツールチップは、キーの面が受ける
                Child = new TextBlock
                {
                    Text = visual.Marks,
                    FontFamily = KeyFont,
                    FontSize = 8,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                },
            });
        }
        return grid;
    }

    internal static readonly Brush MarkBrush = new SolidColorBrush(Color.FromRgb(0xC2, 0x5E, 0x00));
}
