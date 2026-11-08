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
    private static string CatLayer => Loc.T("レイヤー操作", "Layer actions");
    private const string CatModTap = "Mod-Tap (MT)";
    private const string CatLayerTap = "Layer-Tap (LT)";
    private static string CatMacro => Loc.T("マクロ / タップダンス", "Macro / Tap dance");
    private static string CatHelper => Loc.T("ヘルパー動作", "Helper actions");
    private static string CatSoftKeyboard => Loc.T("キーボード図", "Keyboard view"); // 末尾に足す（UIA 試験が分類を番号で選ぶため）
    // 2026-11-05（GUI 計画 1-2）: このキーの割り当ては変えず、別の画面で作る・開く分類。画面のキーボードより後ろ（16・17）に足す。
    private static string CatCombo => Loc.T("コンボ", "Combo");
    private static string CatKeyOverride => Loc.T("キー置換", "Key override");

    private readonly int _layerCount;
    private readonly uint _uniqId;                 // このキーボード（0 = 決まっていない。共通の動作だけ使える）
    private HelperSettingsModel? _helperModel;     // 「ヘルパー動作」を開いたときに読む

    // 修飾キーのチェックボックス（ビット番号 = KBDLAYER_MOD_*のビット。0=左Ctrl ... 7=右Win）。
    private static string[] ModLabels => new[]
    {
        Loc.T("左Ctrl", "Left Ctrl"), Loc.T("左Shift", "Left Shift"), Loc.T("左Alt", "Left Alt"), Loc.T("左Win", "Left Win"),
        Loc.T("右Ctrl", "Right Ctrl"), Loc.T("右Shift", "Right Shift"), Loc.T("右Alt", "Right Alt"), Loc.T("右Win", "Right Win"),
    };
    private readonly CheckBox[] _modChecks = new CheckBox[8];
    private bool _syncingMods;

    // 左のパネル（レイヤー・タップのキー・マクロなどの入力欄と「キーコード欄に入れる」）の入れ忘れ防止。
    // パネルの入力を変えたあと、「キーコード欄に入れる」を押さずに OK すると、変えた内容が捨てられるので、OK の前に聞く。
    private Func<string?>? _formBuild;
    private Button? _formSet;
    private bool _formTouched;
    private int _tokenVersion;       // キーコード欄の内容が変わるたびに増える
    private int _touchedVersion;     // パネルの入力を最後に変えたときの _tokenVersion

    public string ResultToken { get; private set; }

    /// <summary>
    /// true なら、<c>Show()</c> で開く非モーダルの画面として使う（メイン画面から。マクロの編集画面などを開いたまま使える）。
    /// OK で <see cref="Assigned"/> を起こして閉じ、キャンセル・× は何も起こさず閉じる。false（既定）は <c>ShowDialog()</c> 用で、OK が DialogResult を true にする。
    /// </summary>
    public bool NonModal { get; init; }

    /// <summary>非モーダルで OK されたとき（<see cref="ResultToken"/> が確定している）。</summary>
    public event Action? Assigned;

    private readonly KeymapProject? _project; // レイヤー・マクロ・タップダンスの名前を選択肢に出すため（無ければ番号だけ）

    // 開いたときの各パネルの初期値（今の割り当てを戻したもの。GUI 計画 1-1）。パネルを作るたびに使う。
    private readonly KeyAssignPrefill _prefill;

    private readonly KeySlot _slot;
    private readonly int _layer;
    private readonly KeyboardLayout? _layout;
    // メイン画面へ「編集画面を開く」を頼む（コンボ・キー置換の分類と、マクロ / タップダンスの「編集...」「新規...」）。null なら、それらを出さない。
    private readonly Action<EditorRequest>? _editorRequested;
    private bool _noAssignPanel;     // コンボ・キー置換の分類を選んでいる間（このキーの割り当ては変わらないので、OK を押せなくする）
    private bool _tokenValid = true; // キーコード欄の内容が正しいか

    /// <param name="layout">コンボ・キー置換の分類を出すとき（<paramref name="editorRequested"/> と両方あるとき）に使う。</param>
    /// <param name="editorRequested">編集画面を開く要求の受け手（メイン画面）。</param>
    public KeyAssignDialog(KeySlot slot, int layer, string currentToken, int layerCount, uint uniqId = 0, KeymapProject? project = null,
                           KeyboardLayout? layout = null, Action<EditorRequest>? editorRequested = null)
    {
        _slot = slot;
        _layer = layer;
        _layout = layout;
        _editorRequested = project != null ? editorRequested : null;
        InitializeComponent();
        Title = Loc.T("キーの割り当て", "Assign key");
        TxtTokenLabel.Text = Loc.T("キーコード:", "Key code:");
        BtnCapture.Content = Loc.T("押して入力", "Press to enter");
        BtnCapture.ToolTip = Loc.T("次に押したキーを入れます(Esc で取消)。Win キーなどは、一覧かキーボード図から選びます",
            "Enters the next key you press (Esc cancels). Pick keys such as Win from the list or the keyboard view");
        BtnCancel.Content = Loc.T("キャンセル", "Cancel");
        TxtModHint.Text = Loc.T("修飾キー:", "Modifiers:");
        // 説明はツールチップへ（2026-11-08、UI の見直し）。
        TxtModHint.ToolTip = ModPanel.ToolTip = Loc.T("チェックすると、選んだキーを Ctrl+C のようなショートカットにします。LM と OSM の修飾キーも、ここで選びます",
            "When checked, the selected key becomes a shortcut such as Ctrl+C. Also choose the modifiers for LM and OSM here");
        // 左下に「?」（今の分類の説明をマニュアルで開く）。F1 も同じ。
        var help = HelpButton.Create(CurrentHelpTopic);
        help.HorizontalAlignment = HorizontalAlignment.Left;
        help.Margin = new Thickness(0, 4, 0, 0);
        Grid.SetRow(help, 1);
        ((Grid)TxtTokenLabel.Parent).Children.Add(help);
        HelpButton.AttachF1(this, CurrentHelpTopic);
        _layerCount = layerCount;
        _uniqId = uniqId;
        _project = project;
        _prefill = KeyAssignPrefill.From(currentToken, slot.DefaultKeycode, KbdLayerClient.MaxLayers);
        ResultToken = currentToken;
        // 非モーダルのときは、IsCancel だけでは閉じない（ShowDialog 用の動き）ので、自分で閉じる。
        BtnCancel.Click += (_, _) => { if (NonModal) Close(); };
        // トークン欄は、マクロの編集画面の「他の画面の入力欄に挿入」の入れ先にもなる。
        MacroInsert.Register(TxtToken, token => TxtToken.Text = token, acceptsTapDance: true);
        // この画面が前面になったら、欄を一度もクリックしていなくても、トークン欄を入れ先にする（マクロの画面の「入れる」の入れ忘れ防止が働くように）。
        Activated += (_, _) => MacroInsert.Select(TxtToken);
        string? layerName = project == null ? null : ProjectService.LayerName(project, layer);
        TxtHeader.Text = Loc.T($"{slot.Description} ／ レイヤー{layer}", $"{slot.Description} / Layer {layer}") + (layerName != null ? $"({layerName})" : "");

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
        if (_editorRequested != null && _layout != null)
        {
            LstCategories.Items.Add(CatCombo);
            LstCategories.Items.Add(CatKeyOverride);
            // 前面に戻ったときも作り直す（ふだんはメイン画面が RefreshRelated を呼ぶ）。
            Activated += (_, _) => RefreshRelated();
        }
        // 今の割り当てが MT / LT / レイヤー操作 / マクロ / ヘルパーなら、そのパネルを開いておく（中身は _prefill で今の値になる）。
        string? first = _prefill.Panel switch
        {
            KeyAssignPanel.Layer => CatLayer,
            KeyAssignPanel.ModTap => CatModTap,
            KeyAssignPanel.LayerTap => CatLayerTap,
            KeyAssignPanel.Macro => CatMacro,
            KeyAssignPanel.Helper => CatHelper,
            _ => null,
        };
        LstCategories.SelectedIndex = first == null ? 0 : Math.Max(0, LstCategories.Items.IndexOf(first));

        Loaded += (_, _) => { TxtToken.Focus(); TxtToken.SelectAll(); };
    }

    private void LstCategories_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LstCategories.SelectedItem is not string name) return;
        ShowCategory(name);
    }

    private void ShowCategory(string name)
    {
        var cat = KeycodeCatalog.Categories.FirstOrDefault(c => c.Name == name);
        _noAssignPanel = cat == null && (name == CatCombo || name == CatKeyOverride);
        CategoryContent.Content = cat != null ? CategoryKeys(cat) : name switch
        {
            _ when name == CatLayer => LayerPanel(),
            CatModTap => ModTapPanel(),
            CatLayerTap => LayerTapPanel(),
            _ when name == CatHelper => HelperPanel(),
            _ when name == CatSoftKeyboard => SoftKeyboard(),
            _ when name == CatCombo => ComboPanel(),
            _ when name == CatKeyOverride => KeyOverridePanel(),
            _ => MacroPanel(),
        };
        UpdateOk();
    }

    /// <summary>
    /// コンボ・キー置換の分類を出していれば、今のプロジェクトの定義で作り直す。編集画面で OK / 適用を押したときに、メイン画面が呼ぶ
    /// （編集画面を閉じたあとはメイン画面が前面になるので、この画面の Activated だけでは間に合わない。2026-11-05 の実機確認 C3・K2）。
    /// </summary>
    public void RefreshRelated()
    {
        if (LstCategories.SelectedItem is string n && (n == CatCombo || n == CatKeyOverride)) ShowCategory(n);
    }

    private void UpdateOk() => BtnOk.IsEnabled = _tokenValid && !_noAssignPanel;

    // 今の分類の、マニュアルの節（「?」と F1）。
    private string CurrentHelpTopic()
    {
        string? name = LstCategories.SelectedItem as string;
        if (name == null || KeycodeCatalog.Categories.Any(c => c.Name == name)) return HelpTopics.KeyAssign;
        if (name == CatLayer) return HelpTopics.KeyAssignLayer;
        if (name == CatModTap || name == CatLayerTap) return HelpTopics.KeyAssignModTap;
        if (name == CatMacro || name == CatHelper) return HelpTopics.KeyAssignMacroHelper;
        if (name == CatSoftKeyboard) return HelpTopics.KeyAssignKeyboardView;
        if (name == CatCombo || name == CatKeyOverride) return HelpTopics.KeyAssignComboOverride;
        return HelpTopics.KeyAssign;
    }

    // ------------------------------------------------------------------
    // コンボ・キー置換（このキーの割り当ては変えない。別の画面で作る・開く。GUI 計画 1-2）
    // ------------------------------------------------------------------

    // 分類の先頭に出す注意（OK が押せない理由）。何をする分類かの説明は「?」のマニュアル（2026-11-08、UI の見直し）。
    private static TextBlock NoAssignNotice(string what) => new()
    {
        Text = Loc.T($"{what}は別の画面で作ります。このキーの割り当ては変わりません(OK は押せません)。",
                     $"{what} are made in a separate window. This key's assignment does not change (OK is unavailable)."),
        TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8),
    };

    // sub: 補足の色（薄い字）。false なら、ふつうの字（一覧の見出し）。
    private static TextBlock Note(string text, bool sub = true)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        if (sub) t.Foreground = AppTheme.Palette.SubText;
        return t;
    }

    // 関わる定義の一覧と「開く...」。1件だけなら、最初から選んでおく。ダブルクリックでも開く。
    private (ListBox List, Button Open) RelatedList(IReadOnlyList<(int Id, string Text)> rows, EditorTarget target, string listId, string openId)
    {
        var list = Id(new ListBox { Height = 110, Margin = new Thickness(0, 4, 0, 0) }, listId);
        foreach (var (_, text) in rows) list.Items.Add(text);
        var open = Id(new Button { Content = Loc.T("開く...", "Open..."), Width = 90, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) }, openId);
        void Open()
        {
            int i = list.SelectedIndex;
            if (i >= 0 && i < rows.Count) _editorRequested?.Invoke(new EditorRequest(target, rows[i].Id, _slot.Index, null));
        }
        open.Click += (_, _) => Open();
        list.MouseDoubleClick += (_, _) => Open();
        list.SelectionChanged += (_, _) => open.IsEnabled = list.SelectedIndex >= 0;
        if (rows.Count == 1) list.SelectedIndex = 0;
        open.IsEnabled = list.SelectedIndex >= 0;
        return (list, open);
    }

    private static string WithDisabled(string text, bool disabled) => disabled ? text + Loc.T("(無効)", " (disabled)") : text;

    private UIElement ComboPanel()
    {
        var panel = new StackPanel { Margin = new Thickness(4) };
        panel.Children.Add(NoAssignNotice(Loc.T("コンボ", "Combos")));
        var related = KeyMarks.Find(_project!, _layer, _slot);
        var rows = related.Combos.Select(c => (c.Id, WithDisabled(KeyMarks.ComboText(_project!, _layout!, _slot, c), c.Disabled))).ToList();
        if (rows.Count > 0)
        {
            panel.Children.Add(Note(Loc.T("このキーを使っているコンボ:", "Combos that use this key:"), sub: false));
            var (list, open) = RelatedList(rows, EditorTarget.Combo, "RelatedComboList", "OpenComboButton");
            panel.Children.Add(list);
            panel.Children.Add(open);
        }
        var create = Id(new Button { Content = Loc.T("このキーでコンボを作る...", "Create a combo with this key..."), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 10, 0, 0),
                                     ToolTip = Loc.T("空いている番号に、このキーをキー1にしたコンボを作ります", "Creates a combo in a free number with this key as key 1") }, "CreateComboFromKeyButton");
        create.Click += (_, _) => _editorRequested?.Invoke(new EditorRequest(EditorTarget.Combo, null, _slot.Index, null));
        panel.Children.Add(create);
        // 1つのキーは、有効なコンボ1つにしか使えない（コンボの画面の検証と同じ）。
        if (related.Combos.FirstOrDefault(c => !c.Disabled) is { } used)
        {
            create.IsEnabled = false;
            panel.Children.Add(Note(Loc.T($"このキーは、すでにコンボ{used.Id}に使われています。1つのキーは1つのコンボにしか使えないので、新しく作るには、先にコンボ{used.Id}を変えるか無効にしてください。",
                                          $"This key is already used in combo {used.Id}. A key can be used in only one combo, so change or disable combo {used.Id} first to create a new one.")));
        }
        return panel;
    }

    private UIElement KeyOverridePanel()
    {
        var panel = new StackPanel { Margin = new Thickness(4) };
        panel.Children.Add(NoAssignNotice(Loc.T("キー置換", "Key overrides")));
        var related = KeyMarks.Find(_project!, _layer, _slot);
        string? key = KeyMarks.OverrideKeyFor(related.Token);
        var rows = related.KeyOverrides.Select(k => (k.Id, WithDisabled(KeyMarks.KeyOverrideText(_project!, k), k.Disabled))).ToList();
        if (rows.Count > 0)
        {
            panel.Children.Add(Note(Loc.T("このキーを条件にしているキー置換:", "Key overrides with this key as the condition:"), sub: false));
            var (list, open) = RelatedList(rows, EditorTarget.KeyOverride, "RelatedKeyOverrideList", "OpenKeyOverrideButton");
            panel.Children.Add(list);
            panel.Children.Add(open);
        }
        var create = Id(new Button { Content = Loc.T("このキーでキー置換を作る...", "Create a key override with this key..."), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 10, 0, 0),
                                     ToolTip = key == null ? null : Loc.T($"空いている番号に、条件のキーを {key} にしたキー置換を作ります", $"Creates a key override in a free number with {key} as the key") }, "CreateKeyOverrideFromKeyButton");
        create.Click += (_, _) => _editorRequested?.Invoke(new EditorRequest(EditorTarget.KeyOverride, null, _slot.Index, key));
        panel.Children.Add(create);
        if (key == null)
        {
            create.IsEnabled = false;
            panel.Children.Add(Note(Loc.T($"このキーは、このレイヤーではキーを出さない割り当て({related.Token})なので、キー置換の条件にできません。",
                                          $"On this layer this key is assigned something that produces no key ({related.Token}), so it can't be a key override condition.")));
        }
        return panel;
    }

    // 分類のキーのボタン。NLS は、キーボードの配列に関係なく選べることを、一言添える
    // （US配列のキーボードにこれらのキーは無いが、出力としては割り当てられる。たとえば、US配列のキーに「変換」を割り当てられる）。
    private UIElement CategoryKeys(KeycodeCatalog.Category cat)
    {
        // NLS（日本語キーボードのキー）を US 配列のキーにも割り当てられることは、8 章に書く（2026-11-08、UI の見直し）。
        return KeyButtons(cat.Keycodes);
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
            () => { BtnCapture.Content = Loc.T("押して入力", "Press to enter"); TxtToken.Focus(); });
        if (_capture.IsActive) { _capture.Cancel(); return; }
        BtnCapture.Content = Loc.T("キーを押してください…", "Press a key...");
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
        func.SelectedItem = _prefill.LayerFunc;
        var layer = NamedCombo(NamedChoice.Layers(_project), "LayerCombo");
        SelectNumber(layer, _prefill.Layer);
        // 種類の一覧の説明の段落は出さず、選んでいる種類の意味だけを横に出す（2026-11-08、UI の見直し。詳しくは「?」の 8 章）。
        var funcRow = WithDescription(func, f => f switch
        {
            "MO" => Loc.T("押している間だけ、有効にする", "Enabled only while held"),
            "TG" => Loc.T("押すたびに、有効と無効を切り替える", "Toggles on and off each press"),
            "DF" => Loc.T("基本のレイヤーにする", "Becomes the base layer"),
            "OSL" => Loc.T("次の 1 キーだけ、有効にする", "Enabled for the next key only"),
            "TO" => Loc.T("このレイヤーだけを有効にする", "Only this layer is enabled"),
            "TT" => Loc.T("押している間は有効。続けてタップすると固定する", "Enabled while held; tap repeatedly to lock"),
            "LM" => Loc.T("押している間は有効にし、修飾キーも押す", "Enabled while held, with the modifiers"),
            "OSM" => Loc.T("次の 1 キーだけ、修飾キーを掛ける(レイヤーは使わない)", "Modifiers for the next key only (no layer)"),
            _ => "",
        });
        return FormWithLayer(funcRow, layer, func);
    }

    // レイヤー操作のフォーム（種類の行は、説明つきの入れ物）。
    private UIElement FormWithLayer(Control funcRow, ComboBox layer, ComboBox func)
    {
        return Form(null,
            (Loc.T("種類", "Type"), funcRow), (Loc.T("レイヤー", "Layer"), layer),
            () =>
            {
                string f = (string)func.SelectedItem;
                if (f != "LM" && f != "OSM") return $"{f}({Num(layer)})";
                byte mask = CheckedMask();
                if (mask == 0)
                {
                    TxtValidation.Foreground = AppTheme.Palette.ErrorText;
                    TxtValidation.Text = Loc.T($"{f} では、下の修飾キーを1つ以上チェックしてください", $"For {f}, check at least one modifier key below");
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
        mod.SelectedItem = _prefill.ModName;
        // タップのキーは、今の割り当て（MT のタップ側、または普通のキー）か、キーの刻印。どちらも無ければ KC_A。
        var tap = Id(new TextBox { Text = _prefill.ModTapKey, Width = 160, FontFamily = new FontFamily("Consolas") }, "TapKeyBox");
        return Form(Loc.T("短く押すとタップのキー、長押しで修飾キーになります", "Tap for the tap key; hold for the modifier"),
            (Loc.T("修飾キー", "Modifier"), mod), (Loc.T("タップのキー", "Tap key"), tap),
            () => $"MT({mod.SelectedItem},{tap.Text.Trim()})");
    }

    private UIElement LayerTapPanel()
    {
        var layer = NamedCombo(NamedChoice.Layers(_project), "LayerCombo");
        SelectNumber(layer, _prefill.Layer);
        // タップのキーは、今の割り当て（LT のタップ側、または普通のキー）か、キーの刻印。どちらも無ければ KC_SPACE。
        var tap = Id(new TextBox { Text = _prefill.LayerTapKey, Width = 160, FontFamily = new FontFamily("Consolas") }, "TapKeyBox");
        return Form(Loc.T("短く押すとタップのキー、長押しでレイヤーを有効にします", "Tap for the tap key; hold to enable the layer"),
            (Loc.T("レイヤー", "Layer"), layer), (Loc.T("タップのキー", "Tap key"), tap),
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
        var pick = Id(new Button { Content = Loc.T("マクロ...", "Macro..."), Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 1, 8, 1),
                                   ToolTip = Loc.T("一覧(番号・名前・中身)から選びます。", "Choose from a list (number, name, contents).") }, "MacroPickButton");
        kind.SelectionChanged += (_, _) => pick.Content = (string)kind.SelectedItem == "MACRO" ? Loc.T("マクロ...", "Macro...") : Loc.T("タップダンス...", "Tap dance...");
        pick.Click += (_, _) =>
        {
            var dlg = new MacroPickerWindow((string)kind.SelectedItem) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Result is not { } m) return;
            kind.SelectedItem = m.Kind;                   // 種類を変えると番号の一覧が作り直される
            id.SelectedIndex = Math.Min(m.Id, id.Items.Count - 1);
        };
        // 今の割り当てが MACRO(n) / TAPDANCE(n) なら、その種類と番号にしておく（種類を変えると番号の一覧が作り直されるので、種類が先）。
        kind.SelectedItem = _prefill.MacroKind;
        SelectNumber(id, _prefill.MacroId);
        var idButtons = new StackPanel { Orientation = Orientation.Horizontal, Children = { id, pick } };
        if (_editorRequested != null)
        {
            // 「編集...」: 選んでいる番号を、マクロ / タップダンスの画面で開く。「新規...」: 空き番号を選んで開く
            // （タップダンスは、このキーの今のキーを1回タップに入れる）。どちらも、この画面のキーコード欄は変えない
            // （編集画面の「…入力欄に入れる」で入れる）。
            EditorTarget Target() => (string)kind.SelectedItem == "MACRO" ? EditorTarget.Macro : EditorTarget.TapDance;
            var edit = Id(new Button { Content = Loc.T("編集...", "Edit..."), Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 1, 8, 1),
                                       ToolTip = Loc.T("選んでいる番号を、編集画面で開きます", "Opens the selected number in its editor") }, "EditMacroButton");
            var create = Id(new Button { Content = Loc.T("新規...", "New..."), Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 1, 8, 1),
                                         ToolTip = Loc.T("空いている番号を、編集画面で開きます", "Opens a free number in its editor") }, "NewMacroButton");
            // e.Handled: 編集画面を開くだけなので、パネルの「入れていない変更」（Form の入れ忘れ検知）には数えない。
            // 「編集...」の前に番号を選んだのは、中身を直すためとみなし、入れ忘れの確認から外す（2026-11-05 の実機確認 M3）。
            // 割り当てたいときは、これまでどおり「キーコード欄に入れる」を押す。
            edit.Click += (_, e) =>
            {
                e.Handled = true;
                _formTouched = false;
                UpdateFormHint();
                _editorRequested(new EditorRequest(Target(), Num(id), _slot.Index, null));
            };
            create.Click += (_, e) =>
            {
                e.Handled = true;
                _editorRequested(new EditorRequest(Target(), null, _slot.Index,
                    Target() == EditorTarget.TapDance ? KeyMarks.KeyLikeToken(KeyMarks.EffectiveToken(_project!, _layer, _slot)) : null));
            };
            idButtons.Children.Add(edit);
            idButtons.Children.Add(create);
        }
        var idRow = new ContentControl { Content = idButtons };
        var kindRow = WithDescription(kind, k => k == "MACRO" ? Loc.T("並べたキーを順に打つ", "Types the listed keys in order")
                                                              : Loc.T("タップの回数と長押しで、動作を変える", "Acts by tap count or hold"));
        return Form(null,
            (Loc.T("種類", "Type"), kindRow), (Loc.T("番号", "Number"), idRow),
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
        var list = Id(new ListBox { Height = 230, FontFamily = new FontFamily("Consolas"),
                                    ToolTip = Loc.T("HLP(Id) を割り当てたキーで、トレイが動作を実行します(トレイが動いているときだけ)", "The tray runs the action from keys assigned HLP(Id) (only while the tray is running)") }, "HelperActionList");
        var items = new List<(HelperScope Scope, HelperAction Action)>();
        void Fill(int? selectId)
        {
            list.Items.Clear();
            items.Clear();
            if (Helper.DeviceWritable)
                foreach (var a in Helper.Device.Actions) { list.Items.Add(Loc.T("[このキーボード] ", "[This keyboard] ") + HelperSummary.Action(a)); items.Add((HelperScope.Device, a)); }
            foreach (var a in Helper.Global.Actions) { list.Items.Add(Loc.T("[共通] ", "[Shared] ") + HelperSummary.Action(a)); items.Add((HelperScope.Global, a)); }
            // 今の割り当てが HLP(n) なら、その動作を選んでおく（同じ Id がこのキーボード用と共通にあれば、このキーボード用。トレイも先に引く）。
            if (selectId is { } sel) list.SelectedIndex = items.FindIndex(x => x.Action.Id == sel);
        }
        Fill(_prefill.HelperId);
        panel.Children.Add(list);

        var assign = Id(new Button { Content = Loc.T("選んだ動作を割り当てる", "Assign selected action"), Width = 190, Margin = new Thickness(0, 0, 8, 0) }, "AssignHelperButton");
        var edit = Id(new Button { Content = Loc.T("編集...", "Edit..."), Width = 90, Margin = new Thickness(0, 0, 8, 0) }, "EditHelperActionButton");
        var create = Id(new Button { Content = Loc.T("新しい動作...", "New action..."), Width = 120 }, "NewHelperActionButton");
        void Assign()
        {
            if (list.SelectedIndex >= 0 && list.SelectedIndex < items.Count) TxtToken.Text = $"HLP({items[list.SelectedIndex].Action.Id})";
        }
        // 「割り当てる」と「編集...」は、一覧で動作を選んでいるときだけ押せる。
        void UpdateEdit() => assign.IsEnabled = edit.IsEnabled = list.SelectedIndex >= 0 && list.SelectedIndex < items.Count;
        assign.Click += (_, _) => Assign();
        list.MouseDoubleClick += (_, _) => { Assign(); Confirm(); };
        list.SelectionChanged += (_, _) => UpdateEdit();
        edit.Click += (_, _) =>
        {
            int i = list.SelectedIndex;
            if (i < 0 || i >= items.Count) return;
            if (EditHelperAction(items[i].Scope, items[i].Action) is { } id) { Fill(id); UpdateEdit(); }
        };
        create.Click += (_, _) =>
        {
            if (NewHelperAction()) { Fill(null); list.SelectedIndex = -1; UpdateEdit(); }
        };
        UpdateEdit();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(assign);
        buttons.Children.Add(edit);
        buttons.Children.Add(create);
        panel.Children.Add(buttons);
        if (Helper.DeviceLoadError != null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Loc.T("このキーボードの helper.json を読めないため、このキーボード用の動作は使えません。新しい動作は共通に作ります。",
                             "Couldn't read helper.json for this keyboard, so actions for this keyboard are unavailable. New actions will be created as shared."),
                Foreground = AppTheme.Palette.ErrorText,
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
            MessageBox.Show(this, Loc.T("使える Id がありません。", "No Id is available."), Loc.T("動作の追加", "Add Action"));
            return false;
        }
        var dlg = new HelperActionEditorWindow(Loc.T("動作の追加", "Add Action"), HelperEditMode.Action, id, null, null, null,
            new HelperActionBody { Type = HelperActionBody.Launch }, HelperIdTaken) { Owner = this };
        if (dlg.ShowDialog() != true) return false;

        var scope = Helper.DeviceWritable ? HelperScope.Device : HelperScope.Global;
        var added = new HelperAction { Id = dlg.ResultId, Name = dlg.ResultName, Action = dlg.ResultBody };
        var actions = Helper.Config(scope).Actions;
        actions.Add(added);
        if (!Helper.TrySave(out var error))
        {
            actions.Remove(added);
            MessageBox.Show(this, Loc.T($"保存できませんでした: {error}", $"Couldn't save: {error}"), Loc.T("動作の追加", "Add Action"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        TxtToken.Text = $"HLP({added.Id})";
        return true;
    }

    /// <summary>
    /// 選んだ動作を編集して、すぐ helper.json に保存する（新しい動作と同じく、OK を待たない）。保存したら、その動作の Id。
    /// 割り当て（キーコード欄）は変えない。Id を変えたときは、このキーの HLP(n) も自分で選び直す。
    /// </summary>
    private int? EditHelperAction(HelperScope scope, HelperAction action)
    {
        var actions = Helper.Config(scope).Actions;
        int index = actions.IndexOf(action);
        if (index < 0) return null;
        var dlg = new HelperActionEditorWindow(Loc.T("動作の編集", "Edit Action"), HelperEditMode.Action, action.Id, action.Name, null, null,
            HelperSummary.CloneAction(action).Action, otherId => actions.Where((a, i) => i != index).Any(a => a.Id == otherId)) { Owner = this };
        if (dlg.ShowDialog() != true) return null;

        var before = HelperSummary.CloneAction(action);
        action.Id = dlg.ResultId;
        action.Name = dlg.ResultName;
        action.Action = dlg.ResultBody;
        if (!Helper.TrySave(out var error))
        {
            action.Id = before.Id;
            action.Name = before.Name;
            action.Action = before.Action;
            MessageBox.Show(this, Loc.T($"保存できませんでした: {error}", $"Couldn't save: {error}"), Loc.T("動作の編集", "Edit Action"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        return action.Id;
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

    // 選択肢の横に、選んでいるものの短い説明を出す入れ物（説明の段落の代わり）。
    private static ContentControl WithDescription(ComboBox combo, Func<string, string> describe)
    {
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Foreground = AppTheme.Palette.SubText };
        void Update() => text.Text = combo.SelectedItem is string v ? describe(v) : "";
        combo.SelectionChanged += (_, _) => Update();
        Update();
        return new ContentControl { Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { combo, text } } };
    }


    /// <summary>番号の欄で、その番号を選ぶ（一覧に無ければ、今の選択のまま）。</summary>
    private static void SelectNumber(ComboBox combo, int number)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is NamedChoice c && c.Number == number) { combo.SelectedIndex = i; return; }
        }
    }

    /// <param name="tip">パネルの意味（1 文。ラベルのツールチップにする）。説明の段落は出さない（2026-11-08、UI の見直し。詳しくは「?」の 8 章）。</param>
    private UIElement Form(string? tip, (string Label, Control Input) a, (string Label, Control Input) b, Func<string?> build)
    {
        var panel = new StackPanel { Margin = new Thickness(4) };
        foreach (var (label, input) in new[] { a, b })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock { Text = label, Width = 90, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip });
            row.Children.Add(input);
            panel.Children.Add(row);
        }
        var set = Id(new Button { Content = Loc.T("キーコード欄に入れる", "Insert into key code"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(90, 8, 0, 0) }, "InsertButton");
        _formBuild = build;
        _formSet = set;
        _formTouched = false;
        // 利用者が入力を変えたら、「入れていない変更がある」と覚える（作ったときの初期値の設定では、覚えない）。
        void Touched() { _formTouched = true; _touchedVersion = _tokenVersion; UpdateFormHint(); }
        // 入力欄は、コンボボックスやテキストボックスのこともあれば、それを並べた入れ物（番号の横に「マクロ...」がある行など）のこともある。
        // 中のコントロールの変更は、バブルするイベントで受ける。
        foreach (var (_, input) in new[] { a, b })
        {
            input.AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent, new RoutedEventHandler((_, _) => Touched()));
            input.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new RoutedEventHandler((_, _) => Touched()));
            input.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => Touched()));
        }
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

    // パネルで選んだ内容を、まだキーコード欄に入れていなければ、そのトークン（入力を変えたあとに、キーコード欄が変わっていないときだけ）。
    private string? PendingFormToken()
    {
        if (!_formTouched || _touchedVersion != _tokenVersion || _formBuild == null) return null;
        string? token = _formBuild();
        if (token == null || token == TxtToken.Text.Trim()) return null;
        return KeycodeTable.TryResolve(token, out _) ? token : null;
    }

    // 入れていない内容があるあいだ、「キーコード欄に入れる」ボタンを目立たせる（太字と色）。
    private void UpdateFormHint()
    {
        if (_formSet == null) return;
        bool pending = PendingFormToken() != null;
        _formSet.FontWeight = pending ? FontWeights.Bold : FontWeights.Normal;
        if (pending) _formSet.Background = AppTheme.Palette.KeyChanged;
        else _formSet.ClearValue(Control.BackgroundProperty);
    }

    private void TxtToken_TextChanged(object sender, TextChangedEventArgs e)
    {
        _tokenVersion++;
        UpdateFormHint();
        var token = TxtToken.Text.Trim();
        if (KeycodeTable.TryResolve(token, out var action, out var error))
        {
            SyncModChecks(action);
            string? warn = null;
            if (ActionType.ReferencesLayer(action.Type) && action.Param1 >= _layerCount)
                warn = Loc.T($"レイヤー{action.Param1}はまだありません(レイヤーを追加してから適用してください)", $"Layer {action.Param1} doesn't exist yet (add the layer before applying)");
            else if (KeycodeCatalog.IsPowerKey(token))
                warn = Loc.T("注意: 電源キーです。押すとPCがスリープ・シャットダウンすることがあります", "Note: this is a power key. Pressing it may put the PC to sleep or shut it down");
            else if (action.Type == ActionType.Custom && !HelperIdTaken(action.Param2))
                warn = Loc.T($"HLP({action.Param2}) の動作がまだありません(「ヘルパー動作」で作ると、キーを押したときに動きます)", $"No action for HLP({action.Param2}) yet (create one under \"Helper actions\" and it will run when the key is pressed)");
            TxtValidation.Foreground = warn == null ? AppTheme.Palette.OkText : AppTheme.Palette.WarnText;
            TxtValidation.Text = warn ?? "OK: " + KeycodeCatalog.ShortName(token);
            _tokenValid = true;
        }
        else
        {
            TxtValidation.Foreground = AppTheme.Palette.ErrorText;
            TxtValidation.Text = error;
            _tokenValid = false;
        }
        UpdateOk();
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e) => Confirm();

    private void Confirm()
    {
        if (_noAssignPanel) return; // コンボ・キー置換の分類では、割り当てを変えない

        // パネルで選んだ内容を、「キーコード欄に入れる」で入れていなければ、入れるかを聞く。
        if (PendingFormToken() is { } pending)
        {
            var answer = MessageBox.Show(this,
                Loc.T($"左のパネルで選んだ内容({pending})を、まだキーコード欄に入れていません。入れてから OK しますか?\n\n「はい」: 入れて OK\n「いいえ」: 今のキーコード({TxtToken.Text.Trim()})のまま OK\n「キャンセル」: 戻る",
                      $"What you chose in the left panel ({pending}) has not been inserted into the key code field yet. Insert it and press OK?\n\nYes: insert and OK\nNo: OK with the current key code ({TxtToken.Text.Trim()})\nCancel: go back"),
                Kasane.Model.AppInfo.DisplayName, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes) TxtToken.Text = pending;
        }
        var token = TxtToken.Text.Trim();
        if (!KeycodeTable.TryResolve(token, out _)) return;
        // 電源キーは、割り当てたキーを押すだけでPCの状態が変わるので、確かめてから割り当てる
        // （元から同じキーだったときは聞かない）。
        if (KeycodeCatalog.IsPowerKey(token) && token != ResultToken &&
            MessageBox.Show(this,
                Loc.T($"「{KeycodeCatalog.ShortName(token).TrimStart('⚠')}」({token})は電源キーです。\n" +
                      "このキーを押すと、Windows の電源の設定によって、PCがスリープ・休止・シャットダウンすることがあります。\n\n" +
                      "割り当てますか？",
                      $"\"{KeycodeCatalog.ShortName(token).TrimStart('⚠')}\" ({token}) is a power key.\n" +
                      "Pressing it may put the PC to sleep, hibernate, or shut down, depending on your Windows power settings.\n\n" +
                      "Assign it?"),
                Loc.T("電源キーの割り当て", "Assign Power Key"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
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
