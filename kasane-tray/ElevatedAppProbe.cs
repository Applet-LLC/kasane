using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Kasane.Model;

namespace Kasane.Tray;

/// <summary>
/// 管理者権限で動いている他のアプリの情報を、トレイ（uiAccess）が取れるかの調査用（診断コマンド kasane-tray.exe --probe-elevated [回数 [間隔秒]]。2026-10-26）。
/// 管理者保護が有効だと、トレイは ADMIN_ユーザー名（SMAA）で動く。そのとき、昇格したアプリが前面にあっても、
/// 前面のアプリの規則・IME 連動・キーフックが従来どおり働くかを、実機で確かめるために使う。
/// 3秒待つ間に、調べたい昇格済みアプリ（管理者のコマンドプロンプトなど）を前面にする。以降は間隔ごとに前面のウィンドウを調べ、
/// %TEMP%\kasane-elevated-probe.txt に追記する。調べる項目:
///  - 相手の exe のパス（PROCESS_QUERY_LIMITED_INFORMATION。前面のアプリの規則が使う経路）、トークンの昇格・整合性レベル・ユーザー。
///  - ウィンドウのクラスとタイトル（InternalGetWindowText）。
///  - 前面のアプリの情報（<see cref="ForegroundInfo"/>）と IME の状態（<see cref="Win32ImeSource"/>）の、本番と同じ経路での結果。
///  - WM_NULL を送れるか（UIPI。uiAccess が効いていれば、昇格したウィンドウにも送れる）。
///  - 間隔の間にキーボードのフック（WH_KEYBOARD_LL）へ届いたキーの数（その間に、昇格済みアプリへ何かキーを打つ）。
/// 本体の動きには使っていない。
/// </summary>
internal static class ElevatedAppProbe
{
    public static void Run(string path, int count, int intervalSeconds)
    {
        var foreground = new ForegroundInfo();
        var ime = new Win32ImeSource();
        long keyCount = 0;
        HookProc proc = (code, wParam, lParam) =>
        {
            if (code >= 0 && ((int)wParam is WM_KEYDOWN or WM_SYSKEYDOWN)) Interlocked.Increment(ref keyCount);
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        };
        IntPtr hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
        int hookError = hook == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;

        var sb = new StringBuilder();
        sb.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} --probe-elevated ===");
        sb.AppendLine(AdminProtection.Describe(HelperConfig.BaseDirectory));
        sb.AppendLine(hook != IntPtr.Zero ? "キーボードフック: 張れた" : $"キーボードフック: 張れません(エラー {hookError})");
        File.AppendAllText(path, sb.ToString());

        Wait(3);
        Interlocked.Exchange(ref keyCount, 0);
        for (int i = 1; i <= count; i++)
        {
            string line = Sample(foreground, ime);
            Wait(intervalSeconds);
            long keys = Interlocked.Exchange(ref keyCount, 0);
            File.AppendAllText(path, $"[{i}/{count}] {DateTime.Now:HH:mm:ss}{Environment.NewLine}{line}  フックに届いたキー(次の{intervalSeconds}秒間): {keys}{Environment.NewLine}");
        }
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        GC.KeepAlive(proc);
    }

    // フックはこのスレッドのメッセージループで呼ばれるので、待つ間もメッセージを処理する。
    private static void Wait(int seconds)
    {
        var until = Stopwatch.StartNew();
        while (until.Elapsed.TotalSeconds < seconds)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
    }

    private static string Sample(ForegroundInfo foreground, Win32ImeSource ime)
    {
        var sb = new StringBuilder();
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return "  前面のウィンドウがありません" + Environment.NewLine;
        GetWindowThreadProcessId(hwnd, out uint pid);

        var cls = new StringBuilder(256);
        GetClassName(hwnd, cls, cls.Capacity);
        var title = new StringBuilder(512);
        InternalGetWindowText(hwnd, title, title.Capacity);
        sb.AppendLine($"  前面: pid={pid} class={cls} title=\"{title}\"");

        sb.AppendLine("  相手のプロセス: " + DescribeProcess(pid));

        var info = foreground.Read();
        sb.AppendLine(info is null
            ? "  ForegroundInfo.Read(): null(トレイ自身・シェルのウィンドウ、または取得できず)"
            : $"  ForegroundInfo.Read(): exe={(info.ExePath.Length > 0 ? info.ExePath : "(取得できず)")} class={info.ClassName}");

        var state = ime.ReadWithDetail(out string detail);
        sb.AppendLine($"  IME: Known={state.Known} Open={state.Open} ConversionMode=0x{state.ConversionMode:X}  {detail.Replace(Environment.NewLine, " / ")}");

        bool sent = SendMessageTimeout(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 300, out _) != IntPtr.Zero;
        sb.AppendLine(sent ? "  WM_NULL: 送れた" : $"  WM_NULL: 送れません(エラー {Marshal.GetLastWin32Error()}。5=UIPI で拒否の可能性)");
        return sb.ToString();
    }

    private static string DescribeProcess(uint pid)
    {
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return $"開けません(エラー {Marshal.GetLastWin32Error()})";
        try
        {
            var path = new StringBuilder(1024);
            uint size = (uint)path.Capacity;
            string exe = QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : "(パスを取得できません)";

            if (!OpenProcessToken(process, TOKEN_QUERY, out IntPtr token))
                return $"exe={exe}, トークン: 開けません(エラー {Marshal.GetLastWin32Error()})";
            try
            {
                string elevated = TokenUInt(token, TokenElevation) is uint e ? (e != 0 ? "昇格している" : "昇格していない") : "不明";
                string level = IntegrityLevelName(token);
                return $"exe={exe}, 昇格={elevated}, 整合性レベル={level}, ユーザー={TokenUserName(token)}";
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    private static uint? TokenUInt(IntPtr token, int infoClass) =>
        GetTokenInformation(token, infoClass, out uint value, sizeof(uint), out _) ? value : null;

    private static string IntegrityLevelName(IntPtr token)
    {
        GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out int length);
        if (length <= 0) return "不明";
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out _)) return "不明";
            IntPtr sid = Marshal.ReadIntPtr(buffer); // TOKEN_MANDATORY_LABEL.Label.Sid
            IntPtr countPtr = GetSidSubAuthorityCount(sid);
            byte count = Marshal.ReadByte(countPtr);
            uint rid = (uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            return rid switch
            {
                < 0x2000 => $"Low(0x{rid:X})",
                < 0x3000 => $"Medium(0x{rid:X})",
                < 0x4000 => $"High(0x{rid:X})",
                _ => $"System 以上(0x{rid:X})",
            };
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string TokenUserName(IntPtr token)
    {
        GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out int length);
        if (length <= 0) return "不明";
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, TokenUser, buffer, length, out _)) return "不明";
            var sid = new System.Security.Principal.SecurityIdentifier(Marshal.ReadIntPtr(buffer));
            try { return sid.Translate(typeof(System.Security.Principal.NTAccount)).Value; }
            catch (Exception ex) when (ex is System.Security.Principal.IdentityNotMappedException or SystemException) { return sid.Value; }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenUser = 1;
    private const int TokenElevation = 20;
    private const int TokenIntegrityLevel = 25;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const uint WM_NULL = 0;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int InternalGetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int infoClass, out uint info, int length, out int returnLength);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
}
