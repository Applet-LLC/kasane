using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Kasane.Keycodes;
using Kasane.Model;

namespace Kasane.Tray;

/// <summary>
/// ヘルパー動作を実行する専用スレッド（P3、2026-10-08）。キーフックやドライバのイベントを受けるスレッドを止めないよう、依頼は
/// キューに積むだけにして、ここで順に実行する（<see cref="ActionExecutor"/>）。
///  - 入力は SendInput（キーはスキャンコード、文字は Unicode）。ドライバより上の層に入るので、ドライバのリマップを受けず、
///    キーフックも（注入として）見ない。管理者として動くウィンドウには、uiAccess 版のトレイ（インストールしたもの）なら届き、uiAccess の無い版では届かない（UIPI）。
///  - 押下・オートリピートは、実行の直前にも許可（<c>allow</c>）を確かめる（待っている間に、一時停止・ロックされたかもしれない）。
///    解放は、押したときの後始末なので許可に関係なく実行する。
///  - 終了時は、実行中の待ちを打ち切って止める。
/// </summary>
public sealed class ActionRunner : IDisposable
{
    // 押下は 64 件で打ち切る。解放は、押下が入ったのに捨てられると（マウスのボタンを押したままになるなど）困るので、
    // その分の余裕（256 件まで）を持つ。
    private const int PressCapacity = 64;
    private const int TotalCapacity = 256;

    private readonly BlockingCollection<ActionRequest> _queue = new(TotalCapacity);
    private readonly Func<HelperActionBody, bool> _allow;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private readonly Win32ActionEnvironment _env;
    private bool _disposed;

    /// <summary>物理的に押している修飾キー（キーフックが追う）。隠した修飾キーを戻すかの判断に使う。フックを作ったあとに設定する。</summary>
    public Func<byte>? PhysicalKeys { get; set; }

    public ActionRunner(Func<HelperActionBody, bool> allow, Action<LayerRequest> layer, Action togglePause)
    {
        _allow = allow;
        _env = new Win32ActionEnvironment(_cts.Token, layer, togglePause, () => PhysicalKeys?.Invoke() ?? 0);
        _thread = new Thread(Loop) { IsBackground = true, Name = "kasane-actions" };
        _thread.Start();
    }

    /// <summary>依頼を積む（どのスレッドからでもよい。満杯なら捨てる）。</summary>
    public void Enqueue(ActionRequest request)
    {
        if (_disposed) return;
        try
        {
            if ((!request.IsBreak && _queue.Count >= PressCapacity) || !_queue.TryAdd(request))
            {
                TrayLog.Write("ActionRunner: キューが満杯のため、動作を捨てました");
                request.Done?.Invoke();
            }
        }
        catch (InvalidOperationException) { /* 終了中 */ }
    }

    private void Loop()
    {
        var executor = new ActionExecutor(_env);
        try
        {
            foreach (var request in _queue.GetConsumingEnumerable(_cts.Token))
            {
                try
                {
                    if (!request.IsBreak && !_allow(request.Body)) { request.Done?.Invoke(); continue; }
                    executor.Execute(request);
                }
                catch (Exception ex) { TrayLog.Write("ActionRunner: " + ex); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _queue.CompleteAdding();
        _thread.Join(2000);
        _cts.Dispose();
    }
}

/// <summary>実際の入力（SendInput）・起動（ShellExecute）を行う <see cref="IActionEnvironment"/>。</summary>
internal sealed class Win32ActionEnvironment : IActionEnvironment
{
    // SendInput で入れたキーであることの印（ほかのツールが見分けられるように）。"KSNE"。
    private static readonly IntPtr Marker = new(0x4B534E45);

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004, KEYEVENTF_SCANCODE = 0x0008;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
        MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040,
        MOUSEEVENTF_XDOWN = 0x0080, MOUSEEVENTF_XUP = 0x0100, MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000,
        MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    private readonly CancellationToken _token;
    private readonly Action<LayerRequest> _layer;
    private readonly Action _togglePause;
    private readonly Func<byte> _physicalKeys;

    private readonly ImeController _ime;
    private readonly DriverKeyInjector _driverKey = new();

    public Win32ActionEnvironment(CancellationToken token, Action<LayerRequest> layer, Action togglePause, Func<byte> physicalKeys)
    {
        _ime = new ImeController(new Win32ImeSource(), SendVirtualKey, _driverKey.TrySend);
        _physicalKeys = physicalKeys;
        _token = token;
        _layer = layer;
        _togglePause = togglePause;
    }

    // キーフックの値に、KillLine 用の見張りの値を足す（見張りは、初めて使うまで張らない）。
    public byte PhysicalKeys() => (byte)(_physicalKeys() | _monitor.PhysicalKeys);

    private readonly KeyActivityMonitor _monitor = new();

    public byte HeldModifiers()
    {
        _monitor.EnsureStarted();
        return _monitor.PhysicalKeys;
    }

    public bool TakeKeyActivity() => _monitor.TakeKeyActivity();

    public long TickMs() => Environment.TickCount64;

    public long ForegroundWindow() => GetForegroundWindow().ToInt64();

    public uint ClipboardSequence() => GetClipboardSequenceNumber();

    // クリップボードは、ほかのアプリが開いていると開けない。短く待って数回試す。
    public bool TryGetClipboardText(out string text)
    {
        text = "";
        if (!OpenClipboardRetry()) return false;
        try
        {
            IntPtr h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return false;
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) return false;
            try { text = Marshal.PtrToStringUni(p) ?? ""; }
            finally { GlobalUnlock(h); }
            return true;
        }
        finally { CloseClipboard(); }
    }

    public bool SetClipboardText(string? text)
    {
        if (!OpenClipboardRetry()) { TrayLog.Write("KillLine: クリップボードを開けません"); return false; }
        try
        {
            EmptyClipboard();
            if (string.IsNullOrEmpty(text)) return true;
            int bytes = (text.Length + 1) * 2;
            IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            if (h == IntPtr.Zero) return false;
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) { GlobalFree(h); return false; }
            Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
            Marshal.WriteInt16(p, text.Length * 2, 0);
            GlobalUnlock(h);
            if (SetClipboardData(CF_UNICODETEXT, h) == IntPtr.Zero) { GlobalFree(h); return false; }
            return true; // 渡した領域は、クリップボードのものになる
        }
        finally { CloseClipboard(); }
    }

    private bool OpenClipboardRetry()
    {
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            if (!Sleep(10)) return false;
        }
        return false;
    }

    private const uint CF_UNICODETEXT = 13, GMEM_MOVEABLE = 0x0002;

    public bool SetIme(ImeTarget target, ImeMethod method) => _ime.Set(target, method);

    // 仮想キーコードで1件送る（IME 用のキー。VK_IME_ON/OFF・VK_KANJI は、スキャンコードの列には入っていない）。
    private static void SendVirtualKey(ushort vk, ushort scan, bool up)
    {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.u.ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = up ? KEYEVENTF_KEYUP : 0, dwExtraInfo = Marker };
        if (SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) != 1)
            TrayLog.Write("SendInput に失敗: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }

    public void Send(InputOp op)
    {
        var input = new INPUT();
        switch (op.Kind)
        {
            case InputKind.Key:
            {
                ushort sc = (ushort)op.Value;
                if ((sc >> 8) == 0xE1) { TrayLog.Write("Sequence: E1 プレフィックスのキーは送れません"); return; }
                uint flags = KEYEVENTF_SCANCODE | (op.Up ? KEYEVENTF_KEYUP : 0) | ((sc >> 8) == 0xE0 ? KEYEVENTF_EXTENDEDKEY : 0);
                input.type = INPUT_KEYBOARD;
                input.u.ki = new KEYBDINPUT { wScan = (ushort)(sc & 0xFF), dwFlags = flags, dwExtraInfo = Marker };
                break;
            }
            case InputKind.Vk:
            {
                ushort vk = (ushort)op.Value;
                input.type = INPUT_KEYBOARD;
                input.u.ki = new KEYBDINPUT { wVk = vk, dwFlags = (op.Up ? KEYEVENTF_KEYUP : 0) | (VkTable.IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0), dwExtraInfo = Marker };
                break;
            }
            case InputKind.Unicode:
                input.type = INPUT_KEYBOARD;
                input.u.ki = new KEYBDINPUT { wScan = (ushort)op.Value, dwFlags = KEYEVENTF_UNICODE | (op.Up ? KEYEVENTF_KEYUP : 0), dwExtraInfo = Marker };
                break;
            case InputKind.MouseButton:
                input.type = INPUT_MOUSE;
                input.u.mi = MouseButton(op.Value, op.Up);
                break;
            case InputKind.MouseMove:
                input.type = INPUT_MOUSE;
                input.u.mi = op.Flag ? AbsoluteMove(op.Value, op.Value2) : new MOUSEINPUT { dx = op.Value, dy = op.Value2, dwFlags = MOUSEEVENTF_MOVE, dwExtraInfo = Marker };
                break;
            case InputKind.Wheel:
                input.type = INPUT_MOUSE;
                input.u.mi = new MOUSEINPUT { mouseData = unchecked((uint)op.Value), dwFlags = MOUSEEVENTF_WHEEL, dwExtraInfo = Marker };
                break;
            case InputKind.HWheel:
                input.type = INPUT_MOUSE;
                input.u.mi = new MOUSEINPUT { mouseData = unchecked((uint)op.Value), dwFlags = MOUSEEVENTF_HWHEEL, dwExtraInfo = Marker };
                break;
            default:
                return;
        }
        if (SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) != 1)
            TrayLog.Write("SendInput に失敗: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }

    private static MOUSEINPUT MouseButton(int button, bool up)
    {
        var mi = new MOUSEINPUT { dwExtraInfo = Marker };
        switch (button)
        {
            case HelperMouseButtons.Left: mi.dwFlags = up ? MOUSEEVENTF_LEFTUP : MOUSEEVENTF_LEFTDOWN; break;
            case HelperMouseButtons.Right: mi.dwFlags = up ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_RIGHTDOWN; break;
            case HelperMouseButtons.Middle: mi.dwFlags = up ? MOUSEEVENTF_MIDDLEUP : MOUSEEVENTF_MIDDLEDOWN; break;
            default: mi.dwFlags = up ? MOUSEEVENTF_XUP : MOUSEEVENTF_XDOWN; mi.mouseData = button == HelperMouseButtons.X1 ? 1u : 2u; break;
        }
        return mi;
    }

    // 仮想デスクトップの画面座標 → 0〜65535 の正規化座標。
    private static MOUSEINPUT AbsoluteMove(int x, int y)
    {
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN), top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = Math.Max(2, GetSystemMetrics(SM_CXVIRTUALSCREEN)), height = Math.Max(2, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        return new MOUSEINPUT
        {
            dx = (int)Math.Clamp((long)(x - left) * 65535 / (width - 1), 0, 65535),
            dy = (int)Math.Clamp((long)(y - top) * 65535 / (height - 1), 0, 65535),
            dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
            dwExtraInfo = Marker,
        };
    }

    public bool Sleep(int milliseconds) => !_token.WaitHandle.WaitOne(milliseconds);

    public void Shell(string path, string? args, string? workDir)
    {
        try
        {
            var psi = new ProcessStartInfo(path) { UseShellExecute = true };
            if (!string.IsNullOrWhiteSpace(args)) psi.Arguments = args;
            if (!string.IsNullOrWhiteSpace(workDir)) psi.WorkingDirectory = workDir;
            using var _ = Process.Start(psi);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            TrayLog.Write($"起動に失敗: {path}: {ex.Message}");
        }
    }

    public void TogglePause() => _togglePause();

    public void Layer(LayerRequest request) => _layer(request);

    public void Log(string message) => TrayLog.Write(message);

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr mem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr mem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr mem);
}
