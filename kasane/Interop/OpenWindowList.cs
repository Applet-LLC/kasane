using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Kasane.Interop;

/// <summary>開いているウィンドウ1つ分（前面のアプリの規則を作るときに選ぶ）。</summary>
public sealed record OpenWindow(string ExePath, string ClassName, string Title)
{
    public string ExeName => Path.GetFileName(ExePath);
}

/// <summary>今開いているトップレベルのウィンドウの一覧（2026-10-08、P4。「実行中のアプリから選ぶ」用）。</summary>
public static class OpenWindowList
{
    private const int GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    /// <summary>見えていて、タイトルがあり、持ち主のいないウィンドウを、exe 名の順に返す（同じ exe とクラスの重複は1件）。</summary>
    public static List<OpenWindow> Enumerate()
    {
        var result = new List<OpenWindow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
                if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;

                var title = new StringBuilder(512);
                GetWindowText(hwnd, title, title.Capacity);
                if (title.Length == 0) return true;

                var cls = new StringBuilder(256);
                GetClassName(hwnd, cls, cls.Capacity);

                GetWindowThreadProcessId(hwnd, out uint pid);
                string exe = ExePathOf(pid);
                if (exe.Length == 0) return true;

                if (seen.Add(exe + "|" + cls)) result.Add(new OpenWindow(exe, cls.ToString(), title.ToString()));
            }
            catch (Exception ex) when (ex is ExternalException or ArgumentException) { /* 1件の失敗で、一覧全体を止めない */ }
            return true;
        }, IntPtr.Zero);
        return result.OrderBy(w => w.ExeName, StringComparer.OrdinalIgnoreCase).ThenBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // 昇格したプロセスでも、QUERY_LIMITED_INFORMATION なら開ける。開けなければ空。
    private static string ExePathOf(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : "";
        }
        finally { CloseHandle(h); }
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
