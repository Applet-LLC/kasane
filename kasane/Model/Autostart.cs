using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Kasane.Model;

/// <summary>
/// ログオン時のトレイの自動起動を、管理者保護の有無に合わせて切り替える（インストーラーから kasane.exe --setup-autostart で呼ぶ。docs/ADMIN-PROTECTION.md）。
///  - 管理者保護が無効: HKLM の Run キー（MSI が登録）だけを使う。タスクは作らず、あれば消す。
///  - 管理者保護が有効: Run キー経由の起動は、昇格しないトークンで UAC の確認が出る。そこで、ログオンしているユーザーごとに、タスクスケジューラのタスク
///    （ログオン時、最上位の特権）を作り、Run キーはタスクマネージャーの「スタートアップ」と同じ StartupApproved の値で無効にする（Run キーそのものは
///    MSI のコンポーネントの値なので、消すと修復で戻る）。タスクは昇格したトークンで起動し、uiAccess も付く（実機で確認済み）。
/// タスクの形は、のどか（nodoka64.exe）で動いている設定と同じ（powershell.exe 経由の Start-Process）。
/// </summary>
public static class Autostart
{
    public const string TaskPrefix = "kasane-tray";
    public const string RunValueName = "kasane-tray";
    private const string ApprovedKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>StartupApproved\Run の値。先頭の 1 バイトが、02 = 有効、03 = 無効（タスクマネージャーと同じ）。</summary>
    public static readonly byte[] ApprovedEnabled = { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    public static readonly byte[] ApprovedDisabled = { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    /// <summary>ユーザーごとのタスク名。「T480S\applet」→「kasane-tray-applet」。</summary>
    public static string TaskNameFor(string account)
    {
        string user = account.Contains('\\') ? account[(account.LastIndexOf('\\') + 1)..] : account;
        var sb = new StringBuilder(TaskPrefix).Append('-');
        foreach (char c in user) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        return sb.ToString();
    }

    /// <summary>
    /// タスクの XML。そのユーザーがログオンしたときに、最上位の特権（HighestAvailable）で powershell.exe 経由でトレイを起動する。
    /// 直に起動するのではなく powershell.exe の Start-Process を挟むのは、のどかで確認した形に合わせるため（直起動だと失敗することがある）。
    /// </summary>
    public static string BuildTaskXml(string trayExePath, string userSid, string account)
    {
        string path = SecurityElement.Escape(trayExePath.Replace("'", "''"))!;
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>kasane</Author>
              </RegistrationInfo>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(userSid)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <AllowHardTerminate>false</AllowHardTerminate>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
              </Settings>
              <Triggers>
                <LogonTrigger>
                  <UserId>{SecurityElement.Escape(account)}</UserId>
                </LogonTrigger>
              </Triggers>
              <Actions Context="Author">
                <Exec>
                  <Command>powershell.exe</Command>
                  <Arguments>-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command "Start-Process -FilePath '{path}'"</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>
    /// 自動起動を整える。<paramref name="adminProtection"/> が true なら、いまログオンしている各ユーザーにタスクを作り、Run キーを無効にする
    /// （タスクを1つも作れなかったときは、Run キーを有効のままにする）。false ならタスクを消し、Run キーを有効にする。
    /// </summary>
    public static int Apply(string trayExePath, bool adminProtection, IReadOnlyList<(SecurityIdentifier Sid, string Account)> users, Action<string> log)
    {
        if (!adminProtection)
        {
            DeleteTasks(log);
            SetRunApproved(enabled: true);
            log(Loc.T("管理者保護が無効なので、Run キーで自動起動します", "Administrator protection is off, so the Run key starts the tray"));
            return 0;
        }

        int created = 0;
        foreach (var (sid, account) in users)
        {
            string name = TaskNameFor(account);
            if (CreateTask(name, BuildTaskXml(trayExePath, sid.Value, account), log)) created++;
        }

        if (created == 0)
        {
            SetRunApproved(enabled: true);
            log(Loc.T("タスクを作れなかった（ログオン中のユーザーが見つからない）ので、Run キーを有効のままにします",
                      "No task was created (no signed-in user found), so the Run key stays enabled"));
            return users.Count == 0 ? 0 : 2;
        }
        SetRunApproved(enabled: false);
        log(Loc.T($"タスクを {created} 件作り、Run キーを無効にしました", $"Created {created} task(s) and disabled the Run key"));
        return 0;
    }

    /// <summary>アンインストール用。タスクを消し、StartupApproved の値も消す。</summary>
    public static void Remove(Action<string> log)
    {
        DeleteTasks(log);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ApprovedKey, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            log(Loc.T($"エラー: {ex.Message}", $"Error: {ex.Message}"));
        }
    }

    private static void SetRunApproved(bool enabled)
    {
        using var key = Registry.LocalMachine.CreateSubKey(ApprovedKey);
        key.SetValue(RunValueName, enabled ? ApprovedEnabled : ApprovedDisabled, RegistryValueKind.Binary);
    }

    private static bool CreateTask(string name, string xml, Action<string> log)
    {
        string file = Path.Combine(Path.GetTempPath(), $"kasane-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode); // schtasks は UTF-16 の XML を読む
            int code = RunSchtasks($"/create /tn \"{name}\" /xml \"{file}\" /f", out string output);
            log(code == 0
                ? Loc.T($"タスクを作りました: {name}", $"Created the task: {name}")
                : Loc.T($"エラー: タスク {name} を作れませんでした(終了コード {code}): {output.Trim()}", $"Error: could not create the task {name} (exit code {code}): {output.Trim()}"));
            return code == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            log(Loc.T($"エラー: {ex.Message}", $"Error: {ex.Message}"));
            return false;
        }
        finally
        {
            try { File.Delete(file); } catch (Exception) { }
        }
    }

    private static void DeleteTasks(Action<string> log)
    {
        try
        {
            if (RunSchtasks("/query /fo csv /nh", out string list) != 0) return;
            foreach (string line in list.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string first = line.Split(',')[0].Trim().Trim('"');
                if (!first.StartsWith("\\" + TaskPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                string name = first.TrimStart('\\');
                int code = RunSchtasks($"/delete /tn \"{name}\" /f", out _);
                log(code == 0 ? Loc.T($"タスクを消しました: {name}", $"Deleted the task: {name}") : Loc.T($"エラー: タスク {name} を消せませんでした", $"Error: could not delete the task {name}"));
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            log(Loc.T($"エラー: {ex.Message}", $"Error: {ex.Message}"));
        }
    }

    private static int RunSchtasks(string arguments, out string output)
    {
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        output = stdout + stderr;
        return process.ExitCode;
    }
}
