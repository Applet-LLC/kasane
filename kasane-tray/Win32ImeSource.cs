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

    // クラス名が取れない・見慣れないシェルのウィンドウでも除外できるよう、プロセス名でも除外する
    // （エクスプローラー本体のタスクバー・フライアウト・メニュー、スタートメニュー、検索、ロック画面など）。
    // エクスプローラーのファイル窓（名前の変更・検索ボックスで文字を打つ）は除く。
    private static readonly string[] IgnoredProcesses =
    {
        "StartMenuExperienceHost", "SearchHost", "SearchApp", "ShellExperienceHost", "TextInputHost", "LockApp", "ShellHost",
    };
    private static readonly string[] ExplorerFileWindowClasses = { "CabinetWClass", "ExploreWClass", "#32770" };

    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private readonly Dictionary<uint, string> _processNames = new();
    private long _processNamesClearedAt = Environment.TickCount64;

    /// <summary>毎回呼ぶ経路（100msごと）。診断用の文字列は作らず、プロセス名も、前に調べたものを使う。</summary>
    public ImeState Read() => ReadCore(wantDetail: false, out _);

    /// <summary>診断用（kasane-tray.exe --probe-ime）。どのウィンドウを見て、どう判断したかを返す。</summary>
    public ImeState ReadWithDetail(out string detail) => ReadCore(wantDetail: true, out detail);

    /// <summary>
    /// IMEの状態を見る対象の前面のウィンドウを選ぶ。前面が無い・トレイ自身・シェルのウィンドウなら false（見ない）。
    /// TSF から読む経路（<see cref="TsfSiriusImeSource"/>）も、同じ判断を使う（トレイのメニューを開いただけでNICOLAが切れるのを防ぐ）。
    /// </summary>
    internal bool TryGetTarget(bool wantDetail, out IntPtr foreground, out uint threadId, out string cls, out string process, out string detail)
    {
        detail = "";
        cls = "";
        process = "";
        threadId = 0;
        foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) { if (wantDetail) detail = "前面のウィンドウがありません"; return false; }

        threadId = GetWindowThreadProcessId(foreground, out uint pid);
        cls = ClassOf(foreground);
        if (pid == _ownPid) { if (wantDetail) detail = $"前面はトレイ自身（{cls}）なので見ません"; return false; }
        process = ProcessNameCached(pid);
        if (IsShellWindow(cls, process))
        {
            if (wantDetail) detail = $"前面はシェルのウィンドウ（{cls}、{process}）なので見ません";
            return false;
        }
        return true;
    }

    /// <summary>前面のアプリのプロセス名（ログ用。IME 連動のスレッドから呼ぶ。プロセス名は前に調べたものを使う）。</summary>
    internal string ForegroundLabel()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return "";
        GetWindowThreadProcessId(foreground, out uint pid);
        return ProcessNameCached(pid);
    }

    private ImeState ReadCore(bool wantDetail, out string detail)
    {
        if (!TryGetTarget(wantDetail, out IntPtr foreground, out uint threadId, out string cls, out string process, out detail))
            return ImeState.Unknown;

        IntPtr target = foreground;
        var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        if (GetGUIThreadInfo(threadId, ref info) && info.hwndFocus != IntPtr.Zero) target = info.hwndFocus;

        IntPtr imeWindow = ImmGetDefaultIMEWnd(target);
        if (imeWindow == IntPtr.Zero) { if (wantDetail) detail = $"{process}（{cls}）: IMEのウィンドウがありません"; return ImeState.Unknown; }

        if (!Query(imeWindow, IMC_GETOPENSTATUS, out long open))
        {
            if (wantDetail) detail = $"{process}（{cls}）: IMEのON/OFFを問い合わせられません（管理者として動いている・応答しない等）";
            return ImeState.Unknown;
        }
        if (open == 0)
        {
            // IMEがOFFなら、変換モードは関係しない（かな入力にならない）ので聞かない。相手のアプリへのメッセージを半分にする。
            if (wantDetail) detail = $"{process}（{cls}）: IME OFF";
            return new ImeState(true, false, 0);
        }
        if (!Query(imeWindow, IMC_GETCONVERSIONMODE, out long mode))
        {
            if (wantDetail) detail = $"{process}（{cls}）: IMEの変換モードを問い合わせられません";
            return ImeState.Unknown;
        }

        var state = new ImeState(true, open != 0, (uint)mode);
        if (wantDetail)
            detail = $"{process}（{cls}）: IME {(state.Open ? "ON" : "OFF")}、変換モード 0x{state.ConversionMode:X}、かな入力 {(state.IsKanaInput ? "はい" : "いいえ")}";
        return state;
    }

    /// <summary>前面でもIMEの状態を見ないウィンドウか（クラス名、またはプロセス名で判断する）。</summary>
    internal static bool IsShellWindow(string windowClass, string processName)
    {
        if (IgnoredClasses.Contains(windowClass)) return true;
        if (IgnoredProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase)) return true;
        return string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase)
               && !ExplorerFileWindowClasses.Contains(windowClass);
    }

    /// <summary>プロセス名は、同じPIDなら前の結果を使う（Process.GetProcessByIdは重いので、100msごとには呼ばない）。30秒ごとに作り直す。</summary>
    private string ProcessNameCached(uint pid)
    {
        long now = Environment.TickCount64;
        if (now - _processNamesClearedAt > 30_000 || _processNames.Count > 64)
        {
            _processNames.Clear();
            _processNamesClearedAt = now;
        }
        if (!_processNames.TryGetValue(pid, out var name))
        {
            name = ProcessName(pid);
            _processNames[pid] = name;
        }
        return name;
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
