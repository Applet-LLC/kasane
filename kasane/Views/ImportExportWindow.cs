using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    public enum StartTab { Export, Import }

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
    private readonly RadioButton _exAll = new() { Name = "ExportScopeAll", Content = "全体一括（すべてのキーボードと、トレイの設定）", GroupName = "ExScope", Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _exKeyboard = new() { Name = "ExportScopeKeyboard", Content = "このキーボード", GroupName = "ExScope", Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _exLayer = new() { Name = "ExportScopeLayer", Content = "レイヤー1枚（使っているマクロなども一緒に）", GroupName = "ExScope", Margin = new Thickness(0, 2, 8, 2) };
    private readonly ComboBox _exLayerCombo = new() { Name = "ExportLayerCombo", Width = 80 };
    private readonly CheckBox _exHelper = new() { Name = "ExportHelperCheck", Content = "ヘルパー設定（動作・トリガー・アプリ別レイヤー）を含める", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
    private readonly CheckBox _exMeasured = new() { Name = "ExportMeasuredCheck", Content = "実測したスキャンコードを含める（同じキーボードへのバックアップ用）", Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBox _exReport = ReportBox("ExportReport");

    // インポート
    private readonly TextBlock _imPath = new() { Name = "ImportPath", Text = "（ファイルを選んでください）", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _imSummary = new() { Name = "ImportSummary", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly ComboBox _imSource = new() { Name = "ImportSourceCombo", Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _imSourceRow = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
    private readonly StackPanel _imMapPanel = new() { Name = "ImportMapPanel", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
    private readonly StackPanel _imOptions = new() { Name = "ImportOptionsPanel", Margin = new Thickness(0, 0, 0, 6) };
    private readonly CheckBox _imLayers = Check("ImportLayersCheck", "レイヤー（キー割り当て）");
    private readonly CheckBox _imMacros = Check("ImportMacrosCheck", "マクロ");
    private readonly CheckBox _imCombos = Check("ImportCombosCheck", "コンボ");
    private readonly CheckBox _imTapDances = Check("ImportTapDancesCheck", "タップダンス");
    private readonly CheckBox _imKeyOverrides = Check("ImportKeyOverridesCheck", "キー置換");
    private readonly CheckBox _imTiming = Check("ImportTimingCheck", "タイミング");
    private readonly CheckBox _imFeatures = Check("ImportFeaturesCheck", "機能のON/OFF");
    private readonly CheckBox _imNicola = Check("ImportNicolaCheck", "NICOLA（親指シフト）");
    private readonly CheckBox _imHelper = Check("ImportHelperCheck", "ヘルパー設定（読み込むとすぐ helper.json に書かれます）");
    private readonly CheckBox _imMeasured = Check("ImportMeasuredCheck", "実測したスキャンコード（同じレイアウトのときだけ）");
    private readonly StackPanel _imModeRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
    private readonly RadioButton _imReplace = new() { Name = "ImportModeReplace", Content = "置き換え", GroupName = "ImMode", IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
    private readonly RadioButton _imAppend = new() { Name = "ImportModeAppend", Content = "追加（今の設定を残して足す）", GroupName = "ImMode" };
    private readonly StackPanel _imLayerRow = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
    private readonly ComboBox _imLayerTarget = new() { Name = "ImportLayerTargetCombo", Width = 260 };
    private readonly TextBox _imReport = ReportBox("ImportReport");
    private readonly Button _imPreview = new() { Name = "ImportPreviewButton", Content = "確認（変更しない）", Width = 130, IsEnabled = false };
    private readonly Button _imDo = new() { Name = "ImportDoButton", Content = "読み込む", Width = 110, IsEnabled = false };

    private PortableFile? _file;
    private readonly List<ComboBox> _mapCombos = new();

    /// <summary>キーボード／レイヤーの読み込みの結果（プロジェクトの複製。読み込まなかったら null）。</summary>
    public KeymapProject? ResultProject { get; private set; }

    /// <summary>全体一括の読み込みが、今開いているキーボードの保存先を書き換えた（保存先から読み直す必要がある）。</summary>
    public bool CurrentChangedOnDisk { get; private set; }

    public bool Imported => ResultProject != null || CurrentChangedOnDisk;

    public ImportExportWindow(KeymapProject project, KeyboardLayout layout, uint uniqId,
                              IReadOnlyList<PortableService.TargetDevice> devices, bool dirty, Func<bool>? saveProject,
                              StartTab startTab, PortableScope startScope, int startLayer)
    {
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
        _tabs.SelectedIndex = startTab == StartTab.Import ? 1 : 0;

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
        MinHeight = 90, Margin = new Thickness(0, 8, 0, 0), Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7)),
    };

    private static CheckBox Check(string name, string text) => new() { Name = name, Content = text, IsChecked = true, Margin = new Thickness(0, 1, 0, 1) };

    private static TextBlock Help(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 8) };

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
            "（「いいえ」で保存せずに続けると、保存していない変更は反映されません／失われます）",
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
        p.Children.Add(Help("設定をファイル（*.kasane.json）に書き出します。レイヤーとコンボはスキャンコードで書くので、" +
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
        p.Children.Add(_exHelper);
        p.Children.Add(_exMeasured);

        (startScope switch { PortableScope.All => _exAll, PortableScope.Layer => _exLayer, _ => _exKeyboard }).IsChecked = true;
        _exLayerCombo.IsEnabled = _exLayer.IsChecked == true; // レイヤー1枚のときだけ選べる
        _exLayer.Checked += (_, _) => _exLayerCombo.IsEnabled = true;
        _exLayer.Unchecked += (_, _) => _exLayerCombo.IsEnabled = false;

        var go = new Button { Name = "ExportDoButton", Content = "書き出す...", Width = 110, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        go.Click += (_, _) => DoExport();
        p.Children.Add(go);
        p.Children.Add(_exReport);
        return p;
    }

    private PortableScope ExportScope => _exAll.IsChecked == true ? PortableScope.All : _exLayer.IsChecked == true ? PortableScope.Layer : PortableScope.Keyboard;

    private void DoExport()
    {
        var scope = ExportScope;
        var report = new PortableReport();
        var options = new ExportOptions { IncludeHelper = _exHelper.IsChecked == true, IncludeMeasured = _exMeasured.IsChecked == true };
        try
        {
            if (scope == PortableScope.All && !ConfirmSavedForAll("全体一括の書き出し")) return;

            var dlg = new SaveFileDialog
            {
                Title = "書き出し先",
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
                if (helperError != null) report.Warn($"helper.json を読めないため、ヘルパー設定は含めません（{helperError}）");
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
            ShowReport(_exReport, report, $"書き出しました: {dlg.FileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowReport(_exReport, report, $"書き出せませんでした: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // インポート
    // ------------------------------------------------------------------

    private UIElement BuildImport()
    {
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var p = new StackPanel { Margin = new Thickness(10) };
        p.Children.Add(Help("*.kasane.json を読み込みます。キーボード／レイヤーの読み込みは、今の編集内容に入るだけです" +
                            "（ドライバへは、いつもどおり「キーボードに適用」で反映します）。"));

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
        ShowLoaded(dlg.Result, "(のどか) " + dlg.SourceLabel);
        // のどかの設定は、今の設定に足す（「置き換え」だと、のどかに無い項目も消える）のを既定にする。
        _imAppend.IsChecked = true;
    }

    private void ShowLoaded(PortableFile file, string path)
    {
        _file = file;
        _imPath.Text = path;
        _imSummary.Foreground = Brushes.Black;
        var scope = file.ParsedScope!.Value;

        var lines = new List<string> { $"{ScopeText(scope)}（{file.Exported ?? "日時不明"}、{file.App ?? "?"}）" };
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
        if (file.Tray != null) lines.Add("  トレイの設定（共通）");
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
        for (int i = 0; i < _project.Layers.Count; i++) _imLayerTarget.Items.Add($"レイヤー{i} を上書き");
        _imLayerTarget.SelectedIndex = 0;
    }

    private void BuildMapping(PortableFile file)
    {
        _imMapPanel.Children.Clear();
        _mapCombos.Clear();
        _imMapPanel.Children.Add(new TextBlock
        {
            Text = "ファイルのキーボードを、どのキーボードに読み込むかを選びます（UniqId、次に機器の ID が同じものを自動で選んであります）。" +
                   "読み込む前に、今の設定を自動でバックアップします。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 6),
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
            row.Children.Add(new TextBlock { Text = $"{kb.UniqId ?? "?"}  {kb.LayoutName} →", Width = 230, VerticalAlignment = VerticalAlignment.Center });
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
        if (helperError != null) res.Report.Warn($"helper.json を読めないため、ヘルパー設定は読み込みません（{helperError}）");
        if (_uniqId == 0 && src.Helper != null && _imHelper.IsChecked == true)
            res.Report.Warn("ドライバに接続していないため、ヘルパー設定は読み込めません（キーボードが決まらないため）");

        if (res.Report.HasErrors)
        {
            res.Report.Lines.RemoveAll(l => l.Level != ReportLevel.Error); // 失敗したときは、理由だけを見せる（「足しました」などの行は紛らわしい）
            ShowReport(_imReport, res.Report, "読み込めません。");
            return;
        }
        if (preview)
        {
            ShowReport(_imReport, res.Report, "確認の結果です（まだ何も変えていません）。");
            return;
        }

        if (writeHelper && res.Helper != null)
        {
            var ask = MessageBox.Show(this,
                "ヘルパー設定は、「読み込む」とすぐに helper.json へ書き込まれます（「適用」や保存を待ちません）。\n続けますか？",
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
            "選んだキーボードの保存先の設定を、ファイルの内容で" + (o.Mode == ImportMode.Replace ? "置き換え" : "更新") + "ます。\n" +
            "読み込む前に、今の設定を自動でバックアップします。続けますか？",
            Title, MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ask != MessageBoxResult.OK) return;

        var report = new PortableReport();
        var changed = PortableService.ImportAll(_file!, maps, o, null, report);
        if (_uniqId != 0 && changed.Contains(_uniqId)) CurrentChangedOnDisk = true;
        ShowReport(_imReport, report,
            report.HasErrors && changed.Count == 0 ? "読み込めませんでした。"
            : $"{changed.Count}台に読み込みました。「適用」はしていません（各キーボードを開いて「キーボードに適用」してください）。");
    }
}
