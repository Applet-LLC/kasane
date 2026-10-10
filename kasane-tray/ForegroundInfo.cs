using System.Runtime.InteropServices;
using System.Text;
using Kasane.Model;

namespace Kasane.Tray;

/// <summary>
/// 前面のウィンドウの情報を取る（前面のアプリの規則に使う、2026-10-08）。
///  - exe のフルパス: OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) → QueryFullProcessImageName。管理者として動いている
///    アプリでも取れる。同じ PID なら前の結果を使う。
///  - トップレベルのウィンドウのクラスとタイトル。
///  - のどかの window の照合に使う文字列: "exeのフルパス:トップレベルのクラス:…:フォーカスのあるウィンドウのクラス"
///    （タイトルも同じ形）。フォーカスのあるウィンドウ（GetGUIThreadInfo）から親をたどって作る（のどかの hook.cpp と同じ）。
/// 次のときは null（呼び出し側は前の決定を保つ）: 前面のウィンドウが無い、トレイ自身、タスクバー・メニューなどのシェルのウィンドウ
/// （トレイのメニューを開いただけで、規則が外れるのを防ぐ。IME 連動と同じ判定）。
/// </summary>
public sealed class ForegroundInfo
{
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private uint _cachedPid;
    private string _cachedPath = "";

    public ForegroundWindowInfo? Read()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return null;
        uint threadId = GetWindowThreadProcessId(foreground, out uint pid);
        if (pid == 0 || pid == _ownPid) return null;

        string exePath = ExePathCached(pid);
        string topClass = ClassOf(foreground);
        if (Win32ImeSource.IsShellWindow(topClass, Path.GetFileNameWithoutExtension(exePath))) return null;
        string topTitle = TitleOf(foreground);

        // フォーカスのあるウィンドウから、トップレベルまでの鎖。
        IntPtr focus = foreground;
        var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        if (GetGUIThreadInfo(threadId, ref info) && info.hwndFocus != IntPtr.Zero) focus = info.hwndFocus;

        var classes = new List<string>();
        var titles = new List<string>();
        for (IntPtr h = focus; h != IntPtr.Zero && classes.Count < 32; h = GetParent(h))
        {
            classes.Add(ClassOf(h));
            titles.Add(TitleOf(h));
            if (h == foreground) break;
        }
        if (classes.Count == 0 || classes[^1] != topClass)
        {
            // フォーカスが前面のウィンドウの子でない（別のトップレベル）ときは、前面のウィンドウだけにする。
            classes = new List<string> { topClass };
            titles = new List<string> { topTitle };
        }
        classes.Reverse();
        titles.Reverse();

        return new ForegroundWindowInfo(
            exePath,
            topClass,
            topTitle,
            exePath + ":" + string.Join(":", classes),
            exePath + ":" + string.Join(":", titles));
    }

    private string ExePathCached(uint pid)
    {
        if (pid == _cachedPid && _cachedPath.Length > 0) return _cachedPath;
        _cachedPid = pid;
        _cachedPath = ExePath(pid);
        return _cachedPath;
    }

    private static string ExePath(uint pid)
    {
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageName(process, 0, sb, ref size) ? sb.ToString() : "";
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    // GetWindowText は、応答しないアプリのウィンドウで止まることがあるので、InternalGetWindowText を使う
    // （ウィンドウにメッセージを送らずに、持っているタイトルを読む）。
    private static string TitleOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        return InternalGetWindowText(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public uint cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int InternalGetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
