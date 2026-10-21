using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kasane.Interop;

namespace Kasane.Views;

/// <summary>「実行中のアプリから選ぶ」。今開いているウィンドウの一覧から1つ選ぶ（前面のアプリの規則の照合条件を入れるため）。</summary>
public sealed class WindowPickerWindow : Window
{
    private readonly ListBox _list = new() { Name = "WindowList", FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _includeClass = new() { Name = "IncludeClassCheck", Content = "クラス名も条件に入れる", Margin = new Thickness(0, 6, 0, 0) };
    private List<OpenWindow> _windows = new();

    public OpenWindow? Selected { get; private set; }

    /// <summary>クラス名も照合条件にするか。</summary>
    public bool IncludeClass { get; private set; }

    public WindowPickerWindow()
    {
        Title = "実行中のアプリから選ぶ";
        Width = 760;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var help = new TextBlock
        {
            Text = "開いているウィンドウの一覧です。選ぶと、exe のファイル名が照合条件(Exe)に入ります。" +
                   "管理者として動くアプリも、exe の名前は取れます。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = AppTheme.Palette.SubText,
        };
        var refresh = new Button { Name = "RefreshButton", Content = "一覧を更新", Width = 100, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        refresh.Click += (_, _) => Reload();

        var ok = new Button { Name = "OkButton", Content = "選ぶ", IsDefault = true, Width = 90 };
        ok.Click += (_, _) => Accept();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Name = "CancelButton", Content = "キャンセル", IsCancel = true, Width = 90 });

        var top = new StackPanel();
        top.Children.Add(help);
        top.Children.Add(refresh);

        var bottom = new StackPanel();
        bottom.Children.Add(_includeClass);
        bottom.Children.Add(buttons);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(_list);
        Content = root;

        _list.MouseDoubleClick += (_, _) => Accept();
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        _windows = OpenWindowList.Enumerate();
        _list.Items.Clear();
        foreach (var w in _windows)
        {
            string title = w.Title.Length > 48 ? w.Title[..47] + "…" : w.Title; // 長いタイトルで横スクロールが要らないように
            _list.Items.Add($"{w.ExeName,-24} {w.ClassName,-28} {title}");
        }
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private void Accept()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _windows.Count) return;
        Selected = _windows[_list.SelectedIndex];
        IncludeClass = _includeClass.IsChecked == true;
        DialogResult = true;
    }
}
