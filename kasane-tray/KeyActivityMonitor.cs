using System.Runtime.InteropServices;
using Kasane.Interop;
using Kasane.Keycodes;

namespace Kasane.Tray;

/// <summary>
/// 実際のキー入力を見張る低レベルキーボードフック（KillLine＝Emacs の C-k の、連結をやめる合図に使う）。
///  - 最初に使われたときに張り、トレイを終えるまで外さない（KillLine を使わない人には張らない）。
///  - 注入されたキー（自分の SendInput）は見ない。修飾キー以外のキーが押されたら、印を立てる（<see cref="TakeKeyActivity"/> が読んで戻す）。
///  - 修飾キーの押し下げも追う（<see cref="PhysicalKeys"/>。<see cref="KeycodeTable.ModifierScanCodes"/> の並びのビット）。
///  - フックの中では印を立てるだけで、すぐ戻る。
/// </summary>
internal sealed class KeyActivityMonitor : IDisposable
{
    private const uint WM_QUIT = 0x0012;
    private const uint PM_NOREMOVE = 0;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003, WINEVENT_OUTOFCONTEXT = 0;
    private static readonly int[] ModifierVks = { 0xA2, 0xA0, 0xA4, 0x5B, 0xA3, 0xA1, 0xA5, 0x5C };

    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly object _startLock = new();
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private volatile byte _physKeys;
    private volatile bool _activity;
    private IntPtr _winEventHook;
    private readonly WinEventProc _winEventProc;
    private bool _disposed;

    public KeyActivityMonitor()
    {
        _proc = HookProc;
        _winEventProc = (_, _, _, _, _, _, _) => _activity = true; // 前面のウィンドウが変わった（A→B→A と戻っても分かる）
    }

    /// <summary>フックを張る（初回だけ。張れるまで少し待つ）。</summary>
    public void EnsureStarted()
    {
        lock (_startLock)
        {
            if (_thread != null || _disposed) return;
            _thread = new Thread(Loop) { IsBackground = true, Name = "kasane-key-activity" };
            _thread.Start();
        }
        _ready.Wait(2000);
    }

    /// <summary>いま物理的に押している修飾キー（フックが見た分）。</summary>
    public byte PhysicalKeys => _physKeys;

    /// <summary>修飾キー以外のキーが押された、または前面のウィンドウが変わったか。呼ぶたびに戻す。</summary>
    public bool TakeKeyActivity()
    {
        bool v = _activity;
        _activity = false;
        return v;
    }

    private void Loop()
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
        byte keys = 0;
        for (int bit = 0; bit < ModifierVks.Length; bit++)
            if ((GetAsyncKeyState(ModifierVks[bit]) & 0x8000) != 0) keys |= (byte)(1 << bit);
        _physKeys = keys;
        _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero) TrayLog.Write("KeyActivityMonitor: SetWindowsHookEx に失敗 (" + Marshal.GetLastWin32Error() + ")");
        _winEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        _ready.Set();
        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        if (_winEventHook != IntPtr.Zero) { UnhookWinEvent(_winEventHook); _winEventHook = IntPtr.Zero; }
        if (_hook != IntPtr.Zero) { NativeMethods.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode == 0)
            {
                var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                if ((info.flags & NativeMethods.LLKHF_INJECTED) == 0)
                {
                    int message = (int)wParam;
                    bool down = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
                    bool up = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
                    if (down || up)
                    {
                        ushort scan = (ushort)(((info.flags & NativeMethods.LLKHF_EXTENDED) != 0 ? 0xE000 : 0) | (int)(info.scanCode & 0xFF));
                        int bit = Array.IndexOf(KeycodeTable.ModifierScanCodes, scan);
                        if (bit >= 0) _physKeys = down ? (byte)(_physKeys | (1 << bit)) : (byte)(_physKeys & ~(1 << bit));
                        else if (down) _activity = true;
                    }
                }
            }
        }
        catch (Exception ex) { TrayLog.Write("KeyActivityMonitor: " + ex); }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        _thread?.Join(2000);
        _ready.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostThreadMessage(uint threadId, uint msg, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
