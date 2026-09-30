using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KbdLayerKeymapGui.Interop;
using KbdLayerKeymapGui.Keycodes;
using KbdLayerKeymapGui.Layout;
using KbdLayerKeymapGui.Model;

namespace KbdLayerKeymapGui.Views;

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
        foreach (var f in KeyboardLayout.EnumerateBundledLayoutFiles())
            CmbLayout.Items.Add(KeyboardLayout.NameFromPath(f));
        _suppressEvents = false;

        _client = KbdLayerClient.TryOpen(out var error);
        if (_client == null)
            SetStatus($"ドライバに接続できません（{error}）。編集とエクスポートのみ行えます。");
        if (IsReadOnly)
        {
            ReadOnlyBanner.Visibility = Visibility.Visible;
            BtnIdentify.IsEnabled = false;
            BtnCalibrateAll.IsEnabled = false;
        }
        RefreshDevices(null);
    }

    private void BtnRestartAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
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
        if (!ConfirmDiscard()) return;
        RefreshDevices(null);
    }

    private void CmbDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (!ConfirmDiscard())
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
        try { project = ProjectService.TryLoad(device.UniqId); }
        catch (Exception ex) { warnings.Add($"保存済みの設定を読めませんでした: {ex.Message}"); }

        KeyboardLayout? layout = null;
        if (project != null)
        {
            var file = ProjectService.FindLayoutFile(project);
            if (file != null) layout = KeyboardLayout.Load(file);
            else warnings.Add($"レイアウト「{project.LayoutName}」が見つからないため、既定のレイアウトで開きます");
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
                if (project != null) source = "ドライバの現在の設定";
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
        SetStatus($"{device.DisplayName}: {source}を読み込みました。" +
                  (warnings.Count > 0 ? " 注意: " + string.Join(" / ", warnings) : ""));
    }

    private static string GuessDefaultLayoutName() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja" ? "JP109" : "US104";

    private static KeyboardLayout? LoadBundledLayout(string name)
    {
        var files = KeyboardLayout.EnumerateBundledLayoutFiles().ToList();
        var file = files.FirstOrDefault(f => KeyboardLayout.NameFromPath(f).Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? files.FirstOrDefault();
        return file != null ? KeyboardLayout.Load(file) : null;
    }

    private void SetProject(KeymapProject project, KeyboardLayout layout, bool dirty)
    {
        _project = project;
        _layout = layout;
        _dirty = dirty;
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
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "KLE JSON (*.json)|*.json", Title = "KLEレイアウトを開く" };
        if (dlg.ShowDialog(this) != true) return;
        try { ChangeLayout(KeyboardLayout.Load(dlg.FileName), bundled: false); }
        catch (Exception ex) { MessageBox.Show(this, $"読み込めませんでした: {ex.Message}", Title); }
    }

    private void ChangeLayout(KeyboardLayout layout, bool bundled)
    {
        var r = MessageBox.Show(this,
            "レイアウトを変えると、キーの割り当て・実測値・コンボはリセットされます（マクロ・タップダンス・タイミングは残ります）。続けますか？",
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
        p.TapTermMs = _project.TapTermMs;
        p.ComboTermMs = _project.ComboTermMs;
        p.TapDanceTermMs = _project.TapDanceTermMs;
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
        KeycodeTable.TryResolve(token, out var a) &&
        a.Type is ActionType.Momentary or ActionType.Toggle or ActionType.Default or ActionType.OneShotLayer
            or ActionType.LayerTap or ActionType.LayerMod &&
        a.Param1 == layer;

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
        var dlg = new KeyAssignDialog(slot, _layer, current, _project.Layers.Count) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.ResultToken == current) return;
        _project.Layers[_layer][slot.Index] = dlg.ResultToken;
        MarkDirty();
        KbdCanvas.Update(slot, VisualFor(slot));
    }

    private void Keyboard_KeyRightClicked(KeySlot slot)
    {
        if (_project == null) return;
        var menu = new ContextMenu();

        var measure = new MenuItem { Header = "このキーを実測する...", IsEnabled = CanWrite };
        measure.Click += (_, _) => RunCalibration(new[] { slot });
        menu.Items.Add(measure);

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

    private void BtnCalibrateAll_Click(object sender, RoutedEventArgs e)
    {
        if (_layout == null) return;
        RunCalibration(_layout.Slots);
    }

    private void RunCalibration(IReadOnlyList<KeySlot> slots)
    {
        if (!IsOnline || _project == null || _layout == null)
        {
            SetStatus("キャリブレーションには、ドライバが認識しているキーボードを選んでください。");
            return;
        }
        if (IsReadOnly)
        {
            SetStatus("キャリブレーションには管理者権限が必要です（「管理者として再起動」を押してください）。");
            return;
        }
        var dlg = new CalibrationWindow(_layout, slots, SelectedDevice!.UniqId) { Owner = this };
        if (dlg.ShowDialog() != true) return;
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

    private void BtnTiming_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null) return;
        DriverConfig? current = null;
        if (IsOnline)
        {
            try { current = _client!.GetConfig(SelectedDevice!.UniqId); }
            catch (KbdLayerException) { /* 古いドライバ等。現在値の表示を省くだけ */ }
        }
        var dlg = new TimingDialog(_project, current) { Owner = this };
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
            UpdateTitle();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存できませんでした: {ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (SaveProject()) SetStatus($"保存しました: {ProjectService.ProjectPath(_project!.UniqId)}");
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
        UpdateTitle();
    }

    private void UpdateTitle() => Title = "LayerDriver Keymap Editor" + (_dirty ? " *" : "");

    private void UpdateButtons()
    {
        BtnApply.IsEnabled = CanWrite;
        // OFF（SET_LAYERMAP_ENABLED）は読み取り側のIOCTLなので、閲覧専用モードでも使える。
        BtnOff.IsEnabled = IsOnline && SelectedDevice!.Enabled;
        UpdateTitle();
    }

    private void SetStatus(string text) => TxtStatus.Text = text;

    private bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        var r = MessageBox.Show(this, "保存していない変更があります。保存しますか？", Title,
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) return SaveProject();
        _dirty = false;
        return true;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscard()) { e.Cancel = true; return; }
        _client?.Dispose();
    }
}
