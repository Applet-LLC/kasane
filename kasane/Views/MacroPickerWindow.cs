using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>マクロ（またはタップダンス）を一覧（番号: 名前 — 中身）から選ぶ小さな画面。入力欄の「マクロ...」ボタンから開く。</summary>
public sealed class MacroPickerWindow : Window
{
    private readonly ListBox _list = new() { Name = "MacroPickList", Width = 560, Height = 300, FontFamily = new FontFamily("Consolas") };

    /// <summary>選んだマクロ。キャンセルなら null。</summary>
    public MacroInfo? Result { get; private set; }

    /// <param name="kind">"MACRO"（既定）か "TAPDANCE"。</param>
    public MacroPickerWindow(string kind = "MACRO")
    {
        bool tapDance = kind == "TAPDANCE";
        string noun = tapDance ? "タップダンス" : "マクロ";
        string nounEn = tapDance ? "tap dance" : "macro";
        Name = "MacroPickerWindow";
        Title = Loc.T($"{noun}を選ぶ", $"Choose a {nounEn}");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var items = tapDance ? MacroDirectory.TapDances : MacroDirectory.Items;
        foreach (var m in items) _list.Items.Add(m);
        if (items.Count > 0) _list.SelectedIndex = 0;

        var ok = new Button { Name = "MacroPickOk", Content = "OK", IsDefault = true, Width = 90, IsEnabled = items.Count > 0 };
        ok.Click += (_, _) => Choose();
        var cancel = new Button { Name = "MacroPickCancel", Content = Loc.T("キャンセル", "Cancel"), IsCancel = true, Width = 90, Margin = new Thickness(6, 0, 0, 0) };
        _list.MouseDoubleClick += (_, e) =>
        {
            if (_list.SelectedItem != null && ((FrameworkElement)e.OriginalSource).DataContext is MacroInfo) Choose();
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            Text = items.Count > 0
                ? Loc.T($"使う{noun}を選んでください(ダブルクリックでも選べます)。", $"Choose the {nounEn} to use (you can also double-click).")
                : Loc.T($"{noun}がありません。画面下の「{noun}...」で作ってください。", $"There are no {nounEn}s. Create one with \"{(tapDance ? "Tap dances" : "Macros")}...\" at the bottom of the main window."),
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(_list);
        panel.Children.Add(buttons);
        Content = panel;
    }

    private void Choose()
    {
        if (_list.SelectedItem is not MacroInfo m) return;
        Result = m;
        DialogResult = true;
    }
}
