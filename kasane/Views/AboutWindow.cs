using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>バージョン情報。メイン画面のボタンと、右クリックのメニューから開く（トレイにも同じ内容のダイアログがある）。UI Automation 用に Name を付ける。</summary>
public sealed class AboutWindow : Window
{
    public AboutWindow()
    {
        Name = "AboutWindow";
        Title = $"{AppInfo.DisplayName} のバージョン情報";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(28, 22, 28, 18), MinWidth = 280 };
        panel.Children.Add(new TextBlock { Name = "AboutName", Text = AppInfo.DisplayName, FontSize = 26, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Name = "AboutVersion", Text = $"バージョン {AppInfo.VersionText}", FontSize = 15, Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(new TextBlock { Name = "AboutBuild", Text = $"ビルド {AppInfo.BuildText}", Foreground = Brushes.DimGray, Margin = new Thickness(0, 2, 0, 0) });
        panel.Children.Add(new TextBlock { Name = "AboutPublisher", Text = AppInfo.Publisher, Foreground = Brushes.DimGray, Margin = new Thickness(0, 12, 0, 0) });

        var ok = new Button { Name = "AboutOk", Content = "OK", IsDefault = true, IsCancel = true, Width = 80, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        ok.Click += (_, _) => Close();
        panel.Children.Add(ok);
        Content = panel;
    }
}
