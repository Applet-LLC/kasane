using System.Runtime.InteropServices;

namespace Kasane.Tray;

/// <summary>
/// IMEの状態を、天狼（sirius）のフックが書く共有メモリ（TSF のコンパートメントの値）から読む。IMM32 では取れないアプリ
/// （TSF だけのアプリなど）の補いにする。<see cref="SiriusHook"/> を入れていない間は <see cref="ImeState.Unknown"/>。
/// 前面のウィンドウの選び方（トレイ自身・シェルは見ない）は <see cref="Win32ImeSource.TryGetTarget"/> と同じ。
/// </summary>
internal sealed class TsfSiriusImeSource : IImeSource
{
    private readonly Win32ImeSource _foreground;
    private readonly object _gate = new();
    private SiriusHook? _hook;

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
            if (_hook == null) return ImeState.Unknown;
            if (!_foreground.TryGetTarget(wantDetail: false, out IntPtr foreground, out uint threadId, out _, out _, out _))
                return ImeState.Unknown;

            var snapshot = _hook.Read();
            // 共有メモリの HWND は「最後にフォーカスを受けたウィンドウ」。前面のアプリのスレッドのものか。
            bool inForeground = snapshot.HWndFocus != 0 && GetWindowThreadProcessId((IntPtr)snapshot.HWndFocus, out _) == threadId;
            return SiriusDecoder.Decide(snapshot, inForeground);
        }
    }

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
