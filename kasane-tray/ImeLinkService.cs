namespace Kasane.Tray;

/// <summary>
/// IME連動を動かす専用スレッド。UIスレッドを止めないため（IMEウィンドウへの問い合わせは、相手が応答しないと最大100ms待つ）と、
/// ドライバのハンドルをこのスレッドだけで使うため。
///  - 連動していてNICOLAの対象があるときは100msごとにIMEの状態を読む。そうでなければ1秒ごと（読まない）。
///  - <see cref="Poke"/> で、すぐに読み直す（前面のウィンドウが変わったとき）。
/// </summary>
public sealed class ImeLinkService : IDisposable
{
    private const int ActivePollMs = 100;
    private const int IdlePollMs = 1000;

    private readonly ImeLinkController _controller;
    private readonly IImeSource _source;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _sessionActive;
    private readonly AutoResetEvent _poke = new(false);
    private readonly Thread _thread;
    private volatile bool _stop;
    private bool _disposed;
    private ImeLinkStatus _status = ImeLinkStatus.Off;

    public ImeLinkService(ImeLinkController controller, IImeSource source, Func<bool> enabled, Func<bool> sessionActive)
    {
        _controller = controller;
        _source = source;
        _enabled = enabled;
        _sessionActive = sessionActive;
        _thread = new Thread(Loop) { IsBackground = true, Name = "kasane-ime-link" };
    }

    public ImeLinkStatus Status => Volatile.Read(ref _status);

    public void Start() => _thread.Start();

    public void Poke() => _poke.Set();

    private void Loop()
    {
        while (!_stop)
        {
            try
            {
                bool enabled = _enabled();
                bool active = _sessionActive();
                var current = Status;
                // 対象があるときだけIMEの状態を読む（対象を調べるのは、Tick の中）。
                var ime = enabled && active && current.TargetCount > 0 ? _source.Read() : ImeState.Unknown;
                Volatile.Write(ref _status, _controller.Tick(ime, enabled, active));
            }
            catch (Exception ex)
            {
                TrayLog.Write("ImeLink: " + ex);
                Thread.Sleep(1000);
            }

            var s = Status;
            _poke.WaitOne(s.Linked && s.TargetCount > 0 ? ActivePollMs : IdlePollMs);
        }
    }

    public void Dispose()
    {
        if (_disposed) return; // 終了処理（ExitTray）と、後始末（Dispose）の両方から呼ばれる
        _disposed = true;
        _stop = true;
        _poke.Set();
        _thread.Join(2000);
        _controller.Shutdown(); // 立てていたレイヤーを戻す（ハンドルを閉じる）
        _poke.Dispose();
    }
}
