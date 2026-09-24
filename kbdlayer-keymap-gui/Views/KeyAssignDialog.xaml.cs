using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KbdLayerKeymapGui.Interop;
using KbdLayerKeymapGui.Keycodes;
using KbdLayerKeymapGui.Layout;

namespace KbdLayerKeymapGui.Views;

/// <summary>
/// 1キー・1レイヤー分のキーコードを選ぶダイアログ。カテゴリのボタンか下のテキスト欄で指定する。
/// どちらもテキスト欄の値を KeycodeTable.TryResolve で検証してから確定する。
/// </summary>
public partial class KeyAssignDialog : Window
{
    private const string CatLayer = "レイヤー操作";
    private const string CatModTap = "Mod-Tap (MT)";
    private const string CatLayerTap = "Layer-Tap (LT)";
    private const string CatMacro = "マクロ / タップダンス";

    private readonly int _layerCount;

    public string ResultToken { get; private set; }

    public KeyAssignDialog(KeySlot slot, int layer, string currentToken, int layerCount)
    {
        InitializeComponent();
        _layerCount = layerCount;
        ResultToken = currentToken;
        TxtHeader.Text = $"{slot.Description} ／ レイヤー{layer}";
        TxtToken.Text = currentToken;

        foreach (var c in KeycodeCatalog.Categories) LstCategories.Items.Add(c.Name);
        LstCategories.Items.Add(CatLayer);
        LstCategories.Items.Add(CatModTap);
        LstCategories.Items.Add(CatLayerTap);
        LstCategories.Items.Add(CatMacro);
        LstCategories.SelectedIndex = 0;

        Loaded += (_, _) => { TxtToken.Focus(); TxtToken.SelectAll(); };
    }

    private void LstCategories_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LstCategories.SelectedItem is not string name) return;
        var cat = KeycodeCatalog.Categories.FirstOrDefault(c => c.Name == name);
        CategoryContent.Content = cat != null ? KeyButtons(cat.Keycodes) : name switch
        {
            CatLayer => LayerPanel(),
            CatModTap => ModTapPanel(),
            CatLayerTap => LayerTapPanel(),
            _ => MacroPanel(),
        };
    }

    private UIElement KeyButtons(IEnumerable<string> keycodes)
    {
        var panel = new WrapPanel();
        foreach (var kc in keycodes)
        {
            var b = new Button
            {
                Content = new TextBlock
                {
                    Text = $"{KeycodeCatalog.ShortName(kc)}\n{kc}",
                    TextAlignment = TextAlignment.Center,
                    FontSize = 11,
                },
                Width = 96,
                Height = 44,
                ToolTip = kc,
            };
            b.Click += (_, _) => TxtToken.Text = kc;
            b.MouseDoubleClick += (_, _) => { TxtToken.Text = kc; Confirm(); };
            panel.Children.Add(b);
        }
        return panel;
    }

    private IEnumerable<int> Layers => Enumerable.Range(0, KbdLayerClient.MaxLayers);

    private UIElement LayerPanel()
    {
        var func = Combo(new[] { "MO", "TG", "DF", "OSL" });
        var layer = Combo(Layers.Select(i => i.ToString()));
        layer.SelectedIndex = Math.Min(1, KbdLayerClient.MaxLayers - 1);
        return Form(
            "MO(n): 押している間だけレイヤーnを有効にする\nTG(n): 押すたびにレイヤーnをON/OFFする\n" +
            "DF(n): 基本レイヤーをnに切り替える\nOSL(n): 次に押すキー1回だけレイヤーnにする",
            ("種類", func), ("レイヤー", layer),
            () => $"{func.SelectedItem}({layer.SelectedItem})");
    }

    private UIElement ModTapPanel()
    {
        var mod = Combo(KeycodeTable.ModifierNames);
        var tap = new TextBox { Text = "KC_A", Width = 160, FontFamily = new FontFamily("Consolas") };
        return Form("短く押すとタップのキー、長押し（または他のキーと同時押し）で修飾キーになる。",
            ("修飾キー", mod), ("タップのキー", tap),
            () => $"MT({mod.SelectedItem},{tap.Text.Trim()})");
    }

    private UIElement LayerTapPanel()
    {
        var layer = Combo(Layers.Select(i => i.ToString()));
        layer.SelectedIndex = Math.Min(1, KbdLayerClient.MaxLayers - 1);
        var tap = new TextBox { Text = "KC_SPACE", Width = 160, FontFamily = new FontFamily("Consolas") };
        return Form("短く押すとタップのキー、長押し（または他のキーと同時押し）でレイヤーを有効にする。",
            ("レイヤー", layer), ("タップのキー", tap),
            () => $"LT({layer.SelectedItem},{tap.Text.Trim()})");
    }

    private UIElement MacroPanel()
    {
        var kind = Combo(new[] { "MACRO", "TAPDANCE" });
        var id = Combo(Enumerable.Range(0, KbdLayerClient.MaxMacroCount).Select(i => i.ToString()));
        kind.SelectionChanged += (_, _) =>
        {
            int max = (string)kind.SelectedItem == "MACRO" ? KbdLayerClient.MaxMacroCount : KbdLayerClient.MaxTapDanceCount;
            id.ItemsSource = Enumerable.Range(0, max).Select(i => i.ToString()).ToList();
            id.SelectedIndex = 0;
        };
        return Form("MACRO(n): マクロnを実行する（中身は「マクロ...」で編集）\n" +
                    "TAPDANCE(n): タップ回数・長押しで動作を変える（中身は「タップダンス...」で編集）",
            ("種類", kind), ("番号", id),
            () => $"{kind.SelectedItem}({id.SelectedItem})");
    }

    private static ComboBox Combo(IEnumerable<string> items)
    {
        var c = new ComboBox { Width = 160, ItemsSource = items.ToList() };
        c.SelectedIndex = 0;
        return c;
    }

    private UIElement Form(string help, (string Label, Control Input) a, (string Label, Control Input) b, Func<string> build)
    {
        var panel = new StackPanel { Margin = new Thickness(4) };
        panel.Children.Add(new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        foreach (var (label, input) in new[] { a, b })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock { Text = label, Width = 90, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(input);
            panel.Children.Add(row);
        }
        var set = new Button { Content = "キーコード欄に入れる", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(90, 8, 0, 0) };
        set.Click += (_, _) => TxtToken.Text = build();
        panel.Children.Add(set);
        return panel;
    }

    private void TxtToken_TextChanged(object sender, TextChangedEventArgs e)
    {
        var token = TxtToken.Text.Trim();
        if (KeycodeTable.TryResolve(token, out var action, out var error))
        {
            string? warn = null;
            if (action.Type is ActionType.Momentary or ActionType.Toggle or ActionType.Default
                    or ActionType.OneShotLayer or ActionType.LayerTap && action.Param1 >= _layerCount)
                warn = $"レイヤー{action.Param1}はまだありません（レイヤーを追加してから適用してください）";
            TxtValidation.Foreground = warn == null ? Brushes.DarkGreen : Brushes.DarkOrange;
            TxtValidation.Text = warn ?? "OK: " + KeycodeCatalog.ShortName(token);
            BtnOk.IsEnabled = true;
        }
        else
        {
            TxtValidation.Foreground = Brushes.Firebrick;
            TxtValidation.Text = error;
            BtnOk.IsEnabled = false;
        }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e) => Confirm();

    private void Confirm()
    {
        var token = TxtToken.Text.Trim();
        if (!KeycodeTable.TryResolve(token, out _)) return;
        ResultToken = token;
        DialogResult = true;
    }
}
