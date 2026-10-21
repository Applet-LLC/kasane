using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kasane.Interop;
using Kasane.Layout;
using Kasane.Model;

namespace Kasane.Views;

public enum CalibrationMode
{
    /// <summary>キーを押すと、画面のキーの色が変わる。管理者権限は要らない（LL フックだけを使う）。</summary>
    InputCheck,
    /// <summary>画面でキーをクリックして、そのキーだけを実測する。</summary>
    SingleKey,
    /// <summary>黄色のキーを順に押して、全部のキーを実測する。</summary>
    AllKeys,
}

/// <summary>
/// キャリブレーションの画面。モードは3つ（既定は「入力確認」）:
///  - 入力確認: キーを押すと、対応するキーの色が変わり、離すと戻る。一度押したキーには ✓ が付く。ドライバは使わず、LL フックだけ。
///    フックが見るのはレイヤーマップで置き換えた後のキーで、どのキーボードからかは区別できない（<see cref="EmergencyCheckWindow"/> と同じ）。
///  - 個別キー／全体: スキャンコードの記録はドライバの CAPTURE_NEXT_KEY（対象キーボード限定・リマップ前の値。管理者権限が要る）で行い、
///    LL フック（LowLevelKeyboardHook）は押されたキーをWindowsへ渡さずに捨てるためだけに使う。
///
/// - コントロールデバイスのハンドルはメイン画面とは別に開く（同期I/Oのハンドルでは要求が直列化されるため、キャプチャ待機中に
///   メイン画面の操作が止まらないように）。
/// - ウィンドウが非アクティブの間はフックを外し、記録も一時停止する（他のアプリへの入力を誤って記録しないため）。
/// </summary>
public partial class CalibrationWindow : Window
{
    private const uint CaptureTimeoutMs = 500; // 中止・非アクティブ・モード切替への反応を速くするため短めにしてループする

    private readonly KeyboardLayout _layout;
    private readonly KeymapProject _project;
    private readonly uint _uniqId;
    private readonly LowLevelKeyboardHook _hook = new();
    private readonly InputCheckModel _model;
    private KbdLayerClient? _captureClient;
    // 入力確認のキーの読み取り元。管理者でドライバに接続しているときは、ドライバから直接受ける（置き換える前の物理キー。
    // 押下と解放の両方が確実に届く）。そうでない／失敗したときは、LL フック（右 Shift や日本語キーの解放を取りこぼすことがある）。
    // LL フックは、どちらの場合も「キーを Windows に渡さない」ために設置する。
    private KeyStreamSession? _keys;
    private volatile bool _driverKeys;
    private CancellationTokenSource? _loopCts;
    private CalibrationMode _mode = CalibrationMode.InputCheck;
    private bool _ready;
    private bool _active;
    private int _position;         // 全体モード: _layout.Slots の位置
    private KeySlot? _selected;    // 個別キーモード: クリックで選んだキー

    /// <summary>スロット番号 → 実測したスキャンコード。</summary>
    public Dictionary<int, ushort> Results { get; } = new();

    private static Brush CurrentBrush => AppTheme.Palette.CalibCurrent;
    private static Brush DoneBrush => AppTheme.Palette.CalibDone;
    private static Brush TargetBrush => AppTheme.Palette.CalibTarget;
    private static Brush DownBrush => AppTheme.Palette.CalibDown;

    /// <param name="canCalibrate">個別・全体のキャリブレーションを使えるか（ドライバに接続していて、管理者権限がある）。</param>
    public CalibrationWindow(KeyboardLayout layout, KeymapProject project, uint uniqId, bool canCalibrate)
    {
        InitializeComponent();
        _layout = layout;
        _project = project;
        _uniqId = uniqId;
        _model = new InputCheckModel(BuildMap());

        _hook.KeySwallowed += (sc, isDown) => Dispatcher.BeginInvoke(() => OnHookKey(sc, isDown));
        KbdCanvas.KeyClicked += OnKeyClicked;

        if (canCalibrate)
        {
            _captureClient = KbdLayerClient.TryOpen(out var error);
            if (_captureClient == null) TxtLast.Text = Loc.T($"ドライバに接続できないため、キャリブレーションは使えません({error})。", $"Can't connect to the driver, so calibration is unavailable ({error}).");
        }
        if (_captureClient != null)
        {
            _driverKeys = true;
            _keys = new KeyStreamSession(
                e => Dispatcher.BeginInvoke(() => OnDriverKey(e)),
                (state, message) => Dispatcher.BeginInvoke(() => OnKeyStreamFailed(message)));
            _keys.Start();
        }
        ModeSingleKey.IsEnabled = ModeAllKeys.IsEnabled = _captureClient != null;
        if (_captureClient == null)
        {
            string why = canCalibrate
                ? Loc.T("ドライバに接続できません。", "Can't connect to the driver.")
                : Loc.T("キャリブレーションには、ドライバが認識しているキーボードと、管理者権限が要ります(「管理者として再起動」)。",
                        "Calibration needs a keyboard that the driver recognizes, and administrator rights (\"Restart as administrator\").");
            ModeSingleKey.ToolTip = ModeAllKeys.ToolTip = why;
        }

        _ready = true;
        SetMode(CalibrationMode.InputCheck);
    }

    // ------------------------------------------------------------------
    // モード
    // ------------------------------------------------------------------

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        SetMode(ReferenceEquals(sender, ModeSingleKey) ? CalibrationMode.SingleKey
              : ReferenceEquals(sender, ModeAllKeys) ? CalibrationMode.AllKeys
              : CalibrationMode.InputCheck);
    }

    private void SetMode(CalibrationMode mode)
    {
        StopLoop();
        _mode = mode;
        _selected = null;
        _model.ReleaseAll();
        TxtLast.Text = "";
        if (mode == CalibrationMode.AllKeys)
        {
            _position = _layout.Slots.ToList().FindIndex(s => !Results.ContainsKey(s.Index));
            if (_position < 0) _position = _layout.Slots.Count;
        }
        KbdCanvas.Render(_layout, VisualFor);
        UpdateInstruction();
        if (mode != CalibrationMode.InputCheck && _captureClient != null) StartLoop();
    }

    private KeySlot? Current => _mode == CalibrationMode.AllKeys && _position < _layout.Slots.Count ? _layout.Slots[_position] : null;

    /// <summary>キャリブレーションで記録しているキー（全体: 黄色の現在キー、個別: 選んだキー）。</summary>
    private KeySlot? Target => _mode == CalibrationMode.AllKeys ? Current : _mode == CalibrationMode.SingleKey ? _selected : null;

    // ------------------------------------------------------------------
    // 表示
    // ------------------------------------------------------------------

    private KeyVisual VisualFor(KeySlot slot)
    {
        switch (_mode)
        {
            case CalibrationMode.InputCheck:
            {
                bool down = _model.IsDown(slot.Index), done = _model.IsChecked(slot.Index);
                Brush bg = down ? DownBrush : done ? DoneBrush : TargetBrush;
                return new KeyVisual(done ? "✓" : "", bg, KeyboardCanvas.DefaultBorderBrush, down ? 3 : 1, slot.Description);
            }
            default:
            {
                bool isTarget = ReferenceEquals(slot, Target);
                Brush bg = isTarget ? CurrentBrush : Results.ContainsKey(slot.Index) ? DoneBrush : TargetBrush;
                string text = Results.TryGetValue(slot.Index, out var sc) ? $"0x{sc:X4}" : "";
                return new KeyVisual(text, bg, KeyboardCanvas.DefaultBorderBrush, isTarget ? 3 : 1, slot.Description);
            }
        }
    }

    private void RefreshKey(KeySlot? slot)
    {
        if (slot != null) KbdCanvas.Update(slot, VisualFor(slot));
    }

    private void UpdateInstruction()
    {
        bool all = _mode == CalibrationMode.AllKeys, single = _mode == CalibrationMode.SingleKey, check = _mode == CalibrationMode.InputCheck;
        BtnResetChecks.Visibility = check ? Visibility.Visible : Visibility.Collapsed;
        BtnBack.Visibility = BtnSkip.Visibility = all ? Visibility.Visible : Visibility.Collapsed;
        BtnFinish.IsEnabled = Results.Count > 0;

        switch (_mode)
        {
            case CalibrationMode.InputCheck:
                TxtInstruction.Text = Loc.T($"キーを押してみてください(確認済み {_model.CheckedCount} / {_layout.Slots.Count})", $"Try pressing keys ({_model.CheckedCount} / {_layout.Slots.Count} checked)");
                TxtNote.Foreground = AppTheme.Palette.SubText;
                TxtNote.Text = _driverKeys
                    ? Loc.T("押しているキーは橙色になり、一度押したキーには ✓ が付きます。✓ が付かないキーや、押したキーと違うキーが光るときは、" +
                            "キャリブレーションが必要です。この画面が前面にある間、押したキーはWindowsに送られません。" +
                            "キーはドライバから直接受け取ります(レイヤーマップで置き換える前の物理キー。選んでいるキーボードの入力だけ)。",
                            "Keys you hold turn orange, and keys you have pressed once get a ✓. If a key gets no ✓, or a different key lights up than the one you pressed, " +
                            "it needs calibration. While this window is in front, the keys you press are not sent to Windows. " +
                            "Keys are received directly from the driver (physical keys, before layer-map remapping; input from the selected keyboard only).")
                    : Loc.T("押しているキーは橙色になり、一度押したキーには ✓ が付きます。この画面が前面にある間、押したキーはWindowsに送られません。" +
                            "\n管理者権限モードで実施してください。",
                            "Keys you hold turn orange, and keys you have pressed once get a ✓. While this window is in front, the keys you press are not sent to Windows. " +
                            "\nPlease run in administrator mode.");
                break;
            case CalibrationMode.SingleKey:
                TxtInstruction.Text = _selected == null
                    ? Loc.T("実測したいキーを、画面でクリックしてください", "Click the key you want to measure on the screen")
                    : Loc.T($"黄色のキー「{_selected.PrintedLabel}」に対応するキーを、キーボードで押してください", $"Press the keyboard key that corresponds to the yellow key \"{_selected.PrintedLabel}\"");
                break;
            default:
                var current = Current;
                TxtInstruction.Text = current == null
                    ? Loc.T("すべて記録しました。「実測値を保存して閉じる」を押してください。", "All keys recorded. Click \"Save measurements and close\".")
                    : Loc.T($"黄色のキー「{current.PrintedLabel}」を押してください({_position + 1} / {_layout.Slots.Count})", $"Press the yellow key \"{current.PrintedLabel}\" ({_position + 1} / {_layout.Slots.Count})");
                BtnBack.IsEnabled = _position > 0;
                BtnSkip.IsEnabled = current != null;
                break;
        }
        if (!check && _active)
        {
            TxtNote.Foreground = AppTheme.Palette.SubText;
            TxtNote.Text = Loc.T("このウィンドウが前面にある間、キーボードで押したキーはWindowsに送られません(記録だけされます)。操作はマウスで行ってください。",
                                 "While this window is in front, keys pressed on the keyboard are not sent to Windows (they are only recorded). Use the mouse to operate this window.");
        }
        Title = _mode switch
        {
            CalibrationMode.InputCheck => Loc.T("キャリブレーション：入力確認", "Calibration: Input Check"),
            CalibrationMode.SingleKey => Loc.T("キャリブレーション：個別キー", "Calibration: Single Key"),
            _ => Loc.T($"キャリブレーション：全体({_layout.Slots.Count}キー)", $"Calibration: All Keys ({_layout.Slots.Count} keys)"),
        };
    }

    // ------------------------------------------------------------------
    // 入力確認（LL フック）
    // ------------------------------------------------------------------

    // スキャンコード → キー。実測値（この画面で記録したもの → 保存済みのもの → レイアウトの既定）の順。
    private Dictionary<ushort, int> BuildMap()
    {
        var map = new Dictionary<ushort, int>();
        foreach (var slot in _layout.Slots)
        {
            ushort? sc = Results.TryGetValue(slot.Index, out var measured) ? measured : ProjectService.EffectiveScanCode(_project, slot);
            if (sc is ushort v) map.TryAdd(v, slot.Index);
        }
        return map;
    }

    // ドライバから直接受けたキー（管理者のとき）。選んでいるキーボードの入力だけを見る。
    private void OnDriverKey(KeyEvent e)
    {
        if (!_driverKeys || !_active || e.UniqId != _uniqId) return;
        ApplyInputKey(e.ScanCode, !e.IsBreak);
    }

    // ドライバから受けられなくなった（権限・古いドライバ・上限・失敗）。LL フックでの確認に切り替える。
    private void OnKeyStreamFailed(string message)
    {
        _driverKeys = false;
        TxtLast.Text = Loc.T($"ドライバからキーを受け取れないため、Windows のキー入力(LL フック)で確認します: {message}", $"Can't receive keys from the driver, so checking with Windows key input (LL hook): {message}");
        UpdateInstruction();
    }

    private void OnHookKey(ushort scan, bool isDown)
    {
        if (_driverKeys) return; // ドライバから受けているときは、フックは「渡さない」ためだけ
        ApplyInputKey(scan, isDown);
    }

    private void ApplyInputKey(ushort scan, bool isDown)
    {
        if (_mode != CalibrationMode.InputCheck) return;
        var r = _model.OnKey(scan, isDown);
        if (r.Slot is int index)
        {
            var slot = _layout.Slots[index];
            RefreshKey(slot);
            if (r.Changed && isDown)
            {
                TxtLast.Text = Loc.T($"押したキー: {slot.Description}(0x{scan:X4})", $"Key pressed: {slot.Description} (0x{scan:X4})");
                UpdateInstruction();
            }
        }
        else if (isDown)
        {
            TxtLast.Text = Loc.T($"0x{scan:X4} は、この配列のどのキーにも対応しません。配列にないキーか、キャリブレーションでの実測が必要なキーです。",
                                 $"0x{scan:X4} doesn't match any key in this layout. It's either a key that isn't in the layout, or one that needs to be measured by calibration.");
        }
    }

    private void BtnResetChecks_Click(object sender, RoutedEventArgs e)
    {
        foreach (int i in _model.ResetChecked()) RefreshKey(_layout.Slots[i]);
        TxtLast.Text = "";
        UpdateInstruction();
    }

    // ------------------------------------------------------------------
    // キャリブレーション（個別・全体）
    // ------------------------------------------------------------------

    private void OnKeyClicked(KeySlot slot)
    {
        if (_mode != CalibrationMode.SingleKey) return;
        var old = _selected;
        _selected = slot;
        RefreshKey(old);
        RefreshKey(slot);
        TxtLast.Text = "";
        UpdateInstruction();
    }

    private void StartLoop()
    {
        StopLoop();
        _loopCts = new CancellationTokenSource();
        _ = CaptureLoopAsync(_loopCts.Token);
    }

    private void StopLoop()
    {
        _loopCts?.Cancel();
        _loopCts = null;
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        var client = _captureClient!;
        while (!ct.IsCancellationRequested)
        {
            ushort? sc;
            try
            {
                // 右ShiftをE0 36で出すキーボードを実測できるよう、Shiftのスロットを記録中だけ疑似Shiftも捕捉する。
                // 待機は500msで戻って張り直すので、記録対象が変わっても、すぐ追従する。
                bool fakeShift = CaptureFlags.WantsFakeShift(Target?.DefaultScanCode);
                sc = await Task.Run(() => client.CaptureNextKey(_uniqId, CaptureTimeoutMs, fakeShift), ct);
            }
            catch (OperationCanceledException) { return; }
            catch (KbdLayerException ex)
            {
                TxtLast.Text = ex.Message;
                await Task.Delay(1000, ct).ContinueWith(_ => { });
                continue;
            }
            // モードを切り替えたあとに、前のモードの待機が返ってきた分は捨てる。
            if (sc is ushort value && _active && !ct.IsCancellationRequested) OnCaptured(value);
        }
    }

    private void OnCaptured(ushort sc)
    {
        var target = Target;
        if (target == null)
        {
            if (_mode == CalibrationMode.SingleKey) TxtLast.Text = Loc.T("先に、実測したいキーを画面でクリックしてください。", "First, click the key you want to measure on the screen.");
            return;
        }

        int? duplicate = Results.Where(r => r.Value == sc && r.Key != target.Index).Select(r => (int?)r.Key).FirstOrDefault();
        if (duplicate is int dupSlot)
        {
            TxtLast.Text = Loc.T($"0x{sc:X4} は既に {_layout.Slots[dupSlot].Description} として記録済みです。" +
                                 "押し間違えた場合は、もう一度正しいキーを押してください(違うキーなら「1つ戻る」で前のキーをやり直せます)。",
                                 $"0x{sc:X4} is already recorded as {_layout.Slots[dupSlot].Description}. " +
                                 "If you pressed the wrong key, press the correct key again (if it's a different key, use \"Go back one\" to redo the previous key).");
            return;
        }

        Results[target.Index] = sc;
        _model.SetMap(BuildMap());
        string note = target.DefaultScanCode is ushort def && def != sc ? Loc.T($"(既定値 0x{def:X4} と異なります)", $" (differs from the default 0x{def:X4})") : "";
        TxtLast.Text = Loc.T($"{target.Description}: 0x{sc:X4} を記録しました{note}", $"{target.Description}: recorded 0x{sc:X4}{note}");
        if (_mode == CalibrationMode.AllKeys) _position++;
        else _selected = null; // 個別: 続けて別のキーを選べる
        RefreshKey(target);
        RefreshKey(Current);
        UpdateInstruction();
    }

    private void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != CalibrationMode.AllKeys || _position == 0) return;
        var old = Current;
        _position--;
        Results.Remove(_layout.Slots[_position].Index);
        _model.SetMap(BuildMap());
        RefreshKey(old);
        RefreshKey(Current);
        UpdateInstruction();
        TxtLast.Text = "";
    }

    private void BtnSkip_Click(object sender, RoutedEventArgs e)
    {
        var old = Current;
        if (old == null) return;
        _position++;
        RefreshKey(old);
        RefreshKey(Current);
        UpdateInstruction();
        TxtLast.Text = Loc.T($"{old.Description} を飛ばしました(既定のスキャンコードのままになります)", $"Skipped {old.Description} (it keeps the default scan code)");
    }

    private void BtnFinish_Click(object sender, RoutedEventArgs e) => Finish();

    private void Finish()
    {
        StopCapture();
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Results.Clear();
        StopCapture();
        DialogResult = false;
    }

    // ------------------------------------------------------------------
    // ウィンドウの状態
    // ------------------------------------------------------------------

    private void Window_Activated(object? sender, EventArgs e)
    {
        if (!_ready) return;
        try
        {
            _hook.Install();
            _active = true;
            UpdateInstruction();
        }
        catch (Exception ex)
        {
            TxtNote.Text = Loc.T($"キー入力の取得を開始できませんでした({ex.Message})。押したキーはWindowsにも届きます。", $"Couldn't start capturing key input ({ex.Message}). Keys you press will also reach Windows.");
            _active = true;
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        _active = false;
        _hook.Uninstall();
        // 非アクティブの間は解放が見えないので、押している状態を解く。
        foreach (int i in _model.ReleaseAll()) RefreshKey(_layout.Slots[i]);
        TxtNote.Foreground = Brushes.Firebrick;
        TxtNote.Text = Loc.T("一時停止中: このウィンドウをクリックすると再開します(他のウィンドウへの入力は記録しません)。", "Paused: click this window to resume (input to other windows is not recorded).");
    }

    private void StopCapture()
    {
        _active = false;
        _hook.Uninstall();
        StopLoop();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _keys?.Dispose();
        StopCapture();
        _hook.Dispose();
        // 実行中のCaptureNextKeyは最大CaptureTimeoutMsで戻る。SafeHandleは使用中の呼び出しが
        // 終わるまで実際のクローズを遅らせるので、ここで破棄してよい。
        _captureClient?.Dispose();
    }
}
