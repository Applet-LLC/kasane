using System.Windows;
using System.Windows.Controls;

namespace Kasane.Views;

/// <summary>1行の文字列を入力する小さな画面。OK で <see cref="Value"/> に入る。入力欄の Name は引数で決める（UI Automation 用）。</summary>
public sealed class TextPromptWindow : Window
{
    private readonly TextBox _box;

    public string Value { get; private set; } = "";

    public TextPromptWindow(string title, string prompt, string initial, string boxName)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;

        _box = new TextBox { Name = boxName, Text = initial, Margin = new Thickness(0, 4, 0, 10) };
        var ok = new Button { Name = "OkButton", Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => { Value = _box.Text; DialogResult = true; };
        var cancel = new Button { Name = "CancelButton", Content = "キャンセル", IsCancel = true, Width = 80 };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_box);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }
}
