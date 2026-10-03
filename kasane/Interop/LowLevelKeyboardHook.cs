using System.Runtime.InteropServices;

namespace Kasane.Interop;

/// <summary>
/// キャリブレーション中だけ設置する WH_KEYBOARD_LL フック。押されたキーをWindowsへ渡さずに捨て、
/// 押下があったことだけを通知する（どのスキャンコードかの記録はドライバのCAPTURE_NEXT_KEYが担う。
/// フックが見るのはリマップ後の値で、どのキーボードからの入力かも区別できないため）。
///
/// 規則:
///  - 注入されたイベント（LLKHF_INJECTED）は通す。
///  - 押下を捨てたキーは、その解放も捨てる。フック設置前から押されていたキーの解放は通す
///    （通さないとOS側でそのキーが押しっぱなし扱いになる）。
///  - コールバックは即座に戻る（LowLevelHooksTimeoutを超えるとWindowsにフックを外されるため、
///    通知はDispatcherへ非同期で投げるだけ）。
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    private readonly NativeMethods.LowLevelKeyboardProc _proc; // GCされないよう保持する
    private readonly HashSet<uint> _swallowedDown = new();
    private IntPtr _hook = IntPtr.Zero;

    /// <summary>キー押下を捨てたときに呼ばれる（引数は仮想キーコード）。フックのスレッドで呼ばれる。</summary>
    public event Action<uint>? KeyDownSwallowed;

    /// <summary>
    /// 押下・解放を捨てたときに呼ばれる（引数はスキャンコード（Enc16。拡張キーは0xE0xx）と押下か）。
    /// フックのスレッドで呼ばれる。フックが見るのはレイヤーマップで置き換えた後のキー。
    /// </summary>
    public event Action<ushort, bool>? KeySwallowed;

    public LowLevelKeyboardHook()
    {
        _proc = HookProc;
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _swallowedDown.Clear();
        _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _proc,
            NativeMethods.GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _swallowedDown.Clear();
    }

    public void Dispose() => Uninstall();

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            if ((info.flags & NativeMethods.LLKHF_INJECTED) == 0)
            {
                int msg = wParam.ToInt32();
                bool isDown = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
                bool isUp = msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP;
                ushort scan16 = (ushort)(((info.flags & NativeMethods.LLKHF_EXTENDED) != 0 ? 0xE000u : 0u) | (info.scanCode & 0xFFu));
                if (isDown)
                {
                    _swallowedDown.Add(info.vkCode);
                    KeyDownSwallowed?.Invoke(info.vkCode);
                    KeySwallowed?.Invoke(scan16, true);
                    return (IntPtr)1;
                }
                if (isUp && _swallowedDown.Remove(info.vkCode))
                {
                    KeySwallowed?.Invoke(scan16, false);
                    return (IntPtr)1;
                }
            }
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
