using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Kasane.Tray;

/// <summary>IMEの状態の取得元（テストでは偽物に差し替える）。</summary>
public interface IImeSource
{
    ImeState Read();
}

/// <summary>
/// フォーカス中のウィンドウのIMEの状態を、IMEウィンドウへの問い合わせ（WM_IME_CONTROL）で読む。AutoHotkeyのIME.ahkなど、
/// 実績のある方式。
///  1. GetForegroundWindow → そのスレッドのフォーカスウィンドウ（GetGUIThreadInfo）。
///  2. ImmGetDefaultIMEWnd でIMEウィンドウを取り、IMC_GETOPENSTATUS（ON/OFF）と IMC_GETCONVERSIONMODE（変換モード）を問い合わせる。
/// 取れないときは <see cref="ImeState.Unknown"/>（呼び出し側は直前の状態を保つ）。取れない例: 管理者として動いているウィンドウ
/// （UIPIで、昇格していないトレイからは問い合わせが届かない）、応答しないアプリ、IMEのウィンドウを持たないアプリ。
/// タスクバーやメニューなどのシェルのウィンドウ・トレイ自身は、IMEの状態を見ない（トレイのメニューを開いただけで
/// NICOLAが切れるのを防ぐ）。
/// </summary>
public sealed class Win32ImeSource : IImeSource
{
    private const uint WM_IME_CONTROL = 0x0283;
    private const int IMC_GETCONVERSIONMODE = 0x0001;
    private const int IMC_GETOPENSTATUS = 0x0005;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint QueryTimeoutMs = 100;

    // 前面にあってもIMEの状態を見ないウィンドウ（シェル・メニュー）。
    private static readonly string[] IgnoredClasses =
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "#32768", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "ForegroundStaging",
        "MultitaskingViewFrame", "TaskListThumbnailWnd", "SysShadow",
    };

    private readonly uint _ownPid = (uint)Environment.ProcessId;

    public ImeState Read() => ReadWithDetail(out _);

    /// <summary>診断用（kasane-tray.exe --probe-ime）。どのウィンドウを見て、どう判断したかを返す。</summary>
    public ImeState ReadWithDetail(out string detail)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) { detail = "前面のウィンドウがありません"; return ImeState.Unknown; }

        uint threadId = GetWindowThreadProcessId(foreground, out uint pid);
        string cls = ClassOf(foreground);
        string process = ProcessName(pid);
        if (pid == _ownPid) { detail = $"前面はトレイ自身（{cls}）なので見ません"; return ImeState.Unknown; }
        if (IgnoredClasses.Contains(cls)) { detail = $"前面はシェルのウィンドウ（{cls}、{process}）なので見ません"; return ImeState.Unknown; }

        IntPtr target = foreground;
        var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        if (GetGUIThreadInfo(threadId, ref info) && info.hwndFocus != IntPtr.Zero) target = info.hwndFocus;

        IntPtr imeWindow = ImmGetDefaultIMEWnd(target);
        if (imeWindow == IntPtr.Zero) { detail = $"{process}（{cls}）: IMEのウィンドウがありません"; return ImeState.Unknown; }

        if (!Query(imeWindow, IMC_GETOPENSTATUS, out long open))
        {
            detail = $"{process}（{cls}）: IMEのON/OFFを問い合わせられません（管理者として動いている・応答しない等）";
            return ImeState.Unknown;
        }
        if (!Query(imeWindow, IMC_GETCONVERSIONMODE, out long mode))
        {
            detail = $"{process}（{cls}）: IMEの変換モードを問い合わせられません";
            return ImeState.Unknown;
        }

        var state = new ImeState(true, open != 0, (uint)mode);
        detail = $"{process}（{cls}）: IME {(state.Open ? "ON" : "OFF")}、変換モード 0x{state.ConversionMode:X}、かな入力 {(state.IsKanaInput ? "はい" : "いいえ")}";
        return state;
    }

    private static bool Query(IntPtr imeWindow, int command, out long value)
    {
        IntPtr result = SendMessageTimeout(imeWindow, WM_IME_CONTROL, (IntPtr)command, IntPtr.Zero,
            SMTO_ABORTIFHUNG, QueryTimeoutMs, out IntPtr response);
        value = response.ToInt64();
        return result != IntPtr.Zero;
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private static string ProcessName(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return $"pid {pid}"; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public uint cbSize;
        public uint flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("imm32.dll")] private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
