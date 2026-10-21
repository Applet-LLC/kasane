using System.Windows;
using System.Windows.Controls;
using Kasane.Interop;

namespace Kasane.Views;

/// <summary>
/// 「押して特定」: 認識されている全キーボードで同時に CAPTURE_NEXT_KEY を待ち、最初にキーが
/// 押されたキーボードを返す。押したキーはLLフックで捨てる。
/// 同期I/Oのハンドルでは要求が直列化されるため、キーボードごとに別のハンドルを開く。
/// </summary>
public sealed class IdentifyWindow : Window
{
    private const uint TimeoutMs = 8000;

    private readonly List<DeviceInfo> _devices;
    private readonly LowLevelKeyboardHook _hook = new();
    private readonly TextBlock _message;

    public uint? FoundUniqId { get; private set; }

    public IdentifyWindow(List<DeviceInfo> devices)
    {
        _devices = devices;
        Title = "キーボードを特定";
        Width = 460;
        Height = 180;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;

        _message = new TextBlock
        {
            Text = $"設定したいキーボードで、どれかキーを1つ押してください({TimeoutMs / 1000}秒以内)。\n押したキーはWindowsには送られません。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            Margin = new Thickness(16),
        };
        var cancel = new Button { Content = "キャンセル", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 0, 16, 12) };
        var panel = new DockPanel();
        DockPanel.SetDock(cancel, Dock.Bottom);
        panel.Children.Add(cancel);
        panel.Children.Add(_message);
        Content = panel;

        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) => _hook.Dispose();
    }

    private async Task RunAsync()
    {
        try { _hook.Install(); } catch { /* 遮断できなくても特定自体はできる */ }

        var tasks = _devices.Select(d => Task.Run(() =>
        {
            using var client = KbdLayerClient.TryOpen(out _);
            if (client == null) return (uint?)null;
            try { return client.CaptureNextKey(d.UniqId, TimeoutMs) != null ? d.UniqId : (uint?)null; }
            catch (KbdLayerException) { return null; }
        })).ToList();

        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks);
            tasks.Remove(done);
            if (done.Result is uint id)
            {
                FoundUniqId = id;
                _hook.Uninstall();
                if (IsVisible) DialogResult = true;
                return; // 残りのキャプチャは各自のタイムアウトで終わる
            }
        }
        _hook.Uninstall();
        _message.Text = "キー入力を検出できませんでした。ドライバが認識していないキーボードか、ライセンスが有効でない可能性があります。";
    }
}
