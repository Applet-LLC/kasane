using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kasane.Interop;
using Kasane.Keycodes;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>
/// 設定ダイアログ（タブ形式）。
///  - 機能・タイミング・NICOLA: このキーボードのプロジェクトの設定。OKでプロジェクトに入り、「キーボードに適用」で反映する。
///  - 緊急解除・共有範囲: ドライバ全体の設定、またはドライバが設定を保存する場所なので、タブの中のボタンで
///    すぐに反映する（OK・キャンセルとは関係しない）。
/// コントロールには UI Automation 用の Name を付ける（TabFeatures 等のタブ、各入力欄）。
/// </summary>
public sealed class SettingsDialog : Window
{
    private static readonly string[] ModeLabels =
    {
        "別のキーを押したらすぐ長押し扱い（既定）",
        "別のキーを押して離したら長押し扱い（Permissive Hold）",
        "時間内に離せば短押し扱い（時間切れで長押し）",
    };

    private static readonly string[] NicolaModeLabels =
    {
        "GUIでは管理しない（ドライバの設定をそのまま使う）",
        "この設定を使う（適用で反映）",
        "使わない（適用時にドライバの設定を消す）",
    };

    private readonly KeymapProject _project;
    private readonly KbdLayerClient? _client;
    private readonly uint _uniqId;      // 0 = オフライン
    private readonly bool _canWrite;
    private readonly TabControl _tabs = new() { Name = "SettingsTabs" };
    private readonly TextBlock _error = new() { Name = "SettingsError", Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    // 機能
    private readonly CheckBox[] _featureChecks = new CheckBox[4];
    private uint _initialFeatures;
    // タイミング
    private TextBox _tap = null!, _combo = null!, _tapDance = null!, _quick = null!, _tt = null!;
    private ComboBox _mode = null!;
    private CheckBox _quickEnabled = null!;
    // NICOLA
    private NicolaDef? _nicola;
    private ComboBox _nicolaMode = null!;
    private TextBlock _nicolaSummary = null!;

    public SettingsDialog(KeymapProject project, KbdLayerClient? client, uint onlineUniqId, bool canWrite)
    {
        _project = project;
        _client = client;
        _uniqId = client != null ? onlineUniqId : 0;
        _canWrite = canWrite;
        _nicola = project.Nicola?.Clone();

        Title = "設定";
        Width = 760;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        AddTab("TabFeatures", "機能", BuildFeaturesTab());
        AddTab("TabTiming", "タイミング", BuildTimingTab());
        AddTab("TabNicola", "NICOLA", BuildNicolaTab());
        AddTab("TabEmergency", "緊急解除", BuildEmergencyTab());
        AddTab("TabScope", "共有範囲", BuildScopeTab());

        var ok = new Button { Name = "OkButton", Content = "OK", IsDefault = true, Width = 90 };
        ok.Click += (_, _) => { if (Commit()) DialogResult = true; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Name = "CancelButton", Content = "キャンセル", IsCancel = true, Width = 90 });

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_error, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(_error);
        root.Children.Add(_tabs);
        Content = root;
    }

    private bool IsOnline => _uniqId != 0;

    /// <summary>タブを足す。選んでいないタブの中身はUI Automationのツリーに出ないので、試験ではタブを先に選ぶ。</summary>
    private void AddTab(string name, string header, UIElement content)
    {
        var tab = new TabItem { Name = name, Header = header, Content = Scroll(content) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(tab, name);
        _tabs.Items.Add(tab);
    }

    // ------------------------------------------------------------------
    // 共通の部品
    // ------------------------------------------------------------------

    private static ScrollViewer Scroll(UIElement content) =>
        new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10) };

    private static TextBlock Help(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 10) };

    private static StackPanel Row(Panel parent, string label, UIElement input, string? help = null, double labelWidth = 120)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(new TextBlock { Text = label, Width = labelWidth, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(input);
        if (help != null)
            row.Children.Add(new TextBlock { Text = help, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 });
        parent.Children.Add(row);
        return row;
    }

    private static TextBlock Status(string name) =>
        new() { Name = name, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    private static void ShowStatus(TextBlock block, string text, bool isError)
    {
        block.Foreground = isError ? Brushes.Firebrick : Brushes.DarkGreen;
        block.Text = text;
    }

    // ------------------------------------------------------------------
    // 機能
    // ------------------------------------------------------------------

    private UIElement BuildFeaturesTab()
    {
        var panel = new StackPanel();
        panel.Children.Add(Help(
            "使わない機能はOFFにできます。OFFにした機能のキーは何もせず、コンボの構成キーは通常のキーとして動きます。" +
            "OFFにしても設定（マクロ等の定義）は消えません。レイヤーとキーの置き換え、MT/LT、修飾付きキーは常に使えます。\n" +
            "ここでの変更は「キーボードに適用」で反映します。"));

        string current = "";
        uint initial = KbdLayerClient.FeatureAll;
        if (IsOnline)
        {
            try
            {
                uint f = _client!.GetFeatures(_uniqId);
                initial = f;
                current = $"キーボードの現在の値: マクロ {OnOff(f, KbdLayerClient.FeatureMacro)} / コンボ {OnOff(f, KbdLayerClient.FeatureCombo)} / " +
                          $"タップダンス {OnOff(f, KbdLayerClient.FeatureTapDance)} / キー置換 {OnOff(f, KbdLayerClient.FeatureKeyOverride)}";
            }
            catch (KbdLayerException) { current = "（このドライバは機能のON/OFFに対応していません）"; }
        }
        _initialFeatures = _project.EnabledFeatures ?? initial;

        var defs = new (string Name, string Label, uint Bit)[]
        {
            ("FeatureMacro", "マクロ（MACRO(n)）", KbdLayerClient.FeatureMacro),
            ("FeatureCombo", "コンボ", KbdLayerClient.FeatureCombo),
            ("FeatureTapDance", "タップダンス（TAPDANCE(n)）", KbdLayerClient.FeatureTapDance),
            ("FeatureKeyOverride", "キー置換", KbdLayerClient.FeatureKeyOverride),
        };
        for (int i = 0; i < defs.Length; i++)
        {
            _featureChecks[i] = new CheckBox
            {
                Name = defs[i].Name,
                Content = defs[i].Label,
                Tag = defs[i].Bit,
                IsChecked = (_initialFeatures & defs[i].Bit) != 0,
                Margin = new Thickness(0, 3, 0, 3),
            };
            panel.Children.Add(_featureChecks[i]);
        }
        if (current.Length > 0) panel.Children.Add(Help(current));
        return panel;
    }

    private static string OnOff(uint features, uint bit) => (features & bit) != 0 ? "ON" : "OFF";

    private uint SelectedFeatures() =>
        _featureChecks.Where(c => c.IsChecked == true).Aggregate(0u, (acc, c) => acc | (uint)c.Tag);

    // ------------------------------------------------------------------
    // タイミング（旧タイミングダイアログ。コントロール名はそのまま）
    // ------------------------------------------------------------------

    private UIElement BuildTimingTab()
    {
        var panel = new StackPanel();
        string currentText = "";
        if (IsOnline)
        {
            try
            {
                var current = _client!.GetConfig(_uniqId);
                currentText = $"\nキーボードの現在の値: TapTerm {current.TapTermMs} / ComboTerm {current.ComboTermMs} / TapDanceTerm {current.TapDanceTermMs}";
                if (current.HasTapHoldOptions)
                    currentText += $" / 判定方式 {current.TapHoldMode} / Quick Tap {QuickTapText(current.QuickTapTermMs)} / TT {current.TappingToggle}回";
                else
                    currentText += "\n（このドライバは判定方式・Quick Tap・TTの回数に対応していません。設定しても効きません）";
            }
            catch (KbdLayerException) { /* 古いドライバ等。現在値の表示を省くだけ */ }
        }
        panel.Children.Add(Help("単位はミリ秒です。0 はドライバの既定値（TapTerm 200 / ComboTerm 50 / TapDanceTerm 200）を使います。" +
                                "ここでの変更は「キーボードに適用」で反映します。" + currentText));

        TextBox Field(string name, string label, string help, uint value)
        {
            var box = new TextBox { Name = name, Text = value.ToString(), Width = 80 };
            Row(panel, label, box, help);
            return box;
        }
        _tap = Field("TapTermBox", "TapTerm", "MT/LT: これより長く押すと長押し扱い", _project.TapTermMs);
        _combo = Field("ComboTermBox", "ComboTerm", "コンボ: 2キーの押下の間隔の上限", _project.ComboTermMs);
        _tapDance = Field("TapDanceTermBox", "TapDanceTerm", "タップダンス: タップ間隔・長押しの判定", _project.TapDanceTermMs);

        _mode = new ComboBox { Name = "TapHoldModeBox", Width = 330, ItemsSource = ModeLabels };
        _mode.SelectedIndex = _project.TapHoldMode < ModeLabels.Length ? (int)_project.TapHoldMode : 0;
        Row(panel, "判定方式", _mode, "MT/LT（タップダンスは常に既定の方式）");

        bool quickOff = _project.QuickTapTermMs == KbdLayerClient.QuickTapTermDisabled;
        _quick = new TextBox { Name = "QuickTapTermBox", Text = quickOff ? "0" : _project.QuickTapTermMs.ToString(), Width = 80, IsEnabled = !quickOff };
        _quickEnabled = new CheckBox { Name = "QuickTapEnabled", Content = "使う", IsChecked = !quickOff, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _quickEnabled.Checked += (_, _) => _quick.IsEnabled = true;
        _quickEnabled.Unchecked += (_, _) => _quick.IsEnabled = false;
        var quickRow = new StackPanel { Orientation = Orientation.Horizontal };
        quickRow.Children.Add(_quick);
        quickRow.Children.Add(_quickEnabled);
        Row(panel, "Quick Tap", quickRow, "短押しの直後に押し続けるとリピート（0 = TapTermと同じ）");

        _tt = Field("TappingToggleBox", "TT の回数", "TT(n): この回数続けてタップするとON/OFFを固定（0 = 既定の5）", _project.TappingToggle);
        return panel;
    }

    private static string QuickTapText(uint ms) =>
        ms == KbdLayerClient.QuickTapTermDisabled ? "使わない" : ms == 0 ? "TapTermと同じ" : $"{ms}ms";

    // ------------------------------------------------------------------
    // NICOLA
    // ------------------------------------------------------------------

    private UIElement BuildNicolaTab()
    {
        var panel = new StackPanel();
        panel.Children.Add(Help(
            "NICOLA（親指シフト）は、指定したレイヤーがアクティブな間だけ働きます（そのレイヤーに切り替えるキー、" +
            "例えば TG(1) をキーマップに置いてください）。配列の表は「表を編集...」で変えられます。" +
            "nicola.json（CLIの --set-nicola と同じ形式）の読み込み・書き出しもできます。\n" +
            "ここでの変更は「キーボードに適用」で反映します。"));

        if (IsOnline)
        {
            string driverText;
            try { driverText = NicolaService.DescribeBlob(_client!.GetNicola(_uniqId)); }
            catch (KbdLayerException ex) { driverText = ex.Message; }
            panel.Children.Add(new TextBlock { Name = "NicolaDriverStatus", Text = "キーボードの現在の設定: " + driverText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        }

        _nicolaMode = new ComboBox { Name = "NicolaModeBox", Width = 360, ItemsSource = NicolaModeLabels };
        _nicolaMode.SelectedIndex = _project.Nicola != null ? 1 : _project.ClearNicola ? 2 : 0;
        Row(panel, "NICOLA", _nicolaMode);

        _nicolaSummary = new TextBlock { Name = "NicolaSummary", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
        panel.Children.Add(_nicolaSummary);

        var buttons = new WrapPanel();
        Button Btn(string name, string text, RoutedEventHandler click)
        {
            var b = new Button { Name = name, Content = text, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 2, 8, 2) };
            b.Click += click;
            buttons.Children.Add(b);
            return b;
        }
        Btn("NicolaEditButton", "表を編集...", (_, _) =>
        {
            var dlg = new NicolaEditorWindow(_nicola ?? NicolaService.CreateStandard()) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            _nicola = dlg.Result;
            _nicolaMode.SelectedIndex = 1;
            RefreshNicola();
        });
        Btn("NicolaLoadButton", "nicola.json を読み込む...", (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "NICOLA (*.json)|*.json", Title = "nicola.json を開く" };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                _nicola = NicolaService.Load(dlg.FileName);
                _nicolaMode.SelectedIndex = 1;
                RefreshNicola();
            }
            catch (Exception ex) { ShowStatus(_nicolaSummary, $"読み込めませんでした: {ex.Message}", true); }
        });
        Btn("NicolaSaveButton", "nicola.json に書き出す...", (_, _) =>
        {
            if (_nicola == null) { ShowStatus(_nicolaSummary, "書き出す設定がありません（「表を編集...」か「規格書の配列にする」で作ってください）。", true); return; }
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "NICOLA (*.json)|*.json", FileName = "nicola.json", Title = "nicola.json に書き出す" };
            if (dlg.ShowDialog(this) != true) return;
            try { NicolaService.Save(_nicola, dlg.FileName); RefreshNicola(); }
            catch (Exception ex) { ShowStatus(_nicolaSummary, $"書き出せませんでした: {ex.Message}", true); }
        });
        Btn("NicolaStandardButton", "規格書の配列にする", (_, _) =>
        {
            _nicola = NicolaService.CreateStandard();
            _nicolaMode.SelectedIndex = 1;
            RefreshNicola();
        });
        panel.Children.Add(buttons);

        _nicolaMode.SelectionChanged += (_, _) =>
        {
            if (_nicolaMode.SelectedIndex == 1 && _nicola == null) _nicola = NicolaService.CreateStandard();
            RefreshNicola();
        };
        RefreshNicola();
        return panel;
    }

    private void RefreshNicola()
    {
        switch (_nicolaMode.SelectedIndex)
        {
            case 1 when _nicola != null:
                var warnings = new List<string>();
                var errors = new List<string>();
                NicolaService.BuildBlob(_nicola, warnings, errors);
                string text = $"{(_nicola.Output == "jis-kana" ? "JISかな入力" : "ローマ字入力")}、レイヤー{_nicola.Layer}、" +
                              $"文字キー{_nicola.Keys.Count}個、親指 {_nicola.ThumbLeft} / {_nicola.ThumbRight}" +
                              (_nicola.Enabled ? "" : "（判定しない設定）");
                if (_nicola.Layer >= _project.Layers.Count)
                    warnings.Add($"レイヤー{_nicola.Layer}はキーマップにありません（レイヤーを追加し、切り替えるキーを置いてください）");
                if (errors.Count > 0) ShowStatus(_nicolaSummary, text + "\n" + string.Join("\n", errors), true);
                else ShowStatus(_nicolaSummary, text + (warnings.Count > 0 ? "\n注意: " + string.Join("\n", warnings) : ""), false);
                break;
            case 2:
                ShowStatus(_nicolaSummary, "適用すると、キーボードのNICOLAの設定を消します。", false);
                break;
            default:
                ShowStatus(_nicolaSummary, "適用しても、キーボードのNICOLAの設定は変わりません（CLIの --set-nicola で入れた設定もそのまま）。", false);
                break;
        }
    }

    // ------------------------------------------------------------------
    // 緊急解除（すぐに反映）
    // ------------------------------------------------------------------

    private static readonly string[] DefaultEmergencyKeys = { "KC_LCTL", "KC_RCTL", "KC_LSFT", "KC_RSFT" };

    private UIElement BuildEmergencyTab()
    {
        var panel = new StackPanel();
        panel.Children.Add(Help(
            $"設定を誤って入力できなくなったときの逃げ道です。ここで決めた{KbdLayerClient.EmergencyMinKeys}〜{KbdLayerClient.EmergencyMaxKeys}個のキーを" +
            $"同時に{KbdLayerClient.EmergencyHoldMs / 1000}秒押し続けると、接続中の全キーボードのレイヤーマップがOFFになります（マップ自体は消えません）。" +
            "ドライバ全体で1つの設定です（キーボードごとではありません）。全部のキーが同じキーボードにある必要があります。\n" +
            "キーは物理キー（レイヤーマップで置き換える前）で指定します。「この組み合わせにする」を押すとすぐに反映します（OK・キャンセルとは関係しません）。"));

        var boxes = new TextBox[KbdLayerClient.EmergencyMaxKeys];
        var grid = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        for (int i = 0; i < boxes.Length; i++)
        {
            boxes[i] = new TextBox { Name = $"EmergencyKey{i + 1}", Width = 100, Margin = new Thickness(0, 0, 6, 0), FontFamily = new FontFamily("Consolas") };
            grid.Children.Add(boxes[i]);
        }
        Row(panel, "組み合わせ", grid, null, 90);

        var status = Status("EmergencyStatus");
        void Fill(IReadOnlyList<string> names)
        {
            for (int i = 0; i < boxes.Length; i++) boxes[i].Text = i < names.Count ? names[i] : "";
        }

        if (IsOnline)
        {
            try
            {
                var keys = _client!.GetEmergencyRelease();
                Fill(keys.Select(sc => KeycodeTable.NameForScanCode(sc) ?? $"0x{sc:X4}").ToList());
                ShowStatus(status, "ドライバの現在の組み合わせを表示しています。", false);
            }
            catch (KbdLayerException ex) { ShowStatus(status, ex.Message, true); }
        }
        else
        {
            Fill(DefaultEmergencyKeys);
            ShowStatus(status, "ドライバに接続していないため、変更できません（表示は既定の組み合わせです）。", true);
        }

        bool TryCollect(out List<ushort> scanCodes)
        {
            scanCodes = new List<ushort>();
            foreach (var b in boxes)
            {
                string t = b.Text.Trim();
                if (t.Length == 0) continue;
                if (!KeycodeTable.TryResolve(t, out var a) || a.Type != ActionType.Key || a.Param2 == 0)
                {
                    ShowStatus(status, $"「{t}」は単純なキー(KC_*)にしてください。", true);
                    return false;
                }
                if (scanCodes.Contains(a.Param2))
                {
                    ShowStatus(status, $"「{t}」が2回指定されています。", true);
                    return false;
                }
                scanCodes.Add(a.Param2);
            }
            if (scanCodes.Count < KbdLayerClient.EmergencyMinKeys)
            {
                ShowStatus(status, $"キーは{KbdLayerClient.EmergencyMinKeys}個以上指定してください。", true);
                return false;
            }
            return true;
        }

        var buttons = new WrapPanel();
        var check = new Button { Name = "EmergencyCheckButton", Content = "同時押しを試す...", Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 2, 8, 2) };
        check.Click += (_, _) =>
        {
            if (!TryCollect(out var keys)) return;
            var dlg = new EmergencyCheckWindow(keys) { Owner = this };
            dlg.ShowDialog();
            ShowStatus(status, dlg.Succeeded
                ? "この組み合わせは同時に検出できました。"
                : "全部のキーを同時に検出できませんでした（試していない場合も含む）。別の組み合わせを検討してください。", !dlg.Succeeded);
        };
        var apply = new Button { Name = "EmergencyApplyButton", Content = "この組み合わせにする", IsEnabled = IsOnline && _canWrite, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 2, 8, 2) };
        apply.Click += (_, _) =>
        {
            if (!TryCollect(out var keys)) return;
            try
            {
                _client!.SetEmergencyRelease(keys);
                ShowStatus(status, $"緊急解除の組み合わせを {string.Join(" + ", boxes.Select(b => b.Text.Trim()).Where(s => s.Length > 0))} にしました。", false);
            }
            catch (KbdLayerException ex) { ShowStatus(status, ex.Message, true); }
        };
        var reset = new Button { Name = "EmergencyDefaultButton", Content = "既定（左右Ctrl＋左右Shift）を入れる", Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 2, 8, 2) };
        reset.Click += (_, _) =>
        {
            Fill(DefaultEmergencyKeys);
            ShowStatus(status, "既定の組み合わせを入れました。反映するには「この組み合わせにする」を押してください。", false);
        };
        buttons.Children.Add(check);
        buttons.Children.Add(apply);
        buttons.Children.Add(reset);
        panel.Children.Add(buttons);
        if (IsOnline && !_canWrite)
            panel.Children.Add(Help("閲覧専用モード（管理者として実行していない）のため、変更はできません。"));
        panel.Children.Add(status);
        return panel;
    }

    // ------------------------------------------------------------------
    // 共有範囲（すぐに反映）
    // ------------------------------------------------------------------

    private UIElement BuildScopeTab()
    {
        var panel = new StackPanel();
        panel.Children.Add(Help(
            "ドライバがこのキーボードの設定（レイヤーマップ・タイミング・マクロ等）を保存する単位です。\n" +
            "・同じ型番で共有（既定）: 同じ型番のキーボードは、差し込み口が変わっても同じ設定を使います。\n" +
            "・この1台だけ: この差し込み口のこのキーボードだけの設定にします。切り替えると、今の設定をこの1台用にコピーします。\n" +
            "「変更する」を押すとすぐに反映します（OK・キャンセルとは関係しません）。共有している他の台へは、その台を接続し直すまで反映されません。"));

        var model = new RadioButton { Name = "ScopeModel", Content = "同じ型番で共有", GroupName = "Scope", Margin = new Thickness(0, 3, 0, 3) };
        var instance = new RadioButton { Name = "ScopeInstance", Content = "この1台だけ", GroupName = "Scope", Margin = new Thickness(0, 3, 0, 3) };
        panel.Children.Add(model);
        panel.Children.Add(instance);

        var status = Status("ScopeStatus");
        void Show(DeviceScope s)
        {
            model.IsChecked = s.Binding != KbdLayerClient.BindingInstance;
            instance.IsChecked = s.Binding == KbdLayerClient.BindingInstance;
            ShowStatus(status, $"現在: {(s.Binding == KbdLayerClient.BindingInstance ? "この1台だけ" : "同じ型番で共有")}" +
                               $"（InstanceKey {s.InstanceKey:X8} / ModelKey {s.ModelKey:X8}）", false);
        }

        var apply = new Button { Name = "ScopeApplyButton", Content = "変更する", IsEnabled = IsOnline && _canWrite, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
        apply.Click += (_, _) =>
        {
            uint binding = instance.IsChecked == true ? KbdLayerClient.BindingInstance : KbdLayerClient.BindingModel;
            try { Show(_client!.SetDeviceScope(_uniqId, binding)); }
            catch (KbdLayerException ex) { ShowStatus(status, ex.Message, true); }
        };
        panel.Children.Add(apply);

        if (IsOnline)
        {
            try { Show(_client!.GetDeviceScope(_uniqId)); }
            catch (KbdLayerException ex)
            {
                ShowStatus(status, ex.Message, true);
                apply.IsEnabled = false;
            }
            if (!_canWrite) panel.Children.Add(Help("閲覧専用モード（管理者として実行していない）のため、変更はできません。"));
        }
        else
        {
            model.IsEnabled = instance.IsEnabled = false;
            ShowStatus(status, "ドライバに接続していないため、表示・変更できません。", true);
        }
        panel.Children.Add(status);
        return panel;
    }

    // ------------------------------------------------------------------
    // OK
    // ------------------------------------------------------------------

    private bool Commit()
    {
        void Fail(string tab, string message)
        {
            _tabs.SelectedItem = _tabs.Items.Cast<TabItem>().First(item => item.Name == tab);
            _error.Text = message;
        }

        if (!uint.TryParse(_tap.Text, out var t) || !uint.TryParse(_combo.Text, out var c) || !uint.TryParse(_tapDance.Text, out var d)
            || t > 10000 || c > 10000 || d > 10000)
        {
            Fail("TabTiming", "TapTerm・ComboTerm・TapDanceTerm は0〜10000の整数で入力してください。");
            return false;
        }
        uint q = KbdLayerClient.QuickTapTermDisabled;
        if (_quickEnabled.IsChecked == true && (!uint.TryParse(_quick.Text, out q) || q > 10000))
        {
            Fail("TabTiming", "Quick Tap は0〜10000の整数で入力してください。");
            return false;
        }
        if (!uint.TryParse(_tt.Text, out var n) || n > KbdLayerClient.MaxTappingToggle)
        {
            Fail("TabTiming", $"TT の回数は0〜{KbdLayerClient.MaxTappingToggle}の整数で入力してください。");
            return false;
        }
        if (_nicolaMode.SelectedIndex == 1 && _nicola != null)
        {
            var errors = new List<string>();
            NicolaService.BuildBlob(_nicola, new List<string>(), errors);
            if (errors.Count > 0)
            {
                Fail("TabNicola", string.Join("\n", errors));
                return false;
            }
        }

        _project.TapTermMs = t;
        _project.ComboTermMs = c;
        _project.TapDanceTermMs = d;
        _project.TapHoldMode = (uint)Math.Max(0, _mode.SelectedIndex);
        _project.QuickTapTermMs = q;
        _project.TappingToggle = n;

        // 一度も触っていなければ「GUIでは管理しない」のまま（CLIで変えた値を、適用で上書きしないため）。
        uint features = SelectedFeatures();
        if (_project.EnabledFeatures != null || features != _initialFeatures) _project.EnabledFeatures = features;

        _project.Nicola = _nicolaMode.SelectedIndex == 1 ? _nicola : null;
        _project.ClearNicola = _nicolaMode.SelectedIndex == 2;
        return true;
    }
}
