using System.Runtime.InteropServices;
using Loc = Kasane.Model.Loc;

namespace Kasane.Tray;

/// <summary>
/// IMEの状態を、天狼（sirius）のフックが書く共有メモリ（TSF のコンパートメントの値）から読む。IMM32 では取れないアプリ
/// （TSF だけのアプリなど）の補いにする。<see cref="SiriusHook"/> を入れていない間は <see cref="ImeState.Unknown"/>。
/// 前面のウィンドウの選び方（トレイ自身・シェルは見ない）は <see cref="Win32ImeSource.TryGetTarget"/> と同じ。
/// </summary>
internal sealed class TsfSiriusImeSource : IImeSource, IImeSourceDiagnostics
{
    private readonly Win32ImeSource _foreground;
    private readonly object _gate = new();
    private SiriusHook? _hook;

    // 直前の Read の結果の理由（ログ用）。Read と同じスレッドだけが読み書きする。
    private TsfReason _reason = TsfReason.NotAttached;
    private long _lastFocusHwnd;
    private IntPtr _lastForeground;

    public TsfSiriusImeSource(Win32ImeSource foreground) => _foreground = foreground;

    /// <summary>フックを入れた（または入れ替えた）。<paramref name="hook"/> は呼び出し側のもので、ここでは Dispose しない。</summary>
    public void Attach(SiriusHook hook)
    {
        lock (_gate) _hook = hook;
    }

    /// <summary>
    /// 読むのをやめる。戻ったあとは共有メモリを読まないので、呼び出し側は <see cref="SiriusHook.Dispose"/> してよい
    /// （読んでいる最中にメモリを外すと、アクセス違反になる）。
    /// </summary>
    public void Detach()
    {
        lock (_gate) _hook = null;
    }

    public ImeState Read()
    {
        lock (_gate)
        {
            if (_hook == null) { _reason = TsfReason.NotAttached; return ImeState.Unknown; }
            if (!_foreground.TryGetTarget(wantDetail: false, out IntPtr foreground, out _, out _, out _, out _))
            {
                _reason = TsfReason.NoTarget;
                return ImeState.Unknown;
            }

            var snapshot = _hook.Read();
            // 共有メモリの HWND は「最後にフォーカスを受けたウィンドウ」。前面のアプリ（プロセス）のものか。スレッドでは比べない
            // （新しいメモ帳などの WinUI 系アプリは、フォーカスを持つウィンドウが、前面のウィンドウとは別のスレッドにある）。
            bool inForeground = snapshot.HWndFocus != 0
                && GetWindowThreadProcessId((IntPtr)snapshot.HWndFocus, out uint focusPid) != 0
                && GetWindowThreadProcessId(foreground, out uint foregroundPid) != 0
                && focusPid == foregroundPid;
            _lastFocusHwnd = snapshot.HWndFocus;
            _lastForeground = foreground;
            var state = SiriusDecoder.Decide(snapshot, inForeground, out _reason);
            return state;
        }
    }

    public int ReasonCode => (int)_reason;

    /// <summary>直前の Read で採れなかった理由（ログ用。変わったときにだけ呼ぶ）。</summary>
    public string DescribeLast() => _reason switch
    {
        TsfReason.Ok => "",
        TsfReason.NotAttached => Loc.T("TSF: フックを入れていません", "TSF: the hook is not installed"),
        TsfReason.NoTarget => Loc.T("TSF: 前面が対象外のウィンドウです", "TSF: the foreground window is not a target"),
        TsfReason.NotHooked => Loc.T("TSF: DLL がフックの設置を報告していません", "TSF: the DLL has not reported that the hook is installed"),
        TsfReason.OpenCloseUnsupported =>
            Loc.T($"TSF: 前面のアプリの OpenClose が読めていません(共有メモリの最後のフォーカス窓: {_foreground.ProcessLabelOfWindow((IntPtr)_lastFocusHwnd)}、前面: {_foreground.ProcessLabelOfWindow(_lastForeground)})",
                  $"TSF: cannot read OpenClose of the foreground app (last focus window in shared memory: {_foreground.ProcessLabelOfWindow((IntPtr)_lastFocusHwnd)}, foreground: {_foreground.ProcessLabelOfWindow(_lastForeground)})"),
        TsfReason.FocusNotForeground =>
            Loc.T($"TSF: 共有メモリの値は前面のアプリのものではありません(最後のフォーカス窓: {_foreground.ProcessLabelOfWindow((IntPtr)_lastFocusHwnd)}、前面: {_foreground.ProcessLabelOfWindow(_lastForeground)})",
                  $"TSF: the shared memory value does not belong to the foreground app (last focus window: {_foreground.ProcessLabelOfWindow((IntPtr)_lastFocusHwnd)}, foreground: {_foreground.ProcessLabelOfWindow(_lastForeground)})"),
        TsfReason.ConversionUnsupported => Loc.T("TSF: ON ですが、変換モードが読めていません", "TSF: the IME is on, but the conversion mode cannot be read"),
        _ => "",
    };

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}

/// <summary>
/// 2つの取得元を順に試す。先が読めたらそれを使い、読めなかった（<see cref="ImeState.Known"/> でない）ときだけ、後を試す。
/// どちらも読めなければ <see cref="ImeState.Unknown"/>（呼び出し側は直前の状態を保つ）。
/// </summary>
public sealed class FallbackImeSource : IImeSource
{
    private readonly IImeSource _first;
    private readonly IImeSource _second;

    public FallbackImeSource(IImeSource first, IImeSource second)
    {
        _first = first;
        _second = second;
    }

    public ImeState Read()
    {
        var state = _first.Read();
        return state.Known ? state : _second.Read();
    }
}
