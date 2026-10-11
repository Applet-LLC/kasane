using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kasane.Keycodes;

namespace Kasane.Views;

/// <summary>
/// 出力キーを選ぶ画面。「キーボード」（画面上のキーボードをクリック）と「一覧」（カテゴリ別のキーコード）の2つのタブから選ぶ。
/// 修飾キーにチェックすると、選んだキーが LCTL(KC_C) のような修飾付きキーになる（<c>allowMods</c> が true のとき）。
/// OK で <see cref="ResultToken"/> に入る。
/// </summary>
public sealed class KeycodePickerWindow : Window
{
    private static readonly string[] ModLabels = { "左Ctrl", "左Shift", "左Alt", "左Win", "右Ctrl", "右Shift", "右Alt", "右Win" };

    private readonly CheckBox[] _modChecks = new CheckBox[8];
    private readonly TextBox _token = new() { Name = "PickerToken", FontFamily = new FontFamily("Consolas"), FontSize = 14, IsReadOnly = true };
    private string _baseKey = "";

    public string ResultToken { get; private set; } = "";

    public KeycodePickerWindow(bool allowMods)
    {
        Title = "キーを選ぶ";
        Name = "KeycodePicker";
        Width = 1180;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var tabs = new TabControl { Name = "PickerTabs" };
        var soft = new SoftKeyboardPanel { Margin = new Thickness(8) };
        soft.KeySelected += kc => SetBase(kc);
        tabs.Items.Add(new TabItem { Header = "キーボード", Name = "PickerTabKeyboard", Content = soft });
        tabs.Items.Add(new TabItem { Header = "一覧", Name = "PickerTabList", Content = BuildList() });

        var ok = new Button { Name = "PickerOk", Content = "OK", IsDefault = true, Width = 80, IsEnabled = false };
        ok.Click += (_, _) => { ResultToken = _token.Text; DialogResult = true; };
        _token.TextChanged += (_, _) => ok.IsEnabled = _token.Text.Length > 0;

        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Name = "PickerCancel", Content = "キャンセル", IsCancel = true, Width = 80 });
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        var tokenLabel = new TextBlock { Text = "選んだキー:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        DockPanel.SetDock(tokenLabel, Dock.Left);
        bottom.Children.Add(tokenLabel);
        _token.Margin = new Thickness(0, 0, 8, 0);
        bottom.Children.Add(_token);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        if (allowMods)
        {
            var mods = new WrapPanel();
            for (int bit = 0; bit < 8; bit++)
            {
                var cb = new CheckBox { Content = ModLabels[bit], Margin = new Thickness(0, 0, 14, 0), Name = $"PickerMod_{KeycodeTable.ModifierNames[bit]}" };
                cb.Checked += (_, _) => Refresh();
                cb.Unchecked += (_, _) => Refresh();
                _modChecks[bit] = cb;
                mods.Children.Add(cb);
            }
            var modBox = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            modBox.Children.Add(new TextBlock { Text = "修飾キー（チェックすると、Ctrl+C のようなショートカットとして送ります）:", Margin = new Thickness(0, 0, 0, 4) });
            modBox.Children.Add(mods);
            DockPanel.SetDock(modBox, Dock.Bottom);
            root.Children.Add(modBox);
        }

        root.Children.Add(tabs);
        Content = root;
    }

    private UIElement BuildList()
    {
        var categories = new ListBox { Name = "PickerCategories", Width = 150, Margin = new Thickness(0, 0, 8, 0) };
        var content = new ContentControl();
        foreach (var c in KeycodeCatalog.Categories) categories.Items.Add(c.Name);
        categories.SelectionChanged += (_, _) =>
        {
            var cat = KeycodeCatalog.Categories.FirstOrDefault(c => c.Name == categories.SelectedItem as string);
            content.Content = cat == null ? null : KeyButtons(cat.Keycodes);
        };
        categories.SelectedIndex = 0;

        var dock = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(categories, Dock.Left);
        dock.Children.Add(categories);
        dock.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return dock;
    }

    private UIElement KeyButtons(IEnumerable<string> keycodes)
    {
        var panel = new WrapPanel();
        foreach (var kc in keycodes)
        {
            var b = new Button
            {
                Name = $"KeycodeButton_{kc}",
                Content = new TextBlock { Text = $"{KeycodeCatalog.ShortName(kc)}\n{kc}", TextAlignment = TextAlignment.Center, FontSize = 11 },
                Width = 96,
                Height = 44,
                ToolTip = kc,
            };
            string key = kc;
            b.Click += (_, _) => SetBase(key);
            b.MouseDoubleClick += (_, _) => { SetBase(key); if (_token.Text.Length > 0) { ResultToken = _token.Text; DialogResult = true; } };
            panel.Children.Add(b);
        }
        return panel;
    }

    private void SetBase(string keycode)
    {
        _baseKey = keycode;
        Refresh();
    }

    private void Refresh()
    {
        if (_baseKey.Length == 0) { _token.Text = ""; return; }
        byte mask = 0;
        for (int bit = 0; bit < 8; bit++)
            if (_modChecks[bit]?.IsChecked == true) mask |= (byte)(1 << bit);
        _token.Text = mask == 0 ? _baseKey : KeycodeTable.WrapWithMods(mask, _baseKey);
    }
}
