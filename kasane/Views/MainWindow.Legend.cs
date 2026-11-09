using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using Kasane.Layout;
using Kasane.Model;

namespace Kasane.Views;

// キーボード図の凡例（2026-11-08、UI の文言を削る作業）。前は、キーボード図の下に長い 1 段落の文で出していた。
// 今は「凡例」ボタンで、キーと同じ色・枠・印の見本を並べたポップアップを開く。色はその時のテーマ（ライト / ダーク）で作る。
public partial class MainWindow
{
    private Popup? _legendPopup;
    private DateTime _legendClosedAt;

    private void BtnLegend_Click(object sender, RoutedEventArgs e)
    {
        if (_legendPopup is { IsOpen: true }) { _legendPopup.IsOpen = false; return; }
        // 開いているときにボタンをマウスで押すと、押した時点で（StaysOpen=false で）閉じてから Click が来る。開き直さず、閉じたままにする。
        if ((DateTime.UtcNow - _legendClosedAt).TotalMilliseconds < 300) return;
        _legendPopup = new Popup
        {
            PlacementTarget = BtnLegend,
            Placement = PlacementMode.Top,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = BuildLegend(),
        };
        _legendPopup.Closed += (_, _) => _legendClosedAt = DateTime.UtcNow;
        _legendPopup.IsOpen = true;
    }

    private UIElement BuildLegend()
    {
        var p = AppTheme.Palette;
        var rows = new StackPanel { Name = "LegendPanel" };

        void Row(UIElement sample, string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(sample);
            row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
            rows.Children.Add(row);
        }
        Border Key(Brush bg, Brush border, double thickness, string text = "", Brush? fg = null) => new()
        {
            Width = 34, Height = 24, CornerRadius = new CornerRadius(4),
            Background = bg, BorderBrush = border, BorderThickness = new Thickness(thickness),
            Child = new TextBlock { Text = text, Foreground = fg ?? p.KeyText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        Border Mark(string m) => new()
        {
            Background = KeyboardCanvas.MarkBrush, CornerRadius = new CornerRadius(2), Padding = new Thickness(3, 0, 3, 0),
            Width = 34, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = m, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
        };

        Row(Key(p.KeyChanged, p.KeyBorder, 1), Loc.T("既定から変更", "Changed from the default"));
        Row(Key(p.KeyAction, p.KeyBorder, 1), Loc.T("レイヤー・Mod-Tap など", "Layer, Mod-Tap, and so on"));
        Row(Key(p.KeyTransparent, p.KeyBorder, 1, "▽", p.KeyTransparentText), Loc.T("下のレイヤーと同じ(透過)", "Same as the layer below (transparent)"));
        Row(Key(p.KeyDefault, p.KeyMeasuredBorder, 2), Loc.T("実測値が既定と異なる", "Measured value differs from the default"));
        Row(Key(p.KeyDefault, p.KeyUnknownBorder, 2), Loc.T("スキャンコード不明(要実測)", "Scan code unknown (needs measuring)"));
        Row(Mark("M"), Loc.T("マクロ", "Macro"));
        Row(Mark("D"), Loc.T("タップダンス", "Tap dance"));
        Row(Mark("C"), Loc.T("コンボ", "Combo"));
        Row(Mark("R"), Loc.T("キー置換", "Key override"));
        Row(Mark("H"), Loc.T("ヘルパー動作 HLP(Id)", "Helper action HLP(Id)"));
        Row(Mark("T"), Loc.T("ヘルパーのトリガーのキー", "Helper trigger key"));

        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
        var help = HelpButton.Create(HelpTopics.KeyboardView);
        DockPanel.SetDock(help, Dock.Right);
        footer.Children.Add(help);
        footer.Children.Add(new TextBlock
        {
            Text = Loc.T("右クリックでキーのメニューを開きます", "Right-click a key for its menu"),
            Foreground = p.SubText, VerticalAlignment = VerticalAlignment.Center,
        });
        rows.Children.Add(footer);

        var frame = new Border
        {
            BorderBrush = p.KeyBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 8, 10, 8), Child = rows,
        };
        // ポップアップはウィンドウの外に出るので、背景と文字の色を、テーマの資源から明示して取る。
        // （ポップアップは、ウィンドウの資源を引き継がないので、資源の参照ではなく、今の値を写す）
        // ライトのテーマには資源が無い（システムの色のまま）ので、そのときはシステムの色を使う。
        frame.Background = TryFindResource("Theme.WindowBackground") as Brush ?? SystemColors.WindowBrush;
        TextElement.SetForeground(rows, TryFindResource("Theme.Text") as Brush ?? SystemColors.ControlTextBrush);
        return frame;
    }
}
