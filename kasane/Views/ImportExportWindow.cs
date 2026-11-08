using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kasane.Interop;
using Kasane.Layout;
using Kasane.Model;
using Kasane.Model.Portable;
using Microsoft.Win32;

namespace Kasane.Views;

/// <summary>
/// 設定のエクスポート／インポート（2026-10-08、P5）。範囲は「全体一括」「このキーボード」「レイヤー1枚」。
/// ファイルは *.kasane.json（レイヤーとコンボはスキャンコードで持つので、レイアウトの違うキーボードにも持っていける）。
/// ロジックは <see cref="PortableExporter"/> / <see cref="PortableImporter"/> / <see cref="PortableService"/>（画面に依存しない）にあり、
/// ここは選択と報告の表示だけ。キーボード／レイヤーの読み込みは、プロジェクトの複製に入れて <see cref="ResultProject"/> で返す
/// （「適用」は今までどおり利用者が行う）。ヘルパー設定だけは、確認のうえ helper.json へ直接書く。全体一括の読み込みは、
/// 置き換える前に今の設定をバックアップして、保存先へ直接書く。
/// </summary>
public sealed class ImportExportWindow : Window
{
    public enum StartTab { Export, Import, Clear }

    private sealed record Target(uint? Id, string Text)
    {
        public override string ToString() => Text;
    }

    private readonly KeymapProject _project;
    private readonly KeyboardLayout _layout;
    private readonly uint _uniqId;
    private readonly IReadOnlyList<PortableService.TargetDevice> _devices;
    private readonly Func<bool>? _saveProject;
    private bool _dirty;

    private readonly TabControl _tabs = new() { Name = "ImportExportTabs" };

    // エクスポート
    private readonly RadioButton _exAll = new() { Name = "ExportScopeAll", Content = Loc.T("全体一括", "Everything"), ToolTip = Loc.T("すべてのキーボードと、トレイの設定", "All keyboards and the tray settings"), GroupName = "ExScope", Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _exKeyboard = new() { Name = "ExportScopeKeyboard", Content = Loc.T("このキーボード", "This keyboard"), GroupName = "ExScope", Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _exLayer = new() { Name = "ExportScopeLayer", Content = Loc.T("レイヤー1枚", "One layer"), ToolTip = Loc.T("使っているマクロなども一緒に書き出します", "The macros and other definitions it uses are exported too"), GroupName = "ExScope", Margin = new Thickness(0, 2, 8, 2) };
    private readonly ComboBox _exLayerCombo = new() { Name = "ExportLayerCombo", Width = 80 };
    private readonly CheckBox _exHelper = new() { Name = "ExportHelperCheck", Content = Loc.T("ヘルパー設定を含める", "Include helper settings"), ToolTip = Loc.T("動作・トリガー・アプリ別レイヤー", "Actions, triggers and per-app layers"), IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
    private readonly CheckBox _exRecovery = new() { Name = "ExportRecoveryCheck", Content = Loc.T("復旧用の設定を含める", "Include recovery settings"), ToolTip = Loc.T("トレイの表示・色・言語、デスクトップ、緊急解除、i8042prt、共有範囲、自分で追加したレイアウト。同じパソコンへ戻すとき用で、全体一括のときだけ選べます", "Tray display, colors and language, desktop, emergency release, i8042prt, sharing scopes and your own layouts. For restoring to this PC; only for Everything"), Margin = new Thickness(0, 2, 0, 0) };
    private readonly CheckBox _exMeasured = new() { Name = "ExportMeasuredCheck", Content = Loc.T("実測したスキャンコードを含める", "Include measured scan codes"), ToolTip = Loc.T("同じキーボードへ戻すとき用", "For restoring to the same keyboard"), Margin = new Thickness(0, 2, 0, 0) };
    private readonly RadioButton _exFormatKasane = new() { Name = "ExportFormatKasane", Content = Loc.T("kasane の設定ファイル (*.kasane.json)", "kasane settings file (*.kasane.json)"), ToolTip = Loc.T("レイヤー・マクロ・コンボなど、すべて", "Layers, macros, combos and everything else"), GroupName = "ExFormat", IsChecked = true, Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _exFormatKeymapTool = new() { Name = "ExportFormatKeymapTool", Content = Loc.T("KeymapTool 用 JSON", "JSON for KeymapTool"), ToolTip = Loc.T("keymap.json と profile.json。レイヤーとキャリブレーションだけを書き出します(コマンド版の --apply-simple 用)", "keymap.json and profile.json. Only layers and calibration (for the command-line --apply-simple)"), GroupName = "ExFormat", Margin = new Thickness(0, 2, 0, 2) };
    private readonly TextBox _exReport = ReportBox("ExportReport");
    private readonly Button _exOpen = new() { Name = "ExportOpenFolderButton", Content = Loc.T("出力先をエクスプローラーで開く", "Open output folder"), Width = 190, Margin = new Thickness(8, 10, 0, 0) };
    private readonly TextBlock _exDest = new()
    {
        Name = "ExportDestinationText", VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 0, 3),
        Foreground = AppTheme.Palette.SubText, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420,
    };
    private string? _lastExportPath; // 直前に書き出したファイル（または KeymapTool 用 JSON のフォルダ）。書き出す前は null（設定の保存先を出力先として見せる）

    // インポート
    private readonly TextBlock _imPath = new() { Name = "ImportPath", Text = Loc.T("(ファイルを選んでください)", "(Choose a file)"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _imSummary = new() { Name = "ImportSummary", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly ComboBox _imSource = new() { Name = "ImportSourceCombo", Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _imSourceRow = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
    private readonly StackPanel _imMapPanel = new() { Name = "ImportMapPanel", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
    private readonly StackPanel _imOptions = new() { Name = "ImportOptionsPanel", Margin = new Thickness(0, 0, 0, 6) };
    private readonly CheckBox _imLayers = Check("ImportLayersCheck", Loc.T("レイヤー", "Layers"), Loc.T("キーの割り当て", "Key assignments"));
    private readonly CheckBox _imMacros = Check("ImportMacrosCheck", Loc.T("マクロ", "Macros"));
    private readonly CheckBox _imCombos = Check("ImportCombosCheck", Loc.T("コンボ", "Combos"));
    private readonly CheckBox _imTapDances = Check("ImportTapDancesCheck", Loc.T("タップダンス", "Tap dances"));
    private readonly CheckBox _imKeyOverrides = Check("ImportKeyOverridesCheck", Loc.T("キー置換", "Key overrides"));
    private readonly CheckBox _imTiming = Check("ImportTimingCheck", Loc.T("タイミング", "Timing"));
    private readonly CheckBox _imFeatures = Check("ImportFeaturesCheck", Loc.T("機能のON/OFF", "Feature on/off"));
    private readonly CheckBox _imNicola = Check("ImportNicolaCheck", Loc.T("NICOLA(親指シフト)", "NICOLA (thumb shift)"));
    private readonly CheckBox _imHelper = Check("ImportHelperCheck", Loc.T("ヘルパー設定", "Helper settings"), Loc.T("読み込むと、すぐ helper.json に書かれます", "Written to helper.json as soon as you import"));
    private readonly CheckBox _imRecovery = Check("ImportRecoveryCheck", Loc.T("復旧用の設定", "Recovery settings"), Loc.T("トレイの設定、緊急解除、i8042prt、共有範囲、レイアウト。緊急解除・i8042prt・共有範囲は、管理者として起動しているときだけ読み込みます", "Tray settings, emergency release, i8042prt, sharing scopes and layouts. Emergency release, i8042prt and sharing scopes are imported only when running as administrator"));
    private readonly CheckBox _imMeasured = Check("ImportMeasuredCheck", Loc.T("実測したスキャンコード", "Measured scan codes"), Loc.T("同じレイアウトのときだけ読み込みます", "Imported only for the same layout"));
    private readonly StackPanel _imModeRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
    private readonly RadioButton _imReplace = new() { Name = "ImportModeReplace", Content = Loc.T("置き換え", "Replace"), ToolTip = Loc.T("今の設定を、ファイルの内容で置き換えます", "Replaces the current settings with the file's"), GroupName = "ImMode", IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
    private readonly RadioButton _imAppend = new() { Name = "ImportModeAppend", Content = Loc.T("追加", "Add"), ToolTip = Loc.T("今の設定を残して、足します", "Keeps the current settings and adds to them"), GroupName = "ImMode" };
    private readonly StackPanel _imLayerRow = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
    private readonly ComboBox _imLayerTarget = new() { Name = "ImportLayerTargetCombo", Width = 260 };
    private readonly TextBox _imReport = ReportBox("ImportReport");
    private readonly Button _imPreview = new() { Name = "ImportPreviewButton", Content = Loc.T("内容確認", "Preview"), Width = 110, IsEnabled = false,
                                                     ToolTip = Loc.T("何も変えずに、読み込んだときの結果を表示します", "Shows what the import would do, without changing anything") };
    private readonly Button _imDo = new() { Name = "ImportDoButton", Content = Loc.T("読み込む", "Import"), Width = 110, IsEnabled = false };

    private PortableFile? _file;
    private readonly List<ComboBox> _mapCombos = new();

    // クリア
    private readonly KbdLayerClient? _client;
    private readonly bool _canWriteDriver;
    private readonly bool _clientWritable; // 管理者権限でドライバを開いている（キーボードが決まっていなくても、ドライバ全体の設定は書ける）
    private readonly RadioButton _clThis = new() { Name = "ClearScopeThis", GroupName = "ClScope", IsChecked = true, Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _clAll = new() { Name = "ClearScopeAll", GroupName = "ClScope", Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _clPick = new() { Name = "ClearScopePick", Content = Loc.T("選んだキーボード", "Selected keyboards"), GroupName = "ClScope", Margin = new Thickness(0, 2, 0, 2) };
    private readonly StackPanel _clPickList = new() { Name = "ClearPickList", Margin = new Thickness(24, 0, 0, 6), IsEnabled = false };
    private readonly Dictionary<uint, CheckBox> _clPickBoxes = new();
    private readonly CheckBox _clLayers = Clear("ClearLayersCheck", Loc.T("レイヤー", "Layers"), Loc.T("キーの割り当て", "Key assignments"));
    private readonly CheckBox _clMacros = Clear("ClearMacrosCheck", Loc.T("マクロ", "Macros"));
    private readonly CheckBox _clCombos = Clear("ClearCombosCheck", Loc.T("コンボ", "Combos"));
    private readonly CheckBox _clTapDances = Clear("ClearTapDancesCheck", Loc.T("タップダンス", "Tap dances"));
    private readonly CheckBox _clKeyOverrides = Clear("ClearKeyOverridesCheck", Loc.T("キー置換", "Key overrides"));
    private readonly CheckBox _clTiming = Clear("ClearTimingCheck", Loc.T("タイミング", "Timing"));
    private readonly CheckBox _clFeatures = Clear("ClearFeaturesCheck", Loc.T("機能のON/OFF", "Feature on/off"), Loc.T("すべて ON に戻します", "Turns all features back on"));
    private readonly CheckBox _clNicola = Clear("ClearNicolaCheck", Loc.T("NICOLA(親指シフト)", "NICOLA (thumb shift)"));
    private readonly CheckBox _clHelper = Clear("ClearHelperCheck", Loc.T("ヘルパー設定", "Helper settings"), Loc.T("helper.json。接続中のすべてのキーボードのときは、全キーボード共通のヘルパー設定も", "helper.json. With all connected keyboards, also the shared helper settings"));
    private readonly CheckBox _clMeasured = Clear("ClearMeasuredCheck", Loc.T("実測したスキャンコード", "Measured scan codes"));
    private readonly TextBox _clReport = ReportBox("ClearReport");

    /// <summary>今開いているキーボードを消したあとの、保存先から読み直したプロジェクト（消さなかったら null）。</summary>
    public KeymapProject? ClearedProject { get; private set; }

    /// <summary>消したキーボードがある（一覧のON/OFFなどを更新する必要がある）。</summary>
    public bool Cleared { get; private set; }

    /// <summary>ドライバの保存内容も消した（管理者権限があり、ドライバに接続していた）。</summary>
    public bool ClearedDriver { get; private set; }

    private static CheckBox Clear(string name, string text, string? tip = null) => new() { Name = name, Content = text, ToolTip = tip, IsChecked = false, Margin = new Thickness(0, 1, 0, 1) };

    /// <summary>キーボード／レイヤーの読み込みの結果（プロジェクトの複製。読み込まなかったら null）。</summary>
    public KeymapProject? ResultProject { get; private set; }

    /// <summary>全体一括の読み込みが、今開いているキーボードの保存先を書き換えた（保存先から読み直す必要がある）。</summary>
    public bool CurrentChangedOnDisk { get; private set; }

    public bool Imported => ResultProject != null || CurrentChangedOnDisk;

    public ImportExportWindow(KeymapProject project, KeyboardLayout layout, uint uniqId,
                              IReadOnlyList<PortableService.TargetDevice> devices, bool dirty, Func<bool>? saveProject,
                              StartTab startTab, PortableScope startScope, int startLayer,
                              KbdLayerClient? client = null, bool canWriteDriver = false)
    {
        _client = client;
        _canWriteDriver = canWriteDriver && client != null && uniqId != 0;
        _clientWritable = canWriteDriver && client != null;
        _project = project;
        _layout = layout;
        _uniqId = uniqId;
        _devices = devices;
        _dirty = dirty;
        _saveProject = saveProject;

        Title = Loc.T("設定のエクスポート／インポート", "Settings Export / Import");
        Width = 760;
        Height = 700;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _tabs.Items.Add(new TabItem { Name = "TabExport", Header = Loc.T("エクスポート", "Export"), Content = BuildExport(startScope, startLayer) });
        _tabs.Items.Add(new TabItem { Name = "TabImport", Header = Loc.T("インポート", "Import"), Content = BuildImport() });
        _tabs.Items.Add(new TabItem { Name = "TabClear", Header = Loc.T("初期化", "Reset"), Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = BuildClear() } });
        _tabs.SelectedIndex = startTab == StartTab.Import ? 1 : startTab == StartTab.Clear ? 2 : 0;

        var close = new Button { Name = "CloseButton", Content = Loc.T("閉じる", "Close"), IsCancel = true, IsDefault = false, Width = 90 };
        close.Click += (_, _) => Close();
        // 左に「?」（今のタブの説明をマニュアルで開く。F1 も同じ）、右に「閉じる」。
        string Topic() => _tabs.SelectedIndex switch { 1 => HelpTopics.Import, 2 => HelpTopics.Reset, _ => HelpTopics.Export };
        var help = HelpButton.Create(Topic);
        HelpButton.AttachF1(this, Topic);
        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(help, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        bottom.Children.Add(help);
        bottom.Children.Add(close);
        bottom.Children.Add(new Border());
        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(_tabs);
        Content = root;
    }

    private static TextBox ReportBox(string name) => new()
    {
        Name = name, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        MinHeight = 90, Margin = new Thickness(0, 8, 0, 0), Background = AppTheme.Palette.ReportBackground,
    };

    private static CheckBox Check(string name, string text, string? tip = null) => new() { Name = name, Content = text, ToolTip = tip, IsChecked = true, Margin = new Thickness(0, 1, 0, 1) };

    private static TextBlock Help(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Palette.SubText, Margin = new Thickness(0, 0, 0, 8) };

    private static string FileNameFor(PortableScope scope, uint uniq) =>
        scope == PortableScope.All ? "kasane-all.kasane.json" : $"kasane-{scope.ToString().ToLowerInvariant()}-{uniq:X8}.kasane.json";

    // ------------------------------------------------------------------
    // 共通
    // ------------------------------------------------------------------

    /// <summary>未保存の変更があれば、全体一括の前に保存するか尋ねる。続けてよければ true。</summary>
    private bool ConfirmSavedForAll(string action)
    {
        if (!_dirty || _saveProject == null) return true;
        var r = MessageBox.Show(this,
            Loc.T(
                $"このキーボードに、保存していない変更があります。\n\n{action}は保存先の設定を対象にします。先に保存しますか？\n" +
                "(「いいえ」で保存せずに続けると、保存していない変更は反映されず、失われます)",
                $"This keyboard has unsaved changes.\n\n{action} uses the saved settings. Save first?\n" +
                "(If you choose No and continue without saving, the unsaved changes are not included and will be lost.)"),
            Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes)
        {
            if (!_saveProject()) return false;
            _dirty = false;
        }
        return true;
    }

    private HelperConfig? LoadDeviceHelper(out string? error)
    {
        error = null;
        if (_uniqId == 0) return null;
        HelperConfig.TryLoad(HelperConfig.DevicePath(_uniqId), out var h, out error);
        return error == null ? h : null;
    }

    private static void ShowReport(TextBox box, PortableReport report, string header)
    {
        var text = new System.Text.StringBuilder(header);
        if (report.Lines.Count > 0) text.Append("\n\n").Append(report.ToString());
        box.Text = text.ToString();
        box.ScrollToHome();
    }

    // ------------------------------------------------------------------
    // エクスポート
    // ------------------------------------------------------------------

    private UIElement BuildExport(PortableScope startScope, int startLayer)
    {
        var p = new StackPanel { Margin = new Thickness(10) };
        for (int i = 0; i < _project.Layers.Count; i++) _exLayerCombo.Items.Add(i.ToString());
        _exLayerCombo.SelectedIndex = Math.Clamp(startLayer, 0, Math.Max(0, _project.Layers.Count - 1));

        p.Children.Add(_exAll);
        p.Children.Add(_exKeyboard);
        var layerRow = new StackPanel { Orientation = Orientation.Horizontal };
        layerRow.Children.Add(_exLayer);
        layerRow.Children.Add(new TextBlock { Text = Loc.T("レイヤー", "Layer"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        layerRow.Children.Add(_exLayerCombo);
        p.Children.Add(layerRow);
        // 形式は「このキーボード」のときだけ選べる。ほかの範囲では使えない状態（灰色）になるので、見出しで断らない（2026-11-08、利用者の指摘）。
        p.Children.Add(new TextBlock { Text = Loc.T("形式", "Format"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) });
        p.Children.Add(_exFormatKasane);
        p.Children.Add(_exFormatKeymapTool);
        p.Children.Add(new TextBlock { Text = Loc.T("含める項目", "Include"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
        p.Children.Add(_exHelper);
        p.Children.Add(_exMeasured);
        p.Children.Add(_exRecovery);

        (startScope switch { PortableScope.All => _exAll, PortableScope.Layer => _exLayer, _ => _exKeyboard }).IsChecked = true;
        _exLayerCombo.IsEnabled = _exLayer.IsChecked == true; // レイヤー1枚のときだけ選べる
        _exLayer.Checked += (_, _) => _exLayerCombo.IsEnabled = true;
        _exLayer.Unchecked += (_, _) => _exLayerCombo.IsEnabled = false;
        foreach (var r in new[] { _exAll, _exKeyboard, _exLayer, _exFormatKasane, _exFormatKeymapTool }) { r.Checked += (_, _) => UpdateExportFormat(); r.Unchecked += (_, _) => UpdateExportFormat(); }
        UpdateExportFormat();

        var go = new Button { Name = "ExportDoButton", Content = Loc.T("書き出す...", "Export..."), Width = 110, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        go.Click += (_, _) => DoExport();
        _exOpen.Click += (_, _) => OpenInExplorer(_lastExportPath ?? DialogDefaults.DataFolder);
        UpdateExportDestination();
        var goRow = new StackPanel { Orientation = Orientation.Horizontal };
        goRow.Children.Add(go);
        goRow.Children.Add(_exOpen);
        goRow.Children.Add(_exDest);
        p.Children.Add(goRow);
        p.Children.Add(_exReport);
        return p;
    }

    // 形式は「このキーボード」のときだけ選べる。KeymapTool 用 JSON のときは、含める項目（ヘルパー・実測）は関係しない。
    private void UpdateExportFormat()
    {
        bool keyboard = _exKeyboard.IsChecked == true;
        _exFormatKasane.IsEnabled = _exFormatKeymapTool.IsEnabled = keyboard;
        bool kasane = !keyboard || _exFormatKasane.IsChecked == true;
        _exHelper.IsEnabled = _exMeasured.IsEnabled = kasane;
        _exRecovery.IsEnabled = _exAll.IsChecked == true;
        if (!_exRecovery.IsEnabled) _exRecovery.IsChecked = false;
    }

    // 書き出したファイルを選んだ状態で、エクスプローラーを開く（フォルダなら、そのフォルダを開く）。
    private void OpenInExplorer(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            if (File.Exists(path)) info.Arguments = $"/select,\"{path}\"";
            else if (Directory.Exists(path)) info.Arguments = $"\"{path}\"";
            else { ShowReport(_exReport, new PortableReport(), Loc.T($"開けませんでした(見つかりません): {path}", $"Could not open (not found): {path}")); return; }
            System.Diagnostics.Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowReport(_exReport, new PortableReport(), Loc.T($"エクスプローラーを開けませんでした: {ex.Message}", $"Could not open Explorer: {ex.Message}"));
        }
    }

    // 現在の出力先を、ボタンの右に出す。書き出したあとはそのファイル（フォルダ）、書き出す前は、書き出す画面が最初に開く設定の保存先。
    // 書き出す前でも「開く」は使える（設定の保存先のフォルダが開く）。
    private void UpdateExportDestination()
    {
        string path = _lastExportPath ?? DialogDefaults.DataFolder;
        _exDest.Text = (_lastExportPath == null ? Loc.T("出力先(まだ書き出していません。設定の保存先): ", "Output location (not exported yet; settings folder): ") : Loc.T("出力先: ", "Output location: ")) + path;
        _exDest.ToolTip = path;
    }

    // 書き出しに成功した。出力先の表示を、書き出したファイルにする。
    private void MarkExported(string path)
    {
        _lastExportPath = path;
        UpdateExportDestination();
    }

    private bool ExportAsKeymapTool => _exKeyboard.IsChecked == true && _exFormatKeymapTool.IsChecked == true;

    // KeymapTool 用 JSON（keymap.json と profile.json）を、選んだフォルダへ書き出す。
    private void DoExportKeymapTool()
    {
        var dlg = new OpenFolderDialog
        {
            Title = Loc.T("keymap.json と profile.json を書き出すフォルダ", "Folder to export keymap.json and profile.json to"),
            InitialDirectory = DialogDefaults.DataFolder,
            ClientGuid = DialogDefaults.KeymapToolExport,
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var warnings = ProjectService.Export(_project, _layout, dlg.FolderName);
            MarkExported(dlg.FolderName);
            ShowReport(_exReport, new PortableReport(),
                Loc.T($"書き出しました:\n{dlg.FolderName}\\keymap.json\n{dlg.FolderName}\\profile.json" +
                    (warnings.Count > 0 ? "\n\n注意:\n" + string.Join("\n", warnings) : ""),
                    $"Exported:\n{dlg.FolderName}\\keymap.json\n{dlg.FolderName}\\profile.json" +
                    (warnings.Count > 0 ? "\n\nNotes:\n" + string.Join("\n", warnings) : "")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowReport(_exReport, new PortableReport(), Loc.T($"書き出せませんでした: {ex.Message}", $"Could not export: {ex.Message}"));
        }
    }

    /// <summary>復旧用の設定を含めるときの、既定のファイル名。インストーラーが、設定フォルダでこの名前を探す（setup/Product.wxs の CA_FindRecoveryFile）。</summary>
    public const string RecoveryFileName = "kasane-recovery.kasane.json";

    private PortableScope ExportScope => _exAll.IsChecked == true ? PortableScope.All : _exLayer.IsChecked == true ? PortableScope.Layer : PortableScope.Keyboard;

    private void DoExport()
    {
        if (ExportAsKeymapTool) { DoExportKeymapTool(); return; }
        var scope = ExportScope;
        var report = new PortableReport();
        var options = new ExportOptions { IncludeHelper = _exHelper.IsChecked == true, IncludeMeasured = _exMeasured.IsChecked == true };
        try
        {
            if (scope == PortableScope.All && !ConfirmSavedForAll(Loc.T("全体一括の書き出し", "Exporting everything"))) return;

            // 復旧用の設定を含めるときは、設定フォルダの kasane-recovery.kasane.json を既定にする（インストーラーが、再インストールのときに見つけて取り込む）。
            // 前回使ったフォルダを覚える ClientGuid は、この場合は付けない（付けると、前回のフォルダが優先される）。
            bool recoveryFile = scope == PortableScope.All && _exRecovery.IsChecked == true;
            var dlg = new SaveFileDialog
            {
                Title = Loc.T("書き出し先", "Export To"),
                InitialDirectory = DialogDefaults.DataFolder,
                Filter = Loc.T("kasane の設定 (*.kasane.json)|*.kasane.json|すべてのファイル|*.*", "kasane settings (*.kasane.json)|*.kasane.json|All files|*.*"),
                FileName = recoveryFile ? RecoveryFileName : FileNameFor(scope, _uniqId),
                OverwritePrompt = true,
            };
            if (!recoveryFile) dlg.ClientGuid = DialogDefaults.SettingsExport;
            if (dlg.ShowDialog(this) != true) return;

            PortableFile file;
            if (scope == PortableScope.All)
            {
                if (_exRecovery.IsChecked == true)
                    options.Recovery = RecoveryExporter.Collect(_client != null ? new ClientRecoveryDriver(_client) : null, _devices.Select(d => d.UniqId), null, report);
                file = PortableService.ExportAll(null, options, report);
            }
            else
            {
                file = PortableExporter.NewFile(scope);
                var device = LoadDeviceHelper(out string? helperError);
                if (helperError != null) report.Warn(Loc.T($"helper.json を読めないため、ヘルパー設定は含めません({helperError})", $"Helper settings are not included because helper.json could not be read ({helperError})"));
                if (scope == PortableScope.Keyboard)
                {
                    file.Keyboards.Add(PortableExporter.ExportKeyboard(_project, _layout, device, options, report));
                }
                else
                {
                    int layer = _exLayerCombo.SelectedIndex;
                    if (layer < 0 || layer >= _project.Layers.Count) { ShowReport(_exReport, report, Loc.T("レイヤーを選んでください。", "Choose a layer.")); return; }
                    var global = options.IncludeHelper ? TrayConfig.Load().Helper : null;
                    file.Keyboards.Add(PortableExporter.ExportLayer(_project, _layout, layer, options.IncludeHelper ? device : null, global, report));
                }
            }
            PortableIO.Write(dlg.FileName, file);
            MarkExported(dlg.FileName);
            ShowReport(_exReport, report, Loc.T($"書き出しました: {dlg.FileName}", $"Exported: {dlg.FileName}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowReport(_exReport, report, Loc.T($"書き出せませんでした: {ex.Message}", $"Could not export: {ex.Message}"));
        }
    }

    // ------------------------------------------------------------------
    // クリア
    // ------------------------------------------------------------------

    private IEnumerable<CheckBox> ClearCategoryBoxes() =>
        new[] { _clLayers, _clMacros, _clCombos, _clTapDances, _clKeyOverrides, _clTiming, _clFeatures, _clNicola, _clHelper, _clMeasured };

    private UIElement BuildClear()
    {
        var p = new StackPanel { Margin = new Thickness(10) };

        p.Children.Add(new TextBlock { Text = Loc.T("対象のキーボード", "Target keyboards"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 2) });
        _clThis.Content = _uniqId == 0 ? Loc.T("このキーボード(接続していないため、画面の設定だけ)", "This keyboard (not connected, so on-screen settings only)") : Loc.T($"このキーボード({_uniqId:X8})", $"This keyboard ({_uniqId:X8})");
        _clAll.Content = Loc.T($"接続中のすべてのキーボード({_devices.Count}台)", $"All connected keyboards ({_devices.Count})");
        p.Children.Add(_clThis);
        p.Children.Add(_clAll);
        p.Children.Add(_clPick);
        foreach (var d in _devices)
        {
            var box = new CheckBox { Name = $"ClearTarget_{d.UniqId:X8}", Content = d.DisplayName, Margin = new Thickness(0, 1, 0, 1), IsChecked = d.UniqId == _uniqId };
            _clPickBoxes[d.UniqId] = box;
            _clPickList.Children.Add(box);
        }
        if (_devices.Count == 0) _clPickList.Children.Add(new TextBlock { Text = Loc.T("(接続中のキーボードがありません)", "(No keyboards connected)"), Foreground = AppTheme.Palette.SubText });
        p.Children.Add(_clPickList);
        _clPick.Checked += (_, _) => _clPickList.IsEnabled = true;
        _clPick.Unchecked += (_, _) => _clPickList.IsEnabled = false;
        _clAll.IsEnabled = _devices.Count > 0;
        _clPick.IsEnabled = _devices.Count > 0;

        p.Children.Add(new TextBlock { Text = Loc.T("初期化する項目", "Items to reset"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) });
        var presets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var all = new Button { Name = "ClearSelectAllButton", Content = Loc.T("すべて選ぶ", "Select All"), Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 6, 0) };
        all.Click += (_, _) => { foreach (var c in ClearCategoryBoxes()) c.IsChecked = true; };
        var none = new Button { Name = "ClearSelectNoneButton", Content = Loc.T("すべて外す", "Deselect All"), Padding = new Thickness(8, 1, 8, 1) };
        none.Click += (_, _) => { foreach (var c in ClearCategoryBoxes()) c.IsChecked = false; };
        presets.Children.Add(all);
        presets.Children.Add(none);
        p.Children.Add(presets);
        foreach (var c in ClearCategoryBoxes()) p.Children.Add(c);

        p.Children.Add(new TextBlock
        {
            Name = "ClearDriverNote",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = _canWriteDriver ? AppTheme.Palette.ErrorText : AppTheme.Palette.SubText,
            Text = _canWriteDriver
                ? Loc.T("ドライバに保存された設定も、同時に消します(管理者権限があるため)。「同じ型番で共有」のキーボードは、同じ型番のものすべてに効きます。",
                    "Settings saved in the driver are cleared as well (you have administrator rights). Keyboards set to \"Shared by model\" affect every keyboard of the same model.")
                : Loc.T("ドライバには書き込めない(接続していない、または閲覧専用)ため、画面の設定だけを消します。ドライバの設定は残ります(あとで管理者として「キーボードに適用」するか、この初期化をやり直してください)。",
                    "The driver cannot be written to (not connected, or read-only), so only the on-screen settings are cleared. The driver's settings remain (later, apply to the keyboard as administrator, or run this reset again)."),
        });

        var go = new Button { Name = "ClearDoButton", Content = Loc.T("初期化する...", "Reset..."), Width = 110, ToolTip = Loc.T("実行の前に、今の設定を自動でバックアップします", "Your current settings are backed up automatically first"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        go.Click += (_, _) => DoClear();
        p.Children.Add(go);
        p.Children.Add(_clReport);
        return p;
    }

    private ClearOptions BuildClearOptions() => new()
    {
        Layers = _clLayers.IsChecked == true,
        Macros = _clMacros.IsChecked == true,
        Combos = _clCombos.IsChecked == true,
        TapDances = _clTapDances.IsChecked == true,
        KeyOverrides = _clKeyOverrides.IsChecked == true,
        Timing = _clTiming.IsChecked == true,
        Features = _clFeatures.IsChecked == true,
        Nicola = _clNicola.IsChecked == true,
        Helper = _clHelper.IsChecked == true,
        Measured = _clMeasured.IsChecked == true,
    };

    private List<uint> ClearTargets()
    {
        if (_clAll.IsChecked == true) return _devices.Select(d => d.UniqId).Distinct().ToList();
        if (_clPick.IsChecked == true) return _clPickBoxes.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
        return new List<uint> { _uniqId };
    }

    private void DoClear()
    {
        var o = BuildClearOptions();
        var targets = ClearTargets();
        if (!o.Any) { ShowReport(_clReport, new PortableReport(), Loc.T("初期化する項目を選んでください。", "Choose the items to reset.")); return; }
        if (targets.Count == 0) { ShowReport(_clReport, new PortableReport(), Loc.T("対象のキーボードを選んでください。", "Choose the target keyboards.")); return; }

        // 接続していないキーボード（UniqId 0）は、保存先に設定が無い。画面の設定だけを消す（このタブの「このキーボード」）。
        if (targets.Contains(0) && targets.Count == 1)
        {
            var offline = ProjectService.Clone(_project);
            try { ClearService.ResetProject(offline, _layout, o); }
            catch (ArgumentNullException) { ShowReport(_clReport, new PortableReport(), Loc.T("レイアウトが無いため、レイヤーを消せません。", "There is no layout, so layers cannot be cleared.")); return; }
            ClearedProject = offline;
            Cleared = true;
            ShowReport(_clReport, new PortableReport(), Loc.T("画面の設定を初期状態にしました(まだ保存していません)。「閉じる」で編集画面に戻ります。", "The on-screen settings were reset (not saved yet). Click \"Close\" to return to the editor."));
            return;
        }
        targets = targets.Where(t => t != 0).ToList();

        string driverText = _canWriteDriver ? Loc.T("ドライバの保存内容も消します", "The driver's saved settings will also be cleared") : Loc.T("画面の設定だけを消します(ドライバの設定は残ります)", "Only the on-screen settings will be cleared (the driver's settings remain)");
        string sharedWarning = SharedScopeWarning(targets);
        var ask = MessageBox.Show(this,
            Loc.T(
                $"{targets.Count}台のキーボードの、次の項目を初期状態に戻します。\n\n  " + string.Join("\n  ", o.Names()) +
                $"\n\n{driverText}。今の設定は、実行の前に自動でバックアップします。\n画面で編集していて、まだ保存していない変更は失われます。" +
                (sharedWarning.Length > 0 ? "\n\n" + sharedWarning : "") + "\n\n続けますか？",
                $"The following items will be reset to their defaults on {targets.Count} keyboard(s).\n\n  " + string.Join("\n  ", o.Names()) +
                $"\n\n{driverText}. Your current settings are backed up automatically before this runs.\nUnsaved changes you are editing on screen will be lost." +
                (sharedWarning.Length > 0 ? "\n\n" + sharedWarning : "") + "\n\nContinue?"),
            Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ask != MessageBoxResult.OK) return;

        var report = new PortableReport();
        var cleared = ClearService.Clear(targets, o, _canWriteDriver ? _client : null, includeTrayHelper: _clAll.IsChecked == true, null, report);
        if (cleared.Count > 0) Cleared = true;
        if (_canWriteDriver && cleared.Count > 0) ClearedDriver = true;
        if (_uniqId != 0 && cleared.Contains(_uniqId))
        {
            try { ClearedProject = ProjectService.TryLoad(_uniqId); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                report.Warn(Loc.T($"初期化したあとの設定を読み直せませんでした({ex.Message})。編集画面を開き直してください。", $"Could not reload the settings after resetting ({ex.Message}). Reopen the editor."));
            }
        }
        ShowReport(_clReport, report,
            report.HasErrors && cleared.Count == 0 ? Loc.T("初期化できませんでした。", "Could not reset.")
            : Loc.T($"{cleared.Count}台を初期化しました。「閉じる」で編集画面に戻ります。", $"Reset {cleared.Count} keyboard(s). Click \"Close\" to return to the editor."));
    }

    // 保存先が「同じ型番」「すべて」のキーボードは、ドライバの設定を消すと、同じ型番（すべて）のキーボードにも効く。
    private string SharedScopeWarning(IEnumerable<uint> targets)
    {
        if (!_canWriteDriver || _client == null) return "";
        var shared = new List<string>();
        foreach (uint t in targets)
        {
            try
            {
                var scope = _client.GetDeviceScope(t);
                if (scope.Binding != KbdLayerClient.BindingInstance)
                    shared.Add($"{t:X8}({(scope.Binding == KbdLayerClient.BindingGlobal ? Loc.T("すべてのキーボードで共有", "Shared by all keyboards") : Loc.T("同じ型番で共有", "Shared by keyboards of the same model"))})");
            }
            catch (KbdLayerException) { /* 保存先の範囲を読めなくても、クリアはできる */ }
        }
        return shared.Count == 0 ? ""
            : Loc.T("注意: 次のキーボードは、ドライバの設定を他のキーボードと共有しています。消すと、共有しているキーボードにも効きます:\n  ",
                    "Note: the following keyboards share their driver settings with other keyboards. Clearing them also affects the keyboards that share them:\n  ") + string.Join("\n  ", shared);
    }

    // ------------------------------------------------------------------
    // インポート
    // ------------------------------------------------------------------

    private UIElement BuildImport()
    {
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var p = new StackPanel { Margin = new Thickness(10) };
        var pick = new Button { Name = "ImportPickButton", Content = Loc.T("ファイルを選ぶ...", "Choose File..."), Width = 130 };
        pick.Click += (_, _) => PickFile();
        var nodoka = new Button { Name = "ImportNodokaButton", Content = Loc.T("のどかの設定を取り込む...", "Import Nodoka Settings..."), Width = 190, Margin = new Thickness(8, 0, 0, 0) };
        nodoka.Click += (_, _) => PickNodoka();
        var pickRow = new StackPanel { Orientation = Orientation.Horizontal };
        pickRow.Children.Add(pick);
        pickRow.Children.Add(nodoka);
        pickRow.Children.Add(_imPath);
        p.Children.Add(pickRow);
        // Emacs の設定例は、決まったファイル（同梱）を読む。ファイルを選ぶボタンとは別の行に置く（2026-11-08、利用者の指摘）。
        var emacs = new Button { Name = "ImportEmacsButton", Content = Loc.T("Emacs の設定例...", "Emacs Example..."), Width = 150, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0),
                                 ToolTip = Loc.T("同梱の Emacs 風の設定例を読み込みます", "Loads the bundled Emacs-style example") };
        emacs.Click += (_, _) => PickEmacsExample();
        p.Children.Add(emacs);
        p.Children.Add(_imSummary);

        _imSourceRow.Children.Add(new TextBlock { Text = Loc.T("読み込むキーボード:", "Keyboard to import:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        _imSourceRow.Children.Add(_imSource);
        _imSource.SelectionChanged += (_, _) => UpdateOptionAvailability();
        p.Children.Add(_imSourceRow);
        p.Children.Add(_imMapPanel);

        foreach (var c in new[] { _imLayers, _imMacros, _imCombos, _imTapDances, _imKeyOverrides, _imTiming, _imFeatures, _imNicola, _imHelper, _imMeasured, _imRecovery })
            _imOptions.Children.Add(c);
        _imMeasured.IsChecked = false;
        p.Children.Add(new TextBlock { Text = Loc.T("読み込む項目:", "Items to import:"), FontWeight = FontWeights.SemiBold });
        p.Children.Add(_imOptions);

        _imModeRow.Children.Add(_imReplace);
        _imModeRow.Children.Add(_imAppend);
        p.Children.Add(_imModeRow);
        // ファイルを読むまでは、読み込む項目と読み込み方は使えない状態にする。ファイルを読むと、ファイルの中身に合わせて項目が選び直されるため
        // （先に選んでも置き換わる。2026-11-08、利用者の指摘）。
        SetImportChoicesEnabled(false);

        _imLayerRow.Children.Add(new TextBlock { Text = Loc.T("行き先:", "Destination:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        _imLayerRow.Children.Add(_imLayerTarget);
        p.Children.Add(_imLayerRow);

        _imPreview.Click += (_, _) => Run(preview: true);
        _imDo.Click += (_, _) => Run(preview: false);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(_imPreview);
        buttons.Children.Add(_imDo);
        p.Children.Add(buttons);
        p.Children.Add(_imReport);
        scroll.Content = p;
        return scroll;
    }

    private void SetImportChoicesEnabled(bool enabled)
    {
        _imOptions.IsEnabled = enabled;
        _imModeRow.IsEnabled = enabled;
        _imLayerRow.IsEnabled = enabled;
        _imSourceRow.IsEnabled = enabled;
    }

    private void PickFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.T("読み込むファイル", "File to Import"),
            InitialDirectory = DialogDefaults.DataFolder,
            ClientGuid = DialogDefaults.SettingsImport,
            Filter = Loc.T("kasane の設定 (*.kasane.json)|*.kasane.json;*.json|すべてのファイル|*.*", "kasane settings (*.kasane.json)|*.kasane.json;*.json|All files|*.*"),
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        LoadFile(dlg.FileName);
    }

    // 同梱の Emacs 風の設定例を読み込む（読み込む項目と読み込み方は、画面で確かめてから「読み込む」）。
    private void PickEmacsExample()
    {
        string? path = SampleLocator.FindEmacs(AppContext.BaseDirectory);
        if (path == null)
        {
            _file = null;
            _imSummary.Text = Loc.T("Emacs の設定例が見つかりません(インストールし直してください)。", "The Emacs example was not found (please reinstall).");
            _imSummary.Foreground = AppTheme.Palette.ErrorText;
            _imPreview.IsEnabled = _imDo.IsEnabled = false;
            SetImportChoicesEnabled(false);
            return;
        }
        LoadFile(path);
    }

    private void LoadFile(string path)
    {
        _imReport.Text = "";
        if (!PortableIO.TryRead(path, out var file, out string? error))
        {
            _file = null;
            _imPath.Text = path;
            _imSummary.Text = Loc.T($"読み込めません: {error}", $"Cannot import: {error}");
            _imSummary.Foreground = AppTheme.Palette.ErrorText;
            _imPreview.IsEnabled = _imDo.IsEnabled = false;
            SetImportChoicesEnabled(false);
            return;
        }
        ShowLoaded(file!, path);
    }

    private void PickNodoka()
    {
        var dlg = new NodokaImportWindow { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        _imReport.Text = "";
        ShowLoaded(dlg.Result, Loc.T("(のどか)", "(Nodoka)") + dlg.SourceLabel);
        // のどかの設定は、今の設定に足す（「置き換え」だと、のどかに無い項目も消える）のを既定にする。
        _imAppend.IsChecked = true;
    }

    private void ShowLoaded(PortableFile file, string path)
    {
        _file = file;
        _imPath.Text = path;
        _imSummary.Foreground = AppTheme.Palette.StrongText;
        var scope = file.ParsedScope!.Value;

        var lines = new List<string> { Loc.T($"{ScopeText(scope)}({file.Exported ?? "日時不明"}、{file.App ?? "?"})", $"{ScopeText(scope)} ({file.Exported ?? "date unknown"}, {file.App ?? "?"})") };
        foreach (var kb in file.Keyboards)
        {
            var parts = new List<string>();
            if (kb.Layers != null) parts.Add(Loc.T($"レイヤー{kb.Layers.Count}", $"Layers {kb.Layers.Count}"));
            if (kb.Macros is { Count: > 0 }) parts.Add(Loc.T($"マクロ{kb.Macros.Count}", $"Macros {kb.Macros.Count}"));
            if (kb.Combos is { Count: > 0 }) parts.Add(Loc.T($"コンボ{kb.Combos.Count}", $"Combos {kb.Combos.Count}"));
            if (kb.TapDances is { Count: > 0 }) parts.Add(Loc.T($"タップダンス{kb.TapDances.Count}", $"Tap dances {kb.TapDances.Count}"));
            if (kb.KeyOverrides is { Count: > 0 }) parts.Add(Loc.T($"キー置換{kb.KeyOverrides.Count}", $"Key overrides {kb.KeyOverrides.Count}"));
            if (kb.Nicola != null) parts.Add("NICOLA");
            if (kb.Helper != null) parts.Add(Loc.T("ヘルパー設定", "Helper settings"));
            lines.Add(Loc.T($"  {kb.UniqId ?? "(UniqIdなし)"} {kb.LayoutName}: {string.Join("、", parts)}", $"  {kb.UniqId ?? "(no UniqId)"} {kb.LayoutName}: {string.Join(", ", parts)}"));
        }
        if (file.Tray != null) lines.Add(Loc.T("  トレイの設定(共通)", "  Tray settings (shared)"));
        _imSummary.Text = string.Join("\n", lines);

        _imSource.Items.Clear();
        for (int i = 0; i < file.Keyboards.Count; i++)
        {
            var kb = file.Keyboards[i];
            _imSource.Items.Add($"{kb.UniqId ?? Loc.T("(UniqIdなし)", "(no UniqId)")}  {kb.LayoutName}");
        }
        _imSource.SelectedIndex = 0;

        bool all = scope == PortableScope.All, layer = scope == PortableScope.Layer;
        _imSourceRow.Visibility = !all && !layer && file.Keyboards.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _imMapPanel.Visibility = all ? Visibility.Visible : Visibility.Collapsed;
        _imModeRow.Visibility = layer ? Visibility.Collapsed : Visibility.Visible;
        _imLayerRow.Visibility = layer ? Visibility.Visible : Visibility.Collapsed;
        _imOptions.Visibility = layer ? Visibility.Collapsed : Visibility.Visible;
        if (all) BuildMapping(file);
        if (layer) BuildLayerTargets(file.Keyboards[0]);
        SetImportChoicesEnabled(true);
        UpdateOptionAvailability();
        _imPreview.IsEnabled = !all;
        _imDo.IsEnabled = true;
    }

    private static string ScopeText(PortableScope s) => s switch
    {
        PortableScope.All => Loc.T("全体一括", "Everything"),
        PortableScope.Layer => Loc.T("レイヤー1枚", "One layer"),
        _ => Loc.T("キーボード単位", "Per keyboard"),
    };

    private void BuildLayerTargets(PortableKeyboard kb)
    {
        _imLayerTarget.Items.Clear();
        _imLayerTarget.Items.Add(Loc.T("新しいレイヤーとして追加", "Add as a new layer"));
        for (int i = 0; i < _project.Layers.Count; i++) _imLayerTarget.Items.Add(Loc.T($"{ProjectService.LayerLabel(_project, i)} を上書き", $"Overwrite {ProjectService.LayerLabel(_project, i)}"));
        _imLayerTarget.SelectedIndex = 0;
    }

    private void BuildMapping(PortableFile file)
    {
        _imMapPanel.Children.Clear();
        _mapCombos.Clear();
        _imMapPanel.Children.Add(new TextBlock
        {
            Text = Loc.T("ファイルのキーボードを、どのキーボードに読み込むかを選びます(UniqId、次に機器の ID が同じものを自動で選んであります)。" +
                   "読み込む前に、今の設定を自動でバックアップします。",
                   "Choose which keyboard each keyboard in the file is imported into (keyboards with the same UniqId, then the same device ID, are selected automatically). " +
                   "Your current settings are backed up automatically before importing."),
            TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Palette.SubText, Margin = new Thickness(0, 0, 0, 6),
        });

        var targets = new List<Target> { new(null, Loc.T("(読み込まない)", "(Don't import)")) };
        foreach (var d in _devices) if (d.UniqId != 0) targets.Add(new Target(d.UniqId, d.DisplayName)); // DisplayName は UniqId から始まる
        foreach (var s in PortableService.LoadAll(null, new PortableReport()))
            if (targets.All(t => t.Id != s.UniqId)) targets.Add(new Target(s.UniqId, Loc.T($"{s.UniqId:X8}  (保存済みの設定)", $"{s.UniqId:X8}  (saved settings)")));
        // ファイルの UniqId が候補に無くても、そのまま新規に作れるようにする。
        foreach (var kb in file.Keyboards)
            if (kb.UniqIdValue is uint u && targets.All(t => t.Id != u)) targets.Add(new Target(u, Loc.T($"{u:X8}  (新規に作る)", $"{u:X8}  (create new)")));

        if (_uniqId != 0 && targets.All(t => t.Id != _uniqId)) targets.Add(new Target(_uniqId, Loc.T($"{_uniqId:X8}  (今のキーボード)", $"{_uniqId:X8}  (current keyboard)")));
        var auto = PortableService.AutoMap(file, _devices.Where(d => d.UniqId != 0).ToList());
        for (int i = 0; i < file.Keyboards.Count; i++)
        {
            var kb = file.Keyboards[i];
            // UniqId の無い（どのキーボードにも使う）設定は、今のキーボードを既定の行き先にする（のどかの取り込みなど）。
            uint? defaultTarget = auto[i].TargetUniqId ?? (kb.UniqIdValue == null && _uniqId != 0 ? _uniqId : null);
            var combo = new ComboBox { Name = $"ImportMapCombo{i}", Width = 430, ItemsSource = targets, Margin = new Thickness(6, 0, 0, 0) };
            combo.SelectedItem = targets.First(t => t.Id == defaultTarget);
            _mapCombos.Add(combo);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock { Text = $"{kb.UniqId ?? Loc.T("(共通)", "(shared)")}  {kb.LayoutName} →", Width = 230, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(combo);
            _imMapPanel.Children.Add(row);
        }
    }

    private PortableKeyboard? SelectedSource =>
        _file == null ? null : _file.Keyboards[Math.Clamp(_imSource.SelectedIndex, 0, _file.Keyboards.Count - 1)];

    /// <summary>ファイルに入っていない項目は、選べなくする。</summary>
    private void UpdateOptionAvailability()
    {
        var file = _file;
        if (file == null) return;
        bool all = file.ParsedScope == PortableScope.All;
        void Set(CheckBox c, Func<PortableKeyboard, bool> has)
        {
            bool avail = all ? file.Keyboards.Any(has) : SelectedSource is { } kb && has(kb);
            if (!avail) c.IsChecked = false;
            else if (!c.IsEnabled && c != _imMeasured) c.IsChecked = true;
            c.IsEnabled = avail;
        }
        Set(_imLayers, k => k.Layers is { Count: > 0 });
        Set(_imMacros, k => k.Macros != null);
        Set(_imCombos, k => k.Combos != null);
        Set(_imTapDances, k => k.TapDances != null);
        Set(_imKeyOverrides, k => k.KeyOverrides != null);
        Set(_imTiming, k => k.Timing != null);
        Set(_imFeatures, k => k.EnabledFeatures != null);
        Set(_imNicola, k => k.Nicola != null);
        Set(_imMeasured, k => k.MeasuredScanCodes is { Count: > 0 });
        bool helperAvail = all ? file.Tray?.Helper != null || file.Keyboards.Any(k => k.Helper != null) : SelectedSource?.Helper != null;
        if (!helperAvail) _imHelper.IsChecked = false;
        else if (!_imHelper.IsEnabled) _imHelper.IsChecked = true;
        _imHelper.IsEnabled = helperAvail;

        // 復旧用の設定は、全体一括のファイルに入っているときだけ。
        bool recoveryAvail = all && file.Recovery != null;
        if (!recoveryAvail) _imRecovery.IsChecked = false;
        else if (!_imRecovery.IsEnabled) _imRecovery.IsChecked = true;
        _imRecovery.IsEnabled = recoveryAvail;
    }

    private ImportOptions BuildOptions()
    {
        bool layerScope = _file?.ParsedScope == PortableScope.Layer;
        if (layerScope)
        {
            // レイヤー1枚: 参照するマクロなども一緒に（ファイルに入っているものだけ）。
            return new ImportOptions
            {
                Mode = ImportMode.Append,
                TargetLayer = _imLayerTarget.SelectedIndex <= 0 ? null : _imLayerTarget.SelectedIndex - 1,
                Timing = false, Features = false, Nicola = false, Combos = false, KeyOverrides = false,
            };
        }
        return new ImportOptions
        {
            Layers = _imLayers.IsEnabled && _imLayers.IsChecked == true,
            Macros = _imMacros.IsEnabled && _imMacros.IsChecked == true,
            Combos = _imCombos.IsEnabled && _imCombos.IsChecked == true,
            TapDances = _imTapDances.IsEnabled && _imTapDances.IsChecked == true,
            KeyOverrides = _imKeyOverrides.IsEnabled && _imKeyOverrides.IsChecked == true,
            Timing = _imTiming.IsEnabled && _imTiming.IsChecked == true,
            Features = _imFeatures.IsEnabled && _imFeatures.IsChecked == true,
            Nicola = _imNicola.IsEnabled && _imNicola.IsChecked == true,
            Helper = _imHelper.IsEnabled && _imHelper.IsChecked == true,
            MeasuredScanCodes = _imMeasured.IsEnabled && _imMeasured.IsChecked == true,
            Recovery = _imRecovery.IsEnabled && _imRecovery.IsChecked == true,
            Mode = _imAppend.IsChecked == true ? ImportMode.Append : ImportMode.Replace,
        };
    }

    private void Run(bool preview)
    {
        if (_file == null) return;
        var scope = _file.ParsedScope!.Value;
        var o = BuildOptions();
        if (scope == PortableScope.All) { RunAll(o); return; }

        var src = SelectedSource!;
        var work = ProjectService.Clone(ResultProject ?? _project);
        var device = LoadDeviceHelper(out string? helperError);
        if (helperError != null) o.Helper = false;
        var global = TrayConfig.Load().Helper;
        bool writeHelper = o.Helper && src.Helper != null && _uniqId != 0;
        if (o.Helper && src.Helper != null && _uniqId == 0) o.Helper = false;

        var res = PortableImporter.Import(src, work, _layout, device, global, o, layerScope: scope == PortableScope.Layer);
        if (helperError != null) res.Report.Warn(Loc.T($"helper.json を読めないため、ヘルパー設定は読み込みません({helperError})", $"Helper settings are not imported because helper.json could not be read ({helperError})"));
        if (_uniqId == 0 && src.Helper != null && _imHelper.IsChecked == true)
            res.Report.Warn(Loc.T("ドライバに接続していないため、ヘルパー設定は読み込めません(キーボードが決まらないため)", "Helper settings cannot be imported because the driver is not connected (the keyboard cannot be determined)"));

        if (res.Report.HasErrors)
        {
            res.Report.Lines.RemoveAll(l => l.Level != ReportLevel.Error); // 失敗したときは、理由だけを見せる（「足しました」などの行は紛らわしい）
            ShowReport(_imReport, res.Report, Loc.T("読み込めません。", "Cannot import."));
            return;
        }
        if (preview)
        {
            ShowReport(_imReport, res.Report, Loc.T("確認の結果です(まだ何も変えていません)。", "Preview result (nothing has been changed yet)."));
            return;
        }

        if (writeHelper && res.Helper != null)
        {
            var ask = MessageBox.Show(this,
                Loc.T("ヘルパー設定は、「読み込む」とすぐに helper.json へ書き込まれます(「適用」や保存を待ちません)。\n続けますか？",
                    "Helper settings are written to helper.json as soon as you import them (without waiting for Apply or Save).\nContinue?"),
                Title, MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ask != MessageBoxResult.OK) return;
            try { res.Helper.Save(HelperConfig.DevicePath(_uniqId)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                res.Report.Error(Loc.T($"helper.json に書けませんでした: {ex.Message}", $"Could not write helper.json: {ex.Message}"));
                ShowReport(_imReport, res.Report, Loc.T("読み込めませんでした。", "Could not import."));
                return;
            }
        }
        ResultProject = work;
        ShowReport(_imReport, res.Report, Loc.T("読み込みました。「閉じる」で編集画面に戻ります。保存と「キーボードに適用」はこのあと行ってください。", "Imported. Click \"Close\" to return to the editor, then save and use \"Apply to keyboard\"."));
    }

    // 復旧用の設定のうち、ドライバに入れるもの（緊急解除、i8042prt、共有範囲）。管理者権限でドライバを開いているときだけ。
    private void ApplyRecoveryToDriver(PortableRecovery recovery, PortableReport report)
    {
        bool hasDriverItems = recovery.EmergencyRelease != null || recovery.I8042 != null || recovery.DeviceScopes is { Count: > 0 };
        if (!hasDriverItems) return;
        if (!_clientWritable || _client == null)
        {
            report.Warn(Loc.T("緊急解除・i8042prt・共有範囲は、管理者権限でドライバを開いていないため、読み込めませんでした。「管理者として再起動」してから、もう一度読み込んでください。",
                              "Emergency release, i8042prt and sharing scopes were not imported because the driver is not open with administrator rights. Restart as administrator and import again."));
            return;
        }
        RecoveryImporter.ApplyDriver(recovery, new ClientRecoveryDriver(_client), _devices.Select(d => d.UniqId).ToList(), report);
    }

    private void RunAll(ImportOptions o)
    {
        var maps = _mapCombos.Select((c, i) => new PortableService.Mapping(i, (c.SelectedItem as Target)?.Id)).ToList();
        if (maps.All(m => m.TargetUniqId == null))
        {
            ShowReport(_imReport, new PortableReport(), Loc.T("読み込み先のキーボードを選んでください。", "Choose the keyboards to import into."));
            return;
        }
        if (!ConfirmSavedForAll(Loc.T("全体一括の読み込み", "Importing everything"))) return;
        if (o.Recovery && MessageBox.Show(this,
                Loc.T("復旧用の設定(トレイの設定、緊急解除、i8042prt、共有範囲、レイアウト)も読み込みます。\n緊急解除・i8042prt・共有範囲は、ドライバ全体の設定で、今の内容が置き換わります(管理者権限のときだけ)。続けますか？",
                      "The recovery settings (tray settings, emergency release, i8042prt, sharing scopes, layouts) will also be imported.\nEmergency release, i8042prt and sharing scopes are driver-wide settings, and the current values will be replaced (administrator rights only). Continue?"),
                Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var ask = MessageBox.Show(this,
            Loc.T(
                "選んだキーボードの保存先の設定を、ファイルの内容で" + (o.Mode == ImportMode.Replace ? "置き換えます" : "追加します") + "。\n" +
                "読み込む前に、今の設定を自動でバックアップします。続けますか？",
                o.Mode == ImportMode.Replace
                    ? "The saved settings of the selected keyboards will be replaced with the contents of the file.\nYour current settings are backed up automatically before importing. Continue?"
                    : "The contents of the file will be added to the saved settings of the selected keyboards.\nYour current settings are backed up automatically before importing. Continue?"),
            Title, MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ask != MessageBoxResult.OK) return;

        var report = new PortableReport();
        var changed = PortableService.ImportAll(_file!, maps, o, null, report);
        if (o.Recovery && _file!.Recovery is { } recovery) ApplyRecoveryToDriver(recovery, report);
        if (_uniqId != 0 && changed.Contains(_uniqId)) CurrentChangedOnDisk = true;
        ShowReport(_imReport, report,
            report.HasErrors && changed.Count == 0 ? Loc.T("読み込めませんでした。", "Could not import.")
            : Loc.T($"{changed.Count}台に読み込みました。ドライバへの適用はまだです(各キーボードを開いて「キーボードに適用」してください)。",
                    $"Imported into {changed.Count} keyboard(s). Not yet applied to the driver (open each keyboard and use \"Apply to keyboard\")."));
    }
}
