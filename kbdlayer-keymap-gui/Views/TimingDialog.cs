using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KbdLayerKeymapGui.Interop;
using KbdLayerKeymapGui.Model;

namespace KbdLayerKeymapGui.Views;

/// <summary>TapTerm / ComboTerm / TapDanceTerm（ミリ秒、0=ドライバの既定値）の編集。</summary>
public sealed class TimingDialog : Window
{
    public TimingDialog(KeymapProject project, DriverConfig? current)
    {
        Title = "タイミング";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            Text = "単位はミリ秒です。0 はドライバの既定値（TapTerm 200 / ComboTerm 50 / TapDanceTerm 200）を使います。" +
                   (current != null ? $"\nキーボードの現在の値: TapTerm {current.TapTermMs} / ComboTerm {current.ComboTermMs} / TapDanceTerm {current.TapDanceTermMs}" : ""),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 10),
        });

        TextBox Field(string label, string help, uint value)
        {
            var box = new TextBox { Text = value.ToString(), Width = 80 };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(new TextBlock { Text = label, Width = 120, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(box);
            row.Children.Add(new TextBlock { Text = help, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray });
            panel.Children.Add(row);
            return box;
        }
        var tap = Field("TapTerm", "MT/LT: これより長く押すと長押し扱い", project.TapTermMs);
        var combo = Field("ComboTerm", "コンボ: 2キーの押下の間隔の上限", project.ComboTermMs);
        var tapDance = Field("TapDanceTerm", "タップダンス: タップ間隔・長押しの判定", project.TapDanceTermMs);

        var error = new TextBlock { Foreground = Brushes.Firebrick, Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(error);

        var ok = new Button { Content = "OK", IsDefault = true, Width = 90 };
        ok.Click += (_, _) =>
        {
            if (!uint.TryParse(tap.Text, out var t) || !uint.TryParse(combo.Text, out var c) || !uint.TryParse(tapDance.Text, out var d)
                || t > 10000 || c > 10000 || d > 10000)
            {
                error.Text = "0〜10000の整数で入力してください。";
                return;
            }
            project.TapTermMs = t;
            project.ComboTermMs = c;
            project.TapDanceTermMs = d;
            DialogResult = true;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "キャンセル", IsCancel = true, Width = 90 });
        panel.Children.Add(buttons);
        Content = panel;
    }
}
