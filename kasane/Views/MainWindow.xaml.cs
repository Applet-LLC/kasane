using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kasane.Interop;
using Kasane.Keycodes;
using Kasane.Layout;
using Kasane.Model;

namespace Kasane.Views;

public partial class MainWindow : Window
{
    /// <summary>ドライバに接続できないときに選べる、編集・エクスポート専用の仮想デバイス。</summary>
    private static readonly DeviceInfo OfflineDevice =
        new(0, false, false, 0, "(ドライバ未接続: 編集とエクスポートのみ)");

    private KbdLayerClient? _client;
    private KeymapProject? _project;
    private KeyboardLayout? _layout;
    private int _layer;
    private bool _dirty;
    /// <summary>保存したが、キーボード（ドライバ）へ反映していない（適用していない）キーボードの UniqId。閉じるときの確認に使う。</summary>
    private readonly HashSet<uint> _unapplied = new();
    /// <summary>
    /// 保存済みの設定（project.json）があるのに、読めない・レイアウトが見つからないなどで使わずに開いた。この状態で自動保存すると、
    /// 元の設定を上書きして失うので、自動では保存せず、閉じるときなどに利用者に確かめる。
    /// </summary>
    private bool _autoSaveBlocked;
    private bool _suppressEvents;

    private static readonly Brush ChangedBrush = new SolidColorBrush(Color.FromRgb(0xCF, 0xE8, 0xFF));
    private static readonly Brush ActionBrush = new SolidColorBrush(Color.FromRgb(0xD6, 0xF5, 0xD6));
    private static readonly Brush TransparentKeyBrush = new SolidColorBrush(Color.FromRgb(0xFB, 0xFB, 0xFB));
    private static readonly Brush MeasuredBorder = new SolidColorBrush(Color.FromRgb(0xF0, 0x8C, 0x00));
    private static readonly Brush UnknownBorder = new SolidColorBrush(Color.FromRgb(0xD0, 0x20, 0x20));

    public MainWindow()
    {
        InitializeComponent();
        KbdCanvas.KeyClicked += Keyboard_KeyClicked;
        KbdCanvas.KeyRightClicked += Keyboard_KeyRightClicked;
        LstLayers.ContextMenu = BuildLayerMenu();
    }

    private DeviceInfo? SelectedDevice => CmbDevice.SelectedItem as DeviceInfo;
    private bool IsOnline => _client != null && SelectedDevice is { UniqId: not 0 };
    /// <summary>管理者として実行しておらず、ドライバを読み取り専用で開いている（閲覧専用モード）。</summary>
    private bool IsReadOnly => _client is { IsReadOnly: true };
    /// <summary>適用・キャリブレーション（書き込み系IOCTL）を使える。</summary>
    private bool CanWrite => IsOnline && !IsReadOnly;

    // ------------------------------------------------------------------
    // 起動・デバイス一覧
    // ------------------------------------------------------------------

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _suppressEvents = true;
        foreach (var f in KeyboardLayout.EnumerateLayoutFiles())
            CmbLayout.Items.Add(KeyboardLayout.NameFromPath(f));
        _suppressEvents = false;

        // 保存先に取り込みファイル（kasane-import.json）があれば、設定の無いキーボードだけ取り込んでから、一覧を作る。
        try { Kasane.Model.Portable.PendingImport.RunWithDriver(null, new Kasane.Model.Portable.PortableReport()); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { /* 取り込めなくても、設定画面は開く */ }

        _client = KbdLayerClient.TryOpen(out var error);
        if (_client == null)
            SetStatus($"ドライバに接続できません（{error}）。編集とエクスポートのみ行えます。");
        if (IsReadOnly)
        {
            ReadOnlyBanner.Visibility = Visibility.Visible;
            BtnIdentify.IsEnabled = false;
            // キャリブレーションの画面は、入力確認モード（管理者権限が要らない）があるので、閲覧専用でも開ける。
            // 個別・全体のキャリブレーションは、画面の中で無効になる。
        }
        RefreshDevices(null);
    }

    private void BtnRestartAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (!PersistBeforeLeave()) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                // 昇格した側は、こちらが終わるまで待ってから起動する（単一起動のミューテックスを取り合わないため）。
                Arguments = $"{Kasane.Interop.SingleInstance.WaitPidArgument} {Environment.ProcessId}",
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // ERROR_CANCELLED(1223): UACのダイアログで「いいえ」を選んだ。閲覧専用のまま続ける。
            if (ex.NativeErrorCode != 1223) SetStatus($"管理者として起動できませんでした: {ex.Message}");
            return;
        }
        Close();
    }

    private void RefreshDevices(uint? selectUniqId)
    {
        var devices = new List<DeviceInfo>();
        if (_client != null)
        {
            try { devices.AddRange(_client.ListDevices()); }
            catch (KbdLayerException ex) { SetStatus(ex.Message); }
        }
        devices.Add(OfflineDevice);

        uint keep = selectUniqId ?? SelectedDevice?.UniqId ?? devices[0].UniqId;
        _suppressEvents = true;
        CmbDevice.ItemsSource = devices;
        CmbDevice.SelectedItem = devices.FirstOrDefault(d => d.UniqId == keep) ?? devices[0];
        _suppressEvents = false;
        LoadProjectForSelectedDevice();
    }

    private void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        if (!PersistBeforeLeave()) return;
        RefreshDevices(null);
    }

    private void CmbDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!PersistBeforeLeave())
        {
            // 選択を元に戻す
            _suppressEvents = true;
            CmbDevice.SelectedItem = e.RemovedItems.Count > 0 ? e.RemovedItems[0] : null;
            _suppressEvents = false;
            return;
        }
        LoadProjectForSelectedDevice();
    }

    private void BtnIdentify_Click(object sender, RoutedEventArgs e)
    {
        if (_client == null) { SetStatus("ドライバに接続できないため、キーボードを特定できません。"); return; }
        if (IsReadOnly) { SetStatus("キーボードの特定には管理者権限が必要です（「管理者として再起動」を押してください）。"); return; }
        var devices = ((IEnumerable<DeviceInfo>)CmbDevice.ItemsSource).Where(d => d.UniqId != 0).ToList();
        if (devices.Count == 0) { SetStatus("ドライバが認識しているキーボードがありません。"); return; }
        var dlg = new IdentifyWindow(devices) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.FoundUniqId is uint id)
        {
            var device = devices.First(d => d.UniqId == id);
            if (!Equals(CmbDevice.SelectedItem, device))
                CmbDevice.SelectedItem = device; // SelectionChangedで読み込まれる
            SetStatus($"押されたキーボード: {device.DisplayName}");
        }
    }

    // ------------------------------------------------------------------
    // プロジェクトの読み込み
    // ------------------------------------------------------------------

    private void LoadProjectForSelectedDevice()
    {
        var device = SelectedDevice;
        if (device == null) return;

        var warnings = new List<string>();
        KeymapProject? project = null;
        bool blocked = false;
        try { project = ProjectService.TryLoad(device.UniqId); }
        catch (Exception ex) { warnings.Add($"保存済みの設定を読めませんでした: {ex.Message}"); blocked = File.Exists(ProjectService.ProjectPath(device.UniqId)); }

        KeyboardLayout? layout = null;
        if (project != null)
        {
            var file = ProjectService.FindLayoutFile(project);
            if (file != null) layout = KeyboardLayout.Load(file);
            else
            {
                warnings.Add($"レイアウト「{project.LayoutName}」が見つからないため、既定のレイアウトで開きます（保存済みの設定は使いません。自動では保存しません）");
                blocked = true;
            }
        }
        if (layout == null)
        {
            layout = LoadBundledLayout(GuessDefaultLayoutName());
            if (layout == null) { SetStatus("Layouts フォルダにレイアウトがありません。"); return; }
            project = null;
        }

        string source = "保存済みの設定";
        if (project == null && IsOnline)
        {
            try
            {
                project = ProjectService.ImportFromDriver(_client!, device, layout, true, warnings);
                if (project != null) { source = "ドライバの現在の設定"; _unapplied.Remove(device.UniqId); }
            }
            catch (KbdLayerException ex) { warnings.Add(ex.Message); }
        }
        if (project == null)
        {
            project = ProjectService.CreateDefault(device.UniqId, device.HardwareId, layout, true);
            source = "新規（全キーそのまま）";
        }

        ProjectService.NormalizeShape(project, layout);
        SetProject(project, layout, dirty: false);
        _autoSaveBlocked = blocked;
        SetStatus($"{device.DisplayName}: {source}を読み込みました。" +
                  (warnings.Count > 0 ? " 注意: " + string.Join(" / ", warnings) : ""));
    }

    private static string GuessDefaultLayoutName() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja" ? "JP109" : "US104";

    private static KeyboardLayout? LoadBundledLayout(string name)
    {
        var files = KeyboardLayout.EnumerateLayoutFiles().ToList();
        var file = files.FirstOrDefault(f => KeyboardLayout.NameFromPath(f).Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? files.FirstOrDefault();
        return file != null ? KeyboardLayout.Load(file) : null;
    }

    private void SetProject(KeymapProject project, KeyboardLayout layout, bool dirty)
    {
        _project = project;
        _layout = layout;
        SoftKeyboardPanel.CurrentLayoutFile = layout.FilePath; // キー選択の「画面のキーボード」を、この配列で開く
        _dirty = dirty;
        if (dirty && project.UniqId != 0) _unapplied.Add(project.UniqId);
        _layer = 0;

        _suppressEvents = true;
        if (!CmbLayout.Items.Contains(layout.Name)) CmbLayout.Items.Add(layout.Name);
        CmbLayout.SelectedItem = layout.Name;
        _suppressEvents = false;

        RefreshLayerList();
        RenderKeyboard();
        UpdateButtons();
    }

    // ------------------------------------------------------------------
    // レイアウト
    // ------------------------------------------------------------------

    private void CmbLayout_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _project == null || CmbLayout.SelectedItem is not string name) return;
        if (name == _layout?.Name) return;
        var layout = LoadBundledLayout(name);
        if (layout != null) ChangeLayout(layout, bundled: true);
    }

    private void BtnLoadKle_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null) return;
        // KLE の配列を取り込み（確認・手直しのうえ）、利用者のレイアウトとして保存して、そのレイアウトに切り替える。
        var import = new KleImportWindow { Owner = this };
        if (import.ShowDialog() != true || import.SavedPath == null) return;

        _suppressEvents = true;
        CmbLayout.Items.Clear();
        foreach (var f in KeyboardLayout.EnumerateLayoutFiles())
            CmbLayout.Items.Add(KeyboardLayout.NameFromPath(f));
        CmbLayout.SelectedItem = _layout?.Name;
        _suppressEvents = false;

        try { ChangeLayout(KeyboardLayout.Load(import.SavedPath), bundled: true); }
        catch (Exception ex) { MessageBox.Show(this, $"読み込めませんでした: {ex.Message}", Title); }
    }

    private void ChangeLayout(KeyboardLayout layout, bool bundled)
    {
        var r = MessageBox.Show(this,
            "レイアウトを変えると、キーの割り当て・実測値・コンボはリセットされます（マクロ・タップダンス・キー置換と、設定ダイアログの内容は残ります）。続けますか？",
            Title, MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK)
        {
            _suppressEvents = true;
            CmbLayout.SelectedItem = _layout?.Name;
            _suppressEvents = false;
            return;
        }
        var p = ProjectService.CreateDefault(_project!.UniqId, _project.HardwareId, layout, bundled);
        p.Macros = _project.Macros;
        p.TapDances = _project.TapDances;
        p.KeyOverrides = _project.KeyOverrides;
        p.TapTermMs = _project.TapTermMs;
        p.ComboTermMs = _project.ComboTermMs;
        p.TapDanceTermMs = _project.TapDanceTermMs;
        p.TapHoldMode = _project.TapHoldMode;
        p.QuickTapTermMs = _project.QuickTapTermMs;
        p.TappingToggle = _project.TappingToggle;
        p.EnabledFeatures = _project.EnabledFeatures;
        p.Nicola = _project.Nicola;
        p.ClearNicola = _project.ClearNicola;
        SetProject(p, layout, dirty: true);
        SetStatus($"レイアウトを {layout.Name} に変更しました（{layout.Slots.Count}キー）。");
    }

    // ------------------------------------------------------------------
    // レイヤー
    // ------------------------------------------------------------------

    private void RefreshLayerList()
    {
        _suppressEvents = true;
        LstLayers.Items.Clear();
        for (int i = 0; i < _project!.Layers.Count; i++)
            LstLayers.Items.Add(new ListBoxItem { Content = $" {i} ", Padding = new Thickness(10, 3, 10, 3) });
        LstLayers.SelectedIndex = Math.Min(_layer, _project.Layers.Count - 1);
        _suppressEvents = false;
    }

    private void LstLayers_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || LstLayers.SelectedIndex < 0) return;
        _layer = LstLayers.SelectedIndex;
        RenderKeyboard();
    }

    private void BtnAddLayer_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null || _layout == null) return;
        if (_project.Layers.Count >= KbdLayerClient.MaxLayers)
        {
            SetStatus($"レイヤーは{KbdLayerClient.MaxLayers}個までです。");
            return;
        }
        _project.Layers.Add(ProjectService.DefaultLayer(_layout, _project.Layers.Count));
        _layer = _project.Layers.Count - 1;
        MarkDirty();
        RefreshLayerList();
        RenderKeyboard();
        SetStatus($"レイヤー{_layer}を追加しました（全キー透過）。レイヤー0のどれかのキーを MO({_layer}) 等にすると切り替えられます。");
    }

    private void BtnRemoveLayer_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null || _project.Layers.Count <= 1) return;
        int last = _project.Layers.Count - 1;
        if (MessageBox.Show(this, $"レイヤー{last}を削除しますか？", Title, MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        _project.Layers.RemoveAt(last);
        _layer = Math.Min(_layer, last - 1);
        MarkDirty();
        RefreshLayerList();
        RenderKeyboard();
        if (_project.Layers.Any(l => l.Any(t => ReferencesLayer(t, last))))
            SetStatus($"注意: まだレイヤー{last}を参照するキー（MO({last})等）が残っています。適用時にエラーになるので変更してください。");
    }

    private static bool ReferencesLayer(string token, int layer) =>
        KeycodeTable.TryResolve(token, out var a) && ActionType.ReferencesLayer(a.Type) && a.Param1 == layer;

    // ------------------------------------------------------------------
    // キーボード描画・キーの編集
    // ------------------------------------------------------------------

    private void RenderKeyboard()
    {
        if (_project == null || _layout == null) { KbdCanvas.Render(null, _ => null!); return; }
        KbdCanvas.Render(_layout, VisualFor);
    }

    private KeyVisual VisualFor(KeySlot slot)
    {
        var project = _project!;
        string token = project.Layers[_layer][slot.Index];
        bool isTransparent = token == "KC_TRNS";
        KeycodeTable.TryResolve(token, out var action);

        Brush bg = KeyboardCanvas.DefaultKeyBrush;
        Brush? fg = null;
        if (isTransparent && _layer > 0) { bg = TransparentKeyBrush; fg = Brushes.Silver; }
        else if (action.Type is not (ActionType.Key or ActionType.None or ActionType.Transparent)) bg = ActionBrush;
        else if (_layer > 0 || token != (slot.DefaultKeycode ?? "KC_TRNS")) bg = ChangedBrush;

        var sc = ProjectService.EffectiveScanCode(project, slot);
        Brush border = KeyboardCanvas.DefaultBorderBrush;
        double thickness = 1;
        if (sc == null) { border = UnknownBorder; thickness = 2; }
        else if (ProjectService.IsMeasuredDifferent(project, slot)) { border = MeasuredBorder; thickness = 2; }

        string scText = sc == null ? "不明（要実測）"
            : $"0x{sc:X4}" + (project.MeasuredScanCodes.ContainsKey(slot.Index) ? "（実測）" : "（既定）");
        string tip = $"{slot.Description}\nレイヤー{_layer}: {token}\nスキャンコード: {scText}";

        // 変更していないキーは刻印をそのまま出す（キーコード名はUS配列基準なので、JIS配列では
        // 刻印と食い違うキーがある）。変更したキーだけ、割り当てたキーコードの短い名前を出す。
        bool unchanged = _layer == 0 && token == slot.DefaultKeycode;
        string main = unchanged && slot.MainLegend.Length > 0 ? slot.MainLegend : KeycodeCatalog.ShortName(token);
        return new KeyVisual(main, bg, border, thickness, tip, fg);
    }

    private void Keyboard_KeyClicked(KeySlot slot)
    {
        if (_project == null) return;
        string current = _project.Layers[_layer][slot.Index];
        var dlg = new KeyAssignDialog(slot, _layer, current, _project.Layers.Count, _project.UniqId) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.ResultToken == current) return;
        _project.Layers[_layer][slot.Index] = dlg.ResultToken;
        MarkDirty();
        KbdCanvas.Update(slot, VisualFor(slot));
    }

    private void Keyboard_KeyRightClicked(KeySlot slot)
    {
        if (_project == null) return;
        var menu = new ContextMenu();

        var clearMeasured = new MenuItem
        {
            Header = "実測値を消して既定のスキャンコードに戻す",
            IsEnabled = _project.MeasuredScanCodes.ContainsKey(slot.Index),
        };
        clearMeasured.Click += (_, _) =>
        {
            _project.MeasuredScanCodes.Remove(slot.Index);
            MarkDirty();
            KbdCanvas.Update(slot, VisualFor(slot));
        };
        menu.Items.Add(clearMeasured);

        menu.Items.Add(new Separator());
        string resetTo = _layer == 0 ? slot.DefaultKeycode ?? "KC_TRNS" : "KC_TRNS";
        var reset = new MenuItem { Header = $"このレイヤーの割り当てを戻す（{resetTo}）" };
        reset.Click += (_, _) =>
        {
            _project.Layers[_layer][slot.Index] = resetTo;
            MarkDirty();
            KbdCanvas.Update(slot, VisualFor(slot));
        };
        menu.Items.Add(reset);

        menu.IsOpen = true;
    }

    // ------------------------------------------------------------------
    // キャリブレーション・各種エディタ
    // ------------------------------------------------------------------

    private void BtnCalibrateAll_Click(object sender, RoutedEventArgs e) => OpenCalibration();

    // キャリブレーションの画面。最初は入力確認モードで開く（個別キー・全体のキャリブレーションは、画面の中でモードを選ぶ）。
    private void OpenCalibration()
    {
        if (_project == null || _layout == null) return;
        var dlg = new CalibrationWindow(_layout, _project, SelectedDevice?.UniqId ?? 0, canCalibrate: CanWrite) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Results.Count == 0) return;
        foreach (var (slotIndex, sc) in dlg.Results)
            ProjectService.SetMeasured(_project, _layout.Slots[slotIndex], sc);
        MarkDirty();
        RenderKeyboard();
        int differs = dlg.Results.Count(r => r.Value != _layout.Slots[r.Key].DefaultScanCode);
        SetStatus($"{dlg.Results.Count}キーを実測しました（既定と異なる値: {differs}キー）。適用すると反映されます。");
    }
    private void BtnMacros_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null) return;
        var dlg = new MacroEditorWindow(_project.Macros) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _project.Macros = dlg.Result;
        MarkDirty();
    }

    private void BtnCombos_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null || _layout == null) return;
        var dlg = new ComboEditorWindow(_project, _layout) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _project.Combos = dlg.Result;
        MarkDirty();
    }

    private void BtnTapDances_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null) return;
        var dlg = new TapDanceEditorWindow(_project.TapDances) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _project.TapDances = dlg.Result;
        MarkDirty();
    }

    private void BtnKeyOverrides_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null) return;
        var dlg = new KeyOverrideEditorWindow(_project.KeyOverrides) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _project.KeyOverrides = dlg.Result;
        MarkDirty();
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null) return;
        var dlg = new SettingsDialog(_project, IsOnline ? _client : null, IsOnline ? SelectedDevice!.UniqId : 0, CanWrite) { Owner = this };
        if (dlg.ShowDialog() == true) MarkDirty();
    }

    // ------------------------------------------------------------------
    // 保存・エクスポート・適用
    // ------------------------------------------------------------------

    private bool SaveProject()
    {
        if (_project == null) return false;
        try
        {
            ProjectService.Save(_project);
            _dirty = false;
            _autoSaveBlocked = false;
            UpdateTitle();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存できませんでした: {ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }


    private ContextMenu BuildLayerMenu()
    {
        var menu = new ContextMenu();
        var export = new MenuItem { Name = "MenuExportLayer", Header = "このレイヤーをエクスポート..." };
        export.Click += (_, _) => OpenImportExport(ImportExportWindow.StartTab.Export, Kasane.Model.Portable.PortableScope.Layer);
        var import = new MenuItem { Name = "MenuImportLayer", Header = "レイヤーをインポート..." };
        import.Click += (_, _) => OpenImportExport(ImportExportWindow.StartTab.Import, Kasane.Model.Portable.PortableScope.Layer);
        menu.Items.Add(export);
        menu.Items.Add(import);
        return menu;
    }

    private void BtnImportExport_Click(object sender, RoutedEventArgs e) =>
        OpenImportExport(ImportExportWindow.StartTab.Export, Kasane.Model.Portable.PortableScope.Keyboard);

    private void OpenImportExport(ImportExportWindow.StartTab tab, Kasane.Model.Portable.PortableScope scope)
    {
        if (_project == null || _layout == null) return;
        var devices = CmbDevice.Items.OfType<DeviceInfo>().Where(d => d.UniqId != 0)
            .Select(d => new Kasane.Model.Portable.PortableService.TargetDevice(d.UniqId, d.HardwareId, d.DisplayName)).ToList();
        uint uniq = _project.UniqId;
        var dlg = new ImportExportWindow(_project, _layout, uniq, devices, _dirty, SaveProject, tab, scope, _layer) { Owner = this };
        dlg.ShowDialog();
        if (dlg.ResultProject != null)
        {
            ProjectService.NormalizeShape(dlg.ResultProject, _layout);
            SetProject(dlg.ResultProject, _layout, dirty: true);
            SetStatus("読み込みました（まだ保存・適用していません）。");
        }
        else if (dlg.CurrentChangedOnDisk)
        {
            // 全体一括の読み込みが、保存先を書き換えた。保存先から読み直す。
            var reloaded = ProjectService.TryLoad(uniq);
            var layout = reloaded != null && ProjectService.FindLayoutFile(reloaded) is { } file ? KeyboardLayout.Load(file) : null;
            if (reloaded != null && layout != null)
            {
                ProjectService.NormalizeShape(reloaded, layout);
                SetProject(reloaded, layout, dirty: true);
                SetStatus("全体一括で読み込みました（まだ「キーボードに適用」していません）。");
            }
        }
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null || _layout == null) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "keymap.json と profile.json を書き出すフォルダ" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var warnings = ProjectService.Export(_project, _layout, dlg.FolderName);
            MessageBox.Show(this,
                $"書き出しました:\n{dlg.FolderName}\\keymap.json\n{dlg.FolderName}\\profile.json" +
                (warnings.Count > 0 ? "\n\n注意:\n" + string.Join("\n", warnings) : ""), Title);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "エクスポートできません", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        if (!CanWrite || _project == null || _layout == null) return;
        try
        {
            var warnings = ProjectService.Apply(_client!, _project, _layout);
            SaveProject();
            _unapplied.Remove(_project.UniqId);
            RefreshDeviceStatusOnly();
            SetStatus("適用しました（ON）。設定はドライバに保存され、再起動後も有効です。" +
                      (warnings.Count > 0 ? " 注意: " + string.Join(" / ", warnings) : ""));
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "適用できません（何も変更していません）", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (KbdLayerException ex)
        {
            MessageBox.Show(this, ex.Message + "\n\n途中まで反映された可能性があります。もう一度適用してください。",
                "適用に失敗しました", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnOff_Click(object sender, RoutedEventArgs e)
    {
        if (!IsOnline) return;
        try
        {
            _client!.SetEnabled(SelectedDevice!.UniqId, false);
            RefreshDeviceStatusOnly();
            SetStatus("OFFにしました（キーボードは本来の動作に戻りました。設定はドライバに残っています）。");
        }
        catch (KbdLayerException ex) { SetStatus(ex.Message); }
    }

    /// <summary>プロジェクトを読み直さずに、デバイス一覧（ON/OFF状態）だけ更新する。</summary>
    private void RefreshDeviceStatusOnly()
    {
        if (_client == null) return;
        uint id = SelectedDevice?.UniqId ?? 0;
        try
        {
            var devices = _client.ListDevices();
            devices.Add(OfflineDevice);
            _suppressEvents = true;
            CmbDevice.ItemsSource = devices;
            CmbDevice.SelectedItem = devices.FirstOrDefault(d => d.UniqId == id) ?? OfflineDevice;
            _suppressEvents = false;
        }
        catch (KbdLayerException) { }
        UpdateButtons();
    }

    // ------------------------------------------------------------------
    // 補助
    // ------------------------------------------------------------------

    private void MarkDirty()
    {
        _dirty = true;
        if (_project is { UniqId: not 0 }) _unapplied.Add(_project.UniqId);
        UpdateTitle();
    }

    private void UpdateTitle() => Title = "kasane" + (_dirty ? " *" : "");

    private void UpdateButtons()
    {
        BtnApply.IsEnabled = CanWrite;
        // OFF（SET_LAYERMAP_ENABLED）は読み取り側のIOCTLなので、閲覧専用モードでも使える。
        BtnOff.IsEnabled = IsOnline && SelectedDevice!.Enabled;
        UpdateTitle();
    }

    private void SetStatus(string text) => TxtStatus.Text = text;

    /// <summary>
    /// 別の画面・キーボードへ移る前、閉じる前に、変更を保存する。保存ボタンは無く、ここで自動で保存する。
    /// 保存済みの設定を使わずに開いた状態（<see cref="_autoSaveBlocked"/>）では、元の設定を失うので、保存するかを確かめる。
    /// 続けてよければ true。保存に失敗したとき・利用者が取り消したときは false。
    /// </summary>
    private bool PersistBeforeLeave()
    {
        if (!_dirty) return true;
        if (_autoSaveBlocked)
        {
            var r = MessageBox.Show(this,
                "このキーボードの保存済みの設定は、読めない、またはレイアウトが見つからないため、使わずに開いています。\n" +
                "保存すると、保存済みの設定がこの内容で置き換わります。保存しますか？\n\n" +
                "はい: 保存する　／　いいえ: 保存しない（変更を捨てる）　／　キャンセル: 戻る",
                Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Cancel) return false;
            if (r == MessageBoxResult.No) { _dirty = false; _unapplied.Remove(_project?.UniqId ?? 0); UpdateTitle(); return true; }
        }
        return SaveProject();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!PersistBeforeLeave() || !ConfirmUnapplied()) { e.Cancel = true; return; }
        _client?.Dispose();
    }

    /// <summary>閉じる前に、保存したがキーボードに反映していない設定について確かめる。閉じてよければ true。</summary>
    private bool ConfirmUnapplied()
    {
        var targets = _unapplied.Where(id => id != 0).OrderBy(id => id).ToList();
        switch (ClosePolicy.Decide(targets.Count, _client != null, IsReadOnly))
        {
            case UnappliedPrompt.WarnNotApplied:
                return MessageBox.Show(this,
                    "設定は保存されましたが、キーボードに反映されていません。\n" +
                    "必要に応じて、管理者権限で kasane を起動し、「キーボードに適用（ON）」で反映してください。\n\n" +
                    "このまま閉じますか？", Title, MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK;
            case UnappliedPrompt.AskApply:
                var r = MessageBox.Show(this,
                    $"キーボードに反映していない設定が{targets.Count}台分あります。反映しますか？\n\n" +
                    "はい: 反映して閉じる　／　いいえ: 反映せずに閉じる（設定は保存済み）　／　キャンセル: 戻る",
                    Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel) return false;
                if (r == MessageBoxResult.No) return true;
                foreach (var id in targets)
                {
                    string? error = ApplySavedProject(id);
                    if (error != null)
                    {
                        MessageBox.Show(this, $"{id:X8}: {error}\n\n閉じずに戻ります。", "適用できません", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }
                    _unapplied.Remove(id);
                }
                return true;
            default:
                return true;
        }
    }

    /// <summary>保存済みの設定（project.json）を、そのキーボードへ適用する。失敗の理由を返す（成功は null）。</summary>
    private string? ApplySavedProject(uint uniqId)
    {
        try
        {
            var project = ProjectService.TryLoad(uniqId);
            if (project == null) return "保存済みの設定が見つかりません";
            var file = ProjectService.FindLayoutFile(project);
            var layout = file != null ? KeyboardLayout.Load(file) : null;
            if (layout == null) return $"レイアウト「{project.LayoutName}」が見つかりません";
            ProjectService.NormalizeShape(project, layout);
            ProjectService.Apply(_client!, project, layout);
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or KbdLayerException or IOException)
        {
            return ex.Message;
        }
    }
}
