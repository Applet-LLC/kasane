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
    // 判定方式（TapHoldMode の順）。日本語の表示は短い文だけにし、方式の名前（Permissive Hold）は英語の表示だけに出す（2026-11-08 の用語の決定）。
    private static string[] ModeLabels => new[]
    {
        Loc.T("別のキーを押したら長押し(既定)", "Hold when another key is pressed (default)"),
        Loc.T("別のキーを押して離したら長押し", "Hold when another key is pressed and released (Permissive Hold)"),
        Loc.T("時間内に離せば短押し", "Tap if released in time"),
    };

    private static List<ComboBoxItem> ModeItems() => Items(
        (ModeLabels[0], Loc.T("長押しのキーを押している間に別のキーを押すと、すぐ長押しにします", "Becomes a hold as soon as another key is pressed while the key is down")),
        (ModeLabels[1], Loc.T("別のキーを押して離すまで待ってから、長押しにします", "Becomes a hold once another key is pressed and released")),
        (ModeLabels[2], Loc.T("時間内に離せば短押し、時間を過ぎたら長押しにします", "A tap if released within the time; a hold after it")));

    private static List<ComboBoxItem> NicolaModeItems() => Items(
        (Loc.T("GUI では管理しない", "Not managed by the GUI"), Loc.T("ドライバの今の設定を、そのまま使います", "Keeps the driver's current setting")),
        (Loc.T("この設定を使う", "Use this setting"), Loc.T("適用すると反映されます", "Takes effect when you apply")),
        (Loc.T("使わない", "Don't use"), Loc.T("適用すると、ドライバの設定を消します", "Applying clears the driver's setting")));

    private readonly KeymapProject _project;
    private readonly KbdLayerClient? _client;
    private readonly uint _uniqId;      // 0 = オフライン
    private readonly bool _canWrite;
    private readonly TabControl _tabs = new() { Name = "SettingsTabs" };
    private readonly TextBlock _error = new() { Name = "SettingsError", Foreground = AppTheme.Palette.ErrorText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

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
    private ComboBox _imeSwitchBox = null!; // ヘルパー動作の IME の切り替え方（tray.json の ImeSwitch）
    private ComboBox _languageBox = null!;  // 表示言語（tray.json の UiLanguage）。変更は再起動後に効く。
    /// <summary>表示言語を変えて保存した。呼び出し側が、再起動を案内する。</summary>
    public bool LanguageChanged { get; private set; }
    private ComboBox _colorModeBox = null!;  // 色のモード（tray.json の ColorMode）。OK で保存して、すぐ全画面に効かせる
    private readonly TrayConfig _trayConfig = TrayConfig.Load();
    private TextBlock _nicolaSummary = null!;
    // ヘルパー動作・アプリ別レイヤー（このキーボードの helper.json と、共通の tray.json の Helper。OKで直接保存する）
    private readonly HelperSettingsModel _helper;

    private readonly HelperActionsTab _helperTab;

    /// <summary>OK で、キーマップ（HLP(n) の割り当て）を書き換えたか（ヘルパー動作の削除で「割り当ても外す」を選んだとき）。</summary>
    public bool KeymapChanged { get; private set; }

    /// <summary>OK で参照を外した HLP の番号（メイン画面が、開いている編集画面の作業用コピーからも外す）。</summary>
    public List<int> UnassignedHelperIds { get; } = new();

    /// <param name="layout">ヘルパー動作の削除の確認で、キーの名前を出すため（無ければ番号）。</param>
    public SettingsDialog(KeymapProject project, KbdLayerClient? client, uint onlineUniqId, bool canWrite, Kasane.Layout.KeyboardLayout? layout = null)
    {
        _project = project;
        _client = client;
        _uniqId = client != null ? onlineUniqId : 0;
        _canWrite = canWrite;
        _nicola = project.Nicola?.Clone();
        _globalNicolaLink = _trayConfig.ImeLink;
        _helper = new HelperSettingsModel(project.UniqId != 0 ? project.UniqId : _uniqId, _trayConfig);

        Title = Loc.T("設定", "Settings");
        Width = 760;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        AddTab("TabGeneral", Loc.T("全般", "General"), BuildGeneralTab());
        AddTab("TabFeatures", Loc.T("機能", "Features"), BuildFeaturesTab());
        AddTab("TabTiming", Loc.T("タイミング", "Timing"), BuildTimingTab());
        AddTab("TabImeLayer", Loc.T("IME 連動", "IME link"), BuildImeTab()); // 以前の「NICOLA」タブの内容も、ここにある（2026-10-19）
        _helperTab = new HelperActionsTab(_helper, this, project, layout);
        AddTab("TabHelper", Loc.T("ヘルパー動作", "Helper actions"), _helperTab.Root);
        AddTab("TabAppRules", Loc.T("アプリ別レイヤー", "Per-app layers"), new AppRulesTab(_helper, this).Root);
        AddTab("TabEmergency", Loc.T("緊急解除", "Emergency release"), BuildEmergencyTab());
        AddTab("TabScope", Loc.T("共有範囲", "Sharing scope"), BuildScopeTab());
        AddTab("TabDataDir", Loc.T("保存先", "Data folder"), BuildDataDirTab());
        AddTab("TabI8042", "i8042prt", BuildI8042Tab());
        AddTab("TabDesktop", Loc.T("デスクトップ", "Desktop"), BuildDesktopTab());
        AddTab(AboutTab, Loc.T("バージョン情報", "About"), BuildAboutTab());
        _tabs.SelectionChanged += OnTabChanged;

        var ok = new Button { Name = "OkButton", Content = "OK", IsDefault = true, Width = 90 };
        ok.Click += (_, _) => { if (Commit()) DialogResult = true; };
        var okCancel = new StackPanel { Orientation = Orientation.Horizontal };
        okCancel.Children.Add(ok);
        okCancel.Children.Add(new Button { Name = "CancelButton", Content = Loc.T("キャンセル", "Cancel"), IsCancel = true, Width = 90 });
        // 左に「?」（今のタブの説明をマニュアルで開く）、右に OK・キャンセル。
        var buttons = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var help = HelpButton.Create(CurrentHelpTopic);
        DockPanel.SetDock(help, Dock.Left);
        DockPanel.SetDock(okCancel, Dock.Right);
        buttons.Children.Add(help);
        buttons.Children.Add(okCancel);
        buttons.Children.Add(new Border()); // 間を埋める
        HelpButton.AttachF1(this, CurrentHelpTopic);

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
        panel.Children.Add(new TextBlock { Text = Loc.T("トレイ", "Tray"), FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
        _remapOnlyWhileTray = new CheckBox
        {
            Name = "GeneralRemapOnlyWhileTrayCheck",
            Content = Loc.T("トレイが動いている間だけリマップする", "Remap only while the tray is running"),
            ToolTip = Loc.T("トレイの終了・サインアウト・画面のロックでリマップを OFF にし、トレイの起動とロック解除で ON に戻します",
                            "Turns remapping off when the tray exits, you sign out, or the screen locks, and back on when the tray starts or the screen unlocks"),
            IsChecked = _trayConfig.RemapOnlyWhileTrayRuns,
            Margin = new Thickness(0, 0, 0, 6),
        };
        panel.Children.Add(_remapOnlyWhileTray);

        _showLayerOnIcon = new CheckBox
        {
            Name = "ShowLayerOnIconCheck",
            Content = Loc.T("トレイのアイコンにレイヤー番号を出す", "Show the layer number on the tray icon"),
            ToolTip = Loc.T("基底のレイヤーだけのときは出しません", "Not shown when only the base layer is active"),
            IsChecked = _trayConfig.ShowLayerOnIcon,
            Margin = new Thickness(0, 6, 0, 8),
        };
        panel.Children.Add(_showLayerOnIcon);

        // 色のモード（ライト／ダーク）。既定はライト。OK で、開いている画面と、これから開く画面すべてに効く。
        panel.Children.Add(new TextBlock { Text = Loc.T("画面の色", "Colors"), FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 4) });
        _colorModeBox = new ComboBox { Name = "ColorModeBox", Width = 160, ItemsSource = ColorModeLabels, SelectedIndex = (int)_trayConfig.ColorModeValue };
        Row(panel, Loc.T("色のモード", "Color mode"), _colorModeBox, labelWidth: 120);

        panel.Children.Add(new TextBlock { Text = Loc.T("表示言語", "Display language"), FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 4) });
        _languageBox = new ComboBox { Name = "LanguageBox", Width = 200, ItemsSource = LanguageLabels, SelectedIndex = (int)_trayConfig.UiLanguageValue };
        var languageRow = Row(panel, Loc.T("言語", "Language"), _languageBox,
            Loc.T("自動認識は、Windows の表示言語に合わせます", "Auto follows the Windows display language"), labelWidth: 120);
        // 言語は、変えてもすぐには変わらないので、それだけを欄の右に添える（知らないと、効いていないと思う）。
        languageRow.Children.Add(new TextBlock { Text = Loc.T("再起動後に効きます", "Takes effect after a restart"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Palette.SubText });
        return panel;
    }

    private static string[] LanguageLabels => new[] { Loc.T("自動認識(既定)", "Auto (default)"), "日本語", "English" };

    private static string[] ColorModeLabels => new[] { Loc.T("ライト(既定)", "Light (default)"), Loc.T("ダーク", "Dark") };

    private static string[] DesktopViewLabels => new[]
    {
        Loc.T("通常表示", "Icons"), Loc.T("詳細表示", "Details"), Loc.T("小さなアイコン表示", "Small icons"), Loc.T("リスト表示", "List"), Loc.T("タイル表示", "Tiles"),
    };

    private RadioButton _desktopKeep = null!;
    private readonly RadioButton[] _desktopViewRadios = new RadioButton[5];

    /// <summary>デスクトップ: アイコンの表示方法（のどかの DesktopListView。キーのリマップとは無関係）。OK で tray.json に保存する。</summary>
    private UIElement BuildDesktopTab()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = Loc.T("デスクトップのアイコンの表示方法", "Desktop icon view"), FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });

        _desktopKeep = new RadioButton { Name = "DesktopViewKeep", Content = Loc.T("変えない(既定)", "Don't change (default)"), GroupName = "DesktopView", Margin = new Thickness(0, 2, 0, 2) };
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
        // アイコンの絵は、右上に置く（文字の列とは重ねない外側の Grid で）。
        panel.Children.Add(new TextBlock { Name = "AboutName", Text = AppInfo.DisplayName, FontSize = 26, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Name = "AboutVersion", Text = Loc.T($"バージョン {AppInfo.VersionText}", $"Version {AppInfo.VersionText}"), FontSize = 15, Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(new TextBlock { Name = "AboutBuildTime", Text = Loc.T($"ビルド日時 {AppInfo.BuildTimeText}", $"Build time {AppInfo.BuildTimeText}"), Margin = new Thickness(0, 4, 0, 0) });
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

        var logo = new Image
        {
            Name = "AboutLogo", Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Resources/about.png")),
            Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 8, 0),
        };
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(logo, System.Windows.Media.BitmapScalingMode.HighQuality);
        var grid = new Grid();
        grid.Children.Add(panel);
        grid.Children.Add(logo);
        return grid;
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

    /// <param name="tip">欄の意味（ツールチップ。ラベルと欄の両方に付ける）。画面には説明を出さない（2026-11-08、UI の見直し。詳しくは「?」のマニュアル）。</param>
    private static StackPanel Row(Panel parent, string label, UIElement input, string? tip = null, double labelWidth = 120)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(new TextBlock { Text = label, Width = labelWidth, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip });
        if (tip != null && input is FrameworkElement fe && fe.ToolTip == null) fe.ToolTip = tip;
        row.Children.Add(input);
        parent.Children.Add(row);
        return row;
    }

    // 選択肢ごとにツールチップを付けたコンボボックスの項目（選択肢の文は短くし、補足はツールチップへ）。
    private static List<ComboBoxItem> Items(params (string Text, string? Tip)[] items) =>
        items.Select(i => new ComboBoxItem { Content = i.Text, ToolTip = i.Tip }).ToList();

    // 今のタブの、マニュアルの節（「?」と F1）。
    private string CurrentHelpTopic() => (_tabs.SelectedItem as TabItem)?.Name switch
    {
        "TabFeatures" => HelpTopics.SettingsFeatures,
        "TabTiming" => HelpTopics.SettingsTiming,
        "TabImeLayer" => HelpTopics.ImeLayer,
        "TabHelper" => HelpTopics.HelperTab,
        "TabAppRules" => HelpTopics.AppRules,
        "TabEmergency" => HelpTopics.Emergency,
        "TabScope" => HelpTopics.SettingsScope,
        "TabDataDir" => HelpTopics.SettingsDataDir,
        "TabI8042" => HelpTopics.SettingsI8042,
        "TabDesktop" => HelpTopics.SettingsDesktop,
        AboutTab => HelpTopics.SettingsAbout,
        _ => HelpTopics.SettingsGeneral,
    };

    private static TextBlock Status(string name) =>
        new() { Name = name, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    private static void ShowStatus(TextBlock block, string text, bool isError)
    {
        block.Foreground = isError ? AppTheme.Palette.ErrorText : AppTheme.Palette.OkText;
        block.Text = text;
    }

    // ------------------------------------------------------------------
    // 機能
    // ------------------------------------------------------------------

    private UIElement BuildFeaturesTab()
    {
        var panel = new StackPanel();
        string current = "";
        uint initial = KbdLayerClient.FeatureAll;
        if (IsOnline)
        {
            try
            {
                uint f = _client!.GetFeatures(_uniqId);
                initial = f;
                current = Loc.T(($"キーボードの現在の値: マクロ {OnOff(f, KbdLayerClient.FeatureMacro)} / コンボ {OnOff(f, KbdLayerClient.FeatureCombo)} / " +
                          $"タップダンス {OnOff(f, KbdLayerClient.FeatureTapDance)} / キー置換 {OnOff(f, KbdLayerClient.FeatureKeyOverride)}"),
                          ($"Current values on the keyboard: Macro {OnOff(f, KbdLayerClient.FeatureMacro)} / Combo {OnOff(f, KbdLayerClient.FeatureCombo)} / " +
                          $"Tap dance {OnOff(f, KbdLayerClient.FeatureTapDance)} / Key override {OnOff(f, KbdLayerClient.FeatureKeyOverride)}"));
            }
            catch (KbdLayerException) { current = Loc.T("(このドライバは機能のON/OFFに対応していません)", "(This driver doesn't support turning features on or off)"); }
        }
        _initialFeatures = _project.EnabledFeatures ?? initial;

        var defs = new (string Name, string Label, uint Bit)[]
        {
            ("FeatureMacro", Loc.T("マクロ", "Macro"), KbdLayerClient.FeatureMacro),
            ("FeatureCombo", Loc.T("コンボ", "Combo"), KbdLayerClient.FeatureCombo),
            ("FeatureTapDance", Loc.T("タップダンス", "Tap dance"), KbdLayerClient.FeatureTapDance),
            ("FeatureKeyOverride", Loc.T("キー置換", "Key override"), KbdLayerClient.FeatureKeyOverride),
        };
        for (int i = 0; i < defs.Length; i++)
        {
            _featureChecks[i] = new CheckBox
            {
                Name = defs[i].Name,
                Content = defs[i].Label,
                Tag = defs[i].Bit,
                ToolTip = Loc.T("OFF にしても、中身は消えません。適用すると反映されます", "Turning it off keeps the definitions. Takes effect when you apply"),
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
                currentText = Loc.T($"\nキーボードの現在の値: TapTerm {current.TapTermMs} / ComboTerm {current.ComboTermMs} / TapDanceTerm {current.TapDanceTermMs}", $"\nCurrent values on the keyboard: TapTerm {current.TapTermMs} / ComboTerm {current.ComboTermMs} / TapDanceTerm {current.TapDanceTermMs}");
                if (current.HasTapHoldOptions)
                    currentText += Loc.T($" / 判定方式 {current.TapHoldMode} / Quick Tap {QuickTapText(current.QuickTapTermMs)} / TT {current.TappingToggle}回", $" / Tap-hold mode {current.TapHoldMode} / Quick Tap {QuickTapText(current.QuickTapTermMs)} / TT {current.TappingToggle} taps");
                else
                    currentText += Loc.T("\n(このドライバは判定方式・Quick Tap・TTの回数に対応していません。設定しても効きません)", "\n(This driver doesn't support the tap-hold mode, Quick Tap, or the TT count. These settings have no effect)");
            }
            catch (KbdLayerException) { /* 古いドライバ等。現在値の表示を省くだけ */ }
        }
        // キーボードの今の値（状態の文）。説明の段落は出さない（単位・既定値は欄のツールチップと「?」のマニュアル）。
        if (currentText.Length > 0) panel.Children.Add(Help(currentText.TrimStart('\n')));

        TextBox Field(string name, string label, string help, uint value)
        {
            var box = new TextBox { Name = name, Text = value.ToString(), Width = 80 };
            var row = Row(panel, label, box, help);
            row.Children.Add(new TextBlock { Text = "ms", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = AppTheme.Palette.SubText });
            return box;
        }
        _tap = Field("TapTermBox", "TapTerm", Loc.T("MT/LT で、これより長く押すと長押し(0 = 既定の 200)", "MT/LT: pressing longer than this is a hold (0 = default 200)"), _project.TapTermMs);
        _combo = Field("ComboTermBox", "ComboTerm", Loc.T("コンボの 2 つのキーを押す間隔の上限(0 = 既定の 50)", "Maximum interval between the two combo keys (0 = default 50)"), _project.ComboTermMs);
        _tapDance = Field("TapDanceTermBox", "TapDanceTerm", Loc.T("タップダンスのタップの間隔と長押しの判定(0 = 既定の 200)", "Tap dance tap interval and hold detection (0 = default 200)"), _project.TapDanceTermMs);

        _mode = new ComboBox { Name = "TapHoldModeBox", Width = 330, ItemsSource = ModeItems() };
        _mode.SelectedIndex = _project.TapHoldMode < ModeLabels.Length ? (int)_project.TapHoldMode : 0;
        Row(panel, Loc.T("判定方式", "Tap-hold mode"), _mode, Loc.T("MT/LT の判定(タップダンスは常に既定の方式)", "For MT/LT (tap dance always uses the default)"));

        bool quickOff = _project.QuickTapTermMs == KbdLayerClient.QuickTapTermDisabled;
        _quick = new TextBox { Name = "QuickTapTermBox", Text = quickOff ? "0" : _project.QuickTapTermMs.ToString(), Width = 80, IsEnabled = !quickOff };
        _quickEnabled = new CheckBox { Name = "QuickTapEnabled", Content = Loc.T("使う", "Enable"), IsChecked = !quickOff, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _quickEnabled.Checked += (_, _) => _quick.IsEnabled = true;
        _quickEnabled.Unchecked += (_, _) => _quick.IsEnabled = false;
        var quickRow = new StackPanel { Orientation = Orientation.Horizontal };
        quickRow.Children.Add(_quick);
        quickRow.Children.Add(_quickEnabled);
        Row(panel, "Quick Tap", quickRow, Loc.T("短押しの直後に押し続けるとリピートする(0 = TapTerm と同じ)", "Pressing and holding right after a tap repeats the key (0 = same as TapTerm)"));

        _tt = new TextBox { Name = "TappingToggleBox", Text = _project.TappingToggle.ToString(), Width = 80 };
        Row(panel, Loc.T("TT の回数", "TT count"), _tt, Loc.T("TT(n) を固定するのに続けてタップする回数(0 = 既定の 5)", "Taps in a row to lock TT(n) (0 = default 5)"));
        return panel;
    }

    private static string QuickTapText(uint ms) =>
        ms == KbdLayerClient.QuickTapTermDisabled ? Loc.T("使わない", "Off") : ms == 0 ? Loc.T("TapTermと同じ", "Same as TapTerm") : $"{ms}ms";

    // ------------------------------------------------------------------
    // NICOLA
    // ------------------------------------------------------------------

    // 「IME連動」タブ: 上に「IMEのON/OFFとレイヤーの連動」（NICOLAを使わなくても使える）、下に「NICOLA（親指シフト）」の設定。
    // 以前は別々のタブ（IME連動・NICOLA）だった。NICOLAを使わない人も、IME連動をここだけで設定できる。
    private UIElement BuildImeTab()
    {
        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = Loc.T("IME の ON/OFF とレイヤーの連動", "IME on/off and layer link"), FontWeight = FontWeights.Bold, FontSize = 14, Margin = new Thickness(0, 0, 0, 6) });
        root.Children.Add(new ImeLayerTab(_helper, this).Root);
        root.Children.Add(new Separator { Margin = new Thickness(0, 14, 0, 10) });
        root.Children.Add(new TextBlock { Text = Loc.T("IME の状態の読み取りと切り替え", "Reading and switching the IME state"), FontWeight = FontWeights.Bold, FontSize = 14, Margin = new Thickness(0, 0, 0, 6) });
        root.Children.Add(BuildImeSourceRow());
        root.Children.Add(BuildImeSwitchRow());
        root.Children.Add(new Separator { Margin = new Thickness(0, 14, 0, 10) });
        root.Children.Add(new TextBlock { Text = Loc.T("NICOLA(親指シフト)", "NICOLA (thumb shift)"), FontWeight = FontWeights.Bold, FontSize = 14, Margin = new Thickness(0, 0, 0, 6) });
        root.Children.Add(BuildNicolaTab());
        return root;
    }

    // IME の状態の取得方法。IMM32 は、アプリの IME ウィンドウへの問い合わせ。TSF は、天狼（sirius）のフックの DLL が各アプリの中で読んだ値
    // （IMM32 で取れないアプリ・TSF だけのアプリの補い）。フックは全アプリに DLL を読み込ませるので、Imm32 を選ぶと入れない。
    // 選択肢の文は短くし、補足はツールチップへ（2026-11-08、UI の見直し。詳しくは「?」で開く 10 章）。
    private static List<ComboBoxItem> ImeSourceItems() => Items(
        (Loc.T("IMM32 のみ(既定)", "IMM32 only (default)"), Loc.T("Windows の標準の方法で読みます。DLL は入れません", "Reads with the standard Windows method. No DLL is loaded")),
        (Loc.T("IMM32 を先に、取れなければ TSF", "IMM32 first, then TSF"), Loc.T("TSF で読むために、DLL を各アプリに読み込ませます", "Loads a DLL into each app to read with TSF")),
        (Loc.T("TSF を先に、取れなければ IMM32", "TSF first, then IMM32"), Loc.T("TSF で読むために、DLL を各アプリに読み込ませます", "Loads a DLL into each app to read with TSF")));

    private UIElement BuildImeSourceRow()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        _imeSourceBox = new ComboBox { Name = "ImeSourceBox", Width = 360, ItemsSource = ImeSourceItems(), SelectedIndex = (int)_trayConfig.ImeSourceMode };
        Row(panel, Loc.T("IME の状態の取得方法", "How to get the IME state"), _imeSourceBox,
            Loc.T("IME 連動と、ヘルパー動作の IME 操作で使います", "Used by the IME link and by the IME operations of helper actions"), labelWidth: 170);
        return panel;
    }

    // ヘルパー動作の IME の切り替え方（ImeSwitch の順。Auto / ImeKeys / Henkan / Toggle / Imm）。
    private static List<ComboBoxItem> ImeSwitchItems() => Items(
        (Loc.T("自動(既定)", "Automatic (default)"), Loc.T("ふだんは IMM32。トレイが管理者として動いているときは、Alt+半角/全角を送ります", "IMM32 normally; Alt+Hankaku/Zenkaku while the tray runs as administrator")),
        (Loc.T("IME オン / オフ キー", "IME on / IME off keys"), Loc.T("ON 用と OFF 用のキーを送ります。OS の設定は要りません", "Sends separate keys for on and off. No OS setting is needed")),
        (Loc.T("変換 / 無変換", "Henkan / Muhenkan"), Loc.T("Microsoft IME で、変換に IME-オン、無変換に IME-オフを割り当ててから選びます", "Assign IME-On to Henkan and IME-Off to Muhenkan in Microsoft IME first")),
        (Loc.T("Alt+半角/全角で切り替え", "Toggle with Alt+Hankaku/Zenkaku"), Loc.T("今の状態を読めないアプリには送りません", "Not sent to apps whose IME state can't be read")),
        ("IMM32", Loc.T("キーを送らず、IMM32 で切り替えます", "Switches with IMM32 without sending keys")));

    private UIElement BuildImeSwitchRow()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        _imeSwitchBox = new ComboBox { Name = "ImeSwitchBox", Width = 360, ItemsSource = ImeSwitchItems(), SelectedIndex = (int)_trayConfig.ImeSwitchValue };
        Row(panel, Loc.T("IME の切り替え方", "How to switch the IME"), _imeSwitchBox,
            Loc.T("ヘルパー動作で IME を ON/OFF にする方法", "How helper actions turn the IME on or off"), labelWidth: 170);
        return panel;
    }

    private UIElement BuildNicolaTab()
    {
        var panel = new StackPanel();

        if (IsOnline)
        {
            string driverText;
            try { driverText = NicolaService.DescribeBlob(_client!.GetNicola(_uniqId)); }
            catch (KbdLayerException ex) { driverText = ex.Message; }
            panel.Children.Add(new TextBlock { Name = "NicolaDriverStatus", Text = Loc.T("キーボードの現在の設定: ", "Current setting on the keyboard: ") + driverText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        }

        // IME連動（NICOLA）は、このキーボードごとか、全キーボード共通かを選べる（キーボードごとの設定が先）。
        var linkPanel = new NicolaLinkPanel(_helper, this, () => _globalNicolaLink, v => _globalNicolaLink = v, () => RefreshNicola());
        _imeLink = linkPanel.LinkCheck;
        panel.Children.Add(linkPanel.Root);

        _nicolaMode = new ComboBox { Name = "NicolaModeBox", Width = 360, ItemsSource = NicolaModeItems() };
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
        Btn("NicolaEditButton", Loc.T("表を編集...", "Edit table..."), (_, _) =>
        {
            var dlg = new NicolaEditorWindow(_nicola ?? NicolaService.CreateStandard()) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            _nicola = dlg.Result;
            _nicolaMode.SelectedIndex = 1;
            RefreshNicola();
        });
        Btn("NicolaLoadButton", Loc.T("nicola.json を読み込む...", "Load nicola.json..."), (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "NICOLA (*.json)|*.json", Title = Loc.T("nicola.json を開く", "Open nicola.json"), InitialDirectory = DialogDefaults.DataFolder, ClientGuid = DialogDefaults.Nicola };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                _nicola = NicolaService.Load(dlg.FileName);
                _nicolaMode.SelectedIndex = 1;
                RefreshNicola();
            }
            catch (Exception ex) { ShowStatus(_nicolaSummary, Loc.T($"読み込めませんでした: {ex.Message}", $"Couldn't load: {ex.Message}"), true); }
        });
        Btn("NicolaSaveButton", Loc.T("nicola.json に書き出す...", "Export to nicola.json..."), (_, _) =>
        {
            if (_nicola == null) { ShowStatus(_nicolaSummary, Loc.T("書き出す設定がありません。「表を編集...」か「規格書の配列にする」で作ってください。", "There is no setting to export. Create one with \"Edit table...\" or \"Use standard layout\"."), true); return; }
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "NICOLA (*.json)|*.json", FileName = "nicola.json", Title = Loc.T("nicola.json に書き出す", "Export to nicola.json"), InitialDirectory = DialogDefaults.DataFolder, ClientGuid = DialogDefaults.Nicola };
            if (dlg.ShowDialog(this) != true) return;
            try { NicolaService.Save(_nicola, dlg.FileName); RefreshNicola(); }
            catch (Exception ex) { ShowStatus(_nicolaSummary, Loc.T($"書き出せませんでした: {ex.Message}", $"Couldn't export: {ex.Message}"), true); }
        });
        Btn("NicolaStandardButton", Loc.T("規格書の配列にする", "Use standard layout"), (_, _) =>
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
                string text = Loc.T(($"{(_nicola.Output == "jis-kana" ? "JISかな入力" : "ローマ字入力")}、レイヤー{_nicola.Layer}、" +
                              $"文字キー{_nicola.Keys.Count}個、親指 {_nicola.ThumbLeft} / {_nicola.ThumbRight}" +
                              (_nicola.Enabled ? "" : "(判定しない設定)")),
                              ($"{(_nicola.Output == "jis-kana" ? "JIS kana input" : "Romaji input")}, layer {_nicola.Layer}, " +
                              $"{_nicola.Keys.Count} character keys, thumbs {_nicola.ThumbLeft} / {_nicola.ThumbRight}" +
                              (_nicola.Enabled ? "" : " (detection off)")));
                if (_nicola.Layer >= _project.Layers.Count && _imeLink.IsChecked != true)
                    warnings.Add(Loc.T($"レイヤー{_nicola.Layer}はキーマップにありません。IME 連動を使わない場合は、そのレイヤーに切り替えるキー(TG({_nicola.Layer}) など)を置いてください。", $"Layer {_nicola.Layer} is not in the keymap. If you don't use the IME link, place a key that switches to that layer (such as TG({_nicola.Layer}))."));
                if (errors.Count > 0) ShowStatus(_nicolaSummary, text + "\n" + string.Join("\n", errors), true);
                else ShowStatus(_nicolaSummary, text + (warnings.Count > 0 ? Loc.T("\n注意: ", "\nNote: ") + string.Join("\n", warnings) : ""), false);
                break;
            case 2:
                ShowStatus(_nicolaSummary, Loc.T("適用すると、キーボードのNICOLAの設定を消します。", "Applying clears the keyboard's NICOLA setting."), false);
                break;
            default:
                ShowStatus(_nicolaSummary, Loc.T("適用しても、キーボードのNICOLAの設定は変わりません。", "Applying leaves the keyboard's NICOLA setting unchanged."), false);
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
        // 説明の段落は出さない（何のための設定か・いつ反映されるかは「?」の 7 章）。押し続ける秒数と個数だけを、組み合わせの見出しに添える。
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T($"{KbdLayerClient.EmergencyMinKeys}〜{KbdLayerClient.EmergencyMaxKeys} 個のキーを {KbdLayerClient.EmergencyHoldMs / 1000} 秒押し続けると、全キーボードのリマップが OFF になります",
                         $"Holding {KbdLayerClient.EmergencyMinKeys} to {KbdLayerClient.EmergencyMaxKeys} keys for {KbdLayerClient.EmergencyHoldMs / 1000} seconds turns remapping off on all keyboards"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        bool writable = IsOnline && _canWrite;
        var boxes = new TextBox[KbdLayerClient.EmergencyMaxKeys];
        // 6個分は横に長いので、縦に並べる。
        var grid = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 6), IsEnabled = writable };
        for (int i = 0; i < boxes.Length; i++)
        {
            boxes[i] = new TextBox { Name = $"EmergencyKey{i + 1}", Width = 100, FontFamily = new FontFamily("Consolas") };
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            line.Children.Add(new TextBlock { Text = Loc.T($"キー{i + 1}", $"Key {i + 1}"), Width = 44, VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(KeycodeField.Wrap(boxes[i], allowMods: false));
            grid.Children.Add(line);
        }
        Row(panel, Loc.T("組み合わせ", "Combination"), grid, null, 90);

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
                ShowStatus(status, Loc.T("ドライバの現在の組み合わせを表示しています。", "Showing the driver's current combination."), false);
            }
            catch (KbdLayerException ex) { ShowStatus(status, ex.Message, true); }
        }
        else
        {
            Fill(DefaultEmergencyKeys);
            ShowStatus(status, Loc.T("ドライバに接続していないため、変更できません(表示は既定の組み合わせです)。", "Not connected to the driver, so changes aren't possible (showing the default combination)."), true);
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
                    error = Loc.T($"「{t}」は単純なキー(KC_*)にしてください。", $"\"{t}\" must be a plain key (KC_*).");
                    return false;
                }
                if (scanCodes.Contains(a.Param2))
                {
                    error = Loc.T($"「{t}」が2回指定されています。", $"\"{t}\" is specified twice.");
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
            if (keys.Count < KbdLayerClient.EmergencyMinKeys) return Loc.T($"キーは{KbdLayerClient.EmergencyMinKeys}個以上指定してください。", $"Specify at least {KbdLayerClient.EmergencyMinKeys} keys.");
            if (applied != null && applied.SequenceEqual(keys)) return null;
            try
            {
                _client!.SetEmergencyRelease(keys);
                applied = keys;
                ShowStatus(status, Loc.T($"緊急解除の組み合わせを {string.Join(" + ", boxes.Select(b => b.Text.Trim()).Where(s => s.Length > 0))} にしました。", $"Emergency release combination set to {string.Join(" + ", boxes.Select(b => b.Text.Trim()).Where(s => s.Length > 0))}."), false);
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
        var check = new Button { Name = "EmergencyCheckButton", Content = Loc.T("同時押しを試す...", "Test simultaneous press..."), Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 2, 8, 2) };
        check.Click += (_, _) =>
        {
            if (!TryCollect(out var keys, out var error)) { ShowStatus(status, error!, true); return; }
            if (keys.Count < KbdLayerClient.EmergencyMinKeys)
            {
                ShowStatus(status, Loc.T($"キーは{KbdLayerClient.EmergencyMinKeys}個以上指定してください。", $"Specify at least {KbdLayerClient.EmergencyMinKeys} keys."), true);
                return;
            }
            // 管理者で、ドライバに接続しているときは、選んでいるキーボードの入力を、ドライバから直接受ける。
            var dlg = new EmergencyCheckWindow(keys, IsOnline && _canWrite ? _uniqId : 0) { Owner = this };
            dlg.ShowDialog();
            ShowStatus(status, dlg.Succeeded
                ? Loc.T("この組み合わせは同時に検出できました。", "This combination was detected simultaneously.")
                : Loc.T("全部のキーを同時に検出できませんでした。別の組み合わせを試してください。", "Not all keys were detected at once. Try a different combination."), !dlg.Succeeded);
        };
        var clear = new Button { Name = "EmergencyClearButton", Content = Loc.T("空にする", "Clear"), IsEnabled = writable, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 2, 8, 2) };
        clear.Click += (_, _) =>
        {
            Fill(Array.Empty<string>());
            ShowStatus(status, Loc.T("入力欄を空にしました。空のままでは、緊急解除の組み合わせは変わりません。", "Cleared the fields. If left empty, the emergency release combination stays unchanged."), false);
        };
        var reset = new Button { Name = "EmergencyDefaultButton", Content = Loc.T("既定に戻す", "Reset to default"), IsEnabled = writable, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 2, 8, 2),
                                 ToolTip = Loc.T("左右の Ctrl と、左右の Shift にします", "Both Ctrl keys and both Shift keys") };
        reset.Click += (_, _) =>
        {
            Fill(DefaultEmergencyKeys);
            ShowStatus(status, Loc.T("既定の組み合わせを入れました。タブを移るか OK を押すと反映されます。", "Default combination entered. It takes effect when you switch tabs or press OK."), false);
        };
        buttons.Children.Add(check);
        buttons.Children.Add(clear);
        buttons.Children.Add(reset);
        panel.Children.Add(buttons);
        // 管理者で、ドライバに接続していないと、「同時押しを試す」は低レベルキーフックで見る。その制限は、画面を開く前に、このタブで分かるようにする。
        if (!writable)
        {
            var limit = Help(Loc.T("「同時押しを試す」は、管理者として再起動してから使ってください。", "Restart as administrator before using \"Test simultaneous press\"."));
            limit.Name = "EmergencyHookLimitNote";
            limit.Foreground = AppTheme.Palette.ErrorText;
            panel.Children.Add(limit);
        }
        if (IsOnline && !_canWrite)
            panel.Children.Add(Help(Loc.T("閲覧専用モードのため、変更できません。", "Read-only mode, so changes aren't possible.")));
        panel.Children.Add(status);
        return panel;
    }

    // ------------------------------------------------------------------
    // 共有範囲（すぐに反映）
    // ------------------------------------------------------------------

    private UIElement BuildScopeTab()
    {
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock { Text = Loc.T("設定の共有範囲", "Settings sharing scope"), FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });

        var model = new RadioButton { Name = "ScopeModel", Content = Loc.T("同じ型番で共有", "Share across the same model"), GroupName = "Scope", Margin = new Thickness(0, 3, 0, 3) };
        var instance = new RadioButton { Name = "ScopeInstance", Content = Loc.T("この1台だけ", "This keyboard only"), GroupName = "Scope", Margin = new Thickness(0, 3, 0, 3) };
        var global = new RadioButton { Name = "ScopeGlobal", Content = Loc.T("すべてのキーボードで共有", "Share across all keyboards"), GroupName = "Scope", Margin = new Thickness(0, 3, 0, 3) };
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
                KbdLayerClient.BindingInstance => Loc.T("この1台だけ", "This keyboard only"),
                KbdLayerClient.BindingGlobal => Loc.T("すべてのキーボードで共有", "Share across all keyboards"),
                _ => Loc.T("同じ型番で共有", "Share across the same model"),
            };
            ShowStatus(status, Loc.T($"現在: {name}(InstanceKey {s.InstanceKey:X8} / ModelKey {s.ModelKey:X8})", $"Current: {name} (InstanceKey {s.InstanceKey:X8} / ModelKey {s.ModelKey:X8})"), false);
        }

        var apply = new Button { Name = "ScopeApplyButton", Content = Loc.T("変更する", "Change"), IsEnabled = IsOnline && _canWrite, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(8, 2, 8, 2),
                                 ToolTip = Loc.T("すぐに反映されます。今の設定を変更先へ写し、変更先にある設定は上書きします", "Takes effect immediately. The current settings are copied to the new scope, overwriting what is there") };
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
            if (!_canWrite) panel.Children.Add(Help(Loc.T("閲覧専用モードのため、変更できません。", "Read-only mode, so changes aren't possible.")));
        }
        else
        {
            model.IsEnabled = instance.IsEnabled = global.IsEnabled = false;
            ShowStatus(status, Loc.T("ドライバに接続していないため、表示・変更できません。", "Not connected to the driver, so it can't be shown or changed."), true);
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
            Loc.T(($"型番の違うキーボードが{models.Count}種類接続されています。\n\n" +
                   "「すべてのキーボードで共有」にすると、配列の違うキーボード(JISとテンキーなど)では、意図しないキーに割り当てられることがあります。\n\n" +
                   "共有にしてよいですか。"),
                  ($"{models.Count} different keyboard models are connected.\n\n" +
                   "If you choose \"Share across all keyboards\", keyboards with different layouts (such as JIS and a numeric keypad) may get assignments to unintended keys.\n\n" +
                   "Share anyway?")),
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
        string? raw = Environment.GetEnvironmentVariable(HelperConfig.DataDirEnvironmentVariable);
        string effective = HelperConfig.BaseDirectory;
        bool isDefault = HelperConfig.ResolveBaseDirectory(null) == effective;

        string variable = string.IsNullOrWhiteSpace(raw)
            ? Loc.T("未設定", "Not set")
            : isDefault ? Loc.T($"{raw}(絶対パスではないため無視して、既定の場所を使います)", $"{raw} (ignored because it is not an absolute path; the default location is used)") : raw;

        var variableBox = new TextBox { Name = "DataDirVariable", Text = variable, IsReadOnly = true, Margin = new Thickness(0, 0, 0, 4) };
        var effectiveBox = new TextBox { Name = "DataDirEffective", Text = effective, IsReadOnly = true, Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(new TextBlock { Text = Loc.T("環境変数 KASANE の値:", "Value of the KASANE environment variable:") });
        panel.Children.Add(variableBox);
        panel.Children.Add(new TextBlock { Text = Loc.T("いま使っている保存先:", "Folder in use:") });
        panel.Children.Add(effectiveBox);

        var open = new Button { Name = "DataDirOpenButton", Content = Loc.T("フォルダを開く", "Open folder"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
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
                ShowStatus(status, Loc.T($"開けませんでした: {ex.Message}", $"Couldn't open: {ex.Message}"), true);
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
        panel.Children.Add(new TextBlock { Text = Loc.T("インストール時の取り込みの退避", "Backup from install-time import"), FontWeight = FontWeights.Bold, Margin = new Thickness(0, 16, 0, 4) });
        if (!_canWrite) panel.Children.Add(Help(Loc.T("閲覧専用モードのため、戻すことはできません。", "Read-only mode, so restoring isn't possible.")));

        var store = PersistedSettings.OpenLocalMachine();
        List<string> backups;
        try { backups = store.ListBackups(); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            panel.Children.Add(Help(Loc.T($"退避を調べられませんでした: {ex.Message}", $"Couldn't check the backups: {ex.Message}")));
            return;
        }
        if (backups.Count == 0)
        {
            panel.Children.Add(new TextBlock { Name = "ImportBackupNone", Text = Loc.T("退避はありません。", "No backups."), Foreground = AppTheme.Palette.SubText });
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
            string label = models.TryGetValue(name, out var hw) ? Loc.T($"{name}(接続中: {hw})", $"{name} (connected: {hw})") : name;
            var restore = new Button
            {
                Name = $"RestoreBackup_{name}",
                Content = Loc.T("取り込み前の設定に戻す", "Restore pre-import settings"),
                ToolTip = Loc.T("退避と今の設定を入れ替えます。キーボードを接続し直すと反映され、もう一度押すと元に戻ります", "Swaps the backup with the current settings. Takes effect after you reconnect the keyboard; press again to undo"),
                IsEnabled = _canWrite,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(8, 2, 8, 2),
            };
            string target = name;
            restore.Click += (_, _) =>
            {
                if (MessageBox.Show(this,
                        Loc.T($"{target} の設定と、取り込み前の退避を入れ替えます。\n反映には、キーボードの接続し直し(またはPCの再起動)が必要です。続けますか？", $"This swaps the settings of {target} with the backup from before the import.\nReconnect the keyboard (or restart the PC) for it to take effect. Continue?"),
                        Loc.T("取り込み前の設定に戻す", "Restore pre-import settings"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                try
                {
                    ShowStatus(status, store.Restore(target)
                        ? Loc.T($"{target} を入れ替えました。キーボードを接続し直すと反映されます。", $"Swapped {target}. It takes effect when you reconnect the keyboard.")
                        : Loc.T($"{target} の退避が見つかりません。", $"No backup found for {target}."), false);
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    ShowStatus(status, Loc.T($"入れ替えられませんでした: {ex.Message}", $"Couldn't swap: {ex.Message}"), true);
                }
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(restore);
            panel.Children.Add(row);
        }
        panel.Children.Add(status);
    }

    private static (string Name, string Type, string Subtype, string Identifier, string Jpn, string Kor)[] I8042Presets => new[]
    {
        (Loc.T("(手入力)", "(Manual)"), "", "", "", "", ""),
        (Loc.T("日本語(JIS)", "Japanese (JIS)"), "7", "2", "PCAT_106KEY", "kbd106.dll", ""),
    };

    private const string I8042Key = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\i8042prt\Parameters";

    private UIElement BuildI8042Tab()
    {
        var panel = new StackPanel();
        var enabled = new CheckBox
        {
            Name = "I8042Enabled", Content = Loc.T("接続時に、i8042prtの値を初期化する", "Reset the i8042prt values on connection"), Margin = new Thickness(0, 0, 0, 8),
            ToolTip = Loc.T(I8042Key + " の値を、キーボードを接続するたびに書き換えます。プロファイルの無い型番は、OS の既定に戻します", "Rewrites the values under " + I8042Key + " each time a keyboard is connected. Models without a profile are reset to the OS default"),
        };
        var specify = new CheckBox
        {
            Name = "I8042ProfileEnabled", Content = Loc.T("このキーボードでは以下の設定とする", "Use the settings below for this keyboard"), Margin = new Thickness(0, 0, 0, 4),
            ToolTip = Loc.T("接続時に、下の値を書き込みます", "Writes the values below on connection"),
        };
        panel.Children.Add(enabled);
        panel.Children.Add(specify);

        var preset = new ComboBox { Name = "I8042Preset", Width = 200 };
        foreach (var p in I8042Presets) preset.Items.Add(p.Name);
        preset.SelectedIndex = 0;
        Row(panel, Loc.T("国・配列プリセット", "Region/layout preset"), preset, null, 150);

        TextBox Box(string name, double width) => new() { Name = name, Width = width, FontFamily = new FontFamily("Consolas") };
        var type = Box("I8042Type", 80);
        var subtype = Box("I8042Subtype", 80);
        var identifier = Box("I8042Identifier", 200);
        var jpn = Box("I8042LayerJpn", 200);
        var kor = Box("I8042LayerKor", 200);
        Row(panel, Loc.T("タイプ", "Type"), type, Loc.T("OverrideKeyboardType(0〜65535)", "OverrideKeyboardType (0-65535)"), 150);
        Row(panel, Loc.T("サブタイプ", "Subtype"), subtype, Loc.T("OverrideKeyboardSubtype(0〜65535)", "OverrideKeyboardSubtype (0-65535)"), 150);
        Row(panel, "OverrideKeyboardIdentifier", identifier, null, 150);
        Row(panel, "LayerDriver JPN", jpn, Loc.T("レイアウトDLL名(例: kbd106.dll)", "Layout DLL name (e.g. kbd106.dll)"), 150);
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
        // 「適用」は「キーボードに適用」の操作だけに使う（2026-11-08 の用語の決定）。ここは、すぐにレジストリへ保存するボタン。
        var apply = new Button { Name = "I8042ApplyButton", Content = Loc.T("保存する", "Save"), IsEnabled = IsOnline && _canWrite, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8, 2, 8, 2),
                                 ToolTip = Loc.T("すぐに保存します。書き換えは、次にキーボードを接続したときからです", "Saves immediately. Rewriting starts the next time a keyboard is connected") };
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
            others.Text = rest.Count == 0 ? Loc.T("ほかに登録された型番はありません。", "No other models are registered.") : Loc.T($"ほかに登録された型番({rest.Count}件): {string.Join(", ", rest)}(これらは変更しません)", $"Other registered models ({rest.Count}): {string.Join(", ", rest)} (these are not changed)");
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
                    ? Loc.T("このキーボードの型番(ModelKey)を取得できないため、プロファイルは指定できません。", "The keyboard's model (ModelKey) could not be obtained, so a profile can't be specified.")
                    : Loc.T($"このキーボード(DeviceId: {_uniqId:X8})の型番(ModelKey: {modelKey:X8})のプロファイルを表示しています。同じ型番のキーボードで共有されます。", $"Showing the profile for this keyboard's model (DeviceId: {_uniqId:X8}, ModelKey: {modelKey:X8}). It is shared by keyboards of the same model."), modelKey == 0);
            }
            catch (KbdLayerException ex)
            {
                ShowStatus(status, ex.Message, true);
                canEdit = false;
                apply.IsEnabled = false;
                SyncControls();
            }
            if (!_canWrite) panel.Children.Add(Help(Loc.T("閲覧専用モードのため、変更できません。", "Read-only mode, so changes aren't possible.")));
        }
        else
        {
            apply.IsEnabled = false;
            SyncControls();
            ShowStatus(status, Loc.T("ドライバに接続していないため、表示・変更できません。", "Not connected to the driver, so it can't be shown or changed."), true);
        }

        apply.Click += (_, _) =>
        {
            if (current == null) return;
            I8042Profile? mine = null;
            if (specify.IsChecked == true)
            {
                if (!TryParseU16(type.Text, out var t) || !TryParseU16(subtype.Text, out var s))
                {
                    ShowStatus(status, Loc.T("タイプ・サブタイプは0〜65535の整数、または空欄にしてください。", "Type and subtype must be integers from 0 to 65535, or left blank."), true);
                    return;
                }
                string?[] strs = { identifier.Text.Trim(), jpn.Text.Trim(), kor.Text.Trim() };
                foreach (var v in strs)
                {
                    if (v!.Length >= KbdLayerClient.I8042StrChars || v.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0
                        || v.Contains("..") || v.Any(ch => ch < 0x21 || ch > 0x7E))
                    {
                        ShowStatus(status, Loc.T($"「{v}」は使えません。{KbdLayerClient.I8042StrChars - 1}文字以内の半角英数字・記号(\\ / : * ? \" < > | と .. は不可)にしてください。", $"\"{v}\" can't be used. Use up to {KbdLayerClient.I8042StrChars - 1} ASCII letters, digits, or symbols (\\ / : * ? \" < > | and .. are not allowed)."), true);
                        return;
                    }
                }
                mine = new I8042Profile(modelKey, t, s, NullIfEmpty(strs[0]), NullIfEmpty(strs[1]), NullIfEmpty(strs[2]));
                if (mine.Type == null && mine.Subtype == null && mine.Identifier == null && mine.LayerJpn == null && mine.LayerKor == null)
                {
                    ShowStatus(status, Loc.T("値がすべて空欄です。指定しない場合は、チェックを外してください。", "All values are blank. To specify nothing, clear the check box."), true);
                    return;
                }
            }

            var profiles = current.Profiles.Where(p => p.ModelKey != modelKey).ToList();
            if (mine != null) profiles.Add(mine);
            if (profiles.Count > KbdLayerClient.I8042MaxProfiles)
            {
                ShowStatus(status, Loc.T($"プロファイルは{KbdLayerClient.I8042MaxProfiles}個までです。", $"Up to {KbdLayerClient.I8042MaxProfiles} profiles are allowed."), true);
                return;
            }
            try
            {
                _client!.SetI8042Config(new I8042Config(enabled.IsChecked == true, profiles));
                current = _client.GetI8042Config();
                Show();
                ShowStatus(status, enabled.IsChecked == true
                    ? Loc.T("保存しました。次にキーボードを接続したとき(再接続・再起動)から書き換えます。", "Saved. The values will be rewritten from the next time a keyboard is connected (reconnect or restart).")
                    : Loc.T("保存しました(無効のため、何も書き換えません)。", "Saved (disabled, so nothing is rewritten)."), false);
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

    // ヘルパー設定の検証エラーのうち、アプリ別レイヤーの規則のもの（HelperSettingsModel が「…の規則 n」と書く）。
    private static bool IsAppRuleError(string error) => error.Contains("の規則") || error.Contains(" rule ");

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
            Fail("TabTiming", Loc.T("TapTerm・ComboTerm・TapDanceTerm は0〜10000の整数で入力してください。", "Enter an integer from 0 to 10000 for TapTerm, ComboTerm, and TapDanceTerm."));
            return false;
        }
        uint q = KbdLayerClient.QuickTapTermDisabled;
        if (_quickEnabled.IsChecked == true && (!uint.TryParse(_quick.Text, out q) || q > 10000))
        {
            Fail("TabTiming", Loc.T("Quick Tap は0〜10000の整数で入力してください。", "Enter an integer from 0 to 10000 for Quick Tap."));
            return false;
        }
        if (!uint.TryParse(_tt.Text, out var n) || n > KbdLayerClient.MaxTappingToggle)
        {
            Fail("TabTiming", Loc.T($"TT の回数は0〜{KbdLayerClient.MaxTappingToggle}の整数で入力してください。", $"Enter an integer from 0 to {KbdLayerClient.MaxTappingToggle} for the TT count."));
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
            Fail(helperErrors.Any(IsAppRuleError) && !helperErrors.Any(e => !IsAppRuleError(e)) ? "TabAppRules" : "TabHelper",
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
            Fail("TabHelper", Loc.T($"ヘルパー設定を保存できませんでした: {helperError}", $"Couldn't save the helper settings: {helperError}"));
            return false;
        }

        // ヘルパー動作の削除で「割り当ても外す」を選んだ番号: 保存できたので、このキーボードのキーマップなどから HLP(n) を外す（GUI 計画 1-3）。
        // 削除のあとで同じ Id の動作を作り直していれば、その割り当ては新しい動作を指すので、外さない。
        foreach (int id in _helperTab.PendingUnassign.Distinct())
        {
            bool exists = (_helper.HasDevice && _helper.Device.Actions.Any(a => a.Id == id)) || _helper.Global.Actions.Any(a => a.Id == id);
            if (exists) continue;
            if (ProjectService.RemoveReferences(_project, RefKind.Helper, id) > 0) KeymapChanged = true;
            UnassignedHelperIds.Add(id);
        }
        _helperTab.PendingUnassign.Clear();

        // IME連動はプロジェクトではなく、このPCの利用者の設定（トレイが数秒ごとに読む）。変えたときだけ保存する。
        bool imeLink = _globalNicolaLink;
        bool showLayer = _showLayerOnIcon.IsChecked == true;
        bool remapOnlyWhileTray = _remapOnlyWhileTray.IsChecked == true;
        int? desktopView = SelectedDesktopViewMode();
        string imeSource = ((ImeSourceMode)Math.Max(0, _imeSourceBox.SelectedIndex)).ToString();
        var imeSwitch = (ImeSwitch)Math.Max(0, _imeSwitchBox.SelectedIndex);
        bool imeSwitchChanged = imeSwitch != _trayConfig.ImeSwitchValue;
        string colorMode = ((ColorMode)Math.Max(0, _colorModeBox.SelectedIndex)).ToString();
        bool colorChanged = colorMode != _trayConfig.ColorModeName;
        string language = ((UiLanguage)Math.Max(0, _languageBox.SelectedIndex)).ToString();
        bool languageChanged = language != _trayConfig.UiLanguageName;
        if (languageChanged || colorChanged || imeSwitchChanged || imeSource != _trayConfig.ImeSource || imeLink != _trayConfig.ImeLink || showLayer != _trayConfig.ShowLayerOnIcon || remapOnlyWhileTray != _trayConfig.RemapOnlyWhileTrayRuns
            || desktopView != _trayConfig.DesktopViewMode)
        {
            try
            {
                _trayConfig.DesktopViewMode = desktopView;
                _trayConfig.ImeSource = imeSource;
                if (imeSwitchChanged) _trayConfig.ImeSwitchName = imeSwitch == ImeSwitch.Auto ? null : imeSwitch.ToString(); // 既定は書かない
                _trayConfig.ColorModeName = colorMode;
                _trayConfig.UiLanguageName = language;
                _trayConfig.ImeLink = imeLink;
                _trayConfig.ShowLayerOnIcon = showLayer;
                _trayConfig.RemapOnlyWhileTrayRuns = remapOnlyWhileTray;
                _trayConfig.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Fail("TabImeLayer", Loc.T($"トレイの設定を保存できませんでした: {ex.Message}", $"Couldn't save the tray settings: {ex.Message}"));
                return false;
            }
            if (colorChanged) AppTheme.Apply(_trayConfig.ColorModeValue); // 保存できたら、すぐ全画面に効かせる
            if (languageChanged) LanguageChanged = true;
        }
        return true;
    }
}
