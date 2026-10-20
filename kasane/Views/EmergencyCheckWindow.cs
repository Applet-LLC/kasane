using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kasane.Interop;
using Kasane.Keycodes;

namespace Kasane.Views;

/// <summary>
/// 緊急解除の組み合わせを実際に押して、全部のキーを同時に検出できるかを確かめる
/// （N-キーロールオーバーに対応していないキーボードでは、組み合わせの一部が同時に上がってこないことがある）。
/// ドライバは変えず、LLフックで押下を見る。押したキーはWindowsへ渡さない（Ctrl+Shift等のショートカットが
/// 働かないように）。フックが見るのはレイヤーマップで置き換えた後のキーなので、組み合わせのキーの割り当てを
/// 変えている場合は、レイヤーマップをOFFにしてから試す必要がある。
/// </summary>
public sealed class EmergencyCheckWindow : Window
{
    private readonly LowLevelKeyboardHook _hook = new();
    // 管理者でドライバに接続しているときは、ドライバから直接キーを受ける（置き換える前の物理キー。押下と解放が確実に届く）。
    // LL フックは、どちらの場合も「キーを Windows に渡さない」ために設置する。
    private readonly uint _driverUniqId;
    private KeyStreamSession? _stream;
    private volatile bool _driverKeys;
    private readonly List<ushort> _keys;
    private readonly HashSet<ushort> _down = new();
    private readonly Border[] _faces;
    private readonly TextBlock _result;
    private int _maxTogether;
    private bool _succeeded;

    public bool Succeeded => _succeeded;

    /// <param name="driverUniqId">0 以外なら、そのキーボードの入力を、ドライバから直接受ける（管理者のとき）。0 なら LL フック。</param>
    public EmergencyCheckWindow(IReadOnlyList<ushort> keys, uint driverUniqId = 0)
    {
        _driverUniqId = driverUniqId;
        _keys = keys.ToList();
        Title = "緊急解除の組み合わせを試す";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            Text = "組み合わせのキーを全部同時に押してください。押しているキーが緑になります。全部が緑になれば、" +
                   "このキーボードはこの組み合わせを同時に検出できます。\n" +
                   $"注意: {KbdLayerClient.EmergencyHoldMs / 1000}秒以上押し続けると、緊急解除が実際に働き、接続中の全キーボードの" +
                   "レイヤーマップがOFFになります。全部が緑になったら、すぐに離してください。\n" +
                   "この画面を開いている間、押したキーはほかのアプリに届きません。" +
                   (driverUniqId != 0
                       ? "キーはドライバから直接受け取ります（レイヤーマップで置き換える前の物理キー。選んでいるキーボードの入力だけ）。"
                       : "\n低レベルキーフックモードで確認しており、選択しているキーボードレイアウトに依存して、一部キーアップが認識できないキーが存在します。その場合には、管理者権限モードでやり直してください。"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });

        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        _faces = new Border[_keys.Count];
        for (int i = 0; i < _keys.Count; i++)
        {
            _faces[i] = new Border
            {
                Name = $"CheckKey{i + 1}",
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Background = AppTheme.Palette.CheckFaceOff,
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Child = new TextBlock { Text = KeycodeTable.NameForScanCode(_keys[i]) ?? $"0x{_keys[i]:X4}", FontFamily = new FontFamily("Consolas") },
            };
            row.Children.Add(_faces[i]);
        }
        panel.Children.Add(row);

        _result = new TextBlock { Name = "CheckResult", TextWrapping = TextWrapping.Wrap, Text = "まだ押されていません。" };
        panel.Children.Add(_result);

        var close = new Button { Name = "CloseButton", Content = "閉じる", IsCancel = true, Width = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        close.Click += (_, _) => Close();
        panel.Children.Add(close);
        Content = panel;

        _hook.KeySwallowed += (sc, isDown) => Dispatcher.BeginInvoke(() =>
        {
            if (!_driverKeys) OnKey(sc, isDown); // ドライバから受けているときは、フックは「渡さない」ためだけ
        });
        Loaded += (_, _) =>
        {
            try { _hook.Install(); }
            catch (Exception ex) { _result.Text = $"キーの監視を始められませんでした: {ex.Message}"; }
            if (_driverUniqId != 0)
            {
                _driverKeys = true;
                _stream = new KeyStreamSession(
                    e => Dispatcher.BeginInvoke(() =>
                    {
                        if (_driverKeys && e.UniqId == _driverUniqId) OnKey(e.ScanCode, !e.IsBreak);
                    }),
                    (state, message) => Dispatcher.BeginInvoke(() =>
                    {
                        _driverKeys = false;
                        _result.Text = $"ドライバからキーを受け取れないため、Windows のキー入力（LL フック）で確認します: {message}";
                    }));
                _stream.Start();
            }
        };
        Closed += (_, _) =>
        {
            _stream?.Dispose();
            _hook.Dispose();
        };
    }

    private void OnKey(ushort scanCode, bool isDown)
    {
        if (isDown) _down.Add(scanCode);
        else _down.Remove(scanCode);

        int together = 0;
        for (int i = 0; i < _keys.Count; i++)
        {
            bool on = _down.Contains(_keys[i]);
            if (on) together++;
            _faces[i].Background = on ? AppTheme.Palette.CheckFaceOn : AppTheme.Palette.CheckFaceOff;
        }
        _maxTogether = Math.Max(_maxTogether, together);
        if (together == _keys.Count) _succeeded = true;

        _result.Foreground = _succeeded ? AppTheme.Palette.OkText : AppTheme.Palette.StrongText;
        _result.Text = _succeeded
            ? "OK: 全部のキーを同時に検出できました。キーを離してください。"
            : $"同時に検出できたキー: 最大 {_maxTogether} / {_keys.Count}。" +
              (_maxTogether > 0 && together == 0
                  ? " 全部を押しても緑にならないキーがある場合、このキーボードはその組み合わせを同時に検出できません。別の組み合わせを選んでください。"
                  : "");
    }
}
