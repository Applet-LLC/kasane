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
    private readonly RadioButton _exAll = new() { Name = "ExportScopeAll", Content = "全体一括(すべてのキーボードと、トレイの設定)", GroupName = "ExScope", Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _exKeyboard = new() { Name = "ExportScopeKeyboard", Content = "このキーボード", GroupName = "ExScope", Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _exLayer = new() { Name = "ExportScopeLayer", Content = "レイヤー1枚(使っているマクロなども一緒に)", GroupName = "ExScope", Margin = new Thickness(0, 2, 8, 2) };
    private readonly ComboBox _exLayerCombo = new() { Name = "ExportLayerCombo", Width = 80 };
    private readonly CheckBox _exHelper = new() { Name = "ExportHelperCheck", Content = "ヘルパー設定(動作・トリガー・アプリ別レイヤー)を含める", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
    private readonly CheckBox _exMeasured = new() { Name = "ExportMeasuredCheck", Content = "実測したスキャンコードを含める(同じキーボードへのバックアップ用)", Margin = new Thickness(0, 2, 0, 0) };
    private readonly RadioButton _exFormatKasane = new() { Name = "ExportFormatKasane", Content = "kasane の設定ファイル(*.kasane.json。レイヤー・マクロ・コンボなどすべて)", GroupName = "ExFormat", IsChecked = true, Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _exFormatKeymapTool = new() { Name = "ExportFormatKeymapTool", Content = "KeymapTool 用 JSON(keymap.json と profile.json。レイヤーとキャリブレーションだけを書き出し、マクロやコンボなどは含みません)", GroupName = "ExFormat", Margin = new Thickness(0, 2, 0, 2) };
    private readonly TextBox _exReport = ReportBox("ExportReport");
    private readonly Button _exOpen = new() { Name = "ExportOpenFolderButton", Content = "出力先をエクスプローラーで開く", Width = 190, Margin = new Thickness(8, 10, 0, 0) };
    private readonly TextBlock _exDest = new()
    {
        Name = "ExportDestinationText", VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 0, 3),
        Foreground = AppTheme.Palette.SubText, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420,
    };
    private string? _lastExportPath; // 直前に書き出したファイル（または KeymapTool 用 JSON のフォルダ）。書き出す前は null（設定の保存先を出力先として見せる）

    // インポート
    private readonly TextBlock _imPath = new() { Name = "ImportPath", Text = "(ファイルを選んでください)", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _imSummary = new() { Name = "ImportSummary", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly ComboBox _imSource = new() { Name = "ImportSourceCombo", Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _imSourceRow = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
    private readonly StackPanel _imMapPanel = new() { Name = "ImportMapPanel", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
    private readonly StackPanel _imOptions = new() { Name = "ImportOptionsPanel", Margin = new Thickness(0, 0, 0, 6) };
    private readonly CheckBox _imLayers = Check("ImportLayersCheck", "レイヤー(キー割り当て)");
    private readonly CheckBox _imMacros = Check("ImportMacrosCheck", "マクロ");
    private readonly CheckBox _imCombos = Check("ImportCombosCheck", "コンボ");
    private readonly CheckBox _imTapDances = Check("ImportTapDancesCheck", "タップダンス");
    private readonly CheckBox _imKeyOverrides = Check("ImportKeyOverridesCheck", "キー置換");
    private readonly CheckBox _imTiming = Check("ImportTimingCheck", "タイミング");
    private readonly CheckBox _imFeatures = Check("ImportFeaturesCheck", "機能のON/OFF");
    private readonly CheckBox _imNicola = Check("ImportNicolaCheck", "NICOLA(親指シフト)");
    private readonly CheckBox _imHelper = Check("ImportHelperCheck", "ヘルパー設定(読み込むとすぐ helper.json に書かれます)");
    private readonly CheckBox _imMeasured = Check("ImportMeasuredCheck", "実測したスキャンコード(同じレイアウトのときだけ)");
    private readonly StackPanel _imModeRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
    private readonly RadioButton _imReplace = new() { Name = "ImportModeReplace", Content = "置き換え", GroupName = "ImMode", IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
    private readonly RadioButton _imAppend = new() { Name = "ImportModeAppend", Content = "追加(今の設定を残して足す)", GroupName = "ImMode" };
    private readonly StackPanel _imLayerRow = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
    private readonly ComboBox _imLayerTarget = new() { Name = "ImportLayerTargetCombo", Width = 260 };
    private readonly TextBox _imReport = ReportBox("ImportReport");
    private readonly Button _imPreview = new() { Name = "ImportPreviewButton", Content = "確認(変更しない)", Width = 130, IsEnabled = false };
    private readonly Button _imDo = new() { Name = "ImportDoButton", Content = "読み込む", Width = 110, IsEnabled = false };

    private PortableFile? _file;
    private readonly List<ComboBox> _mapCombos = new();

    // クリア
    private readonly KbdLayerClient? _client;
    private readonly bool _canWriteDriver;
    private readonly RadioButton _clThis = new() { Name = "ClearScopeThis", GroupName = "ClScope", IsChecked = true, Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _clAll = new() { Name = "ClearScopeAll", GroupName = "ClScope", Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _clPick = new() { Name = "ClearScopePick", Content = "選んだキーボード", GroupName = "ClScope", Margin = new Thickness(0, 2, 0, 2) };
    private readonly StackPanel _clPickList = new() { Name = "ClearPickList", Margin = new Thickness(24, 0, 0, 6), IsEnabled = false };
    private readonly Dictionary<uint, CheckBox> _clPickBoxes = new();
    private readonly CheckBox _clLayers = Clear("ClearLayersCheck", "レイヤー(キー割り当て)");
    private readonly CheckBox _clMacros = Clear("ClearMacrosCheck", "マクロ");
    private readonly CheckBox _clCombos = Clear("ClearCombosCheck", "コンボ");
    private readonly CheckBox _clTapDances = Clear("ClearTapDancesCheck", "タップダンス");
    private readonly CheckBox _clKeyOverrides = Clear("ClearKeyOverridesCheck", "キー置換");
    private readonly CheckBox _clTiming = Clear("ClearTimingCheck", "タイミング");
    private readonly CheckBox _clFeatures = Clear("ClearFeaturesCheck", "機能のON/OFF(すべて有効に戻す)");
    private readonly CheckBox _clNicola = Clear("ClearNicolaCheck", "NICOLA(親指シフト)");
    private readonly CheckBox _clHelper = Clear("ClearHelperCheck", "ヘルパー設定(helper.json。「すべて」のときは、トレイの共通のヘルパー設定も)");
    private readonly CheckBox _clMeasured = Clear("ClearMeasuredCheck", "実測したスキャンコード");
    private readonly TextBox _clReport = ReportBox("ClearReport");

    /// <summary>今開いているキーボードを消したあとの、保存先から読み直したプロジェクト（消さなかったら null）。</summary>
    public KeymapProject? ClearedProject { get; private set; }

    /// <summary>消したキーボードがある（一覧のON/OFFなどを更新する必要がある）。</summary>
    public bool Cleared { get; private set; }

    /// <summary>ドライバの保存内容も消した（管理者権限があり、ドライバに接続していた）。</summary>
    public bool ClearedDriver { get; private set; }

    private static CheckBox Clear(string name, string text) => new() { Name = name, Content = text, IsChecked = false, Margin = new Thickness(0, 1, 0, 1) };

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
        _project = project;
        _layout = layout;
        _uniqId = uniqId;
        _devices = devices;
        _dirty = dirty;
        _saveProject = saveProject;

        Title = "設定のエクスポート／インポート";
        Width = 760;
        Height = 700;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _tabs.Items.Add(new TabItem { Name = "TabExport", Header = "エクスポート", Content = BuildExport(startScope, startLayer) });
        _tabs.Items.Add(new TabItem { Name = "TabImport", Header = "インポート", Content = BuildImport() });
        _tabs.Items.Add(new TabItem { Name = "TabClear", Header = "クリア", Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = BuildClear() } });
        _tabs.SelectedIndex = startTab == StartTab.Import ? 1 : startTab == StartTab.Clear ? 2 : 0;

        var close = new Button { Name = "CloseButton", Content = "閉じる", IsCancel = true, IsDefault = false, Width = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        close.Click += (_, _) => Close();
        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);
        root.Children.Add(_tabs);
        Content = root;
    }

    private static TextBox ReportBox(string name) => new()
    {
        Name = name, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        MinHeight = 90, Margin = new Thickness(0, 8, 0, 0), Background = AppTheme.Palette.ReportBackground,
    };

    private static CheckBox Check(string name, string text) => new() { Name = name, Content = text, IsChecked = true, Margin = new Thickness(0, 1, 0, 1) };

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
            $"このキーボードに、保存していない変更があります。\n\n{action}は保存先の設定を対象にします。先に保存しますか？\n" +
            "(「いいえ」で保存せずに続けると、保存していない変更は反映されず、失われます)",
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
        p.Children.Add(Help("設定をファイル(*.kasane.json)に書き出します。レイヤーとコンボはスキャンコードで書くので、" +
                            "別のレイアウトのキーボードにも読み込めます。バックアップや、他のパソコンへの持ち運びに使えます。"));

        for (int i = 0; i < _project.Layers.Count; i++) _exLayerCombo.Items.Add(i.ToString());
        _exLayerCombo.SelectedIndex = Math.Clamp(startLayer, 0, Math.Max(0, _project.Layers.Count - 1));

        p.Children.Add(_exAll);
        p.Children.Add(_exKeyboard);
        var layerRow = new StackPanel { Orientation = Orientation.Horizontal };
        layerRow.Children.Add(_exLayer);
        layerRow.Children.Add(new TextBlock { Text = "レイヤー", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        layerRow.Children.Add(_exLayerCombo);
        p.Children.Add(layerRow);
        p.Children.Add(new TextBlock { Text = "形式(「このキーボード」のときだけ選べます)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) });
        p.Children.Add(_exFormatKasane);
        p.Children.Add(_exFormatKeymapTool);
        p.Children.Add(Help("KeymapTool 用 JSON は、コマンドライン版の KeymapTool --apply-simple に渡す入力ファイルです(CLI で管理したい場合に使います)。" +
                            "バックアップや、他のパソコンへの受け渡しには、kasane の設定ファイルを使ってください。"));
        p.Children.Add(_exHelper);
        p.Children.Add(_exMeasured);

        (startScope switch { PortableScope.All => _exAll, PortableScope.Layer => _exLayer, _ => _exKeyboard }).IsChecked = true;
        _exLayerCombo.IsEnabled = _exLayer.IsChecked == true; // レイヤー1枚のときだけ選べる
        _exLayer.Checked += (_, _) => _exLayerCombo.IsEnabled = true;
        _exLayer.Unchecked += (_, _) => _exLayerCombo.IsEnabled = false;
        foreach (var r in new[] { _exAll, _exKeyboard, _exLayer, _exFormatKasane, _exFormatKeymapTool }) { r.Checked += (_, _) => UpdateExportFormat(); r.Unchecked += (_, _) => UpdateExportFormat(); }
        UpdateExportFormat();

        var go = new Button { Name = "ExportDoButton", Content = "書き出す...", Width = 110, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
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
            else { ShowReport(_exReport, new PortableReport(), $"開けませんでした(見つかりません): {path}"); return; }
            System.Diagnostics.Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowReport(_exReport, new PortableReport(), $"エクスプローラーを開けませんでした: {ex.Message}");
        }
    }

    // 現在の出力先を、ボタンの右に出す。書き出したあとはそのファイル（フォルダ）、書き出す前は、書き出す画面が最初に開く設定の保存先。
    // 書き出す前でも「開く」は使える（設定の保存先のフォルダが開く）。
    private void UpdateExportDestination()
    {
        string path = _lastExportPath ?? DialogDefaults.DataFolder;
        _exDest.Text = (_lastExportPath == null ? "出力先(まだ書き出していません。設定の保存先): " : "出力先: ") + path;
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
            Title = "keymap.json と profile.json を書き出すフォルダ",
            InitialDirectory = DialogDefaults.DataFolder,
            ClientGuid = DialogDefaults.KeymapToolExport,
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var warnings = ProjectService.Export(_project, _layout, dlg.FolderName);
            MarkExported(dlg.FolderName);
            ShowReport(_exReport, new PortableReport(),
                $"書き出しました:\n{dlg.FolderName}\\keymap.json\n{dlg.FolderName}\\profile.json" +
                (warnings.Count > 0 ? "\n\n注意:\n" + string.Join("\n", warnings) : ""));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowReport(_exReport, new PortableReport(), $"書き出せませんでした: {ex.Message}");
        }
    }

    private PortableScope ExportScope => _exAll.IsChecked == true ? PortableScope.All : _exLayer.IsChecked == true ? PortableScope.Layer : PortableScope.Keyboard;

    private void DoExport()
    {
        if (ExportAsKeymapTool) { DoExportKeymapTool(); return; }
        var scope = ExportScope;
        var report = new PortableReport();
        var options = new ExportOptions { IncludeHelper = _exHelper.IsChecked == true, IncludeMeasured = _exMeasured.IsChecked == true };
        try
        {
            if (scope == PortableScope.All && !ConfirmSavedForAll("全体一括の書き出し")) return;

            var dlg = new SaveFileDialog
            {
                Title = "書き出し先",
                InitialDirectory = DialogDefaults.DataFolder,
                ClientGuid = DialogDefaults.SettingsExport,
                Filter = "kasane の設定 (*.kasane.json)|*.kasane.json|すべてのファイル|*.*",
                FileName = FileNameFor(scope, _uniqId),
                OverwritePrompt = true,
            };
            if (dlg.ShowDialog(this) != true) return;

            PortableFile file;
            if (scope == PortableScope.All)
            {
                file = PortableService.ExportAll(null, options, report);
            }
            else
            {
                file = PortableExporter.NewFile(scope);
                var device = LoadDeviceHelper(out string? helperError);
                if (helperError != null) report.Warn($"helper.json を読めないため、ヘルパー設定は含めません({helperError})");
                if (scope == PortableScope.Keyboard)
                {
                    file.Keyboards.Add(PortableExporter.ExportKeyboard(_project, _layout, device, options, report));
                }
                else
                {
                    int layer = _exLayerCombo.SelectedIndex;
                    if (layer < 0 || layer >= _project.Layers.Count) { ShowReport(_exReport, report, "レイヤーを選んでください。"); return; }
                    var global = options.IncludeHelper ? TrayConfig.Load().Helper : null;
                    file.Keyboards.Add(PortableExporter.ExportLayer(_project, _layout, layer, options.IncludeHelper ? device : null, global, report));
                }
            }
            PortableIO.Write(dlg.FileName, file);
            MarkExported(dlg.FileName);
            ShowReport(_exReport, report, $"書き出しました: {dlg.FileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowReport(_exReport, report, $"書き出せませんでした: {ex.Message}");
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
        p.Children.Add(Help("選んだ項目だけを、初期状態に戻します。選ばなかった項目は残ります。実行の前に、今の設定を自動でバックアップします" +
                            "(保存先の backup フォルダ。インポートで戻せます)。ドライバにだけ入っていて、画面に出ていない設定は、バックアップに入りません。"));

        p.Children.Add(new TextBlock { Text = "対象のキーボード", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 2) });
        _clThis.Content = _uniqId == 0 ? "このキーボード(接続していないため、画面の設定だけ)" : $"このキーボード({_uniqId:X8})";
        _clAll.Content = $"接続中のすべてのキーボード({_devices.Count}台)";
        p.Children.Add(_clThis);
        p.Children.Add(_clAll);
        p.Children.Add(_clPick);
        foreach (var d in _devices)
        {
            var box = new CheckBox { Name = $"ClearTarget_{d.UniqId:X8}", Content = d.DisplayName, Margin = new Thickness(0, 1, 0, 1), IsChecked = d.UniqId == _uniqId };
            _clPickBoxes[d.UniqId] = box;
            _clPickList.Children.Add(box);
        }
        if (_devices.Count == 0) _clPickList.Children.Add(new TextBlock { Text = "(接続中のキーボードがありません)", Foreground = AppTheme.Palette.SubText });
        p.Children.Add(_clPickList);
        _clPick.Checked += (_, _) => _clPickList.IsEnabled = true;
        _clPick.Unchecked += (_, _) => _clPickList.IsEnabled = false;
        _clAll.IsEnabled = _devices.Count > 0;
        _clPick.IsEnabled = _devices.Count > 0;

        p.Children.Add(new TextBlock { Text = "消す項目", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) });
        var presets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var all = new Button { Name = "ClearSelectAllButton", Content = "すべて選ぶ(全初期化)", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 6, 0) };
        all.Click += (_, _) => { foreach (var c in ClearCategoryBoxes()) c.IsChecked = true; };
        var none = new Button { Name = "ClearSelectNoneButton", Content = "すべて外す", Padding = new Thickness(8, 1, 8, 1) };
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
            Foreground = _canWriteDriver ? Brushes.DarkRed : AppTheme.Palette.SubText,
            Text = _canWriteDriver
                ? "ドライバに保存された設定も、同時に消します(管理者権限があるため)。「同じ型番で共有」のキーボードは、同じ型番のものすべてに効きます。"
                : "ドライバには書き込めない(接続していない、または閲覧専用)ため、画面の設定だけを消します。ドライバの設定は残ります(あとで管理者として「キーボードに適用」するか、このクリアをやり直してください)。",
        });

        var go = new Button { Name = "ClearDoButton", Content = "クリアする...", Width = 110, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
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
        if (!o.Any) { ShowReport(_clReport, new PortableReport(), "消す項目を選んでください。"); return; }
        if (targets.Count == 0) { ShowReport(_clReport, new PortableReport(), "対象のキーボードを選んでください。"); return; }

        // 接続していないキーボード（UniqId 0）は、保存先に設定が無い。画面の設定だけを消す（このタブの「このキーボード」）。
        if (targets.Contains(0) && targets.Count == 1)
        {
            var offline = ProjectService.Clone(_project);
            try { ClearService.ResetProject(offline, _layout, o); }
            catch (ArgumentNullException) { ShowReport(_clReport, new PortableReport(), "レイアウトが無いため、レイヤーを消せません。"); return; }
            ClearedProject = offline;
            Cleared = true;
            ShowReport(_clReport, new PortableReport(), "画面の設定を初期状態にしました(まだ保存していません)。「閉じる」で編集画面に戻ります。");
            return;
        }
        targets = targets.Where(t => t != 0).ToList();

        string driverText = _canWriteDriver ? "ドライバの保存内容も消します" : "画面の設定だけを消します(ドライバの設定は残ります)";
        string sharedWarning = SharedScopeWarning(targets);
        var ask = MessageBox.Show(this,
            $"{targets.Count}台のキーボードの、次の項目を初期状態に戻します。\n\n  " + string.Join("\n  ", o.Names()) +
            $"\n\n{driverText}。今の設定は、実行の前に自動でバックアップします。\n画面で編集していて、まだ保存していない変更は失われます。" +
            (sharedWarning.Length > 0 ? "\n\n" + sharedWarning : "") + "\n\n続けますか？",
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
                report.Warn($"クリアしたあとの設定を読み直せませんでした({ex.Message})。編集画面を開き直してください。");
            }
        }
        ShowReport(_clReport, report,
            report.HasErrors && cleared.Count == 0 ? "クリアできませんでした。"
            : $"{cleared.Count}台をクリアしました。「閉じる」で編集画面に戻ります。");
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
                    shared.Add($"{t:X8}({(scope.Binding == KbdLayerClient.BindingGlobal ? "すべてのキーボードで共有" : "同じ型番で共有")})");
            }
            catch (KbdLayerException) { /* 保存先の範囲を読めなくても、クリアはできる */ }
        }
        return shared.Count == 0 ? ""
            : "注意: 次のキーボードは、ドライバの設定を他のキーボードと共有しています。消すと、共有しているキーボードにも効きます:\n  " + string.Join("\n  ", shared);
    }

    // ------------------------------------------------------------------
    // インポート
    // ------------------------------------------------------------------

    private UIElement BuildImport()
    {
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var p = new StackPanel { Margin = new Thickness(10) };
        p.Children.Add(Help("*.kasane.json を読み込みます。キーボード／レイヤーの読み込みは、今の編集内容に入るだけです" +
                            "(ドライバへは、いつもどおり「キーボードに適用」で反映します)。"));

        var pick = new Button { Name = "ImportPickButton", Content = "ファイルを選ぶ...", Width = 130 };
        pick.Click += (_, _) => PickFile();
        var nodoka = new Button { Name = "ImportNodokaButton", Content = "のどかの設定を取り込む...", Width = 190, Margin = new Thickness(8, 0, 0, 0) };
        nodoka.Click += (_, _) => PickNodoka();
        var pickRow = new StackPanel { Orientation = Orientation.Horizontal };
        pickRow.Children.Add(pick);
        pickRow.Children.Add(nodoka);
        pickRow.Children.Add(_imPath);
        p.Children.Add(pickRow);
        p.Children.Add(_imSummary);

        _imSourceRow.Children.Add(new TextBlock { Text = "読み込むキーボード:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        _imSourceRow.Children.Add(_imSource);
        _imSource.SelectionChanged += (_, _) => UpdateOptionAvailability();
        p.Children.Add(_imSourceRow);
        p.Children.Add(_imMapPanel);

        foreach (var c in new[] { _imLayers, _imMacros, _imCombos, _imTapDances, _imKeyOverrides, _imTiming, _imFeatures, _imNicola, _imHelper, _imMeasured })
            _imOptions.Children.Add(c);
        _imMeasured.IsChecked = false;
        p.Children.Add(new TextBlock { Text = "読み込む項目:", FontWeight = FontWeights.SemiBold });
        p.Children.Add(_imOptions);

        _imModeRow.Children.Add(_imReplace);
        _imModeRow.Children.Add(_imAppend);
        p.Children.Add(_imModeRow);

        _imLayerRow.Children.Add(new TextBlock { Text = "行き先:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
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

    private void PickFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "読み込むファイル",
            InitialDirectory = DialogDefaults.DataFolder,
            ClientGuid = DialogDefaults.SettingsImport,
            Filter = "kasane の設定 (*.kasane.json)|*.kasane.json;*.json|すべてのファイル|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        LoadFile(dlg.FileName);
    }

    private void LoadFile(string path)
    {
        _imReport.Text = "";
        if (!PortableIO.TryRead(path, out var file, out string? error))
        {
            _file = null;
            _imPath.Text = path;
            _imSummary.Text = $"読み込めません: {error}";
            _imSummary.Foreground = Brushes.Firebrick;
            _imPreview.IsEnabled = _imDo.IsEnabled = false;
            return;
        }
        ShowLoaded(file!, path);
    }

    private void PickNodoka()
    {
        var dlg = new NodokaImportWindow { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        _imReport.Text = "";
        ShowLoaded(dlg.Result, "(のどか)" + dlg.SourceLabel);
        // のどかの設定は、今の設定に足す（「置き換え」だと、のどかに無い項目も消える）のを既定にする。
        _imAppend.IsChecked = true;
    }

    private void ShowLoaded(PortableFile file, string path)
    {
        _file = file;
        _imPath.Text = path;
        _imSummary.Foreground = AppTheme.Palette.StrongText;
        var scope = file.ParsedScope!.Value;

        var lines = new List<string> { $"{ScopeText(scope)}({file.Exported ?? "日時不明"}、{file.App ?? "?"})" };
        foreach (var kb in file.Keyboards)
        {
            var parts = new List<string>();
            if (kb.Layers != null) parts.Add($"レイヤー{kb.Layers.Count}");
            if (kb.Macros is { Count: > 0 }) parts.Add($"マクロ{kb.Macros.Count}");
            if (kb.Combos is { Count: > 0 }) parts.Add($"コンボ{kb.Combos.Count}");
            if (kb.TapDances is { Count: > 0 }) parts.Add($"タップダンス{kb.TapDances.Count}");
            if (kb.KeyOverrides is { Count: > 0 }) parts.Add($"キー置換{kb.KeyOverrides.Count}");
            if (kb.Nicola != null) parts.Add("NICOLA");
            if (kb.Helper != null) parts.Add("ヘルパー設定");
            lines.Add($"  {kb.UniqId ?? "(UniqIdなし)"} {kb.LayoutName}: {string.Join("、", parts)}");
        }
        if (file.Tray != null) lines.Add("  トレイの設定(共通)");
        _imSummary.Text = string.Join("\n", lines);

        _imSource.Items.Clear();
        for (int i = 0; i < file.Keyboards.Count; i++)
        {
            var kb = file.Keyboards[i];
            _imSource.Items.Add($"{kb.UniqId ?? "(UniqIdなし)"}  {kb.LayoutName}");
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
        UpdateOptionAvailability();
        _imPreview.IsEnabled = !all;
        _imDo.IsEnabled = true;
    }

    private static string ScopeText(PortableScope s) => s switch
    {
        PortableScope.All => "全体一括",
        PortableScope.Layer => "レイヤー1枚",
        _ => "キーボード単位",
    };

    private void BuildLayerTargets(PortableKeyboard kb)
    {
        _imLayerTarget.Items.Clear();
        _imLayerTarget.Items.Add("新しいレイヤーとして追加");
        for (int i = 0; i < _project.Layers.Count; i++) _imLayerTarget.Items.Add($"{ProjectService.LayerLabel(_project, i)} を上書き");
        _imLayerTarget.SelectedIndex = 0;
    }

    private void BuildMapping(PortableFile file)
    {
        _imMapPanel.Children.Clear();
        _mapCombos.Clear();
        _imMapPanel.Children.Add(new TextBlock
        {
            Text = "ファイルのキーボードを、どのキーボードに読み込むかを選びます(UniqId、次に機器の ID が同じものを自動で選んであります)。" +
                   "読み込む前に、今の設定を自動でバックアップします。",
            TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Palette.SubText, Margin = new Thickness(0, 0, 0, 6),
        });

        var targets = new List<Target> { new(null, "(読み込まない)") };
        foreach (var d in _devices) if (d.UniqId != 0) targets.Add(new Target(d.UniqId, d.DisplayName)); // DisplayName は UniqId から始まる
        foreach (var s in PortableService.LoadAll(null, new PortableReport()))
            if (targets.All(t => t.Id != s.UniqId)) targets.Add(new Target(s.UniqId, $"{s.UniqId:X8}  (保存済みの設定)"));
        // ファイルの UniqId が候補に無くても、そのまま新規に作れるようにする。
        foreach (var kb in file.Keyboards)
            if (kb.UniqIdValue is uint u && targets.All(t => t.Id != u)) targets.Add(new Target(u, $"{u:X8}  (新規に作る)"));

        if (_uniqId != 0 && targets.All(t => t.Id != _uniqId)) targets.Add(new Target(_uniqId, $"{_uniqId:X8}  (今のキーボード)"));
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
            row.Children.Add(new TextBlock { Text = $"{kb.UniqId ?? "(共通)"}  {kb.LayoutName} →", Width = 230, VerticalAlignment = VerticalAlignment.Center });
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
        if (helperError != null) res.Report.Warn($"helper.json を読めないため、ヘルパー設定は読み込みません({helperError})");
        if (_uniqId == 0 && src.Helper != null && _imHelper.IsChecked == true)
            res.Report.Warn("ドライバに接続していないため、ヘルパー設定は読み込めません(キーボードが決まらないため)");

        if (res.Report.HasErrors)
        {
            res.Report.Lines.RemoveAll(l => l.Level != ReportLevel.Error); // 失敗したときは、理由だけを見せる（「足しました」などの行は紛らわしい）
            ShowReport(_imReport, res.Report, "読み込めません。");
            return;
        }
        if (preview)
        {
            ShowReport(_imReport, res.Report, "確認の結果です(まだ何も変えていません)。");
            return;
        }

        if (writeHelper && res.Helper != null)
        {
            var ask = MessageBox.Show(this,
                "ヘルパー設定は、「読み込む」とすぐに helper.json へ書き込まれます(「適用」や保存を待ちません)。\n続けますか？",
                Title, MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ask != MessageBoxResult.OK) return;
            try { res.Helper.Save(HelperConfig.DevicePath(_uniqId)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                res.Report.Error($"helper.json に書けませんでした: {ex.Message}");
                ShowReport(_imReport, res.Report, "読み込めませんでした。");
                return;
            }
        }
        ResultProject = work;
        ShowReport(_imReport, res.Report, "読み込みました。「閉じる」で編集画面に戻ります。保存と「キーボードに適用」はこのあと行ってください。");
    }

    private void RunAll(ImportOptions o)
    {
        var maps = _mapCombos.Select((c, i) => new PortableService.Mapping(i, (c.SelectedItem as Target)?.Id)).ToList();
        if (maps.All(m => m.TargetUniqId == null))
        {
            ShowReport(_imReport, new PortableReport(), "読み込み先のキーボードを選んでください。");
            return;
        }
        if (!ConfirmSavedForAll("全体一括の読み込み")) return;
        var ask = MessageBox.Show(this,
            "選んだキーボードの保存先の設定を、ファイルの内容で" + (o.Mode == ImportMode.Replace ? "置き換えます" : "追加します") + "。\n" +
            "読み込む前に、今の設定を自動でバックアップします。続けますか？",
            Title, MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ask != MessageBoxResult.OK) return;

        var report = new PortableReport();
        var changed = PortableService.ImportAll(_file!, maps, o, null, report);
        if (_uniqId != 0 && changed.Contains(_uniqId)) CurrentChangedOnDisk = true;
        ShowReport(_imReport, report,
            report.HasErrors && changed.Count == 0 ? "読み込めませんでした。"
            : $"{changed.Count}台に読み込みました。ドライバへの適用はまだです(各キーボードを開いて「キーボードに適用」してください)。");
    }
}
