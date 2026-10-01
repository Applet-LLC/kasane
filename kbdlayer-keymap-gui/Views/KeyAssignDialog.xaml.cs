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

    // 修飾キーのチェックボックス（ビット番号 = KBDLAYER_MOD_*のビット。0=左Ctrl ... 7=右Win）。
    private static readonly string[] ModLabels =
        { "左Ctrl", "左Shift", "左Alt", "左Win", "右Ctrl", "右Shift", "右Alt", "右Win" };
    private readonly CheckBox[] _modChecks = new CheckBox[8];
    private bool _syncingMods;

    public string ResultToken { get; private set; }

    public KeyAssignDialog(KeySlot slot, int layer, string currentToken, int layerCount)
    {
        InitializeComponent();
        _layerCount = layerCount;
        ResultToken = currentToken;
        TxtHeader.Text = $"{slot.Description} ／ レイヤー{layer}";

        for (int bit = 0; bit < 8; bit++)
        {
            var cb = Id(new CheckBox { Content = ModLabels[bit], Margin = new Thickness(0, 0, 14, 0) },
                        $"ModCheck_{KeycodeTable.ModifierNames[bit]}"); // 例: ModCheck_MOD_LSFT
            cb.Checked += (_, _) => ApplyModsToToken();
            cb.Unchecked += (_, _) => ApplyModsToToken();
            _modChecks[bit] = cb;
            ModPanel.Children.Add(cb);
        }

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
            var b = Id(new Button
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
            }, $"KeycodeButton_{kc}"); // 例: KeycodeButton_KC_A
            b.Click += (_, _) => TxtToken.Text = WithCheckedMods(kc);
            b.MouseDoubleClick += (_, _) => { TxtToken.Text = WithCheckedMods(kc); Confirm(); };
            panel.Children.Add(b);
        }
        return panel;
    }

    private IEnumerable<int> Layers => Enumerable.Range(0, KbdLayerClient.MaxLayers);

    private UIElement LayerPanel()
    {
        var func = Combo(new[] { "MO", "TG", "DF", "OSL", "TO", "TT", "LM", "OSM" }, "FuncCombo");
        var layer = Combo(Layers.Select(i => i.ToString()), "LayerCombo");
        layer.SelectedIndex = Math.Min(1, KbdLayerClient.MaxLayers - 1);
        return Form(
            "MO(n): 押している間だけレイヤーnを有効にする\nTG(n): 押すたびにレイヤーnをON/OFFする\n" +
            "DF(n): 基本レイヤーをnに切り替える\nOSL(n): 次に押すキー1回だけレイヤーnにする\n" +
            "TO(n): レイヤーnだけを有効にする（他のレイヤーはOFF。基本レイヤーはそのまま）\n" +
            "TT(n): 押している間だけレイヤーnを有効にし、続けてタップする（既定5回。「タイミング...」で変更）とON/OFFを固定する\n" +
            "LM(n): 押している間、レイヤーnを有効にし、下で選んだ修飾キーも押す" +
            "（例: レイヤーnに▽のままのキーを押すと Ctrl+そのキー になる）\n" +
            "OSM: 下で選んだ修飾キーを、次に押すキー1回だけに掛ける（押したまま使えば普通の修飾キー。" +
            "もう一度タップで取り消し。レイヤーは使わない）",
            ("種類", func), ("レイヤー", layer),
            () =>
            {
                string f = (string)func.SelectedItem;
                if (f != "LM" && f != "OSM") return $"{f}({layer.SelectedItem})";
                byte mask = CheckedMask();
                if (mask == 0)
                {
                    TxtValidation.Foreground = Brushes.Firebrick;
                    TxtValidation.Text = $"{f} では、下の修飾キーを1つ以上チェックしてください";
                    return null;
                }
                return f == "OSM"
                    ? $"OSM({KeycodeTable.ModMaskToNames(mask)})"
                    : $"LM({layer.SelectedItem},{KeycodeTable.ModMaskToNames(mask)})";
            });
    }

    private UIElement ModTapPanel()
    {
        var mod = Combo(KeycodeTable.ModifierNames, "ModCombo");
        var tap = Id(new TextBox { Text = "KC_A", Width = 160, FontFamily = new FontFamily("Consolas") }, "TapKeyBox");
        return Form("短く押すとタップのキー、長押し（または他のキーと同時押し）で修飾キーになる。",
            ("修飾キー", mod), ("タップのキー", tap),
            () => $"MT({mod.SelectedItem},{tap.Text.Trim()})");
    }

    private UIElement LayerTapPanel()
    {
        var layer = Combo(Layers.Select(i => i.ToString()), "LayerCombo");
        layer.SelectedIndex = Math.Min(1, KbdLayerClient.MaxLayers - 1);
        var tap = Id(new TextBox { Text = "KC_SPACE", Width = 160, FontFamily = new FontFamily("Consolas") }, "TapKeyBox");
        return Form("短く押すとタップのキー、長押し（または他のキーと同時押し）でレイヤーを有効にする。",
            ("レイヤー", layer), ("タップのキー", tap),
            () => $"LT({layer.SelectedItem},{tap.Text.Trim()})");
    }

    private UIElement MacroPanel()
    {
        var kind = Combo(new[] { "MACRO", "TAPDANCE" }, "KindCombo");
        var id = Combo(Enumerable.Range(0, KbdLayerClient.MaxMacroCount).Select(i => i.ToString()), "IdCombo");
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

    private static ComboBox Combo(IEnumerable<string> items, string automationId)
    {
        var c = Id(new ComboBox { Width = 160, ItemsSource = items.ToList() }, automationId);
        c.SelectedIndex = 0;
        return c;
    }

    private UIElement Form(string help, (string Label, Control Input) a, (string Label, Control Input) b, Func<string?> build)
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
        var set = Id(new Button { Content = "キーコード欄に入れる", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(90, 8, 0, 0) }, "InsertButton");
        set.Click += (_, _) =>
        {
            var token = build();
            if (token != null) TxtToken.Text = token;
        };
        panel.Children.Add(set);
        return panel;
    }

    /// <summary>
    /// UI Automationの試験用にAutomationIdを付ける（動的に作るコントロールはx:Nameを持たないため）。
    /// 付けている主なID: ModCheck_MOD_xxx（修飾キーのチェックボックス）、KeycodeButton_KC_xxx（キーのボタン）、
    /// FuncCombo・LayerCombo・ModCombo・TapKeyBox・KindCombo・IdCombo・InsertButton（各パネル）。
    /// XAML側: TxtToken・BtnOk・BtnCancel・TxtValidation・LstCategories・ModPanel。
    /// </summary>
    private static T Id<T>(T element, string automationId) where T : UIElement
    {
        System.Windows.Automation.AutomationProperties.SetAutomationId(element, automationId);
        return element;
    }

    private byte CheckedMask()
    {
        byte mask = 0;
        for (int bit = 0; bit < 8; bit++)
        {
            if (_modChecks[bit].IsChecked == true) mask |= (byte)(1 << bit);
        }
        return mask;
    }

    /// <summary>基本キー（KC_*）なら、チェックした修飾キーを付けた形にする。それ以外はそのまま。</summary>
    private string WithCheckedMods(string token)
    {
        byte mask = CheckedMask();
        if (mask == 0 || !KeycodeTable.TryResolveBaseKey(token, out var sc) || sc == 0) return token;
        return KeycodeTable.WrapWithMods(mask, token);
    }

    /// <summary>チェックボックスの変更を、キーコード欄のキー（または LM）に反映する。</summary>
    private void ApplyModsToToken()
    {
        if (_syncingMods) return;
        if (!KeycodeTable.TryResolve(TxtToken.Text.Trim(), out var action)) return;
        byte mask = CheckedMask();
        switch (action.Type)
        {
            case ActionType.Key:
            case ActionType.ModdedKey:
            {
                var baseName = KeycodeTable.NameForScanCode(action.Param2);
                if (baseName == null) return;
                TxtToken.Text = mask == 0 ? baseName : KeycodeTable.WrapWithMods(mask, baseName);
                break;
            }
            case ActionType.LayerMod when mask != 0:
                TxtToken.Text = $"LM({action.Param1},{KeycodeTable.ModMaskToNames(mask)})";
                break;
            case ActionType.OneShotMod when mask != 0:
                TxtToken.Text = $"OSM({KeycodeTable.ModMaskToNames(mask)})";
                break;
        }
    }

    /// <summary>キーコード欄の内容に合わせて、チェックボックスの状態を変える（手で書き換えた場合も含む）。</summary>
    private void SyncModChecks(KeyAction action)
    {
        byte mask = action.Type switch
        {
            ActionType.ModdedKey => action.Param1,
            ActionType.LayerMod => (byte)(action.Param2 & 0xFF),
            ActionType.OneShotMod => action.Param1,
            ActionType.Key => 0,
            _ => CheckedMask(), // 修飾キーと関係ないキーコードでは今のチェックを保つ
        };
        _syncingMods = true;
        try
        {
            for (int bit = 0; bit < 8; bit++) _modChecks[bit].IsChecked = (mask & (1 << bit)) != 0;
        }
        finally { _syncingMods = false; }
    }

    private void TxtToken_TextChanged(object sender, TextChangedEventArgs e)
    {
        var token = TxtToken.Text.Trim();
        if (KeycodeTable.TryResolve(token, out var action, out var error))
        {
            SyncModChecks(action);
            string? warn = null;
            if (ActionType.ReferencesLayer(action.Type) && action.Param1 >= _layerCount)
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
