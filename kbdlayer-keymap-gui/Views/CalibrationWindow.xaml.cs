using System.Windows;
using System.Windows.Media;
using KbdLayerKeymapGui.Interop;
using KbdLayerKeymapGui.Layout;

namespace KbdLayerKeymapGui.Views;

/// <summary>
/// キーを1つずつ実測する。スキャンコードの記録はドライバの CAPTURE_NEXT_KEY（対象キーボード限定・
/// リマップ前の値）で行い、LLフック（LowLevelKeyboardHook）は押されたキーをWindowsへ渡さずに
/// 捨てるためだけに使う。
///
/// - コントロールデバイスのハンドルはメイン画面とは別に開く（同期I/Oのハンドルでは要求が直列化
///   されるため、キャプチャ待機中にメイン画面の操作が止まらないように）。
/// - ウィンドウが非アクティブの間はフックを外し、記録も一時停止する（他のアプリへの入力を
///   誤って記録しないため）。
/// </summary>
public partial class CalibrationWindow : Window
{
    private const uint CaptureTimeoutMs = 500; // 中止・非アクティブへの反応を速くするため短めにしてループする

    private readonly KeyboardLayout _layout;
    private readonly IReadOnlyList<KeySlot> _targets;
    private readonly uint _uniqId;
    private readonly LowLevelKeyboardHook _hook = new();
    private readonly CancellationTokenSource _cts = new();
    private KbdLayerClient? _captureClient;
    private int _position;
    private bool _active;

    /// <summary>スロット番号 → 実測したスキャンコード。</summary>
    public Dictionary<int, ushort> Results { get; } = new();

    private static readonly Brush CurrentBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x66));
    private static readonly Brush DoneBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xEE, 0xC8));
    private static readonly Brush TargetBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly Brush OtherBrush = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4));

    public CalibrationWindow(KeyboardLayout layout, IReadOnlyList<KeySlot> targets, uint uniqId)
    {
        InitializeComponent();
        _layout = layout;
        _targets = targets;
        _uniqId = uniqId;
        Title = targets.Count == 1 ? "キーの実測" : $"キャリブレーション（{targets.Count}キー）";
        BtnSkip.Visibility = BtnBack.Visibility = targets.Count == 1 ? Visibility.Collapsed : Visibility.Visible;
        _hook.KeyDownSwallowed += vk => Dispatcher.BeginInvoke(() =>
        {
            if (_position < _targets.Count)
                TxtLast.Text = $"キー入力を検出しました（仮想キー 0x{vk:X2}）…";
        });

        _captureClient = KbdLayerClient.TryOpen(out var error);
        if (_captureClient == null)
        {
            TxtInstruction.Text = $"ドライバに接続できません: {error}";
            return;
        }
        KbdCanvas.Render(_layout, VisualFor);
        UpdateInstruction();
        _ = CaptureLoopAsync(_cts.Token);
    }

    private KeySlot? Current => _position < _targets.Count ? _targets[_position] : null;

    private KeyVisual VisualFor(KeySlot slot)
    {
        bool isTarget = _targets.Contains(slot);
        Brush bg = slot == Current ? CurrentBrush
            : Results.ContainsKey(slot.Index) ? DoneBrush
            : isTarget ? TargetBrush : OtherBrush;
        string text = Results.TryGetValue(slot.Index, out var sc) ? $"0x{sc:X4}" : "";
        return new KeyVisual(text, bg, KeyboardCanvas.DefaultBorderBrush, slot == Current ? 3 : 1, slot.Description);
    }

    private void RefreshKey(KeySlot? slot)
    {
        if (slot != null) KbdCanvas.Update(slot, VisualFor(slot));
    }

    private void UpdateInstruction()
    {
        var current = Current;
        TxtInstruction.Text = current == null
            ? "すべて記録しました。「保存して閉じる」を押してください。"
            : $"黄色のキー「{current.PrintedLabel}」を押してください（{_position + 1} / {_targets.Count}）";
        BtnBack.IsEnabled = _position > 0;
        BtnSkip.IsEnabled = current != null;
        BtnFinish.IsEnabled = Results.Count > 0;
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        var client = _captureClient!;
        while (!ct.IsCancellationRequested)
        {
            ushort? sc;
            try
            {
                sc = await Task.Run(() => client.CaptureNextKey(_uniqId, CaptureTimeoutMs), ct);
            }
            catch (OperationCanceledException) { return; }
            catch (KbdLayerException ex)
            {
                TxtLast.Text = ex.Message;
                await Task.Delay(1000, ct).ContinueWith(_ => { });
                continue;
            }
            if (sc is ushort value && _active) OnCaptured(value);
        }
    }

    private void OnCaptured(ushort sc)
    {
        var current = Current;
        if (current == null) return;

        int? duplicate = Results.Where(r => r.Value == sc && r.Key != current.Index).Select(r => (int?)r.Key).FirstOrDefault();
        if (duplicate is int dupSlot)
        {
            TxtLast.Text = $"0x{sc:X4} は既に {_layout.Slots[dupSlot].Description} として記録済みです。" +
                           "押し間違えた場合は、もう一度正しいキーを押してください（違うキーなら「1つ戻る」で前のキーをやり直せます）。";
            return;
        }

        Results[current.Index] = sc;
        string note = current.DefaultScanCode is ushort def && def != sc ? $"（既定値 0x{def:X4} と異なります）" : "";
        TxtLast.Text = $"{current.Description}: 0x{sc:X4} を記録しました{note}";
        _position++;
        RefreshKey(current);
        RefreshKey(Current);
        UpdateInstruction();
        if (_targets.Count == 1) Finish(); // キー単位の実測は押した時点で完了
    }

    private void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        if (_position == 0) return;
        var old = Current;
        _position--;
        Results.Remove(_targets[_position].Index);
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
        TxtLast.Text = $"{old.Description} を飛ばしました（既定のスキャンコードのままになります）";
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

    private void Window_Activated(object? sender, EventArgs e)
    {
        if (_captureClient == null || _cts.IsCancellationRequested) return;
        try
        {
            _hook.Install();
            _active = true;
            TxtNote.Foreground = Brushes.DimGray;
            TxtNote.Text = "このウィンドウが前面にある間、キーボードで押したキーはWindowsに送られません（記録だけされます）。操作はマウスで行ってください。";
        }
        catch (Exception ex)
        {
            TxtNote.Text = $"キー入力の遮断を開始できませんでした（{ex.Message}）。押したキーはWindowsにも届きます。";
            _active = true;
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        _active = false;
        _hook.Uninstall();
        TxtNote.Foreground = Brushes.Firebrick;
        TxtNote.Text = "一時停止中: このウィンドウをクリックすると再開します（他のウィンドウへの入力は記録しません）。";
    }

    private void StopCapture()
    {
        _active = false;
        _hook.Uninstall();
        _cts.Cancel();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        StopCapture();
        _hook.Dispose();
        // 実行中のCaptureNextKeyは最大CaptureTimeoutMsで戻る。SafeHandleは使用中の呼び出しが
        // 終わるまで実際のクローズを遅らせるので、ここで破棄してよい。
        _captureClient?.Dispose();
    }
}
