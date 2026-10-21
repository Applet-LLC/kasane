using System.IO;
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
///  - 緊急解除・共有範囲・i8042prt: ドライバ全体の設定、またはドライバが設定を保存する場所なので、タブの中のボタンで
///    すぐに反映する（OK・キャンセルとは関係しない）。
/// コントロールには UI Automation 用の Name を付ける（TabFeatures 等のタブ、各入力欄）。
/// </summary>
public sealed class SettingsDialog : Window
{
    private static readonly string[] ModeLabels =
    {
        "別のキーを押したらすぐ長押し扱い(既定)",
        "別のキーを押して離したら長押し扱い(Permissive Hold)",
        "時間内に離せば短押し扱い(時間切れで長押し)",
    };

    private static readonly string[] NicolaModeLabels =
    {
        "GUIでは管理しない(ドライバの設定をそのまま使う)",
        "この設定を使う(適用で反映)",
        "使わない(適用時にドライバの設定を消す)",
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
    private CheckBox _imeLink = null!;
    private bool _globalNicolaLink; // 全キーボード共通の、NICOLAのIME連動（tray.json の ImeLink。OK のとき保存）
    private CheckBox _showLayerOnIcon = null!;
    private ComboBox _imeSourceBox = null!; // IME の状態の取得方法（tray.json の ImeSource）
    private ComboBox _colorModeBox = null!;  // 色のモード（tray.json の ColorMode）。OK で保存して、すぐ全画面に効かせる
    private readonly TrayConfig _trayConfig = TrayConfig.Load();
    private TextBlock _nicolaSummary = null!;
    // ヘルパー動作・アプリ別レイヤー（このキーボードの helper.json と、共通の tray.json の Helper。OKで直接保存する）
    private readonly HelperSettingsModel _helper;

    public SettingsDialog(KeymapProject project, KbdLayerClient? client, uint onlineUniqId, bool canWrite)
    {
        _project = project;
        _client = client;
        _uniqId = client != null ? onlineUniqId : 0;
        _canWrite = canWrite;
        _nicola = project.Nicola?.Clone();
        _globalNicolaLink = _trayConfig.ImeLink;
        _helper = new HelperSettingsModel(project.UniqId != 0 ? project.UniqId : _uniqId, _trayConfig);

        Title = "設定";
        Width = 760;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        AddTab("TabGeneral", "全般", BuildGeneralTab());
        AddTab("TabFeatures", "機能", BuildFeaturesTab());
        AddTab("TabTiming", "タイミング", BuildTimingTab());
        AddTab("TabImeLayer", "IME連動", BuildImeTab()); // 以前の「NICOLA」タブの内容も、ここにある（2026-10-19）
        AddTab("TabHelper", "ヘルパー動作", new HelperActionsTab(_helper, this).Root);
        AddTab("TabAppRules", "アプリ別レイヤー", new AppRulesTab(_helper, this).Root);
        AddTab("TabEmergency", "緊急解除", BuildEmergencyTab());
        AddTab("TabScope", "共有範囲", BuildScopeTab());
        AddTab("TabDataDir", "保存先", BuildDataDirTab());
        AddTab("TabI8042", "i8042prt", BuildI8042Tab());
        AddTab("TabDesktop", "デスクトップ", BuildDesktopTab());
        AddTab(AboutTab, "バージョン情報", BuildAboutTab());
        _tabs.SelectionChanged += OnTabChanged;

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

    private CheckBox _remapOnlyWhileTray = null!;

    /// <summary>全般: このPCの利用者の設定（tray.json。トレイが数秒ごとに読む）。OK で保存する。</summary>
    private UIElement BuildGeneralTab()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "トレイ(タスクトレイの kasane)", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
        _remapOnlyWhileTray = new CheckBox
        {
            Name = "GeneralRemapOnlyWhileTrayCheck",
            Content = "トレイが動いている間だけ、キーのリマップを有効にする",
            IsChecked = _trayConfig.RemapOnlyWhileTrayRuns,
            Margin = new Thickness(0, 0, 0, 6),
        };
        panel.Children.Add(_remapOnlyWhileTray);
        panel.Children.Add(Help(
            "ドライバが設定を保存しているので、Windows のログオン画面や、起動した直後(トレイが動く前)でも、リマップは続きます。" +
            "ONにすると、次のように動きます。\n" +
            "・トレイを終了したとき、サインアウトやシャットダウンをするとき、画面をロックしたとき(ユーザーの切り替えを含む)は、リマップをOFF(素通し)にします。\n" +
            "・トレイが起動したときとロックを解除したときは、この設定でOFFにしたキーボードだけをONに戻します(緊急解除や「OFF」ボタンで止めたキーボードは、戻しません)。\n" +
            "既定はONです。\n" +
            "注意: トレイが異常終了したとき(クラッシュ・強制終了・電源断など)は、OFFにできません。次の起動のログオン画面では、リマップが効いたままです。" +
            "インストーラーの更新や削除でトレイを止めるときも、OFFにしません。\n" +
            "この設定は、この PC のこの利用者の設定で、キーボードごとの設定ではありません。OK で保存すると、トレイが数秒で読み直します。"));

        _showLayerOnIcon = new CheckBox
        {
            Name = "ShowLayerOnIconCheck",
            Content = "トレイのアイコンに、今のレイヤー番号を重ねる(基底のレイヤーだけのときは重ねない)",
            IsChecked = _trayConfig.ShowLayerOnIcon,
            Margin = new Thickness(0, 12, 0, 8),
        };
        panel.Children.Add(_showLayerOnIcon);

        // 色のモード（ライト／ダーク）。既定はライト。OK で、開いている画面と、これから開く画面すべてに効く。
        panel.Children.Add(new TextBlock { Text = "画面の色", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 4) });
        _colorModeBox = new ComboBox { Name = "ColorModeBox", Width = 160, ItemsSource = ColorModeLabels, SelectedIndex = (int)_trayConfig.ColorModeValue };
        Row(panel, "色のモード", _colorModeBox,
            "各ダイアログの色のモードを、ライト(既定)あるいはダークに設定できます。",
            labelWidth: 120);
        return panel;
    }

    private static readonly string[] ColorModeLabels = { "ライト(既定)", "ダーク" };

    private static readonly string[] DesktopViewLabels =
    {
        "通常表示", "詳細表示", "小さなアイコン表示", "リスト表示", "タイル表示",
    };

    private RadioButton _desktopKeep = null!;
    private readonly RadioButton[] _desktopViewRadios = new RadioButton[5];

    /// <summary>デスクトップ: アイコンの表示方法（のどかの DesktopListView。キーのリマップとは無関係）。OK で tray.json に保存する。</summary>
    private UIElement BuildDesktopTab()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "デスクトップのアイコンの表示方法", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(Help(
            "デスクトップのアイコンの表示方法を変更します。ここでの変更時と、kasane-trayの起動時に反映されます。\n" +
            "OSやデスクトップの状態によっては、選んだ表示方法にならないことがあります。"));

        _desktopKeep = new RadioButton { Name = "DesktopViewKeep", Content = "変えない(既定)", GroupName = "DesktopView", Margin = new Thickness(0, 2, 0, 2) };
        panel.Children.Add(_desktopKeep);
        for (int i = 0; i < _desktopViewRadios.Length; i++)
        {
            _desktopViewRadios[i] = new RadioButton
            {
                Name = "DesktopView" + i,
                Content = $"{i}: {DesktopViewLabels[i]}",
                GroupName = "DesktopView",
                Margin = new Thickness(0, 2, 0, 2),
            };
            panel.Children.Add(_desktopViewRadios[i]);
        }
        int? current = _trayConfig.DesktopViewMode;
        if (current is >= 0 and < 5) _desktopViewRadios[current.Value].IsChecked = true;
        else _desktopKeep.IsChecked = true;
        return panel;
    }

    private int? SelectedDesktopViewMode()
    {
        for (int i = 0; i < _desktopViewRadios.Length; i++)
            if (_desktopViewRadios[i].IsChecked == true) return i;
        return null;
    }

    /// <summary>バージョン情報のタブの名前（一番右）。メイン画面の右クリックのメニューからも、このタブを選んで開く。</summary>
    public const string AboutTab = "TabAbout";

    /// <summary>名前を指定してタブを選ぶ。</summary>
    public void SelectTab(string name)
    {
        if (_tabs.Items.Cast<TabItem>().FirstOrDefault(item => item.Name == name) is { } tab) _tabs.SelectedItem = tab;
    }

    /// <summary>バージョン情報: 名前・バージョン・（1行あけて）発行元。保存するものはない。</summary>
    private UIElement BuildAboutTab()
    {
        var panel = new StackPanel { Margin = new Thickness(8, 8, 0, 0) };
        panel.Children.Add(new TextBlock { Name = "AboutName", Text = AppInfo.DisplayName, FontSize = 26, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Name = "AboutVersion", Text = $"バージョン {AppInfo.VersionText}", FontSize = 15, Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(new TextBlock { Name = "AboutBuildTime", Text = $"ビルド日時 {AppInfo.BuildTimeText}", Margin = new Thickness(0, 4, 0, 0) });
        // 実行しているファイルの、インストール先を含むフルパス（長くても折り返して、選んでコピーできる）。
        panel.Children.Add(new TextBox
        {
            Name = "AboutPath", Text = AppInfo.ExePath, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Foreground = AppTheme.Palette.SubText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(-2, 6, 0, 0),
        });
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(AppInfo.Website)) { NavigateUri = new Uri(AppInfo.Website) };
        link.RequestNavigate += (_, e) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { /* ブラウザーを起動できない */ }
            e.Handled = true;
        };
        panel.Children.Add(new TextBlock(link) { Name = "AboutWebsite", Margin = new Thickness(0, 14, 0, 0) });
        panel.Children.Add(new TextBlock { Name = "AboutCopyright", Text = AppInfo.Copyright, Foreground = AppTheme.Palette.SubText, Margin = new Thickness(0, 4, 0, 0) });
        return panel;
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
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Palette.SubText, Margin = new Thickness(0, 0, 0, 10) };

    private static StackPanel Row(Panel parent, string label, UIElement input, string? help = null, double labelWidth = 120)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(new TextBlock { Text = label, Width = labelWidth, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(input);
        if (help != null)
            row.Children.Add(new TextBlock { Text = help, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Palette.SubText, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 });
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
            "OFFにしても設定(マクロなどの定義)は消えません。レイヤーとキーの置き換え、MT/LT、修飾付きキーは常に使えます。\n" +
            "ここでの変更は、「キーボードに適用」で反映されます。"));

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
            catch (KbdLayerException) { current = "(このドライバは機能のON/OFFに対応していません)"; }
        }
        _initialFeatures = _project.EnabledFeatures ?? initial;

        var defs = new (string Name, string Label, uint Bit)[]
        {
            ("FeatureMacro", "マクロ(MACRO(n))", KbdLayerClient.FeatureMacro),
            ("FeatureCombo", "コンボ", KbdLayerClient.FeatureCombo),
            ("FeatureTapDance", "タップダンス(TAPDANCE(n))", KbdLayerClient.FeatureTapDance),
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
                    currentText += "\n(このドライバは判定方式・Quick Tap・TTの回数に対応していないので、設定しても効きません)";
            }
            catch (KbdLayerException) { /* 古いドライバ等。現在値の表示を省くだけ */ }
        }
        panel.Children.Add(Help("単位はミリ秒です。0 にすると、ドライバの既定値(TapTerm 200 / ComboTerm 50 / TapDanceTerm 200)を使います。" +
                                "ここでの変更は、「キーボードに適用」で反映されます。" + currentText));

        TextBox Field(string name, string label, string help, uint value)
        {
            var box = new TextBox { Name = name, Text = value.ToString(), Width = 80 };
            Row(panel, label, box, help);
            return box;
        }
        _tap = Field("TapTermBox", "TapTerm", "MT/LT: これより長く押すと長押し扱い", _project.TapTermMs);
        _combo = Field("ComboTermBox", "ComboTerm", "コンボ: 2つのキーを押す間隔の上限", _project.ComboTermMs);
        _tapDance = Field("TapDanceTermBox", "TapDanceTerm", "タップダンス: タップ間隔・長押しの判定", _project.TapDanceTermMs);

        _mode = new ComboBox { Name = "TapHoldModeBox", Width = 330, ItemsSource = ModeLabels };
        _mode.SelectedIndex = _project.TapHoldMode < ModeLabels.Length ? (int)_project.TapHoldMode : 0;
        Row(panel, "判定方式", _mode, "MT/LT に使います(タップダンスは常に既定の方式)");

        bool quickOff = _project.QuickTapTermMs == KbdLayerClient.QuickTapTermDisabled;
        _quick = new TextBox { Name = "QuickTapTermBox", Text = quickOff ? "0" : _project.QuickTapTermMs.ToString(), Width = 80, IsEnabled = !quickOff };
        _quickEnabled = new CheckBox { Name = "QuickTapEnabled", Content = "使う", IsChecked = !quickOff, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _quickEnabled.Checked += (_, _) => _quick.IsEnabled = true;
        _quickEnabled.Unchecked += (_, _) => _quick.IsEnabled = false;
        var quickRow = new StackPanel { Orientation = Orientation.Horizontal };
        quickRow.Children.Add(_quick);
        quickRow.Children.Add(_quickEnabled);
        Row(panel, "Quick Tap", quickRow, "短押しの直後に押し続けるとリピートします(0 = TapTerm と同じ)");

        _tt = Field("TappingToggleBox", "TT の回数", "TT(n): この回数続けてタップすると、ON/OFFが固定されます(0 = 既定の5)", _project.TappingToggle);
        return panel;
    }

    private static string QuickTapText(uint ms) =>
        ms == KbdLayerClient.QuickTapTermDisabled ? "使わない" : ms == 0 ? "TapTermと同じ" : $"{ms}ms";

    // ------------------------------------------------------------------
    // NICOLA
    // ------------------------------------------------------------------

    // 「IME連動」タブ: 上に「IMEのON/OFFとレイヤーの連動」（NICOLAを使わなくても使える）、下に「NICOLA（親指シフト）」の設定。
    // 以前は別々のタブ（IME連動・NICOLA）だった。NICOLAを使わない人も、IME連動をここだけで設定できる。
    private UIElement BuildImeTab()
    {
        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = "IME の ON/OFF とレイヤーの連動", FontWeight = FontWeights.Bold, FontSize = 14, Margin = new Thickness(0, 0, 0, 6) });
        root.Children.Add(new ImeLayerTab(_helper, this).Root);
        root.Children.Add(BuildImeSourceRow());
        root.Children.Add(new Separator { Margin = new Thickness(0, 14, 0, 10) });
        root.Children.Add(new TextBlock { Text = "NICOLA(親指シフト)", FontWeight = FontWeights.Bold, FontSize = 14, Margin = new Thickness(0, 0, 0, 6) });
        root.Children.Add(BuildNicolaTab());
        return root;
    }

    // IME の状態の取得方法。IMM32 は、アプリの IME ウィンドウへの問い合わせ。TSF は、天狼（sirius）のフックの DLL が各アプリの中で読んだ値
    // （IMM32 で取れないアプリ・TSF だけのアプリの補い）。フックは全アプリに DLL を読み込ませるので、Imm32 を選ぶと入れない。
    private static readonly string[] ImeSourceLabels =
    {
        "IMM32 のみ(フックを入れない。既定)",
        "IMM32 を先に、取れなければ TSF",
        "TSF を先に、取れなければ IMM32",
    };

    private UIElement BuildImeSourceRow()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        _imeSourceBox = new ComboBox { Name = "ImeSourceBox", Width = 360, ItemsSource = ImeSourceLabels, SelectedIndex = (int)_trayConfig.ImeSourceMode };
        Row(panel, "IME の状態の取得方法", _imeSourceBox,
            "既定の「IMM32 のみ」で、多くのアプリの IME の状態を取得できます。IMM32 で取得できないアプリ(TSF だけのアプリなど)を補いたいときだけ、TSF を選びます。" +
            "TSF を選ぶと、DLL(sirius_hook)が読んだ値を使います。Explorer を開くのが遅く感じられることがあります。「IMM32 のみ」では、この DLL を使いません。" +
            "変更は、OK で保存すると、数秒でトレイに反映されます。",
            labelWidth: 170);
        return panel;
    }

    private UIElement BuildNicolaTab()
    {
        var panel = new StackPanel();
        panel.Children.Add(Help(
            "NICOLA(親指シフト)は、指定したレイヤーが有効な間だけ働きます。「IME連動」を使うと、IMEがかなモードのときだけ、" +
            "タスクトレイの kasane が自動でそのレイヤーを立てます(切り替えるキーは要りません)。使わない場合は、そのレイヤーに切り替えるキー" +
            "(たとえば TG(1))をキーマップに置いてください。配列の表は「表を編集...」で変えられます。" +
            "nicola.json(CLIの --set-nicola と同じ形式)の読み込みと書き出しもできます。\n" +
            "NICOLAを使うと、Ctrl や Shift を押している間のために、親指キーを単独で打つキー(Enter・Spaceなど)が、" +
            "NICOLAのレイヤーのキー割り当てに自動で足されます。\n" +
            "キーボードへの反映は、「キーボードに適用」で行います。IME連動の設定は、OK で保存すると数秒でトレイに反映されます(このキーボードごとの設定か、このPCの利用者ごとの全キーボード共通の設定)。"));

        if (IsOnline)
        {
            string driverText;
            try { driverText = NicolaService.DescribeBlob(_client!.GetNicola(_uniqId)); }
            catch (KbdLayerException ex) { driverText = ex.Message; }
            panel.Children.Add(new TextBlock { Name = "NicolaDriverStatus", Text = "キーボードの現在の設定: " + driverText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        }

        // IME連動（NICOLA）は、このキーボードごとか、全キーボード共通かを選べる（キーボードごとの設定が先）。
        var linkPanel = new NicolaLinkPanel(_helper, this, () => _globalNicolaLink, v => _globalNicolaLink = v, () => RefreshNicola());
        _imeLink = linkPanel.LinkCheck;
        panel.Children.Add(linkPanel.Root);

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
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "NICOLA (*.json)|*.json", Title = "nicola.json を開く", InitialDirectory = DialogDefaults.DataFolder, ClientGuid = DialogDefaults.Nicola };
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
            if (_nicola == null) { ShowStatus(_nicolaSummary, "書き出す設定がありません。「表を編集...」か「規格書の配列にする」で作ってください。", true); return; }
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "NICOLA (*.json)|*.json", FileName = "nicola.json", Title = "nicola.json に書き出す", InitialDirectory = DialogDefaults.DataFolder, ClientGuid = DialogDefaults.Nicola };
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
                              (_nicola.Enabled ? "" : "(判定しない設定)");
                if (_nicola.Layer >= _project.Layers.Count && _imeLink.IsChecked != true)
                    warnings.Add($"レイヤー{_nicola.Layer}はキーマップにありません。IME連動を使わない場合は、そのレイヤーに切り替えるキー(TG({_nicola.Layer}) など)を置いてください。");
                if (errors.Count > 0) ShowStatus(_nicolaSummary, text + "\n" + string.Join("\n", errors), true);
                else ShowStatus(_nicolaSummary, text + (warnings.Count > 0 ? "\n注意: " + string.Join("\n", warnings) : ""), false);
                break;
            case 2:
                ShowStatus(_nicolaSummary, "適用すると、キーボードのNICOLAの設定を消します。", false);
                break;
            default:
                ShowStatus(_nicolaSummary, "適用しても、キーボードのNICOLAの設定は変わりません(CLIの --set-nicola で入れた設定もそのまま)。", false);
                break;
        }
    }

    // ------------------------------------------------------------------
    // 緊急解除（タブを移るとき・OK のときに反映）
    // ------------------------------------------------------------------

    private static readonly string[] DefaultEmergencyKeys = { "KC_LCTL", "KC_RCTL", "KC_LSFT", "KC_RSFT" };

    /// <summary>
    /// 緊急解除タブの入力を、ドライバへ反映する（タブを離れるときと、OK のとき）。失敗の理由を返す（成功・変更なしは null）。
    /// 入力が空なら「変更しない」。タブを作るときに設定する。
    /// </summary>
    private Func<string?>? _emergencyCommit;
    private bool _revertingTab;

    // タブを離れるとき、緊急解除の入力が不正ならエラーを出して元のタブに留める。
    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, _tabs) || _revertingTab || _emergencyCommit == null) return;
        var left = e.RemovedItems.OfType<TabItem>().FirstOrDefault(t => t.Name == "TabEmergency");
        if (left == null) return;
        if (_emergencyCommit() == null) return;
        // 選択の変更の途中で別の選択に戻すと不安定なので、あとで戻す。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _revertingTab = true;
            try { _tabs.SelectedItem = left; }
            finally { _revertingTab = false; }
        }));
    }

    private UIElement BuildEmergencyTab()
    {
        var panel = new StackPanel();
        panel.Children.Add(Help(
            $"設定を誤って入力できなくなったときの緊急用の手段です。ここで決めた{KbdLayerClient.EmergencyMinKeys}〜{KbdLayerClient.EmergencyMaxKeys}個のキーを" +
            $"同時に{KbdLayerClient.EmergencyHoldMs / 1000}秒押し続けると、接続中の全キーボードのレイヤーマップがOFFになります(マップ自体は消えません)。" +
            "設定はドライバ全体で1つで、キーボードごとには持ちません。全部のキーが同じキーボードにある必要があります。\n" +
            "キーは、レイヤーマップで置き換える前の物理キーで指定します。各欄の「押して入力」でキーを押して入れるか、「選ぶ...」で選びます。\n" +
            "入力した組み合わせは、別のタブに移るときか OK を押したときに、ドライバへ反映されます。タブを移った時点で反映したものは、キャンセルしても戻りません。" +
            "全部の欄を空にした場合は、変更しません。"));

        bool writable = IsOnline && _canWrite;
        var boxes = new TextBox[KbdLayerClient.EmergencyMaxKeys];
        // 6個分は横に長いので、縦に並べる。
        var grid = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 6), IsEnabled = writable };
        for (int i = 0; i < boxes.Length; i++)
        {
            boxes[i] = new TextBox { Name = $"EmergencyKey{i + 1}", Width = 100, FontFamily = new FontFamily("Consolas") };
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            line.Children.Add(new TextBlock { Text = $"キー{i + 1}", Width = 44, VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(KeycodeField.Wrap(boxes[i], allowMods: false));
            grid.Children.Add(line);
        }
        Row(panel, "組み合わせ", grid, null, 90);

        var status = Status("EmergencyStatus");
        void Fill(IReadOnlyList<string> names)
        {
            for (int i = 0; i < boxes.Length; i++) boxes[i].Text = i < names.Count ? names[i] : "";
        }

        List<ushort>? applied = null; // ドライバの今の組み合わせ（変わっていなければ書き込まない）
        if (IsOnline)
        {
            try
            {
                var keys = _client!.GetEmergencyRelease();
                applied = keys.ToList();
                Fill(keys.Select(KeycodeTable.KeyToken).ToList());
                ShowStatus(status, "ドライバの現在の組み合わせを表示しています。", false);
            }
            catch (KbdLayerException ex) { ShowStatus(status, ex.Message, true); }
        }
        else
        {
            Fill(DefaultEmergencyKeys);
            ShowStatus(status, "ドライバに接続していないため、変更できません(表示は既定の組み合わせです)。", true);
        }

        // 入力をスキャンコードにする。空の欄は飛ばす（全部空なら空のリスト）。個数の下限は見ない。
        bool TryCollect(out List<ushort> scanCodes, out string? error)
        {
            scanCodes = new List<ushort>();
            error = null;
            foreach (var b in boxes)
            {
                string t = b.Text.Trim();
                if (t.Length == 0) continue;
                if (!KeycodeTable.TryResolve(t, out var a) || a.Type != ActionType.Key || a.Param2 == 0)
                {
                    error = $"「{t}」は単純なキー(KC_*)にしてください。";
                    return false;
                }
                if (scanCodes.Contains(a.Param2))
                {
                    error = $"「{t}」が2回指定されています。";
                    return false;
                }
                scanCodes.Add(a.Param2);
            }
            return true;
        }

        string? CommitCore()
        {
            if (!writable) return null;
            if (!TryCollect(out var keys, out var error)) return error;
            if (keys.Count == 0) return null; // 空 = 変更しない
            if (keys.Count < KbdLayerClient.EmergencyMinKeys) return $"キーは{KbdLayerClient.EmergencyMinKeys}個以上指定してください。";
            if (applied != null && applied.SequenceEqual(keys)) return null;
            try
            {
                _client!.SetEmergencyRelease(keys);
                applied = keys;
                ShowStatus(status, $"緊急解除の組み合わせを {string.Join(" + ", boxes.Select(b => b.Text.Trim()).Where(s => s.Length > 0))} にしました。", false);
                return null;
            }
            catch (KbdLayerException ex) { return ex.Message; }
        }
        _emergencyCommit = () =>
        {
            string? error = CommitCore();
            if (error != null) ShowStatus(status, error, true);
            return error;
        };

        var buttons = new WrapPanel();
        var check = new Button { Name = "EmergencyCheckButton", Content = "同時押しを試す...", Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 2, 8, 2) };
        check.Click += (_, _) =>
        {
            if (!TryCollect(out var keys, out var error)) { ShowStatus(status, error!, true); return; }
            if (keys.Count < KbdLayerClient.EmergencyMinKeys)
            {
                ShowStatus(status, $"キーは{KbdLayerClient.EmergencyMinKeys}個以上指定してください。", true);
                return;
            }
            // 管理者で、ドライバに接続しているときは、選んでいるキーボードの入力を、ドライバから直接受ける。
            var dlg = new EmergencyCheckWindow(keys, IsOnline && _canWrite ? _uniqId : 0) { Owner = this };
            dlg.ShowDialog();
            ShowStatus(status, dlg.Succeeded
                ? "この組み合わせは同時に検出できました。"
                : "全部のキーを同時に検出できませんでした(試していない場合も含む)。別の組み合わせを検討してください。", !dlg.Succeeded);
        };
        var clear = new Button { Name = "EmergencyClearButton", Content = "クリア", IsEnabled = writable, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 2, 8, 2) };
        clear.Click += (_, _) =>
        {
            Fill(Array.Empty<string>());
            ShowStatus(status, "入力欄を空にしました。空のままタブを移ったり OK を押したりしても、緊急解除の組み合わせは変わりません。", false);
        };
        var reset = new Button { Name = "EmergencyDefaultButton", Content = "既定(左右Ctrl＋左右Shift)にする", IsEnabled = writable, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 2, 8, 2) };
        reset.Click += (_, _) =>
        {
            Fill(DefaultEmergencyKeys);
            ShowStatus(status, "既定の組み合わせを入れました。タブを移るか OK を押すと反映されます。", false);
        };
        buttons.Children.Add(check);
        buttons.Children.Add(clear);
        buttons.Children.Add(reset);
        panel.Children.Add(buttons);
        // 管理者で、ドライバに接続していないと、「同時押しを試す」は低レベルキーフックで見る。その制限は、画面を開く前に、このタブで分かるようにする。
        if (!writable)
        {
            var limit = Help("「同時押しを試す」は、管理者権限モードで実施してください。");
            limit.Name = "EmergencyHookLimitNote";
            limit.Foreground = Brushes.Firebrick;
            panel.Children.Add(limit);
        }
        if (IsOnline && !_canWrite)
            panel.Children.Add(Help("閲覧専用モード(管理者として実行していない)のため、変更はできません。"));
        panel.Children.Add(status);
        return panel;
    }

    // ------------------------------------------------------------------
    // 共有範囲（すぐに反映）
    // ------------------------------------------------------------------

    private UIElement BuildScopeTab()
    {
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock { Text = "設定の共有範囲", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(Help(
            "ドライバがこのキーボードの設定(レイヤーマップ・タイミング・マクロなど)を保存する単位です。\n" +
            "・同じ型番で共有(既定): 同じ型番のキーボードは、差し込み口が変わっても同じ設定を使います。\n" +
            "・この1台だけ: この差し込み口のこのキーボードだけの設定にします。切り替えると、今の設定をこの1台用にコピーします。\n" +
            "・すべてのキーボードで共有: 型番も差し込み口も区別せず、1つの設定をすべてのキーボードで使います。後から接続したキーボードも、" +
            "この設定にしていれば同じ設定になります。すでに共有の設定があれば、それを使います。" +
            "配列の違うキーボード(JISとテンキーなど)を混ぜると、意図しない割り当てになることがあります。\n" +
            "切り替えると、今の設定が切替先へコピーされ、切替先にすでにある設定は上書きされます。たとえば「すべて」や「この1台」から「同じ型番」へ戻すと、" +
            "同じ型番で共有している設定が置き換わります。\n" +
            "「変更する」を押すとすぐに反映します(OK・キャンセルとは関係しません)。共有している他の台には、その台を接続し直すまで反映されません。"));

        panel.Children.Add(new TextBlock { Text = "共有範囲とポートの関係(トレイを含む)", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 4, 0, 4) });
        panel.Children.Add(Help(
            "・ポートを替えたとき: ドライバが保存する設定は、共有範囲が「同じ型番」か「すべて」なら引き継がれ、「この1台だけ」なら引き継がれません。" +
            "設定画面で作った設定(project.json)とトレイの設定(ヘルパー動作・アプリ別レイヤー・一時停止の記憶)は、接続先(USBポートなど)ごとに別のキーボードとして扱います。" +
            "そのため、同じキーボードを別のポートに挿すと引き継がれません(エクスポートとインポートで移せます)。\n" +
            "・同じ型番を2台つないだとき: 接続先が違うので、設定画面とトレイでは別のキーボードとして扱います(ドライバの設定は、共有範囲が「同じ型番」なら共有されます)。\n" +
            "・トレイの設定は、設定画面と同じく接続先ごとのキーボード単位です。ここで共有範囲を変えても、共有されません。"));

        var model = new RadioButton { Name = "ScopeModel", Content = "同じ型番で共有", GroupName = "Scope", Margin = new Thickness(0, 3, 0, 3) };
        var instance = new RadioButton { Name = "ScopeInstance", Content = "この1台だけ", GroupName = "Scope", Margin = new Thickness(0, 3, 0, 3) };
        var global = new RadioButton { Name = "ScopeGlobal", Content = "すべてのキーボードで共有", GroupName = "Scope", Margin = new Thickness(0, 3, 0, 3) };
        panel.Children.Add(model);
        panel.Children.Add(instance);
        panel.Children.Add(global);

        var status = Status("ScopeStatus");
        void Show(DeviceScope s)
        {
            model.IsChecked = s.Binding != KbdLayerClient.BindingInstance && s.Binding != KbdLayerClient.BindingGlobal;
            instance.IsChecked = s.Binding == KbdLayerClient.BindingInstance;
            global.IsChecked = s.Binding == KbdLayerClient.BindingGlobal;
            string name = s.Binding switch
            {
                KbdLayerClient.BindingInstance => "この1台だけ",
                KbdLayerClient.BindingGlobal => "すべてのキーボードで共有",
                _ => "同じ型番で共有",
            };
            ShowStatus(status, $"現在: {name}(InstanceKey {s.InstanceKey:X8} / ModelKey {s.ModelKey:X8})", false);
        }

        var apply = new Button { Name = "ScopeApplyButton", Content = "変更する", IsEnabled = IsOnline && _canWrite, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
        apply.Click += (_, _) =>
        {
            uint binding = global.IsChecked == true ? KbdLayerClient.BindingGlobal
                         : instance.IsChecked == true ? KbdLayerClient.BindingInstance
                         : KbdLayerClient.BindingModel;
            if (binding == KbdLayerClient.BindingGlobal && !ConfirmGlobalScope()) return;
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
            if (!_canWrite) panel.Children.Add(Help("閲覧専用モード(管理者として実行していない)のため、変更はできません。"));
        }
        else
        {
            model.IsEnabled = instance.IsEnabled = global.IsEnabled = false;
            ShowStatus(status, "ドライバに接続していないため、表示・変更できません。", true);
        }
        panel.Children.Add(status);
        return panel;
    }

    /// <summary>
    /// 「すべてのキーボードで共有」に切り替える前の確認。キーマップは物理スキャンコードで決まるので、
    /// 接続中のキーボードに型番（HardwareId）の違うものが複数あると、意図しない割り当てになりうる。
    /// 1種類だけなら確認せずに進む（後から接続するキーボードのことは、タブの説明に書いてある）。
    /// </summary>
    private bool ConfirmGlobalScope()
    {
        List<DeviceInfo> devices;
        try { devices = _client!.ListDevices(); }
        catch (KbdLayerException) { return true; } // 一覧を取れなければ確認を省く（変更自体の失敗は別に表示される）
        var models = devices.Where(d => d.UniqId != 0).Select(d => d.HardwareId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (models.Count <= 1) return true;
        var r = MessageBox.Show(this,
            $"接続中のキーボードに、型番の違うものが{models.Count}種類あります。\n\n" +
            "「すべてのキーボードで共有」にすると、これらのキーボードで同じ設定を使います。キーマップは物理キーの位置(スキャンコード)で" +
            "決まるので、配列の違うキーボード(JISとテンキーなど)では、意図しないキーに割り当てられることがあります。\n\n" +
            "共有にしてよいですか。",
            Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        return r == MessageBoxResult.OK;
    }

    // ------------------------------------------------------------------
    // i8042prt（すぐに反映。ドライバ全体の設定で、プロファイルはこのキーボードの型番単位）
    // ------------------------------------------------------------------

    /// <summary>プリセット: 名前, Type, Subtype, Identifier, LayerDriver JPN, LayerDriver KOR。</summary>
    // ------------------------------------------------------------------
    // 保存先（環境変数 KASANE の表示。変更はインストーラーで行う）
    // ------------------------------------------------------------------

    private UIElement BuildDataDirTab()
    {
        var panel = new StackPanel();
        panel.Children.Add(Help(
            "キーボードごとの設定(project.json・helper.json)、トレイの設定、ログを保存するフォルダです。\n" +
            "環境変数 KASANE に絶対パスが設定されていればそのフォルダを使い、無ければ(または相対パスなど正しくない値のときは)" +
            @"既定の %LOCALAPPDATA%\applet\kasane を使います。Windows 11 の管理者保護で、昇格した画面が別のユーザーとして動いても同じ場所を使うための設定です。" +
            "値の変更は、インストーラーの「設定フォルダの指定」で行います。変更後にサインインし直すと、トレイを含むすべてのアプリに反映されます。"));

        string? raw = Environment.GetEnvironmentVariable(HelperConfig.DataDirEnvironmentVariable);
        string effective = HelperConfig.BaseDirectory;
        bool isDefault = HelperConfig.ResolveBaseDirectory(null) == effective;

        string variable = string.IsNullOrWhiteSpace(raw)
            ? "未設定"
            : isDefault ? $"{raw}(絶対パスではないため無視して、既定の場所を使います)" : raw;

        var variableBox = new TextBox { Name = "DataDirVariable", Text = variable, IsReadOnly = true, Margin = new Thickness(0, 0, 0, 4) };
        var effectiveBox = new TextBox { Name = "DataDirEffective", Text = effective, IsReadOnly = true, Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(new TextBlock { Text = "環境変数 KASANE の値:" });
        panel.Children.Add(variableBox);
        panel.Children.Add(new TextBlock { Text = "いま使っている保存先:" });
        panel.Children.Add(effectiveBox);

        var open = new Button { Name = "DataDirOpenButton", Content = "フォルダを開く", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
        var status = Status("DataDirStatus");
        open.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(effective);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(effective) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                ShowStatus(status, $"開けませんでした: {ex.Message}", true);
            }
        };
        panel.Children.Add(open);
        panel.Children.Add(status);
        AddImportBackupSection(panel);
        return panel;
    }

    /// <summary>
    /// インストール時の取り込み（設定ファイルをドライバのレジストリへ書く）が、前の設定を退避していれば、それを一覧にして、戻せるようにする。
    /// 「戻す」は、退避と今の設定の入れ替え（もう一度押すと元に戻る）。ドライバのレジストリを書くので、管理者権限が要る。
    /// 反映は、キーボードを接続し直したとき（またはPCの再起動後）。
    /// </summary>
    private void AddImportBackupSection(StackPanel panel)
    {
        panel.Children.Add(new TextBlock { Text = "インストール時の取り込みの退避", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 16, 0, 4) });
        panel.Children.Add(Help(
            "インストール時に設定ファイルを取り込むと、ドライバの設定が置き換わり、前の設定が1世代だけ退避されます。ここで、取り込み前の設定に戻せます。" +
            "戻すと今の設定が退避に回るので、もう一度押すと元に戻ります。反映されるのは、キーボードを接続し直したあと(またはPCの再起動後)です。" +
            (_canWrite ? "" : "\n閲覧専用モード(管理者として実行していない)のため、戻すことはできません。")));

        var store = PersistedSettings.OpenLocalMachine();
        List<string> backups;
        try { backups = store.ListBackups(); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            panel.Children.Add(Help($"退避を調べられませんでした: {ex.Message}"));
            return;
        }
        if (backups.Count == 0)
        {
            panel.Children.Add(new TextBlock { Name = "ImportBackupNone", Text = "退避はありません。", Foreground = AppTheme.Palette.SubText });
            return;
        }

        // 接続中のキーボードの型番（M_<ModelKey>）から、どのキーボードの分かを表示する。
        var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (IsOnline)
        {
            try
            {
                foreach (var d in _client!.ListDevices())
                    if (DeviceKeys.ModelKey(d.HardwareId) is uint k) models[DeviceKeys.ModelSubKeyName(k)] = d.HardwareId;
            }
            catch (KbdLayerException) { /* 名前なしで一覧にする */ }
        }

        var status = Status("ImportBackupStatus");
        foreach (var name in backups)
        {
            string label = models.TryGetValue(name, out var hw) ? $"{name}(接続中: {hw})" : name;
            var restore = new Button
            {
                Name = $"RestoreBackup_{name}",
                Content = "取り込み前の設定に戻す",
                IsEnabled = _canWrite,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(8, 2, 8, 2),
            };
            string target = name;
            restore.Click += (_, _) =>
            {
                if (MessageBox.Show(this,
                        $"{target} の設定と、取り込み前の退避を入れ替えます。\n反映には、キーボードの接続し直し(またはPCの再起動)が必要です。\n\nもう一度この操作をすると、元に戻せます。続けますか？",
                        "取り込み前の設定に戻す", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                try
                {
                    ShowStatus(status, store.Restore(target)
                        ? $"{target} を入れ替えました。キーボードを接続し直すと反映されます。"
                        : $"{target} の退避が見つかりません。", false);
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    ShowStatus(status, $"入れ替えられませんでした: {ex.Message}", true);
                }
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(restore);
            panel.Children.Add(row);
        }
        panel.Children.Add(status);
    }

    private static readonly (string Name, string Type, string Subtype, string Identifier, string Jpn, string Kor)[] I8042Presets =
    {
        ("(手入力)", "", "", "", "", ""),
        ("日本語(JIS)", "7", "2", "PCAT_106KEY", "kbd106.dll", ""),
    };

    private const string I8042Key = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\i8042prt\Parameters";

    private UIElement BuildI8042Tab()
    {
        var panel = new StackPanel();
        panel.Children.Add(Help(
            "PS/2(ACPI)キーボードのレイアウトは、レジストリ " + I8042Key + " の値でしか指定できません。" +
            "HIDキーボードでは、これらの値が残っていると、個別の指定が無効になります。\n" +
            "有効にすると、キーボードを接続するたびに(起動時を含む)、次のように書き換えます。\n" +
            "・プロファイルのある型番のキーボード: 下の値を書き込みます(空欄の値は削除します)。\n" +
            "・プロファイルの無いキーボード: OverrideKeyboardType/Subtype/Identifier と LayerDriver JPN/KOR を削除します(OSの既定に戻します)。\n" +
            "無効(既定)のときは何もしません。その間は、下のプロファイルの設定はできませんが、保存済みの内容は残り、有効にしたときに使われます。\n" +
            "これはドライバ全体の設定です。「反映する」を押すとすぐに保存されます(OK・キャンセルとは関係しません)。" +
            "書き換えが行われるのは、次にキーボードを接続したとき(再接続・再起動)からです。"));

        var enabled = new CheckBox { Name = "I8042Enabled", Content = "キーボードの接続時にi8042prtの値を書き換える／初期化する", Margin = new Thickness(0, 0, 0, 8) };
        var specify = new CheckBox { Name = "I8042ProfileEnabled", Content = "現在選択しているキーボードに対する指定(接続時に下の値を書き込む)", Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(enabled);
        panel.Children.Add(specify);

        var preset = new ComboBox { Name = "I8042Preset", Width = 200 };
        foreach (var p in I8042Presets) preset.Items.Add(p.Name);
        preset.SelectedIndex = 0;
        Row(panel, "国・配列プリセット", preset, null, 150);

        TextBox Box(string name, double width) => new() { Name = name, Width = width, FontFamily = new FontFamily("Consolas") };
        var type = Box("I8042Type", 80);
        var subtype = Box("I8042Subtype", 80);
        var identifier = Box("I8042Identifier", 200);
        var jpn = Box("I8042LayerJpn", 200);
        var kor = Box("I8042LayerKor", 200);
        Row(panel, "タイプ", type, "OverrideKeyboardType(0〜65535)", 150);
        Row(panel, "サブタイプ", subtype, "OverrideKeyboardSubtype(0〜65535)", 150);
        Row(panel, "OverrideKeyboardIdentifier", identifier, null, 150);
        Row(panel, "LayerDriver JPN", jpn, "レイアウトDLL名(例: kbd106.dll)", 150);
        Row(panel, "LayerDriver KOR", kor, null, 150);

        // 操作できる範囲: 全体のスイッチ（enabled）がオフのあいだは、下のチェックと値の欄は使われないので無効にする
        // （値は表示したまま。保存済みのプロファイルは消えない）。値の欄は、さらに下のチェックがオンのときだけ操作できる。
        var fields = new UIElement[] { preset, type, subtype, identifier, jpn, kor };
        bool canEdit = IsOnline && _canWrite; // ドライバへ接続でき、書き込める（読み込みに失敗したら下で落とす）
        uint modelKey = 0;
        void SyncControls()
        {
            enabled.IsEnabled = canEdit;
            specify.IsEnabled = canEdit && modelKey != 0 && enabled.IsChecked == true;
            foreach (var f in fields) f.IsEnabled = specify.IsEnabled && specify.IsChecked == true;
        }
        enabled.Checked += (_, _) => SyncControls();
        enabled.Unchecked += (_, _) => SyncControls();
        specify.Checked += (_, _) => SyncControls();
        specify.Unchecked += (_, _) => SyncControls();
        preset.SelectionChanged += (_, _) =>
        {
            if (preset.SelectedIndex <= 0) return; // 手入力: 今の値のまま
            var p = I8042Presets[preset.SelectedIndex];
            type.Text = p.Type; subtype.Text = p.Subtype; identifier.Text = p.Identifier; jpn.Text = p.Jpn; kor.Text = p.Kor;
        };

        var others = new TextBlock { Name = "I8042Others", Foreground = AppTheme.Palette.SubText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        var status = Status("I8042Status");
        var apply = new Button { Name = "I8042ApplyButton", Content = "反映する", IsEnabled = IsOnline && _canWrite, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
        panel.Children.Add(others);
        panel.Children.Add(apply);

        I8042Config? current = null;
        void Show()
        {
            var mine = current!.Profiles.FirstOrDefault(p => p.ModelKey == modelKey);
            enabled.IsChecked = current.Enabled;
            specify.IsChecked = mine != null;
            type.Text = mine?.Type?.ToString() ?? "";
            subtype.Text = mine?.Subtype?.ToString() ?? "";
            identifier.Text = mine?.Identifier ?? "";
            jpn.Text = mine?.LayerJpn ?? "";
            kor.Text = mine?.LayerKor ?? "";
            var rest = current.Profiles.Where(p => p.ModelKey != modelKey).Select(p => p.ModelKey.ToString("X8")).ToList();
            others.Text = rest.Count == 0 ? "ほかに登録された型番はありません。" : $"ほかに登録された型番({rest.Count}件): {string.Join(", ", rest)}(これらは変更しません)";
            SyncControls();
        }

        if (IsOnline)
        {
            try
            {
                modelKey = _client!.GetDeviceScope(_uniqId).ModelKey;
                current = _client.GetI8042Config();
                Show();
                ShowStatus(status, modelKey == 0
                    ? "このキーボードの型番(ModelKey)を取得できないため、プロファイルは指定できません。"
                    : $"このキーボードの型番(ModelKey {modelKey:X8})のプロファイルを表示しています。", modelKey == 0);
            }
            catch (KbdLayerException ex)
            {
                ShowStatus(status, ex.Message, true);
                canEdit = false;
                apply.IsEnabled = false;
                SyncControls();
            }
            if (!_canWrite) panel.Children.Add(Help("閲覧専用モード(管理者として実行していない)のため、変更はできません。"));
        }
        else
        {
            apply.IsEnabled = false;
            SyncControls();
            ShowStatus(status, "ドライバに接続していないため、表示・変更できません。", true);
        }

        apply.Click += (_, _) =>
        {
            if (current == null) return;
            I8042Profile? mine = null;
            if (specify.IsChecked == true)
            {
                if (!TryParseU16(type.Text, out var t) || !TryParseU16(subtype.Text, out var s))
                {
                    ShowStatus(status, "タイプ・サブタイプは0〜65535の整数、または空欄にしてください。", true);
                    return;
                }
                string?[] strs = { identifier.Text.Trim(), jpn.Text.Trim(), kor.Text.Trim() };
                foreach (var v in strs)
                {
                    if (v!.Length >= KbdLayerClient.I8042StrChars || v.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0
                        || v.Contains("..") || v.Any(ch => ch < 0x21 || ch > 0x7E))
                    {
                        ShowStatus(status, $"「{v}」は使えません。{KbdLayerClient.I8042StrChars - 1}文字以内の半角英数字・記号(\\ / : * ? \" < > | と .. は不可)にしてください。", true);
                        return;
                    }
                }
                mine = new I8042Profile(modelKey, t, s, NullIfEmpty(strs[0]), NullIfEmpty(strs[1]), NullIfEmpty(strs[2]));
                if (mine.Type == null && mine.Subtype == null && mine.Identifier == null && mine.LayerJpn == null && mine.LayerKor == null)
                {
                    ShowStatus(status, "値がすべて空欄です。指定しない場合は、チェックを外してください。", true);
                    return;
                }
            }

            var profiles = current.Profiles.Where(p => p.ModelKey != modelKey).ToList();
            if (mine != null) profiles.Add(mine);
            if (profiles.Count > KbdLayerClient.I8042MaxProfiles)
            {
                ShowStatus(status, $"プロファイルは{KbdLayerClient.I8042MaxProfiles}個までです。", true);
                return;
            }
            try
            {
                _client!.SetI8042Config(new I8042Config(enabled.IsChecked == true, profiles));
                current = _client.GetI8042Config();
                Show();
                ShowStatus(status, enabled.IsChecked == true
                    ? "保存しました。次にキーボードを接続したとき(再接続・再起動)から書き換えます。"
                    : "保存しました(無効のため、何も書き換えません)。", false);
            }
            catch (KbdLayerException ex) { ShowStatus(status, ex.Message, true); }
        };

        panel.Children.Add(status);
        return panel;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>空欄はnull（その値は書かない）。0〜65535の整数でなければfalse。</summary>
    private static bool TryParseU16(string text, out uint? value)
    {
        value = null;
        text = text.Trim();
        if (text.Length == 0) return true;
        if (!uint.TryParse(text, out var v) || v > 0xFFFF) return false;
        value = v;
        return true;
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

        // 緊急解除の入力は、ここでドライバへ反映する（空なら変更しない）。不正なら緊急解除タブで止める。
        if (_emergencyCommit?.Invoke() is { } emergencyError)
        {
            Fail("TabEmergency", emergencyError);
            return false;
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
                Fail("TabImeLayer", string.Join("\n", errors));
                return false;
            }
        }

        var helperErrors = _helper.Validate();
        if (helperErrors.Count > 0)
        {
            Fail(helperErrors.Any(e => e.Contains("の規則")) && !helperErrors.Any(e => !e.Contains("の規則")) ? "TabAppRules" : "TabHelper",
                 string.Join("\n", helperErrors));
            return false;
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

        // ヘルパー動作・アプリ別レイヤーも、プロジェクトではなく利用者の設定（トレイが数秒ごとに読む）。変えた分だけ直接保存する。
        if (!_helper.TrySave(out var helperError))
        {
            Fail("TabHelper", $"ヘルパー設定を保存できませんでした: {helperError}");
            return false;
        }

        // IME連動はプロジェクトではなく、このPCの利用者の設定（トレイが数秒ごとに読む）。変えたときだけ保存する。
        bool imeLink = _globalNicolaLink;
        bool showLayer = _showLayerOnIcon.IsChecked == true;
        bool remapOnlyWhileTray = _remapOnlyWhileTray.IsChecked == true;
        int? desktopView = SelectedDesktopViewMode();
        string imeSource = ((ImeSourceMode)Math.Max(0, _imeSourceBox.SelectedIndex)).ToString();
        string colorMode = ((ColorMode)Math.Max(0, _colorModeBox.SelectedIndex)).ToString();
        bool colorChanged = colorMode != _trayConfig.ColorModeName;
        if (colorChanged || imeSource != _trayConfig.ImeSource || imeLink != _trayConfig.ImeLink || showLayer != _trayConfig.ShowLayerOnIcon || remapOnlyWhileTray != _trayConfig.RemapOnlyWhileTrayRuns
            || desktopView != _trayConfig.DesktopViewMode)
        {
            try
            {
                _trayConfig.DesktopViewMode = desktopView;
                _trayConfig.ImeSource = imeSource;
                _trayConfig.ColorModeName = colorMode;
                _trayConfig.ImeLink = imeLink;
                _trayConfig.ShowLayerOnIcon = showLayer;
                _trayConfig.RemapOnlyWhileTrayRuns = remapOnlyWhileTray;
                _trayConfig.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Fail("TabImeLayer", $"トレイの設定を保存できませんでした: {ex.Message}");
                return false;
            }
            if (colorChanged) AppTheme.Apply(_trayConfig.ColorModeValue); // 保存できたら、すぐ全画面に効かせる
        }
        return true;
    }
}
