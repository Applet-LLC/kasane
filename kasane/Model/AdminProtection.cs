using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace Kasane.Model;

/// <summary>
/// Windows 11 の「管理者保護」（Administrator protection）の検出と、いま動いているプロセスの権限の診断。
/// 管理者保護が有効だと、昇格したプロセスやトレイ（uiAccess）は、ログオンユーザーとは別の管理者アカウント（ADMIN_ユーザー名）で動く。
/// 保存先に書けないなどの問題の原因をログから切り分けるために、診断の文字列を作る（docs/ADMIN-PROTECTION.md）。
/// </summary>
public static class AdminProtection
{
    private const string PolicyKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string PolicyValue = "TypeOfAdminApprovalMode";

    /// <summary>TypeOfAdminApprovalMode の値。2 が「管理者保護を使用した管理者承認モード」。</summary>
    public const int ModeAdminProtection = 2;

    /// <summary>管理者保護が有効か。値が読めない・無いときは無効として扱う。</summary>
    public static bool IsEnabled() => IsEnabled(ReadMode());

    public static bool IsEnabled(int? mode) => mode == ModeAdminProtection;

    public static int? ReadMode()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyKey);
            return key?.GetValue(PolicyValue) is int v ? v : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// 診断の1行。実行ユーザー、Administrators が有効か（フィルタ済みトークンでは deny-only で無効）、uiAccess の有無、管理者保護の有無、
    /// 保存先とそこへの書き込み試験の結果。
    /// </summary>
    public static string Describe(string dataDirectory)
    {
        string user, admin;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            user = $"{identity.Name} ({identity.User?.Value})";
            admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ? "有効" : "無効(deny-only または非所属)";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            user = "取得できません: " + ex.Message;
            admin = "不明";
        }

        string mode = ReadMode() is int m ? m.ToString() : "未設定";
        return $"環境: user={user}, Administrators={admin}, uiAccess={(HasUiAccess() ? "あり" : "なし")}, " +
               $"管理者保護={(IsEnabled() ? "有効" : "無効")}(TypeOfAdminApprovalMode={mode}), " +
               $"KASANE={Environment.GetEnvironmentVariable(HelperConfig.DataDirEnvironmentVariable) ?? "(未設定)"}, " +
               $"保存先={dataDirectory}, 書き込み={ProbeWrite(dataDirectory)}";
    }

    private const string Writable = "できる";

    /// <summary>フォルダに一時ファイルを作って消せるか。</summary>
    public static bool CanWrite(string directory) => ProbeWrite(directory) == Writable;

    /// <summary>フォルダに一時ファイルを作って消せるか。成功なら「できる」、失敗なら例外の種類とメッセージ。</summary>
    public static string ProbeWrite(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $".write-probe-{Environment.ProcessId}.tmp");
            File.WriteAllText(path, "probe");
            File.Delete(path);
            return Writable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"できない({ex.GetType().Name}: {ex.Message})";
        }
    }

    private const uint TokenQuery = 0x0008;
    private const int TokenUIAccess = 26;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, out uint tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int TokenUser = 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    /// <summary>
    /// このセッションのエクスプローラー（シェル）を動かしているユーザーの SID。見つからない・読めないときは null。
    /// 管理者保護では、トレイや昇格したプロセスは ADMIN_ユーザー名 で動くが、タスクバーなどのシェルは元のユーザーのもの。
    /// 元のユーザーの HKCU（HKEY_USERS\&lt;この SID&gt;）への書き込みなど、「いまログオンしている人」を指したいときに使う。
    /// </summary>
    public static SecurityIdentifier? GetShellUserSid()
    {
        int session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("explorer"))
        {
            using (process)
            {
                if (process.SessionId != session) continue;
                var sid = TokenUserSid((uint)process.Id);
                if (sid != null) return sid;
            }
        }
        return null;
    }

    /// <summary>
    /// いまログオンしている（エクスプローラーが動いている）ユーザーの一覧。SYSTEM のインストーラーから、自動起動のタスクを作る相手を知るために使う。
    /// セッション 0 は除く。同じユーザーが複数のセッションにいても、1人として数える。
    /// </summary>
    public static List<(SecurityIdentifier Sid, string Account)> GetInteractiveUsers()
    {
        var result = new List<(SecurityIdentifier, string)>();
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("explorer"))
        {
            using (process)
            {
                if (process.SessionId <= 0) continue;
                var sid = TokenUserSid((uint)process.Id);
                if (sid == null || result.Any(r => r.Item1.Equals(sid))) continue;
                string account;
                try { account = sid.Translate(typeof(NTAccount)).Value; }
                catch (IdentityNotMappedException) { continue; }
                result.Add((sid, account));
            }
        }
        return result;
    }

    private static SecurityIdentifier? TokenUserSid(uint pid)
    {
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out IntPtr token)) return null;
            try
            {
                GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out int length);
                if (length <= 0) return null;
                IntPtr buffer = Marshal.AllocHGlobal(length);
                try
                {
                    return GetTokenInformation(token, TokenUser, buffer, length, out _) ? new SecurityIdentifier(Marshal.ReadIntPtr(buffer)) : null;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    /// <summary>このプロセスのトークンに uiAccess が付いているか（付かないときはエラーにならず、UIPI を越えられないだけで動く）。</summary>
    public static bool HasUiAccess()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out IntPtr token)) return false;
        try
        {
            return GetTokenInformation(token, TokenUIAccess, out uint value, sizeof(uint), out _) && value != 0;
        }
        finally { CloseHandle(token); }
    }
}
