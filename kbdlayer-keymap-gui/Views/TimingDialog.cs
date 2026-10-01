using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KbdLayerKeymapGui.Interop;
using KbdLayerKeymapGui.Model;

namespace KbdLayerKeymapGui.Views;

/// <summary>
/// TapTerm / ComboTerm / TapDanceTerm（ミリ秒、0=ドライバの既定値）と、MT/LT・タップダンスの判定方式・
/// Quick Tap・TT(n)の回数の編集。
/// </summary>
public sealed class TimingDialog : Window
{
    private static readonly string[] ModeLabels =
    {
        "別のキーを押したらすぐ長押し扱い（既定）",
        "別のキーを押して離したら長押し扱い（Permissive Hold）",
        "時間内に離せば短押し扱い（時間切れで長押し）",
    };

    public TimingDialog(KeymapProject project, DriverConfig? current)
    {
        Title = "タイミング";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;

        var panel = new StackPanel { Margin = new Thickness(14) };
        string currentText = "";
        if (current != null)
        {
            currentText = $"\nキーボードの現在の値: TapTerm {current.TapTermMs} / ComboTerm {current.ComboTermMs} / TapDanceTerm {current.TapDanceTermMs}";
            if (current.HasTapHoldOptions)
                currentText += $" / 判定方式 {current.TapHoldMode} / Quick Tap {QuickTapText(current.QuickTapTermMs)} / TT {current.TappingToggle}回";
            else
                currentText += "\n（このドライバは判定方式・Quick Tap・TTの回数に対応していません。設定しても効きません）";
        }
        panel.Children.Add(new TextBlock
        {
            Text = "単位はミリ秒です。0 はドライバの既定値（TapTerm 200 / ComboTerm 50 / TapDanceTerm 200）を使います。" + currentText,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 10),
        });

        StackPanel Row(string label, UIElement input, string help)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(new TextBlock { Text = label, Width = 120, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(input);
            row.Children.Add(new TextBlock { Text = help, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray });
            panel.Children.Add(row);
            return row;
        }
        TextBox Field(string name, string label, string help, uint value)
        {
            var box = new TextBox { Name = name, Text = value.ToString(), Width = 80 };
            Row(label, box, help);
            return box;
        }
        var tap = Field("TapTermBox", "TapTerm", "MT/LT: これより長く押すと長押し扱い", project.TapTermMs);
        var combo = Field("ComboTermBox", "ComboTerm", "コンボ: 2キーの押下の間隔の上限", project.ComboTermMs);
        var tapDance = Field("TapDanceTermBox", "TapDanceTerm", "タップダンス: タップ間隔・長押しの判定", project.TapDanceTermMs);

        var mode = new ComboBox { Name = "TapHoldModeBox", Width = 330, ItemsSource = ModeLabels };
        mode.SelectedIndex = project.TapHoldMode < ModeLabels.Length ? (int)project.TapHoldMode : 0;
        Row("判定方式", mode, "MT/LT（タップダンスは常に既定の方式）");

        bool quickOff = project.QuickTapTermMs == KbdLayerClient.QuickTapTermDisabled;
        var quick = new TextBox { Name = "QuickTapTermBox", Text = quickOff ? "0" : project.QuickTapTermMs.ToString(), Width = 80, IsEnabled = !quickOff };
        var quickEnabled = new CheckBox { Name = "QuickTapEnabled", Content = "使う", IsChecked = !quickOff, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        quickEnabled.Checked += (_, _) => quick.IsEnabled = true;
        quickEnabled.Unchecked += (_, _) => quick.IsEnabled = false;
        var quickRow = new StackPanel { Orientation = Orientation.Horizontal };
        quickRow.Children.Add(quick);
        quickRow.Children.Add(quickEnabled);
        Row("Quick Tap", quickRow, "短押しの直後に押し続けるとリピート（0 = TapTermと同じ）");

        var tt = Field("TappingToggleBox", "TT の回数", "TT(n): この回数続けてタップするとON/OFFを固定（0 = 既定の5）", project.TappingToggle);

        var error = new TextBlock { Foreground = Brushes.Firebrick, Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(error);

        var ok = new Button { Name = "OkButton", Content = "OK", IsDefault = true, Width = 90 };
        ok.Click += (_, _) =>
        {
            if (!uint.TryParse(tap.Text, out var t) || !uint.TryParse(combo.Text, out var c) || !uint.TryParse(tapDance.Text, out var d)
                || t > 10000 || c > 10000 || d > 10000)
            {
                error.Text = "TapTerm・ComboTerm・TapDanceTerm は0〜10000の整数で入力してください。";
                return;
            }
            uint q = KbdLayerClient.QuickTapTermDisabled;
            if (quickEnabled.IsChecked == true && (!uint.TryParse(quick.Text, out q) || q > 10000))
            {
                error.Text = "Quick Tap は0〜10000の整数で入力してください。";
                return;
            }
            if (!uint.TryParse(tt.Text, out var n) || n > KbdLayerClient.MaxTappingToggle)
            {
                error.Text = $"TT の回数は0〜{KbdLayerClient.MaxTappingToggle}の整数で入力してください。";
                return;
            }
            project.TapTermMs = t;
            project.ComboTermMs = c;
            project.TapDanceTermMs = d;
            project.TapHoldMode = (uint)Math.Max(0, mode.SelectedIndex);
            project.QuickTapTermMs = q;
            project.TappingToggle = n;
            DialogResult = true;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Name = "CancelButton", Content = "キャンセル", IsCancel = true, Width = 90 });
        panel.Children.Add(buttons);
        Content = panel;
    }

    private static string QuickTapText(uint ms) =>
        ms == KbdLayerClient.QuickTapTermDisabled ? "使わない" : ms == 0 ? "TapTermと同じ" : $"{ms}ms";
}
