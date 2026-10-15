using System.Diagnostics;

namespace Kasane.Tray;

/// <summary>
/// 設定画面などを新しく起動したとき、ウィンドウが出るまでの間だけ出す小さな「準備中」の表示。
/// 起動した相手のウィンドウが現れる・プロセスが終わる・30 秒たつ、のどれかで自動的に閉じる（閉じ忘れを残さない）。
/// 起動待ちの間に、同じものを何度も起動しないために、表示中かどうかも持つ。
/// </summary>
internal sealed class StartingNotice : Form
{
    private const int TimeoutMs = 30_000;

    private static StartingNotice? _current;

    /// <summary>表示中。トレイのクリックが続いたときに、2つ目を起動しないための判定に使う。</summary>
    public static bool IsShowing => _current != null;

    private readonly Process _process;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();

    private StartingNotice(Process process, string message)
    {
        _process = process;

        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        ControlBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        Text = Kasane.Model.AppInfo.DisplayName;
        Name = "StartingNotice";
        ClientSize = new Size(340, 84);
        Font = SystemFonts.MessageBoxFont ?? Font;

        var label = new Label
        {
            Name = "StartingNoticeText",
            Text = message,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Bounds = new Rectangle(16, 12, 308, 32),
        };
        var bar = new ProgressBar
        {
            Name = "StartingNoticeProgress",
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Bounds = new Rectangle(16, 52, 308, 14),
        };
        Controls.Add(label);
        Controls.Add(bar);

        _timer.Tick += (_, _) => CloseIfReady();
        _timer.Start();
    }

    /// <summary>フォーカスを奪わない（起動してきた相手のウィンドウが前面に出るのを邪魔しない）。</summary>
    protected override bool ShowWithoutActivation => true;

    /// <summary>起動した <paramref name="process"/> を見守る準備中の表示を出す。UI スレッドから呼ぶ。表示は、閉じるときにプロセスの後始末もする。</summary>
    public static void ShowFor(Process process, string message)
    {
        if (_current != null) { process.Dispose(); return; }
        var notice = new StartingNotice(process, message);
        _current = notice;
        notice.Show();
    }

    private void CloseIfReady()
    {
        bool done;
        try
        {
            _process.Refresh();
            done = _process.HasExited || _process.MainWindowHandle != IntPtr.Zero;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            done = true;
        }
        if (done || _elapsed.ElapsedMilliseconds > TimeoutMs) Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        _process.Dispose();
        _current = null;
        base.OnFormClosed(e);
    }
}
