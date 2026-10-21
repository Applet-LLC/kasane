using System.IO;
using System.Text;
using Kasane.Model;
using Kasane.Model.Portable;

namespace Kasane.Cli;

/// <summary>
/// 画面を出さずに実行するコマンド（kasane.exe の引数）。インストーラー（MSI のカスタムアクション。SYSTEM で動く）や、復元の操作から呼ぶ。
///   --install-import &lt;ファイル&gt;                 設定ファイルの内容を、ドライバのレジストリへ書く（前の設定は .bak に1世代退避）。
///   --restore-import-backup [&lt;保存先名&gt;|--all]    退避（.bak）と今の設定を入れ替える（もう一度で元に戻る）。
///   --prepare-data-dir &lt;フォルダ&gt; [&lt;取り込むファイル&gt;]   保存先のフォルダを（無ければ）作り、Users に変更権限を付ける。取り込むファイルがあれば、そこへ kasane-import.json として置く（インストーラーが使う）。
///   --import-pending                           保存先（環境変数 KASANE のフォルダ）の kasane-import.json を、設定が無いものだけ取り込む（通常権限。トレイが起動時に呼ぶ）。
/// 結果は、ログ（%ProgramData%\applet\kasane\import.log）に追記する。終了コード: 0 = 成功、1 = 引数・ファイルの問題、2 = 一部または全部が失敗。
/// </summary>
public static class HeadlessCommands
{
    public const string InstallImport = "--install-import";
    public const string RestoreBackup = "--restore-import-backup";
    public const string ImportPending = "--import-pending";
    public const string PrepareDataDir = "--prepare-data-dir";

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "applet", "kasane", "import.log");

    /// <summary>引数がこのコマンドなら実行して true（終了コードを <paramref name="exitCode"/> に）。そうでなければ false。</summary>
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0) return false;

        if (args[0].Equals(InstallImport, StringComparison.OrdinalIgnoreCase))
        {
            exitCode = RunInstallImport(args.Length > 1 ? args[1] : null, PersistedSettings.OpenLocalMachine(), new FileLog(LogPath));
            return true;
        }
        if (args[0].Equals(RestoreBackup, StringComparison.OrdinalIgnoreCase))
        {
            string? target = args.Length > 1 ? args[1] : null;
            exitCode = RunRestore(target, PersistedSettings.OpenLocalMachine(), new FileLog(LogPath));
            return true;
        }
        if (args[0].Equals(PrepareDataDir, StringComparison.OrdinalIgnoreCase))
        {
            exitCode = RunPrepareDataDir(args.Length > 1 ? args[1] : null, args.Length > 2 ? args[2] : null, new FileLog(LogPath));
            return true;
        }
        if (args[0].Equals(ImportPending, StringComparison.OrdinalIgnoreCase))
        {
            exitCode = RunImportPending(new FileLog(Path.Combine(HelperConfig.BaseDirectory, "import.log")));
            return true;
        }
        return false;
    }

    /// <summary>
    /// 保存先のフォルダを用意する。無ければ作り、Users に変更権限（サブフォルダ・ファイルへ継承）を付ける
    /// （標準ユーザーのトレイ・設定画面が、設定を保存できるように。すでにあるフォルダの権限は触らない）。
    /// 取り込むファイルが指定されていれば、フォルダへ kasane-import.json として置く。
    /// 引数のフォルダは、末尾が「\」でも渡せるように、「&lt;フォルダ&gt;\.」の形でも受ける（コマンドラインの「\"」の解釈を避けるため）。
    /// </summary>
    public static int RunPrepareDataDir(string? dir, string? importFile, ILog log)
    {
        log.Write($"{PrepareDataDir} {dir} {importFile}");
        if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathFullyQualified(dir) || dir.StartsWith(@"\\"))
        {
            log.Write(Loc.T("エラー: ドライブ文字から始まるフルパスを指定してください", "Error: specify a full path that starts with a drive letter"));
            return 1;
        }

        try
        {
            string full = Path.GetFullPath(dir.Trim().Trim('"'));
            if (!Directory.Exists(full))
            {
                Directory.CreateDirectory(full);
                GrantUsersModify(full);
                log.Write(Loc.T($"フォルダを作り、Users に変更権限を付けました: {full}", $"Created the folder and granted Users modify permission: {full}"));
            }
            if (!string.IsNullOrWhiteSpace(importFile))
            {
                string src = importFile.Trim().Trim('"');
                if (File.Exists(src))
                {
                    File.Copy(src, Path.Combine(full, PendingImport.FileName), overwrite: true);
                    log.Write(Loc.T($"取り込みファイルを置きました: {Path.Combine(full, PendingImport.FileName)}", $"Placed the import file: {Path.Combine(full, PendingImport.FileName)}"));
                }
                else log.Write(Loc.T($"エラー: 取り込みファイルがありません: {src}", $"Error: import file not found: {src}"));
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            log.Write(Loc.T($"エラー: {ex.Message}", $"Error: {ex.Message}"));
            return 2;
        }
    }

    private static void GrantUsersModify(string folder)
    {
        var info = new DirectoryInfo(folder);
        var acl = info.GetAccessControl();
        var users = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null);
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            users,
            System.Security.AccessControl.FileSystemRights.Modify,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None,
            System.Security.AccessControl.AccessControlType.Allow));
        info.SetAccessControl(acl);
    }

    public static int RunImportPending(ILog log)
    {
        try
        {
            var report = new PortableReport();
            var result = PendingImport.RunWithDriver(null, report);
            if (!result.FileFound) return 0;
            foreach (var l in report.Lines)
                log.Write((l.Level switch { ReportLevel.Error => Loc.T("エラー: ", "Error: "), ReportLevel.Warning => Loc.T("注意: ", "Note: "), _ => "" }) + l.Text);
            return report.HasErrors ? 2 : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write(Loc.T($"エラー: {ex.Message}", $"Error: {ex.Message}"));
            return 2;
        }
    }

    public static int RunInstallImport(string? path, PersistedSettings store, ILog log)
    {
        log.Write($"{InstallImport} {path}");
        if (string.IsNullOrWhiteSpace(path)) { log.Write(Loc.T("エラー: 設定ファイルが指定されていません", "Error: no settings file was specified")); return 1; }
        if (!File.Exists(path)) { log.Write(Loc.T($"エラー: ファイルがありません: {path}", $"Error: file not found: {path}")); return 1; }
        if (!PortableIO.TryRead(path, out var file, out string? error))
        {
            log.Write(Loc.T($"エラー: {error}", $"Error: {error}"));
            return 1;
        }

        var report = new PortableReport();
        var result = DriverImport.Apply(file!, store, report);
        foreach (var l in report.Lines)
            log.Write((l.Level switch { ReportLevel.Error => Loc.T("エラー: ", "Error: "), ReportLevel.Warning => Loc.T("注意: ", "Note: "), _ => "" }) + l.Text);
        log.Write(Loc.T($"結果: 書き込み {result.Written}(退避 {result.BackedUp})、飛ばした {result.Skipped}", $"Result: written {result.Written} (backed up {result.BackedUp}), skipped {result.Skipped}"));
        return result.Skipped > 0 || report.HasErrors ? 2 : 0;
    }

    public static int RunRestore(string? target, PersistedSettings store, ILog log)
    {
        log.Write($"{RestoreBackup} {target}");
        var names = string.IsNullOrEmpty(target) || target.Equals("--all", StringComparison.OrdinalIgnoreCase)
            ? store.ListBackups()
            : new List<string> { target };
        if (names.Count == 0) { log.Write(Loc.T("復元できる退避がありません", "There are no backups to restore")); return 1; }

        int failed = 0;
        foreach (var n in names)
        {
            try
            {
                if (store.Restore(n)) log.Write(Loc.T($"{n}: 退避と入れ替えました(キーボードの接続し直しか再起動で反映されます)", $"{n}: swapped with the backup (takes effect after reconnecting the keyboard or restarting)"));
                else { log.Write(Loc.T($"エラー: {n}: 退避がありません", $"Error: {n}: no backup")); failed++; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                log.Write(Loc.T($"エラー: {n}: {ex.Message}", $"Error: {n}: {ex.Message}"));
                failed++;
            }
        }
        return failed > 0 ? 2 : 0;
    }

    public interface ILog
    {
        void Write(string line);
    }

    /// <summary>ファイルへ追記するログ（書けなくても処理は続ける）。</summary>
    public sealed class FileLog : ILog
    {
        private readonly string _path;
        public FileLog(string path) { _path = path; }

        public void Write(string line)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                if (File.Exists(_path) && new FileInfo(_path).Length > 200_000) File.Delete(_path);
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}", new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 記録できなくても続ける */ }
        }
    }
}
