using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kasane.Interop;
using Kasane.Keycodes;
using Kasane.Layout;
using Kasane.Model;

namespace Kasane.Views;

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
    private const string CatHelper = "ヘルパー動作";
    private const string CatSoftKeyboard = "画面のキーボード"; // 末尾に足す（UIA 試験が分類を番号で選ぶため）

    private readonly int _layerCount;
    private readonly uint _uniqId;                 // このキーボード（0 = 決まっていない。共通の動作だけ使える）
    private HelperSettingsModel? _helperModel;     // 「ヘルパー動作」を開いたときに読む

    // 修飾キーのチェックボックス（ビット番号 = KBDLAYER_MOD_*のビット。0=左Ctrl ... 7=右Win）。
    private static readonly string[] ModLabels =
        { "左Ctrl", "左Shift", "左Alt", "左Win", "右Ctrl", "右Shift", "右Alt", "右Win" };
    private readonly CheckBox[] _modChecks = new CheckBox[8];
    private bool _syncingMods;

    public string ResultToken { get; private set; }

    /// <summary>
    /// true なら、<c>Show()</c> で開く非モーダルの画面として使う（メイン画面から。マクロの編集画面などを開いたまま使える）。
    /// OK で <see cref="Assigned"/> を起こして閉じ、キャンセル・× は何も起こさず閉じる。false（既定）は <c>ShowDialog()</c> 用で、OK が DialogResult を true にする。
    /// </summary>
    public bool NonModal { get; init; }

    /// <summary>非モーダルで OK されたとき（<see cref="ResultToken"/> が確定している）。</summary>
    public event Action? Assigned;

    private readonly KeymapProject? _project; // レイヤー・マクロ・タップダンスの名前を選択肢に出すため（無ければ番号だけ）

    public KeyAssignDialog(KeySlot slot, int layer, string currentToken, int layerCount, uint uniqId = 0, KeymapProject? project = null)
    {
        InitializeComponent();
        _layerCount = layerCount;
        _uniqId = uniqId;
        _project = project;
        ResultToken = currentToken;
        // 非モーダルのときは、IsCancel だけでは閉じない（ShowDialog 用の動き）ので、自分で閉じる。
        BtnCancel.Click += (_, _) => { if (NonModal) Close(); };
        // トークン欄は、マクロの編集画面の「他の画面の入力欄に挿入」の入れ先にもなる。
        MacroInsert.Register(TxtToken, token => TxtToken.Text = token, acceptsTapDance: true);
        string? layerName = project == null ? null : ProjectService.LayerName(project, layer);
        TxtHeader.Text = $"{slot.Description} ／ レイヤー{layer}" + (layerName != null ? $"（{layerName}）" : "");

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
        LstCategories.Items.Add(CatHelper);
        LstCategories.Items.Add(CatSoftKeyboard);
        LstCategories.SelectedIndex = 0;

        Loaded += (_, _) => { TxtToken.Focus(); TxtToken.SelectAll(); };
    }

    private void LstCategories_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LstCategories.SelectedItem is not string name) return;
        var cat = KeycodeCatalog.Categories.FirstOrDefault(c => c.Name == name);
        CategoryContent.Content = cat != null ? CategoryKeys(cat) : name switch
        {
            CatLayer => LayerPanel(),
            CatModTap => ModTapPanel(),
            CatLayerTap => LayerTapPanel(),
            CatHelper => HelperPanel(),
            CatSoftKeyboard => SoftKeyboard(),
            _ => MacroPanel(),
        };
    }

    // 分類のキーのボタン。NLS は、キーボードの配列に関係なく選べることを、一言添える
    // （US配列のキーボードにこれらのキーは無いが、出力としては割り当てられる。たとえば、US配列のキーに「変換」を割り当てられる）。
    private UIElement CategoryKeys(KeycodeCatalog.Category cat)
    {
        if (cat.Name != "NLS") return KeyButtons(cat.Keycodes);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "NLS（National Language Support）キー: 日本語キーボードにあるキーです。使っているキーボード（US配列など）に無いキーも、" +
                   "出力としては割り当てられます（例: US配列のキーに「変換」を割り当てる）。",
            TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Palette.SubText, Margin = new Thickness(4, 4, 4, 8),
        });
        panel.Children.Add(KeyButtons(cat.Keycodes));
        return panel;
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

    private UIElement SoftKeyboard()
    {
        var panel = new SoftKeyboardPanel();
        panel.KeySelected += kc => TxtToken.Text = WithCheckedMods(kc);
        return panel;
    }

    private KeycodeCapture? _capture;

    private void BtnCapture_Click(object sender, RoutedEventArgs e)
    {
        _capture ??= new KeycodeCapture(
            token => TxtToken.Text = WithCheckedMods(token),
            () => { BtnCapture.Content = "押して入力"; TxtToken.Focus(); });
        if (_capture.IsActive) { _capture.Cancel(); return; }
        BtnCapture.Content = "キーを押してください…";
        _capture.Start();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        _capture?.Cancel();
        base.OnDeactivated(e);
    }

    private IEnumerable<int> Layers => Enumerable.Range(0, KbdLayerClient.MaxLayers);

    private UIElement LayerPanel()
    {
        var func = Combo(new[] { "MO", "TG", "DF", "OSL", "TO", "TT", "LM", "OSM" }, "FuncCombo");
        var layer = NamedCombo(NamedChoice.Layers(_project), "LayerCombo");
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
                if (f != "LM" && f != "OSM") return $"{f}({Num(layer)})";
                byte mask = CheckedMask();
                if (mask == 0)
                {
                    TxtValidation.Foreground = Brushes.Firebrick;
                    TxtValidation.Text = $"{f} では、下の修飾キーを1つ以上チェックしてください";
                    return null;
                }
                return f == "OSM"
                    ? $"OSM({KeycodeTable.ModMaskToNames(mask)})"
                    : $"LM({Num(layer)},{KeycodeTable.ModMaskToNames(mask)})";
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
        var layer = NamedCombo(NamedChoice.Layers(_project), "LayerCombo");
        layer.SelectedIndex = Math.Min(1, KbdLayerClient.MaxLayers - 1);
        var tap = Id(new TextBox { Text = "KC_SPACE", Width = 160, FontFamily = new FontFamily("Consolas") }, "TapKeyBox");
        return Form("短く押すとタップのキー、長押し（または他のキーと同時押し）でレイヤーを有効にする。",
            ("レイヤー", layer), ("タップのキー", tap),
            () => $"LT({Num(layer)},{tap.Text.Trim()})");
    }

    private UIElement MacroPanel()
    {
        var kind = Combo(new[] { "MACRO", "TAPDANCE" }, "KindCombo");
        var id = NamedCombo(NamedChoice.Macros(_project), "IdCombo");
        kind.SelectionChanged += (_, _) =>
        {
            id.ItemsSource = (string)kind.SelectedItem == "MACRO" ? NamedChoice.Macros(_project) : NamedChoice.TapDances(_project);
            id.SelectedIndex = 0;
        };
        // 番号の横の「マクロ...」「タップダンス...」で、番号・名前・中身の一覧から選べる（選ぶと種類も合わせる）。ボタンの名前は種類に合わせて変わる。
        var pick = Id(new Button { Content = "マクロ...", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 1, 8, 1),
                                   ToolTip = "一覧（番号・名前・中身）から選びます。" }, "MacroPickButton");
        kind.SelectionChanged += (_, _) => pick.Content = (string)kind.SelectedItem == "MACRO" ? "マクロ..." : "タップダンス...";
        pick.Click += (_, _) =>
        {
            var dlg = new MacroPickerWindow((string)kind.SelectedItem) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Result is not { } m) return;
            kind.SelectedItem = m.Kind;                   // 種類を変えると番号の一覧が作り直される
            id.SelectedIndex = Math.Min(m.Id, id.Items.Count - 1);
        };
        var idRow = new ContentControl { Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { id, pick } } };
        return Form("MACRO(n): マクロnを実行する（中身は「マクロ...」で編集）\n" +
                    "TAPDANCE(n): タップ回数・長押しで動作を変える（中身は「タップダンス...」で編集）",
            ("種類", kind), ("番号", idRow),
            () => $"{kind.SelectedItem}({Num(id)})");
    }

    // ------------------------------------------------------------------
    // ヘルパー動作（HLP(id)。helper.json の Actions）
    // ------------------------------------------------------------------

    private HelperSettingsModel Helper => _helperModel ??= new HelperSettingsModel(_uniqId, TrayConfig.Load());

    private bool HelperIdTaken(int id) =>
        Helper.Device.Actions.Any(a => a.Id == id) || Helper.Global.Actions.Any(a => a.Id == id);

    private UIElement HelperPanel()
    {
        var panel = new StackPanel { Margin = new Thickness(4) };
        panel.Children.Add(new TextBlock
        {
            Text = "HLP(Id) を割り当てたキーは、キー自体は何も出さず、トレイ（kasane-tray）が動作を実行します" +
                   "（プログラムの起動・文字列の入力・マウス操作・レイヤーの操作など）。動作はこのキーボード用か、全キーボード共通です。\n" +
                   "HLP は、トレイ（kasane-tray）が動いているときだけ働きます。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var list = Id(new ListBox { Height = 230, FontFamily = new FontFamily("Consolas") }, "HelperActionList");
        var ids = new List<int>();
        void Fill()
        {
            list.Items.Clear();
            ids.Clear();
            if (Helper.DeviceWritable)
                foreach (var a in Helper.Device.Actions) { list.Items.Add("[このキーボード] " + HelperSummary.Action(a)); ids.Add(a.Id); }
            foreach (var a in Helper.Global.Actions) { list.Items.Add("[共通] " + HelperSummary.Action(a)); ids.Add(a.Id); }
        }
        Fill();
        panel.Children.Add(list);

        var assign = Id(new Button { Content = "選んだ動作を割り当てる", Width = 170, Margin = new Thickness(0, 0, 8, 0) }, "AssignHelperButton");
        var create = Id(new Button { Content = "新しい動作...", Width = 120 }, "NewHelperActionButton");
        void Assign()
        {
            if (list.SelectedIndex >= 0 && list.SelectedIndex < ids.Count) TxtToken.Text = $"HLP({ids[list.SelectedIndex]})";
        }
        assign.Click += (_, _) => Assign();
        list.MouseDoubleClick += (_, _) => { Assign(); Confirm(); };
        create.Click += (_, _) =>
        {
            if (NewHelperAction()) Fill();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(assign);
        buttons.Children.Add(create);
        panel.Children.Add(buttons);
        if (Helper.DeviceLoadError != null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "このキーボードの helper.json を読めないため、このキーボード用の動作は使えません。新しい動作は共通に作ります。",
                Foreground = Brushes.Firebrick,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }
        return panel;
    }

    /// <summary>新しい動作を作って、すぐ helper.json（このキーボードが使えなければ共通）に保存し、HLP(Id) をキーコード欄に入れる。</summary>
    private bool NewHelperAction()
    {
        int id = Helper.NextFreeActionId();
        if (id < 0)
        {
            MessageBox.Show(this, "使える Id がありません。", "動作の追加");
            return false;
        }
        var dlg = new HelperActionEditorWindow("動作の追加", HelperEditMode.Action, id, null, null, null,
            new HelperActionBody { Type = HelperActionBody.Launch }, HelperIdTaken) { Owner = this };
        if (dlg.ShowDialog() != true) return false;

        var scope = Helper.DeviceWritable ? HelperScope.Device : HelperScope.Global;
        var added = new HelperAction { Id = dlg.ResultId, Name = dlg.ResultName, Action = dlg.ResultBody };
        var actions = Helper.Config(scope).Actions;
        actions.Add(added);
        if (!Helper.TrySave(out var error))
        {
            actions.Remove(added);
            MessageBox.Show(this, $"保存できませんでした: {error}", "動作の追加", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        TxtToken.Text = $"HLP({added.Id})";
        return true;
    }

    private static ComboBox Combo(IEnumerable<string> items, string automationId)
    {
        var c = Id(new ComboBox { Width = 160, ItemsSource = items.ToList() }, automationId);
        c.SelectedIndex = 0;
        return c;
    }

    /// <summary>番号で選ぶ欄（レイヤー・マクロ・タップダンス）。名前を付けてあるものは「3: 名前」と出す。値は <see cref="Num"/> で取る。</summary>
    private static ComboBox NamedCombo(IEnumerable<NamedChoice> items, string automationId)
    {
        var c = Id(new ComboBox { Width = 240, ItemsSource = items.ToList() }, automationId);
        c.SelectedIndex = 0;
        return c;
    }

    private static int Num(ComboBox combo) => ((NamedChoice)combo.SelectedItem).Number;

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
                // 名前の無いキー（SC_XXXX）にも修飾キーを付け外しできるよう、KeyTokenで名前を作る。
                var baseName = KeycodeTable.KeyToken(action.Param2);
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
            else if (KeycodeCatalog.IsPowerKey(token))
                warn = "注意: 電源キーです。押すとPCがスリープ・シャットダウンすることがあります";
            else if (action.Type == ActionType.Custom && !HelperIdTaken(action.Param2))
                warn = $"HLP({action.Param2}) の動作がまだありません（「ヘルパー動作」で作ると、キーを押したときに動きます）";
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
        // 電源キーは、割り当てたキーを押すだけでPCの状態が変わるので、確かめてから割り当てる
        // （元から同じキーだったときは聞かない）。
        if (KeycodeCatalog.IsPowerKey(token) && token != ResultToken &&
            MessageBox.Show(this,
                $"「{KeycodeCatalog.ShortName(token).TrimStart('⚠')}」（{token}）は電源キーです。\n" +
                "このキーを押すと、Windows の電源の設定によって、PCがスリープ・休止・シャットダウンすることがあります。\n\n" +
                "割り当てますか？",
                "電源キーの割り当て", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }
        ResultToken = token;
        if (NonModal)
        {
            Assigned?.Invoke();
            Close();
        }
        else DialogResult = true;
    }
}
