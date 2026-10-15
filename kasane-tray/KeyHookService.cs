using System.Runtime.InteropServices;
using Kasane.Interop;
using Kasane.Model;

namespace Kasane.Tray;

/// <summary>
/// F13〜F24 などのキー（＋修飾キー）をトリガーにする、低レベルキーボードフック（P3、2026-10-08）。
///  - トリガー（<see cref="TriggerTable"/>）が1つでもあるときだけフックを張る。HLP(id) の動作だけなら張らない
///    （ドライバが直接知らせるので、フックは要らない）。
///  - 専用スレッドでメッセージループを回す。フックの中では表の引き当てと依頼を積むことだけをして、すぐ戻る
///    （LowLevelHooksTimeout を超えると、Windows にフックを外される）。
///  - 注入されたキー（LLKHF_INJECTED。自分の SendInput など）は見ない。キーボードは dwExtraInfo の上位16bit（ドライバが刻印する
///    DeviceId の上位16bit）で区別する。
///  - 一致した押下と、それに対応する解放・オートリピートを飲み込む。飲み込んだキーは覚えておき、押下の動作を最後まで使う
///    （途中で設定が変わっても、解放の対応が崩れない）。
///  - 動作を許すか（<c>allow</c>。セッションが非アクティブ・一時停止中は許さない）が false なら、飲み込まずに素通しする。
/// </summary>
public sealed class KeyHookService : IDisposable
{
    private const int HC_ACTION = 0;
    private const uint WM_APP_RECONCILE = 0x8001; // WM_APP + 1
    private const uint WM_QUIT = 0x0012;
    private const uint PM_NOREMOVE = 0;

    private readonly Func<TriggerTable> _table;
    private readonly Func<HelperActionBody, bool> _allow;
    private readonly Action<ActionRequest> _dispatch;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly NativeMethods.LowLevelKeyboardProc _proc; // GCされないよう保持する
    private readonly Dictionary<ushort, ActionRequest> _swallowed = new(); // フックのスレッドだけで使う
    // 物理的に押している修飾キー（KeycodeTable.ModifierScanCodes の並びのビット）。フックが見る、注入でないイベントから追う。
    // 実行中に修飾キーを OS から隠す（注入で離す）と GetAsyncKeyState の値が変わるため、そちらは使えない。
    private volatile byte _physKeys;
    private int _suppressActive; // 修飾キーを隠している実行の数。0 のときだけ、OS の状態で追跡を直す
    private volatile bool _stop;
    private uint _threadId;
    private IntPtr _hook;
    private bool _disposed;

    public KeyHookService(Func<TriggerTable> table, Func<HelperActionBody, bool> allow, Action<ActionRequest> dispatch)
    {
        _table = table;
        _allow = allow;
        _dispatch = dispatch;
        _proc = HookProc;
        _thread = new Thread(Loop) { IsBackground = true, Name = "kasane-key-hook" };
    }

    /// <summary>フックを張っているか（テストと診断用）。</summary>
    public bool Installed => _hook != IntPtr.Zero;

    /// <summary>いま物理的に押している修飾キー（どのスレッドからでも読める）。</summary>
    public byte PhysicalKeys => _physKeys;

    public void Start()
    {
        _thread.Start();
        _ready.Wait(2000);
    }

    /// <summary>トリガーの有無が変わったかもしれない。張る・外すを見直す（どのスレッドからでもよい）。</summary>
    public void Update()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_APP_RECONCILE, UIntPtr.Zero, IntPtr.Zero);
    }

    private void Loop()
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE); // このスレッドにメッセージキューを作る
        _ready.Set();
        Reconcile();
        while (!_stop && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_APP_RECONCILE) Reconcile();
        }
        Uninstall();
    }

    // トリガーがあれば張る、無ければ外す。
    private void Reconcile()
    {
        bool want;
        try { want = !_table().IsEmpty; }
        catch (Exception ex) { TrayLog.Write("KeyHook.Reconcile: " + ex); want = false; }
        if (want && _hook == IntPtr.Zero) Install();
        else if (!want && _hook != IntPtr.Zero) Uninstall();
    }

    private void Install()
    {
        _physKeys = ReadPhysicalKeys();
        _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero) TrayLog.Write("KeyHook: SetWindowsHookEx に失敗 (" + Marshal.GetLastWin32Error() + ")");
    }

    private void Uninstall()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        _swallowed.Clear();
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode == HC_ACTION && Handle((int)wParam, Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam)))
                return (IntPtr)1;
        }
        catch (Exception ex) { TrayLog.Write("KeyHook: " + ex); }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    // 飲み込むなら true。
    private bool Handle(int message, NativeMethods.KBDLLHOOKSTRUCT info)
    {
        if ((info.flags & NativeMethods.LLKHF_INJECTED) != 0) return false;

        bool down = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
        bool up = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
        if (!down && !up) return false;

        ushort scan = (ushort)(((info.flags & NativeMethods.LLKHF_EXTENDED) != 0 ? 0xE000 : 0) | (int)(info.scanCode & 0xFF));

        int modBit = Array.IndexOf(Kasane.Keycodes.KeycodeTable.ModifierScanCodes, scan);
        if (modBit >= 0) _physKeys = down ? (byte)(_physKeys | (1 << modBit)) : (byte)(_physKeys & ~(1 << modBit)); // 修飾キー自身は飲み込まない

        if (_swallowed.TryGetValue(scan, out var pressed))
        {
            if (up)
            {
                _swallowed.Remove(scan);
                if (ActionExecutor.NeedsRelease(pressed.Body)) _dispatch(pressed with { IsBreak = true, IsRepeat = false, SuppressKeys = 0, Done = null });
            }
            else if (pressed.Body.Repeat) _dispatch(pressed with { IsBreak = false, IsRepeat = true, Done = null });
            return true; // 押している間のオートリピートも、解放も飲み込む
        }
        if (!down) return false;

        var table = _table();
        if (table.IsEmpty || !table.HasKey(scan)) return false;
        ushort hash16 = (ushort)(((ulong)info.dwExtraInfo.ToInt64() >> 16) & 0xFFFF);
        // 隠している実行が無いときは、OS の状態で追跡を直す（フックが見逃したイベントがあっても、ずれたままにしない）。
        if (Volatile.Read(ref _suppressActive) == 0) _physKeys = ReadPhysicalKeys();
        byte mods = CurrentMods();
        var body = table.Lookup(scan, mods, hash16);
        if (body == null || !_allow(body)) return false;

        // Sequence は、実行の間だけトリガーの修飾キー（押している側）を OS から隠す。KeepMods で残せる。
        byte suppress = body.Type == HelperActionBody.Sequence && !body.KeepMods
            ? (byte)(_physKeys & (mods | (mods << 4)))
            : (byte)0;
        Action? done = null;
        if (suppress != 0)
        {
            Interlocked.Increment(ref _suppressActive);
            done = () => Interlocked.Decrement(ref _suppressActive);
        }
        var request = new ActionRequest(body, 0, hash16, IsBreak: false, IsRepeat: false, suppress, done);
        _swallowed[scan] = request;
        _dispatch(request);
        return true;
    }

    // 押している修飾キー（左右を区別しない）。TriggerTable の ModCtrl・ModShift・ModAlt・ModWin のビット（= ModifierScanCodes の下位4ビットと同じ並び）。
    private byte CurrentMods()
    {
        byte k = _physKeys;
        return (byte)((k | (k >> 4)) & 0x0F);
    }

    // ModifierScanCodes の並び（LCTL LSFT LALT LGUI RCTL RSFT RALT RGUI）の仮想キー。
    private static readonly int[] ModifierVks = { 0xA2, 0xA0, 0xA4, 0x5B, 0xA3, 0xA1, 0xA5, 0x5C };

    private static byte ReadPhysicalKeys()
    {
        byte keys = 0;
        for (int bit = 0; bit < ModifierVks.Length; bit++)
            if (Down(ModifierVks[bit])) keys |= (byte)(1 << bit);
        return keys;
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop = true;
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        _thread.Join(2000);
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

    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostThreadMessage(uint threadId, uint msg, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
