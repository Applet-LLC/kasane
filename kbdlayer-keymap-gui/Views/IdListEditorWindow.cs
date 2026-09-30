using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KbdLayerKeymapGui.Views;

/// <summary>
/// マクロ・コンボ・タップダンス編集画面の共通部分: 左にID一覧、右に選択中IDの編集欄、下にOK/キャンセル。
/// 編集内容は派生クラスが作業用コピーに持ち、OKで検証が通ったときだけ呼び出し元へ返す。
/// </summary>
public abstract class IdListEditorWindow : Window
{
    private readonly ListBox _list = new() { Name = "IdList", Width = 300, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock _validation = new() { Name = "ValidationText", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    protected readonly StackPanel Detail = new();
    private readonly int _count;

    // UI Automationでの要素特定用（WPFの既定AutomationPeerはx:Name/Nameを
    // AutomationIdとして使うため、コード生成のコントロールにも明示的に設定しておく）。
    protected IdListEditorWindow(string title, int count, string help)
    {
        _count = count;
        Title = title;
        Width = 900;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var ok = new Button { Name = "OkButton", Content = "OK", IsDefault = true, Width = 90 };
        ok.Click += (_, _) =>
        {
            if (ValidateAll(out var error)) DialogResult = true;
            else ShowValidation(error, isError: true);
        };
        var cancel = new Button { Name = "CancelButton", Content = "キャンセル", IsCancel = true, Width = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var right = new StackPanel();
        right.Children.Add(new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 10) });
        right.Children.Add(Detail);
        right.Children.Add(_validation);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_list, Dock.Left);
        root.Children.Add(buttons);
        root.Children.Add(_list);
        root.Children.Add(new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedIndex < 0) return;
            Detail.Children.Clear();
            ShowDetail(_list.SelectedIndex);
            ShowValidation("", false);
        };
        Loaded += (_, _) =>
        {
            for (int i = 0; i < _count; i++) _list.Items.Add(ItemText(i));
            _list.SelectedIndex = 0;
        };
    }

    private string ItemText(int id)
    {
        var s = Summary(id);
        return $"{id,2}: {(string.IsNullOrEmpty(s) ? "（未使用）" : s)}";
    }

    /// <summary>派生クラスが編集内容を変えたら呼ぶ（一覧の表示を更新する）。</summary>
    protected void RefreshItem(int id)
    {
        if (id >= 0 && id < _list.Items.Count) _list.Items[id] = ItemText(id);
    }

    protected void ShowValidation(string text, bool isError)
    {
        _validation.Foreground = isError ? Brushes.Firebrick : Brushes.DarkGreen;
        _validation.Text = text;
    }

    protected static UIElement Row(string label, UIElement input)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(input);
        return row;
    }

    /// <summary><paramref name="name"/>を渡すとUI Automationでの要素特定用に<c>Name</c>を設定する
    /// （例: "SingleTap"）。省略時は無名（呼び出し側が複数箇所で使い回す場合など）。</summary>
    protected static TextBox KeycodeBox(string text, string? name = null) =>
        new() { Name = name ?? "", Text = text, Width = 300, FontFamily = new FontFamily("Consolas") };

    protected abstract string Summary(int id);
    protected abstract void ShowDetail(int id);
    protected abstract bool ValidateAll(out string error);
}
