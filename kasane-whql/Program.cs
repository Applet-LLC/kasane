using KasaneWhql;

// kasane-whql: WHQL 申請用の CodeQL 解析と DVL 作成を、4 本（Subsc/Pro × x64/ARM64）で同じ手順で実行する。

const string Usage = """
    使い方:
      kasane-whql check  [--edition subsc|pro] [--arch x64|arm64] [--dvl manual|cli]
      kasane-whql run    --hlk-subsc <HLK 版フォルダ> --hlk-pro <HLK 版フォルダ> [--edition ..] [--arch ..] [--dvl manual|cli]
      kasane-whql verify --hlk-subsc <HLK 版フォルダ> --hlk-pro <HLK 版フォルダ> [--edition ..] [--arch ..]

    --hlk-subsc / --hlk-pro は、指定した版だけが対象になる（片方だけなら、その版の 2 本）。
    --dvl manual（既定）: DVL は VS2022 の拡張で作る。SARIF の配置と DVL の検査・回収は自動。
    --dvl cli          : dvl.exe で作る（Code Analysis の結果が入らず、CodeAnalysis.NORUN になる点に注意）。
    """;

Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Length == 0 || args[0] is "-h" or "--help") { Console.WriteLine(Usage); return 0; }

var cmd = args[0];
var opt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var i = 1; i < args.Length; i++)
{
    if (!args[i].StartsWith("--") || i + 1 >= args.Length) { Console.Error.WriteLine($"引数が不正: {args[i]}\n{Usage}"); return 2; }
    opt[args[i][2..]] = args[++i];
}

var cfg = Config.Load();
var hlk = new Dictionary<string, string>();
if (opt.TryGetValue("hlk-subsc", out var hs)) hlk["subsc"] = hs;
if (opt.TryGetValue("hlk-pro", out var hp)) hlk["pro"] = hp;

// 対象を絞る。--edition 省略時は、HLK フォルダを指定した版（check では両方）。
opt.TryGetValue("edition", out var ed);
opt.TryGetValue("arch", out var ar);
var targets = BuildTarget.All
    .Where(t => ed == null ? (cmd == "check" || hlk.ContainsKey(t.Edition)) : t.Edition.Equals(ed, StringComparison.OrdinalIgnoreCase))
    .Where(t => ar == null || t.Arch.Equals(ar, StringComparison.OrdinalIgnoreCase))
    .ToList();
if (targets.Count == 0) { Console.Error.WriteLine("対象が 0 本。--edition/--arch/--hlk-* を確認してください。"); return 2; }

var mode = opt.TryGetValue("dvl", out var m) && m.Equals("cli", StringComparison.OrdinalIgnoreCase) ? DvlMode.Cli : DvlMode.Manual;
if (opt.TryGetValue("dvl", out var m2) && !m2.Equals("cli", StringComparison.OrdinalIgnoreCase) && !m2.Equals("manual", StringComparison.OrdinalIgnoreCase))
{ Console.Error.WriteLine("--dvl は manual か cli"); return 2; }

if (cmd != "check")
{
    foreach (var t in targets)
        if (!hlk.ContainsKey(t.Edition)) { Console.Error.WriteLine($"{t.Label}: --hlk-{t.Edition} が必要"); return 2; }
}

switch (cmd)
{
    case "check": return Check();
    case "run": return Run();
    case "verify": return Verify();
    default: Console.Error.WriteLine(Usage); return 2;
}

int Check()
{
    using var log = new RunLog(Path.Combine(Path.GetTempPath(), "kasane-whql-check.log"));
    log.Line($"対象: {string.Join(", ", targets.Select(t => t.Label))}");
    var r = Checks.Preflight(cfg, targets, needDvlExe: mode == DvlMode.Cli);
    r.Print(log, "");
    log.Line(r.Ok ? "前提の検査: OK" : "前提の検査: NG");
    return r.Ok ? 0 : 1;
}

int Run()
{
    var pre = Checks.Preflight(cfg, targets, needDvlExe: mode == DvlMode.Cli);
    if (!pre.Ok) { foreach (var e in pre.Errors) Console.Error.WriteLine($"[NG] {e}"); return 1; }

    // フォルダ名と版の食い違いは、1 本も動かす前にまとめて確かめる。
    var bad = false;
    foreach (var t in targets)
    {
        var r = Checks.HlkFolder(hlk[t.Edition], t);
        foreach (var e in r.Errors) { Console.Error.WriteLine($"[NG] {t.Label}: {e}"); bad = true; }
    }
    if (bad) return 1;

    var results = new List<(BuildTarget t, bool ok)>();
    foreach (var group in targets.GroupBy(t => t.Edition))
    {
        using var log = new RunLog(Path.Combine(hlk[group.Key], "whql-run.log"));
        var pipe = new Pipeline(cfg, log, mode);
        foreach (var t in group)
        {
            bool ok;
            try { ok = pipe.Run(t, hlk[group.Key]); }
            catch (Exception e) { log.Line($"  [失敗] 例外: {e.Message}"); ok = false; }
            results.Add((t, ok));
            if (!ok) log.Line($"  {t.Label} は失敗。残りの本は続けます。");
        }
    }

    Console.WriteLine("\n==== 結果 ====");
    foreach (var (t, ok) in results) Console.WriteLine($"  {(ok ? "OK" : "NG")}  {t.Label}");
    return results.All(r => r.ok) ? 0 : 1;
}

int Verify()
{
    var allOk = true;
    foreach (var t in targets)
    {
        var dir = hlk[t.Edition];
        var archDir = Path.Combine(dir, t.HlkArchDir);
        var r = Checks.HlkFolder(dir, t);
        if (r.Ok)
        {
            foreach (var sub in new[] { "driver", "Symbol" })
                if (!Directory.Exists(Path.Combine(archDir, sub)) || !Directory.EnumerateFileSystemEntries(Path.Combine(archDir, sub)).Any())
                    r.Errors.Add($"{sub}\\ が無いか空");
            var s = Checks.Sarif(Path.Combine(archDir, t.SarifName));
            r.Errors.AddRange(s.Errors); r.Warnings.AddRange(s.Warnings);
            var d = Checks.Dvl(Path.Combine(archDir, $"{cfg.DriverName}.DVL.XML"), cfg.DriverName, t.Arch);
            r.Errors.AddRange(d.Errors); r.Warnings.AddRange(d.Warnings);
            // 解析に使った DB が残っていれば、取り違えがないか見る（残っていなくても失敗にはしない）。
            var db = Path.Combine(cfg.CodeQlHome, t.DbFolder);
            if (Directory.Exists(db))
            {
                var dbr = Checks.Database(cfg, t, db);
                // 申請後は別のドライバの解析で DB が使い回されるので、ここでは警告にとどめる（run の途中ではエラー）。
                r.Warnings.AddRange(dbr.Errors.Select(e => "DB（作業後に使い回されたなら問題なし）: " + e)); r.Warnings.AddRange(dbr.Warnings);
            }
        }
        Console.WriteLine($"{(r.Ok ? "OK" : "NG")}  {t.Label}  ({archDir})");
        foreach (var e in r.Errors) Console.WriteLine($"    [NG] {e}");
        foreach (var w in r.Warnings) Console.WriteLine($"    [注意] {w}");
        allOk &= r.Ok;
    }
    return allOk ? 0 : 1;
}
