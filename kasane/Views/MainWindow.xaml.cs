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
    private static DeviceInfo? _offlineDevice;
    private static DeviceInfo OfflineDevice => _offlineDevice ??=
        new(0, false, false, 0, Loc.T("(ドライバ未接続: 編集とエクスポートのみ)", "(Driver not connected: edit and export only)"));

    private KbdLayerClient? _client;
    /// <summary>Windows 上のキーボード名（UniqId → FriendlyName・ShellFriendlyName）。一覧を更新するときに取り直す。</summary>
    private Dictionary<uint, KeyboardNameInfo> _windowsNames = new();
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

    // キーの色（色のモードで変わる。AppTheme.Palette）。
    private static Brush ChangedBrush => AppTheme.Palette.KeyChanged;
    private static Brush ActionBrush => AppTheme.Palette.KeyAction;
    private static Brush TransparentKeyBrush => AppTheme.Palette.KeyTransparent;
    private static Brush MeasuredBorder => AppTheme.Palette.KeyMeasuredBorder;
    private static Brush UnknownBorder => AppTheme.Palette.KeyUnknownBorder;

    public MainWindow()
    {
        InitializeComponent();
        KbdCanvas.KeyClicked += Keyboard_KeyClicked;
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.F1 && !e.Handled) { e.Handled = true; OpenManual(); } }; // F1 でマニュアル
        KbdCanvas.KeyRightClicked += Keyboard_KeyRightClicked;
        LstLayers.ContextMenu = BuildLayerMenu();
        MacroDirectory.Provider = CurrentMacros; // 他の画面の「マクロ...」の選択肢
        MacroDirectory.TapDanceProvider = CurrentTapDances; // 同じく「タップダンス...」の選択肢
        AppTheme.Changed += OnThemeChanged; // 色のモードを変えたら、キー図を描き直す
        Closed += (_, _) => AppTheme.Changed -= OnThemeChanged;

        if (Content is FrameworkElement root) root.ContextMenu = BuildMainMenu();
        KbdCanvas.ContextMenuOpening += (_, e) => e.Handled = true;
    }

    /// <summary>利用者がつけた名前を返す。いま開いているキーボードは、編集中の内容（まだ保存していない名前の変更）を使う。</summary>
    private string? UserNameFor(uint uniqId)
    {
        if (_project != null && _project.UniqId == uniqId) return _project.KeyboardName;
        try { return ProjectService.TryLoad(uniqId)?.KeyboardName; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    /// <summary>一覧の各キーボードに、表示名（UniqId・つけた名前・Windows 上の名前・ハードウェアID）を付ける。</summary>
    private List<DeviceInfo> LabelDevices(List<DeviceInfo> devices) =>
        devices.Select(d => d.UniqId == 0 ? d : d with
        {
            Label = KeyboardNameRules.DisplayName(d.UniqId, UserNameFor(d.UniqId),
                _windowsNames.TryGetValue(d.UniqId, out var n) ? n : null, d.HardwareId),
        }).ToList();

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
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { /* 取り込めなくても、エディタは開く */ }

        _client = KbdLayerClient.TryOpen(out var error);
        if (_client == null)
            SetStatus(Loc.T($"ドライバに接続できません({error})。編集とエクスポートのみ行えます。", $"Cannot connect to the driver ({error}). Only editing and export are available."), StatusKind.Warn);
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
        // 閉じる前の確認は、新しいプロセスを起こす前に済ませる（起こしてから確かめると、新しい側は元のプロセスの終了を
        // 15 秒しか待たないので、利用者が確認に答える前に諦めて終わる）。
        if (!ConfirmLeave()) return;
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
            if (ex.NativeErrorCode != 1223) SetStatus(Loc.T($"管理者として起動できませんでした: {ex.Message}", $"Could not start as administrator: {ex.Message}"), StatusKind.Error);
            return;
        }
        _leaveConfirmed = true;
        Close();
    }

    private void RefreshDevices(uint? selectUniqId)
    {
        var devices = new List<DeviceInfo>();
        if (_client != null)
        {
            try { devices.AddRange(_client.ListDevices()); }
            catch (KbdLayerException ex) { SetStatus(ex.Message, StatusKind.Error); }
        }
        // Windows 上の名前は、一覧を更新するたびに取り直す（キーボードの抜き差しに追従する）。取れなくても、一覧は出る。
        try { _windowsNames = KeyboardNameResolver.Resolve(devices); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { _windowsNames = new(); }
        devices = LabelDevices(devices);
        devices.Add(OfflineDevice);

        uint keep = selectUniqId ?? SelectedDevice?.UniqId ?? devices[0].UniqId;
        _suppressEvents = true;
        CmbDevice.ItemsSource = devices;
        CmbDevice.SelectedItem = devices.FirstOrDefault(d => d.UniqId == keep) ?? devices[0];
        _suppressEvents = false;
        LoadProjectForSelectedDevice();
    }

    private void BtnRenameKeyboard_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null || SelectedDevice is not { UniqId: not 0 } device) return;
        var dlg = new TextPromptWindow(Loc.T("キーボードの名前", "Keyboard name"), Loc.T("このキーボードの名前:", "Name for this keyboard:"), _project.KeyboardName ?? "", "KeyboardNameBox") { Owner = this };
        if (dlg.ShowDialog() != true) return;
        string name = dlg.Value.Trim();
        _project.KeyboardName = name.Length == 0 ? null : name;
        MarkDirty();
        RefreshDeviceStatusOnly();
        SetStatus(name.Length == 0 ? Loc.T("キーボードの名前を消しました。", "Removed the keyboard name.") : Loc.T($"キーボードの名前を「{name}」にしました。", $"Set the keyboard name to \"{name}\"."));
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
        if (_client == null) { SetStatus(Loc.T("ドライバに接続できないため、キーボードを特定できません。", "Cannot identify the keyboard because the driver is not connected."), StatusKind.Warn); return; }
        if (IsReadOnly) { SetStatus(Loc.T("キーボードの特定には管理者権限が必要です(「管理者として再起動」を押してください)。", "Identifying a keyboard requires administrator rights (click \"Restart as administrator\")."), StatusKind.Warn); return; }
        var devices = ((IEnumerable<DeviceInfo>)CmbDevice.ItemsSource).Where(d => d.UniqId != 0).ToList();
        if (devices.Count == 0) { SetStatus(Loc.T("ドライバが認識しているキーボードがありません。", "The driver does not recognize any keyboards."), StatusKind.Warn); return; }
        var dlg = new IdentifyWindow(devices) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.FoundUniqId is uint id)
        {
            var device = devices.First(d => d.UniqId == id);
            if (!Equals(CmbDevice.SelectedItem, device))
                CmbDevice.SelectedItem = device; // SelectionChangedで読み込まれる
            SetStatus(Loc.T($"押されたキーボード: {device.DisplayName}", $"Keyboard pressed: {device.DisplayName}"));
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
        catch (Exception ex) { warnings.Add(Loc.T($"保存済みの設定を読めませんでした: {ex.Message}", $"Could not read the saved settings: {ex.Message}")); blocked = File.Exists(ProjectService.ProjectPath(device.UniqId)); }

        KeyboardLayout? layout = null;
        if (project != null)
        {
            var file = ProjectService.FindLayoutFile(project);
            if (file != null) layout = KeyboardLayout.Load(file);
            else
            {
                warnings.Add(Loc.T($"レイアウト「{project.LayoutName}」が見つからないため、既定のレイアウトで開きます(保存済みの設定は使いません。自動では保存しません)",
                                   $"Layout \"{project.LayoutName}\" was not found, so the default layout is open (the saved settings are not used and will not be saved automatically)"));
                blocked = true;
            }
        }
        if (layout == null)
        {
            layout = LoadBundledLayout(GuessDefaultLayoutName());
            if (layout == null) { SetStatus(Loc.T("Layouts フォルダにレイアウトがありません。", "There are no layouts in the Layouts folder."), StatusKind.Warn); return; }
            project = null;
        }

        string source = Loc.T("保存済みの設定", "saved settings");
        if (project == null && IsOnline)
        {
            try
            {
                project = ProjectService.ImportFromDriver(_client!, device, layout, true, warnings);
                if (project != null) { source = Loc.T("ドライバの現在の設定", "current driver settings"); _unapplied.Remove(device.UniqId); }
            }
            catch (KbdLayerException ex) { warnings.Add(ex.Message); }
        }
        if (project == null)
        {
            project = ProjectService.CreateDefault(device.UniqId, device.HardwareId, layout, true);
            source = Loc.T("新規(全キーそのまま)", "new (all keys unchanged)");
        }

        ProjectService.NormalizeShape(project, layout);
        SetProject(project, layout, dirty: false);
        _autoSaveBlocked = blocked;
        SetStatus(Loc.T($"{device.DisplayName}: {source}を読み込みました。" +
                  (warnings.Count > 0 ? " 注意: " + string.Join(" / ", warnings) : ""),
                  $"{device.DisplayName}: Loaded {source}." +
                  (warnings.Count > 0 ? " Note: " + string.Join(" / ", warnings) : "")),
                      warnings.Count > 0 ? StatusKind.Warn : StatusKind.Info);
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
        catch (Exception ex) { MessageBox.Show(this, Loc.T($"読み込めませんでした: {ex.Message}", $"Could not load: {ex.Message}"), DialogCaption); }
    }

    private void ChangeLayout(KeyboardLayout layout, bool bundled)
    {
        var r = MessageBox.Show(this,
            Loc.T("レイアウトを変えると、キーの割り当て・実測値・コンボはリセットされます(マクロ・タップダンス・キー置換と、設定ダイアログの内容は残ります)。続けますか？",
                  "Changing the layout resets key assignments, measured values, and combos (macros, tap dances, key overrides, and the Settings dialog contents are kept). Continue?"),
            DialogCaption, MessageBoxButton.OKCancel, MessageBoxImage.Question);
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
        p.KeyboardName = _project.KeyboardName;
        p.MacroNames = _project.MacroNames;       // 名前は、中身が残るものも、リセットされるコンボの番号も、そのまま残す
        p.ComboNames = _project.ComboNames;
        p.TapDanceNames = _project.TapDanceNames;
        p.KeyOverrideNames = _project.KeyOverrideNames;
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
        SetStatus(Loc.T($"レイアウトを {layout.Name} に変更しました({layout.Slots.Count}キー)。", $"Changed the layout to {layout.Name} ({layout.Slots.Count} keys)."));
    }

    // ------------------------------------------------------------------
    // レイヤー
    // ------------------------------------------------------------------

    private void RefreshLayerList()
    {
        _suppressEvents = true;
        LstLayers.Items.Clear();
        for (int i = 0; i < _project!.Layers.Count; i++)
        {
            string? name = ProjectService.LayerName(_project, i);
            LstLayers.Items.Add(new ListBoxItem
            {
                Content = name == null ? $" {i} " : $" {i}: {name} ",
                Padding = new Thickness(10, 3, 10, 3),
                ToolTip = Loc.T("左: 選択、右: メニュー", "Left: select, right: menu"),
            });
        }
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
            SetStatus(Loc.T($"レイヤーは{KbdLayerClient.MaxLayers}個までです。", $"You can have at most {KbdLayerClient.MaxLayers} layers."), StatusKind.Warn);
            return;
        }
        _project.Layers.Add(ProjectService.DefaultLayer(_layout, _project.Layers.Count));
        _layer = _project.Layers.Count - 1;
        MarkDirty();
        RefreshLayerList();
        RenderKeyboard();
        SetStatus(Loc.T($"レイヤー{_layer}を追加しました(全キー透過)。レイヤー0のどれかのキーを MO({_layer}) 等にすると切り替えられます。",
                        $"Added layer {_layer} (all keys transparent). Set a key on layer 0 to MO({_layer}) or similar to switch to it."));
    }

    private void BtnRemoveLayer_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null || _project.Layers.Count <= 1) return;
        int last = _project.Layers.Count - 1;
        if (MessageBox.Show(this, Loc.T($"レイヤー{last}を削除しますか？", $"Delete layer {last}?"), DialogCaption, MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        _project.Layers.RemoveAt(last);
        ProjectService.NormalizeLayerNames(_project); // 消したレイヤーの名前も捨てる
        _layer = Math.Min(_layer, last - 1);
        MarkDirty();
        RefreshLayerList();
        RenderKeyboard();
        if (_project.Layers.Any(l => l.Any(t => ReferencesLayer(t, last))))
            SetStatus(Loc.T($"注意: まだレイヤー{last}を参照するキー(MO({last})等)が残っています。適用時にエラーになるので変更してください。",
                            $"Note: some keys (such as MO({last})) still refer to layer {last}. Applying will fail, so change them."), StatusKind.Warn);
    }

    private static bool ReferencesLayer(string token, int layer) =>
        KeycodeTable.TryResolve(token, out var a) && ActionType.ReferencesLayer(a.Type) && a.Param1 == layer;

    // ------------------------------------------------------------------
    // キーボード描画・キーの編集
    // ------------------------------------------------------------------

    private void OnThemeChanged()
    {
        if (IsLoaded) RenderKeyboard();
        UpdateIndicators();
    }

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
        if (isTransparent && _layer > 0) { bg = TransparentKeyBrush; fg = AppTheme.Palette.KeyTransparentText; }
        else if (action.Type is not (ActionType.Key or ActionType.None or ActionType.Transparent)) bg = ActionBrush;
        else if (_layer > 0 || token != (slot.DefaultKeycode ?? "KC_TRNS")) bg = ChangedBrush;

        var sc = ProjectService.EffectiveScanCode(project, slot);
        Brush border = KeyboardCanvas.DefaultBorderBrush;
        double thickness = 1;
        if (sc == null) { border = UnknownBorder; thickness = 2; }
        else if (ProjectService.IsMeasuredDifferent(project, slot)) { border = MeasuredBorder; thickness = 2; }

        string scText = sc == null ? Loc.T("不明(要実測)", "unknown (needs measuring)")
            : $"0x{sc:X4}" + (project.MeasuredScanCodes.ContainsKey(slot.Index) ? Loc.T("(実測)", " (measured)") : Loc.T("(既定)", " (default)"));
        string tip = $"{slot.Description}\n{ProjectService.LayerLabel(_project!, _layer)}: {token}";
        if (ProjectService.TargetNameNote(project, action) is { } targetNote) tip += $"\n→ {targetNote}"; // 指す先のレイヤー・マクロ・タップダンスの名前
        tip += $"\n{Loc.T("スキャンコード", "Scan code")}: {scText}";

        // 変更していないキーは刻印をそのまま出す（キーコード名はUS配列基準なので、JIS配列では
        // 刻印と食い違うキーがある）。変更したキーだけ、割り当てたキーコードの短い名前を出す。
        bool unchanged = _layer == 0 && token == slot.DefaultKeycode;
        string main = unchanged && slot.MainLegend.Length > 0 ? slot.MainLegend : KeycodeCatalog.ShortName(token);
        // キー置換・コンボ・マクロ・タップダンスの印（右下）と、その説明。
        var marks = KeyMarks.For(project, _layout!, _layer, slot);
        if (marks.ToolTip.Length > 0) tip += "\n" + marks.ToolTip;
        return new KeyVisual(main, bg, border, thickness, tip, fg, marks.Marks);
    }

    // キーの割り当て画面は、編集画面（マクロなど）と同じ非モーダルの別ウィンドウ。マクロの画面を開いたまま、一覧を見たり、
    // 行のダブルクリックで MACRO(n) を入れたりできる。1つだけ（開いている間にほかのキーを押すと、その画面が前に出る）。
    // 開いている間は、編集画面と同じく、キーボード・レイアウトの切り替えなどを無効にする（UpdateEditorLock）。
    private KeyAssignDialog? _keyAssign;
    private (int Slot, int Layer)? _keyAssignTarget; // 開いている割り当て画面のキーとレイヤー

    private void Keyboard_KeyClicked(KeySlot slot)
    {
        if (_project == null) return;
        if (_keyAssign != null)
        {
            _keyAssign.Activate();
            return;
        }
        int layer = _layer;
        string current = _project.Layers[layer][slot.Index];
        var dlg = new KeyAssignDialog(slot, layer, current, _project.Layers.Count, _project.UniqId, _project, _layout, HandleEditorRequest) { Owner = this, NonModal = true };
        // 高さはメイン画面と同じにする（「画面のキーボード」で、キーボード全体が縦に収まるように）。画面の外に出ないよう収める。
        var work = SystemParameters.WorkArea;
        dlg.Height = Math.Min(ActualHeight, work.Height);
        dlg.WindowStartupLocation = WindowStartupLocation.Manual;
        dlg.Left = Math.Max(work.Left, Math.Min(Left + 40 + 40 * _editors.Count, work.Right - dlg.Width)); // 開いている編集画面と重ならないよう、少しずらす
        dlg.Top = Math.Max(work.Top, Math.Min(Top + 20 * _editors.Count, work.Bottom - dlg.Height));
        dlg.Assigned += () =>
        {
            if (dlg.ResultToken == current || layer >= _project.Layers.Count) return;
            _project.Layers[layer][slot.Index] = dlg.ResultToken;
            MarkDirty();
            KbdCanvas.Update(slot, VisualFor(slot));
        };
        dlg.Closed += (_, _) =>
        {
            _keyAssign = null;
            _keyAssignTarget = null;
            UpdateEditorLock();
        };
        _keyAssign = dlg;
        _keyAssignTarget = (slot.Index, layer);
        UpdateEditorLock();
        dlg.Show();
    }

    // 他の画面の「タップダンス...」が出す一覧。タップダンスの編集画面が開いていれば、その編集中の内容、なければプロジェクトの内容。
    private IReadOnlyList<MacroInfo> CurrentTapDances()
    {
        if (_editors.TryGetValue("tapdances", out var editor) && editor is TapDanceEditorWindow tapDanceEditor) return tapDanceEditor.Snapshot();
        var list = new List<MacroInfo>();
        if (_project == null) return list;
        var defs = new Dictionary<int, TapDanceDef>();
        foreach (var d in _project.TapDances) defs[d.Id] = d;
        for (int i = 0; i < KbdLayerClient.MaxTapDanceCount; i++)
        {
            defs.TryGetValue(i, out var def);
            string? name = _project.TapDanceNames != null && _project.TapDanceNames.TryGetValue(i, out var n) ? n : null;
            if (def == null && string.IsNullOrWhiteSpace(name)) continue;
            string content = def == null ? "" : Loc.T($"{def.SingleTap} / 長押し {def.SingleHold} / {def.DoubleTap} / {def.TripleTap}", $"{def.SingleTap} / hold {def.SingleHold} / {def.DoubleTap} / {def.TripleTap}");
            list.Add(new MacroInfo(i, name, content, "TAPDANCE", def?.Disabled ?? false));
        }
        return list;
    }

    // 他の画面の「マクロ...」が出す一覧。マクロの編集画面が開いていれば、その画面の編集中の内容、なければプロジェクトの内容。
    private IReadOnlyList<MacroInfo> CurrentMacros()
    {
        if (_editors.TryGetValue("macros", out var editor) && editor is MacroEditorWindow macroEditor) return macroEditor.Snapshot();
        var list = new List<MacroInfo>();
        if (_project == null) return list;
        var contents = new Dictionary<int, string>();
        foreach (var d in _project.Macros) contents[d.Id] = string.Join(" ", d.Keys);
        var disabledMacros = _project.Macros.Where(d => d.Disabled).Select(d => d.Id).ToHashSet();
        for (int i = 0; i < KbdLayerClient.MaxMacroCount; i++)
        {
            contents.TryGetValue(i, out var content);
            string? name = _project.MacroNames != null && _project.MacroNames.TryGetValue(i, out var n) ? n : null;
            if (!string.IsNullOrEmpty(content) || !string.IsNullOrWhiteSpace(name)) list.Add(new MacroInfo(i, name, content ?? "", "MACRO", disabledMacros.Contains(i)));
        }
        return list;
    }

    private void Keyboard_KeyRightClicked(KeySlot slot)
    {
        if (_project == null) return;
        var menu = new ContextMenu();

        var clearMeasured = new MenuItem
        {
            Header = Loc.T("実測値を削除", "Clear measured value"),
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
        var reset = new MenuItem { Header = NoAccessKey(Loc.T($"このレイヤーの割り当てを戻す({resetTo})", $"Reset assignment on this layer ({resetTo})")) };
        reset.Click += (_, _) =>
        {
            _project.Layers[_layer][slot.Index] = resetTo;
            MarkDirty();
            KbdCanvas.Update(slot, VisualFor(slot));
        };
        menu.Items.Add(reset);

        if (_layout != null) AddEditorItems(menu, slot);
        menu.IsOpen = true;
    }

    // メニューの項目の文字列は、_ をアクセスキーの印として扱う（KC_J が「KCJ」と出る）。キーコードや名前を出すときは、_ を重ねて文字のまま出す。
    private static string NoAccessKey(string header) => header.Replace("_", "__");

    // 右クリックのメニューの、コンボ・キー置換・マクロ・タップダンスの項目（GUI 計画 1-2）。割り当て画面のパネルと同じ判定（KeyMarks.Find）で出し分ける:
    // 関わる定義があれば「〜を開く」、無ければ「このキーで〜を作る...」。どれも編集画面を開くだけで、このキーの割り当ては変えない。
    private void AddEditorItems(ContextMenu menu, KeySlot slot)
    {
        if (_project == null) return;
        var r = KeyMarks.Find(_project, _layer, slot);
        menu.Items.Add(new Separator());
        string Suffix(bool disabled) => disabled ? Loc.T("(無効)", " (disabled)") : "";
        MenuItem Item(string name, string header, EditorRequest request, bool enabled = true)
        {
            var item = new MenuItem { Name = name, Header = NoAccessKey(header), IsEnabled = enabled }; // 名前に _ があっても消えないように
            item.Click += (_, _) => HandleEditorRequest(request);
            return item;
        }

        foreach (var c in r.Combos)
            menu.Items.Add(Item($"MenuOpenCombo{c.Id}", Loc.T($"{KeyMarks.Describe("コンボ", c.Id, _project.ComboNames)}{Suffix(c.Disabled)} を開く...",
                                                              $"Open {KeyMarks.Describe("Combo", c.Id, _project.ComboNames)}{Suffix(c.Disabled)}..."),
                                new EditorRequest(EditorTarget.Combo, c.Id, slot.Index, null)));
        // 1つのキーは有効なコンボ1つにしか使えないので、有効なコンボがあれば「作る」は出さない。
        if (!r.Combos.Any(c => !c.Disabled))
            menu.Items.Add(Item("MenuCreateCombo", Loc.T("このキーでコンボを作る...", "Create a combo with this key..."), new EditorRequest(EditorTarget.Combo, null, slot.Index, null)));

        foreach (var k in r.KeyOverrides)
            menu.Items.Add(Item($"MenuOpenKeyOverride{k.Id}", Loc.T($"{KeyMarks.Describe("キー置換", k.Id, _project.KeyOverrideNames)}{Suffix(k.Disabled)} を開く...",
                                                                    $"Open {KeyMarks.Describe("Key override", k.Id, _project.KeyOverrideNames)}{Suffix(k.Disabled)}..."),
                                new EditorRequest(EditorTarget.KeyOverride, k.Id, slot.Index, null)));
        string? overrideKey = KeyMarks.OverrideKeyFor(r.Token);
        menu.Items.Add(Item("MenuCreateKeyOverride", Loc.T("このキーでキー置換を作る...", "Create a key override with this key..."),
                            new EditorRequest(EditorTarget.KeyOverride, null, slot.Index, overrideKey), enabled: overrideKey != null));

        if (r.MacroId is int macro)
            menu.Items.Add(Item("MenuOpenMacro", Loc.T($"{KeyMarks.Describe("マクロ", macro, _project.MacroNames)} を開く...", $"Open {KeyMarks.Describe("Macro", macro, _project.MacroNames)}..."),
                                new EditorRequest(EditorTarget.Macro, macro, slot.Index, null)));
        if (r.TapDanceId is int td)
            menu.Items.Add(Item("MenuOpenTapDance", Loc.T($"{KeyMarks.Describe("タップダンス", td, _project.TapDanceNames)} を開く...", $"Open {KeyMarks.Describe("Tap dance", td, _project.TapDanceNames)}..."),
                                new EditorRequest(EditorTarget.TapDance, td, slot.Index, null)));
        else
        {
            // タップダンスは、作ったあと TAPDANCE(n) をこのキーに割り当てるので、このキーの割り当て画面も開く（タップダンスの画面の「入れる」の入れ先）。
            // 別のキーの割り当て画面が開いているときは、そちらに入ってしまうので、押せなくする。
            bool otherOpen = _keyAssign != null && _keyAssignTarget != (slot.Index, _layer);
            var create = new MenuItem
            {
                Name = "MenuCreateTapDance",
                Header = Loc.T("このキーでタップダンスを作る...", "Create a tap dance with this key..."),
                ToolTip = otherOpen ? Loc.T("開いているキーの割り当て画面を閉じると使えます", "Close the open key assignment window first") : null,
                IsEnabled = !otherOpen,
            };
            ToolTipService.SetShowOnDisabled(create, true);
            create.Click += (_, _) =>
            {
                if (_keyAssign == null) Keyboard_KeyClicked(slot);
                HandleEditorRequest(new EditorRequest(EditorTarget.TapDance, null, slot.Index, KeyMarks.KeyLikeToken(r.Token)));
            };
            menu.Items.Add(create);
        }
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
        SetStatus(Loc.T($"{dlg.Results.Count}キーを実測しました(既定と異なる値: {differs}キー)。適用すると反映されます。",
                        $"Measured {dlg.Results.Count} keys ({differs} differ from the default). Apply to take effect."));
    }
    // ------------------------------------------------------------------
    // マクロ・コンボ・タップダンス・キー置換の編集画面
    // 4つとも、同時に開ける別ウィンドウ（非モーダル）。マクロの画面を開いたまま、他の画面で MACRO(n) の番号と名前を見ながら編集できる。
    // 同じ種類は1つだけ（もう一度ボタンを押すと前面に出す）。「適用」・OKで、その時点の内容をプロジェクトへ取り込む。
    // 開いている間は、プロジェクトや配列が入れ替わる操作（キーボード・レイアウトの切り替え、インポート、レイヤーの追加・削除など）を無効にする。
    // ------------------------------------------------------------------

    private readonly Dictionary<string, IdListEditorWindow> _editors = new();

    /// <param name="request">開いた画面（すでに開いていればその画面）に対して行うこと（番号を選ぶ・空き番号に初期値を入れる。GUI 計画 1-2）。</param>
    private void OpenEditor<T>(string key, Func<T> create, Action<T> apply, Action<T>? request = null) where T : IdListEditorWindow
    {
        if (_editors.TryGetValue(key, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            if (request != null && existing is T same) request(same);
            existing.Activate();
            return;
        }
        var window = create();
        window.Owner = this;
        // 開いた順に少しずらして、先に開いた画面（マクロの一覧など）が隠れないようにする。
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        double offset = 40 * _editors.Count;
        var work = SystemParameters.WorkArea;
        window.Left = Math.Max(work.Left, Math.Min(Left + 40 + offset, work.Right - window.Width));
        window.Top = Math.Max(work.Top, Math.Min(Top + 40 + offset, work.Bottom - window.Height));
        // 削除の確認に出す「使っている場所」（今のプロジェクトのキーマップと定義）。
        window.ReferenceProvider = (kind, id) => _project == null ? Array.Empty<string>()
            : ProjectService.FindReferences(_project, kind, id).Select(r => ProjectService.DescribeReference(_project, _layout, r)).ToList();
        window.Applied += () =>
        {
            apply(window);
            // 削除で「割り当ても外す」を選んだ番号: プロジェクトから参照を外し、開いているほかの編集画面の作業用コピーからも外す（GUI 計画 1-3）。
            if (_project != null && window.PendingUnassign.Count > 0)
            {
                int removed = 0;
                foreach (var (kind, id) in window.PendingUnassign)
                {
                    removed += ProjectService.RemoveReferences(_project, kind, id);
                    foreach (var other in _editors.Values.Where(e => e != window)) other.ReferencesRemoved(kind, id);
                }
                window.PendingUnassign.Clear();
                SetStatus(Loc.T($"削除した定義を使っていた {removed} か所の割り当てを外しました。", $"Removed {removed} assignment(s) that used the deleted definition."));
            }
            RenderKeyboard(); // コンボ・キー置換の印が変わる
            _keyAssign?.RefreshRelated(); // 開いている割り当て画面のコンボ・キー置換の一覧も
            MarkDirty();
        };
        window.Closed += (_, _) =>
        {
            _editors.Remove(key);
            UpdateEditorLock();
        };
        _editors[key] = window;
        UpdateEditorLock();
        request?.Invoke(window);
        window.Show();
    }

    // 編集画面が開いている間、プロジェクトや配列を入れ替える操作を無効にする（画面の内容とプロジェクトがずれるのを防ぐ）。
    private void UpdateEditorLock()
    {
        bool free = _editors.Count == 0 && _keyAssign == null;
        CmbDevice.IsEnabled = free;
        CmbLayout.IsEnabled = free;
        BtnRenameKeyboard.IsEnabled = free;
        BtnIdentify.IsEnabled = free && !IsReadOnly;
        BtnRefreshDevices.IsEnabled = free;
        BtnAddKle.IsEnabled = free;
        BtnAddLayer.IsEnabled = free;
        BtnRemoveLayer.IsEnabled = free;
        BtnImportExport.IsEnabled = free;
        // 編集画面を開いている間は、状態の印「編集中」に出す（ステータス欄のメッセージは、閉じる直前の結果を消してしまうので使わない。2026-11-09）。
        UpdateIndicators();
    }

    private void BtnMacros_Click(object sender, RoutedEventArgs e) => OpenMacros();
    private void BtnCombos_Click(object sender, RoutedEventArgs e) => OpenCombos();
    private void BtnTapDances_Click(object sender, RoutedEventArgs e) => OpenTapDances();
    private void BtnKeyOverrides_Click(object sender, RoutedEventArgs e) => OpenKeyOverrides();

    private void OpenMacros(Action<MacroEditorWindow>? request = null)
    {
        if (_project == null) return;
        OpenEditor("macros", () => new MacroEditorWindow(_project.Macros, _project.MacroNames), w =>
        {
            _project.Macros = w.Result;
            _project.MacroNames = w.ResultNames;
        }, request);
    }

    private void OpenCombos(Action<ComboEditorWindow>? request = null)
    {
        if (_project == null || _layout == null) return;
        OpenEditor("combos", () => new ComboEditorWindow(_project, _layout), w =>
        {
            _project.Combos = w.Result;
            _project.ComboNames = w.ResultNames;
        }, request);
    }

    private void OpenTapDances(Action<TapDanceEditorWindow>? request = null)
    {
        if (_project == null) return;
        OpenEditor("tapdances", () => new TapDanceEditorWindow(_project.TapDances, _project.TapDanceNames), w =>
        {
            _project.TapDances = w.Result;
            _project.TapDanceNames = w.ResultNames;
        }, request);
    }

    private void OpenKeyOverrides(Action<KeyOverrideEditorWindow>? request = null)
    {
        if (_project == null) return;
        OpenEditor("keyoverrides", () => new KeyOverrideEditorWindow(_project.KeyOverrides, _project.KeyOverrideNames), w =>
        {
            _project.KeyOverrides = w.Result;
            _project.KeyOverrideNames = w.ResultNames;
        }, request);
    }

    /// <summary>
    /// 割り当て画面・右クリックのメニューからの「編集画面を開く」（GUI 計画 1-2）。番号があればその番号を選び、無ければ空き番号に
    /// このキーの初期値を入れる。プロジェクトは、編集画面の適用・OK まで変わらない。空き番号が無ければ、そう伝える。
    /// </summary>
    private void HandleEditorRequest(EditorRequest r)
    {
        void NoFree(int id)
        {
            if (id >= 0) return;
            MessageBox.Show(this, Loc.T("空いている番号がありません。使っていない番号の中身と名前を消してから、もう一度試してください。",
                                        "There is no free number. Clear the content and name of an unused number, then try again."),
                            AppInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        switch (r.Target)
        {
            case EditorTarget.Combo:
                OpenCombos(w => { if (r.Id is int id) w.ShowId(id); else NoFree(w.StartNew(r.Slot)); });
                break;
            case EditorTarget.KeyOverride:
                OpenKeyOverrides(w => { if (r.Id is int id) w.ShowId(id); else NoFree(w.StartNew(r.Key ?? "")); });
                break;
            case EditorTarget.Macro:
                OpenMacros(w => { if (r.Id is int id) w.ShowId(id); else NoFree(w.StartNew()); });
                break;
            case EditorTarget.TapDance:
                OpenTapDances(w => { if (r.Id is int id) w.ShowId(id); else NoFree(w.StartNew(r.Key)); });
                break;
        }
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e) => OpenSettings(null);

    // 割り当ての一覧（文字。GUI 計画 2-2）。非モーダルで1つだけ。「最新にする」で、そのときの _project と _layout から作り直す
    // （キーボードを切り替えたあとなら、切り替え先の内容になる）。
    private RemapTextWindow? _remapText;

    private void BtnRemapText_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null || _layout == null) return;
        if (_remapText != null) { _remapText.Activate(); return; }
        _remapText = new RemapTextWindow(BuildRemapText, $"kasane-{_project.UniqId:X8}-assignments.txt") { Owner = this };
        _remapText.Closed += (_, _) => _remapText = null;
        _remapText.Show();
    }

    private string BuildRemapText()
    {
        if (_project == null || _layout == null) return "";
        var helper = new HelperSettingsModel(_project.UniqId, TrayConfig.Load());
        return RemapTextFormatter.Format(_project, _layout, helper.HasDevice && helper.DeviceLoadError == null ? helper.Device : null, helper.Global,
                                         RemapTextFormatter.KeyboardLabel(_project), DateTime.Now);
    }

    // 設定の「ヘルパー動作」タブを選んだ状態で、設定画面を開く。
    private void BtnHelper_Click(object sender, RoutedEventArgs e) => OpenSettings("TabHelper");

    private void OpenSettings(string? tab)
    {
        if (_project == null) return;
        var dlg = new SettingsDialog(_project, IsOnline ? _client : null, IsOnline ? SelectedDevice!.UniqId : 0, CanWrite, _layout) { Owner = this };
        if (tab != null) dlg.SelectTab(tab);
        if (dlg.ShowDialog() == true)
        {
            MarkDirty();
            foreach (int id in dlg.UnassignedHelperIds)
                foreach (var editor in _editors.Values) editor.ReferencesRemoved(RefKind.Helper, id);
            if (dlg.KeymapChanged)
            {
                RenderKeyboard(); // ヘルパー動作の削除で、HLP(n) の割り当てを外した
                SetStatus(Loc.T("削除したヘルパー動作を使っていた割り当てを外しました。適用すると反映されます。", "Removed the assignments that used the deleted helper action. Apply to take effect."));
            }
        }
        if (dlg.LanguageChanged) OfferRestartForLanguage();
    }

    // 表示言語を変えたとき: kasane を再起動すると新しい言語で開く（トレイは、設定の変更を見つけて自分で再起動する）。
    private void OfferRestartForLanguage()
    {
        var r = MessageBox.Show(this,
            Loc.T("表示言語の変更は、再起動後に効きます。今すぐ kasane を再起動しますか？\n(トレイは、数秒後に自動で再起動します)",
                  "The display language change takes effect after a restart. Restart kasane now?\n(The tray restarts automatically in a few seconds.)"),
            DialogCaption, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes || !ConfirmLeave()) return; // 確認は、新しいプロセスを起こす前に済ませる（BtnRestartAsAdmin_Click と同じ理由）
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = $"{Kasane.Interop.SingleInstance.WaitPidArgument} {Environment.ProcessId}",
                UseShellExecute = true,
            });
            _leaveConfirmed = true;
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            SetStatus(Loc.T($"再起動できませんでした: {ex.Message}", $"Could not restart: {ex.Message}"), StatusKind.Error);
        }
    }

    // ------------------------------------------------------------------
    // 保存・エクスポート・適用
    // ------------------------------------------------------------------

    private bool SaveProject()
    {
        if (_project == null) return false;
        if (TrySaveProject(out string? error)) return true;
        MessageBox.Show(this, Loc.T($"保存できませんでした: {error}", $"Could not save: {error}"), DialogCaption, MessageBoxButton.OK, MessageBoxImage.Error);
        return false;
    }

    /// <summary>保存する。画面は出さない。失敗したときは理由を <paramref name="error"/> に入れて false。</summary>
    private bool TrySaveProject(out string? error)
    {
        error = null;
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
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 保存に失敗したとき、保存せずに続けるかを確かめる。保存先に書けない環境（管理者保護で、昇格しないトレイや設定画面が ADMIN_ユーザー名 として動くとき、
    /// など）では、保存の失敗で閉じられなくなるのを避けるため。続けてよければ true（変更は捨てる）。「いいえ」が既定。
    /// </summary>
    private bool ConfirmDiscardAfterSaveFailure(string? error)
    {
        var r = MessageBox.Show(this,
            Loc.T($"保存できませんでした: {error}\n\n" +
                  $"保存先: {HelperConfig.BaseDirectory}\n" +
                  $"実行ユーザー: {Environment.UserDomainName}\\{Environment.UserName}\n\n" +
                  "トレイのメニューに「保存先の権限を直す…」があれば、それで直せることがあります。\n\n" +
                  "はい: 保存せずに続ける(今の変更は捨てる)　／　いいえ: 戻る",
                  $"Could not save: {error}\n\n" +
                  $"Data folder: {HelperConfig.BaseDirectory}\n" +
                  $"Running as: {Environment.UserDomainName}\\{Environment.UserName}\n\n" +
                  "If the tray menu has \"Fix data folder access…\", it may fix this.\n\n" +
                  "Yes: continue without saving (discard the current changes) / No: go back"),
            DialogCaption, MessageBoxButton.YesNo, MessageBoxImage.Error, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return false;
        _dirty = false;
        _unapplied.Remove(_project?.UniqId ?? 0);
        UpdateTitle();
        return true;
    }


    // 選んでいるレイヤーに名前を付ける（番号は変わらない。画面の表示とエクスポートのファイルだけに使う）。
    private void RenameLayer()
    {
        if (_project == null) return;
        int layer = _layer;
        var dlg = new TextPromptWindow(Loc.T($"レイヤー{layer}の名前", $"Layer {layer} name"), Loc.T($"レイヤー{layer}の名前(空にすると、名前を消します):", $"Name for layer {layer} (leave empty to remove the name):"),
            ProjectService.LayerName(_project, layer) ?? "", "LayerNameBox") { Owner = this };
        if (dlg.ShowDialog() != true) return;
        ProjectService.SetLayerName(_project, layer, dlg.Value);
        MarkDirty();
        RefreshLayerList();
        RenderKeyboard();
        SetStatus(ProjectService.LayerName(_project, layer) is { } n
            ? Loc.T($"レイヤー{layer}の名前を「{n}」にしました。", $"Set the name of layer {layer} to \"{n}\".")
            : Loc.T($"レイヤー{layer}の名前を消しました。", $"Removed the name of layer {layer}."));
    }

    private ContextMenu BuildLayerMenu()
    {
        var menu = new ContextMenu();
        var rename = new MenuItem { Name = "MenuRenameLayer", Header = Loc.T("このレイヤーに名前を付ける...", "Name this layer...") };
        rename.Click += (_, _) => RenameLayer();
        menu.Opened += (_, _) =>
        {
            rename.IsEnabled = _project != null;
            rename.Header = _project != null ? Loc.T($"レイヤー{_layer}に名前を付ける...", $"Name layer {_layer}...") : Loc.T("このレイヤーに名前を付ける...", "Name this layer...");
        };
        menu.Items.Add(rename);
        menu.Items.Add(new Separator());
        var export = new MenuItem { Name = "MenuExportLayer", Header = Loc.T("このレイヤーをエクスポート...", "Export this layer...") };
        export.Click += (_, _) => OpenImportExport(ImportExportWindow.StartTab.Export, Kasane.Model.Portable.PortableScope.Layer);
        var import = new MenuItem { Name = "MenuImportLayer", Header = Loc.T("レイヤーをインポート...", "Import layer...") };
        import.Click += (_, _) => OpenImportExport(ImportExportWindow.StartTab.Import, Kasane.Model.Portable.PortableScope.Layer);
        menu.Items.Add(export);
        menu.Items.Add(import);
        return menu;
    }

    private void BtnHelp_Click(object sender, RoutedEventArgs e) => OpenManual();

    // マニュアルを、エクスプローラー経由で開く（ブラウザーは Windows の設定に任せる）。英語の表示でも、同じマニュアル。
    private void OpenManual()
    {
        string? error = ManualLocator.Open(AppContext.BaseDirectory);
        if (error != null) MessageBox.Show(this, error, AppInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void BtnImportExport_Click(object sender, RoutedEventArgs e) =>
        OpenImportExport(ImportExportWindow.StartTab.Export, Kasane.Model.Portable.PortableScope.Keyboard);

    private void OpenImportExport(ImportExportWindow.StartTab tab, Kasane.Model.Portable.PortableScope scope)
    {
        if (_project == null || _layout == null) return;
        var devices = CmbDevice.Items.OfType<DeviceInfo>().Where(d => d.UniqId != 0)
            .Select(d => new Kasane.Model.Portable.PortableService.TargetDevice(d.UniqId, d.HardwareId, d.DisplayName)).ToList();
        uint uniq = _project.UniqId;
        var dlg = new ImportExportWindow(_project, _layout, uniq, devices, _dirty, SaveProject, tab, scope, _layer, _client, !IsReadOnly) { Owner = this };
        dlg.ShowDialog();
        if (dlg.ClearedProject != null)
        {
            // 初期化（旧「クリア」）: 今のキーボードの設定は、保存先（とドライバ）で初期状態に戻っている。画面を、その内容に入れ替える。
            ProjectService.NormalizeShape(dlg.ClearedProject, _layout);
            SetProject(dlg.ClearedProject, _layout, dirty: false);
            if (!dlg.ClearedDriver && uniq != 0) _unapplied.Add(uniq); // ドライバの設定は残っている
            RefreshDeviceStatusOnly();
            SetStatus(dlg.ClearedDriver
                ? Loc.T("初期化しました(ドライバの設定も消しました)。", "Reset (the driver settings were also removed).")
                : Loc.T("初期化しました(画面の設定だけ。ドライバの設定は残っています)。", "Reset (on-screen settings only; the driver settings remain)."));
            return;
        }
        if (dlg.Cleared) RefreshDeviceStatusOnly();
        if (dlg.ResultProject != null)
        {
            ProjectService.NormalizeShape(dlg.ResultProject, _layout);
            SetProject(dlg.ResultProject, _layout, dirty: true);
            SetStatus(Loc.T("読み込みました(まだ保存・適用していません)。", "Imported (not saved or applied yet)."));
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
                SetStatus(Loc.T("全体一括で読み込みました(まだ「キーボードに適用」していません)。", "Imported everything at once (not yet applied to the keyboard)."));
            }
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
            SetStatus(Loc.T("適用しました(ON)。設定はドライバに保存され、再起動後も有効です。" +
                      (warnings.Count > 0 ? " 注意: " + string.Join(" / ", warnings) : ""),
                      "Applied (ON). The settings are stored in the driver and stay in effect after a restart." +
                      (warnings.Count > 0 ? " Note: " + string.Join(" / ", warnings) : "")),
                      warnings.Count > 0 ? StatusKind.Warn : StatusKind.Info);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("適用できません(何も変更していません)", "Cannot apply (nothing was changed)"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (KbdLayerException ex)
        {
            MessageBox.Show(this, ex.Message + Loc.T("\n\n途中まで反映された可能性があります。もう一度適用してください。", "\n\nThe settings may have been applied only partway. Apply again."),
                Loc.T("適用に失敗しました", "Apply failed"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnOff_Click(object sender, RoutedEventArgs e)
    {
        if (!IsOnline) return;
        try
        {
            _client!.SetEnabled(SelectedDevice!.UniqId, false);
            RefreshDeviceStatusOnly();
            SetStatus(Loc.T("OFFにしました(キーボードは本来の動作に戻りました。設定はドライバに残っています)。", "Turned OFF (the keyboard is back to its normal behavior; the settings remain in the driver)."));
        }
        catch (KbdLayerException ex) { SetStatus(ex.Message, StatusKind.Error); }
    }

    /// <summary>プロジェクトを読み直さずに、デバイス一覧（ON/OFF状態）だけ更新する。</summary>
    private void RefreshDeviceStatusOnly()
    {
        if (_client == null) return;
        uint id = SelectedDevice?.UniqId ?? 0;
        try
        {
            var devices = LabelDevices(_client.ListDevices());
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
        UpdateDefinitionButtons();
        UpdateTitle();
    }

    /// <summary>メッセージボックスの見出し（長いウィンドウのタイトルではなく、アプリ名だけ）。</summary>
    private static string DialogCaption => Kasane.Model.AppInfo.DisplayName;

    // 画面の何もないところ（ボタンの列・余白）を右クリックしたときのメニュー。キーボードの図の上は、キーごとの右クリック（実測値を消す等）があるので出さない。
    // バージョン情報は、設定の一番右のタブ。
    private ContextMenu BuildMainMenu()
    {
        var menu = new ContextMenu();
        var about = new MenuItem { Name = "MenuAbout", Header = Loc.T("バージョン情報...", "About...") };
        about.Click += (_, _) => OpenSettings(SettingsDialog.AboutTab);
        menu.Opened += (_, _) => about.IsEnabled = _project != null;
        menu.Items.Add(about);
        return menu;
    }

    private void UpdateButtons()
    {
        BtnApply.IsEnabled = CanWrite;
        // OFF（SET_LAYERMAP_ENABLED）は読み取り側のIOCTLなので、閲覧専用モードでも使える。
        BtnOff.IsEnabled = IsOnline && SelectedDevice!.Enabled;
        UpdateDefinitionButtons();
        UpdateTitle();
    }

    // 「マクロ(2)...」のように、定義の数を出す（機能がOFFなら「(2・OFF)」）。キー図からは見えない設定に気づくため。
    private void UpdateDefinitionButtons()
    {
        var p = _project;
        BtnMacros.Content = KeyMarks.ButtonText(Loc.T("マクロ", "Macros"), p?.Macros.Count ?? 0, p == null || KeyMarks.FeatureOn(p, KbdLayerClient.FeatureMacro), p?.Macros.Count(d => d.Disabled) ?? 0);
        BtnCombos.Content = KeyMarks.ButtonText(Loc.T("コンボ", "Combos"), p?.Combos.Count ?? 0, p == null || KeyMarks.FeatureOn(p, KbdLayerClient.FeatureCombo), p?.Combos.Count(d => d.Disabled) ?? 0);
        BtnTapDances.Content = KeyMarks.ButtonText(Loc.T("タップダンス", "Tap dances"), p?.TapDances.Count ?? 0, p == null || KeyMarks.FeatureOn(p, KbdLayerClient.FeatureTapDance), p?.TapDances.Count(d => d.Disabled) ?? 0);
        BtnKeyOverrides.Content = KeyMarks.ButtonText(Loc.T("キー置換", "Key overrides"), p?.KeyOverrides.Count ?? 0, p == null || KeyMarks.FeatureOn(p, KbdLayerClient.FeatureKeyOverride), p?.KeyOverrides.Count(d => d.Disabled) ?? 0);
    }

    // ドライバの状態（今リマップが効いているか・画面の変更を適用したか）。状態の印のツールチップに出す。
    private string DriverStateText()
    {
        if (_client == null) return Loc.T("ドライバ: 接続できません(編集とエクスポートのみ)", "Driver: not connected (edit and export only)");
        if (SelectedDevice is not { UniqId: not 0 } d) return Loc.T("ドライバ: このキーボードは接続していません(編集のみ)", "Driver: this keyboard is not connected (edit only)");
        string state = !d.HasLayerMap ? Loc.T("設定なし", "no settings")
            : d.Enabled ? Loc.T("ON(リマップ中)", "ON (remapping)")
            : Loc.T("OFF(設定はドライバに残っています)", "OFF (settings remain in the driver)");
        string applied = _unapplied.Contains(d.UniqId) ? Loc.T("　画面の変更は未適用", "  Changes not applied") : "";
        return Loc.T($"ドライバ: {state}{applied}", $"Driver: {state}{applied}");
    }

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
                Loc.T("このキーボードの保存済みの設定は、読めない、またはレイアウトが見つからないため、使わずに開いています。\n" +
                "保存すると、保存済みの設定がこの内容で置き換わります。保存しますか？\n\n" +
                "はい: 保存する　／　いいえ: 保存しない(変更を捨てる)　／　キャンセル: 戻る",
                "The saved settings for this keyboard could not be read or their layout was not found, so they were not used.\n" +
                "If you save, the saved settings will be replaced with the current contents. Save?\n\n" +
                "Yes: save / No: do not save (discard changes) / Cancel: go back"),
                DialogCaption, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Cancel) return false;
            if (r == MessageBoxResult.No) { _dirty = false; _unapplied.Remove(_project?.UniqId ?? 0); UpdateTitle(); return true; }
        }
        // 保存できないときは、そのまま閉じられなくなるのを避けるため、保存せずに続けるかを確かめる。
        return TrySaveProject(out string? error) || ConfirmDiscardAfterSaveFailure(error);
    }

    // 閉じる前の確認（ConfirmLeave）を済ませて、再起動のために閉じるところ。Window_Closing で、もう一度は聞かない。
    private bool _leaveConfirmed;

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_leaveConfirmed && !ConfirmLeave()) { e.Cancel = true; return; }
        _client?.Dispose();
    }

    /// <summary>
    /// 閉じる前の確認をまとめて行う。開いている編集画面、保存していない変更、キーボードに反映していない設定。閉じてよければ true。
    /// </summary>
    private bool ConfirmLeave()
    {
        // 編集画面が開いていれば、閉じると「適用」していない内容が捨てられることを確かめる。
        if ((_editors.Count > 0 || _keyAssign != null) &&
            MessageBox.Show(this, Loc.T("編集画面が開いています。閉じると、「適用」していない編集内容は捨てられます。\n閉じますか？",
                "An editor is open. If you close, edits you have not applied will be discarded.\nClose?"),
                DialogCaption, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return false;
        return PersistBeforeLeave() && ConfirmUnapplied();
    }

    /// <summary>閉じる前に、保存したがキーボードに反映していない設定について確かめる。閉じてよければ true。</summary>
    private bool ConfirmUnapplied()
    {
        var targets = _unapplied.Where(id => id != 0).OrderBy(id => id).ToList();
        switch (ClosePolicy.Decide(targets.Count, _client != null, IsReadOnly))
        {
            case UnappliedPrompt.WarnNotApplied:
                return MessageBox.Show(this,
                    Loc.T("設定は保存されましたが、キーボードに反映されていません。\n" +
                    "必要に応じて、管理者権限で kasane を起動し、「キーボードに適用(ON)」で反映してください。\n\n" +
                    "このまま閉じますか？",
                    "The settings were saved but have not been applied to the keyboard.\n" +
                    "If needed, start kasane as administrator and click \"Apply to keyboard (ON)\".\n\n" +
                    "Close anyway?"), DialogCaption, MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK;
            case UnappliedPrompt.AskApply:
                var r = MessageBox.Show(this,
                    Loc.T($"キーボードに反映していない設定が{targets.Count}台分あります。反映しますか？\n\n" +
                    "はい: 反映して閉じる　／　いいえ: 反映せずに閉じる(設定は保存済み)　／　キャンセル: 戻る",
                    $"Settings for {targets.Count} keyboard(s) have not been applied. Apply them now?\n\n" +
                    "Yes: apply and close / No: close without applying (settings are saved) / Cancel: go back"),
                    DialogCaption, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel) return false;
                if (r == MessageBoxResult.No) return true;
                foreach (var id in targets)
                {
                    string? error = ApplySavedProject(id);
                    if (error != null)
                    {
                        MessageBox.Show(this, Loc.T($"{id:X8}: {error}\n\n閉じずに戻ります。", $"{id:X8}: {error}\n\nGoing back without closing."), Loc.T("適用できません", "Cannot apply"), MessageBoxButton.OK, MessageBoxImage.Warning);
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
            if (project == null) return Loc.T("保存済みの設定が見つかりません", "The saved settings were not found");
            var file = ProjectService.FindLayoutFile(project);
            var layout = file != null ? KeyboardLayout.Load(file) : null;
            if (layout == null) return Loc.T($"レイアウト「{project.LayoutName}」が見つかりません", $"Layout \"{project.LayoutName}\" was not found");
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
