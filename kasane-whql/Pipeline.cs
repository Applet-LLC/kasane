namespace KasaneWhql;

internal enum DvlMode { Manual, Cli }

// 1 本ぶんの処理: DB 作成 → 解析 → SARIF を HLK へ → DVL 作成 → 検査して HLK へ。
internal sealed class Pipeline
{
    private readonly Config _c;
    private readonly RunLog _log;
    private readonly DvlMode _mode;

    public Pipeline(Config c, RunLog log, DvlMode mode) { _c = c; _log = log; _mode = mode; }

    private string Home(string name) => Path.Combine(_c.CodeQlHome, name);

    // 成功なら true。途中の失敗は理由をログに出して false。
    public bool Run(BuildTarget t, string hlkDir)
    {
        _log.Line($"\n---- {t.Label} ----");
        var pre = Checks.HlkFolder(hlkDir, t);
        if (!pre.Ok) { pre.Print(_log); return false; }

        var archDir = Path.Combine(hlkDir, t.HlkArchDir);
        Directory.CreateDirectory(archDir);

        // 1. 古い DB と SARIF を消す。消すのは、このツールが決めた名前（databases-*）の中身だけ。
        var db = Home(t.DbFolder);
        var sarif = Home(t.SarifName);
        if (!ClearDatabase(db)) return false;
        if (File.Exists(sarif)) File.Delete(sarif);

        // 2. DB 作成（bat でビルドしながら CodeQL が観察する）
        var bat = Path.Combine(_c.DriverDir, t.BuildBat);
        if (_log.Run($"\"{_c.CodeQlCmd}\" database create \"{db}\" --language=cpp --source-root=\"{_c.DriverDir}\" --command=\"{bat}\"", _c.DriverDir) != 0)
            return Fail("database create が失敗した");
        var dbCheck = Checks.Database(_c, t, db);
        dbCheck.Print(_log);
        if (!dbCheck.Ok) return Fail("DB の検査で止めた");

        // 3. 解析
        if (_log.Run($"\"{_c.CodeQlCmd}\" database analyze \"{db}\" {_c.QuerySuite} --format=sarifv2.1.0 --output=\"{sarif}\"", _c.CodeQlHome) != 0)
            return Fail("database analyze が失敗した");
        var sarifCheck = Checks.Sarif(sarif);
        sarifCheck.Print(_log);
        if (!sarifCheck.Ok) return Fail("SARIF の検査で止めた");

        // 4. SARIF を HLK 版フォルダへ
        var sarifDest = Path.Combine(archDir, t.SarifName);
        File.Copy(sarif, sarifDest, overwrite: true);
        _log.Line($"  SARIF -> {sarifDest}  SHA-256 {RunLog.Sha256(sarifDest)}");

        // 5. DVL
        var dvlDest = Path.Combine(archDir, $"{_c.DriverName}.DVL.XML");
        if (!MakeDvl(t, sarifDest, dvlDest)) return false;

        _log.Line($"  完了: {t.Label}");
        return true;
    }

    private bool MakeDvl(BuildTarget t, string sarif, string dvlDest)
    {
        var projDvl = Path.Combine(_c.DriverDir, $"{_c.DriverName}.DVL.XML");
        // プロジェクトのフォルダには、いまの 1 本の SARIF だけを置く（VS 拡張も dvl.exe も、ここを見る）。
        CleanStage(projDvl);
        var staged = Path.Combine(_c.DriverDir, t.SarifName);
        File.Copy(sarif, staged, overwrite: true);
        var started = DateTime.Now.AddSeconds(-1);
        try
        {
            if (_mode == DvlMode.Cli)
            {
                if (_log.Run($"\"{_c.DvlExe}\" /manualCreate {_c.DriverName} {t.Arch}", _c.DriverDir) != 0)
                    return Fail("dvl.exe が失敗した");
            }
            else
            {
                _log.Line($"  ▶ VS2022 で、{(t.Arch == "arm64" ? "kbdlayer arm64" : "kbdlayer")} プロジェクトを選び、");
                _log.Line("    Driver → Create Driver Verification Log を実行してください（SARIF が Detected になっているのを確認して Create）。");
                _log.Line("    クリップボードのエラーダイアログは × で閉じて構いません。終わったら Enter を押してください。");
                Console.ReadLine();
            }

            if (!File.Exists(projDvl) || File.GetLastWriteTime(projDvl) < started)
                return Fail($"新しい {_c.DriverName}.DVL.XML が見つからない（{projDvl}）");
            var check = Checks.Dvl(projDvl, _c.DriverName, t.Arch);
            check.Print(_log);
            if (!check.Ok) return Fail("DVL の検査で止めた");

            File.Copy(projDvl, dvlDest, overwrite: true);
            _log.Line($"  DVL -> {dvlDest}  SHA-256 {RunLog.Sha256(dvlDest)}");
            return true;
        }
        finally { CleanStage(projDvl); }
    }

    // プロジェクトのフォルダから、このツールが置いた/作った SARIF と DVL を片づける（どちらも .gitignore 済み）。
    private void CleanStage(string projDvl)
    {
        foreach (var s in BuildTarget.All.Select(x => x.SarifName).Distinct())
        {
            var p = Path.Combine(_c.DriverDir, s);
            if (File.Exists(p)) File.Delete(p);
        }
        if (File.Exists(projDvl)) File.Delete(projDvl);
    }

    private bool ClearDatabase(string db)
    {
        var full = Path.GetFullPath(db);
        var home = Path.GetFullPath(_c.CodeQlHome).TrimEnd('\\') + "\\";
        var known = BuildTarget.All.Any(x => Home(x.DbFolder).Equals(full, StringComparison.OrdinalIgnoreCase));
        if (!full.StartsWith(home, StringComparison.OrdinalIgnoreCase) || !known)
            return Fail($"消そうとしたフォルダが想定外のため止めた: {full}");
        if (Directory.Exists(full))
        {
            _log.Line($"  DB を空にする: {full}");
            foreach (var d in Directory.GetDirectories(full)) Directory.Delete(d, true);
            foreach (var f in Directory.GetFiles(full)) File.Delete(f);
        }
        return true;
    }

    private bool Fail(string why) { _log.Line($"  [失敗] {why}"); return false; }
}
