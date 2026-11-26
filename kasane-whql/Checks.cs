using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;

namespace KasaneWhql;

// 取り違えと失敗を、その場で止めるための検査。エラーは「止めるべきもの」、警告は「見て判断するもの」。
internal sealed class CheckResult
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool Ok => Errors.Count == 0;

    public void Print(RunLog log, string prefix = "  ")
    {
        foreach (var e in Errors) log.Line($"{prefix}[NG] {e}");
        foreach (var w in Warnings) log.Line($"{prefix}[注意] {w}");
    }
}

internal static class Checks
{
    // 外部コマンドの標準出力を文字列で受け取る（バージョン確認用）。
    private static string Capture(string file, string args)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/s /c \"\"{file}\" {args}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return o.Trim();
    }

    // 前提の検査。1 本も動かす前に、足りないものを全部まとめて出す。
    public static CheckResult Preflight(Config c, IEnumerable<BuildTarget> targets, bool needDvlExe)
    {
        var r = new CheckResult();
        if (!File.Exists(c.CodeQlCmd)) r.Errors.Add($"codeql.cmd が無い: {c.CodeQlCmd}");
        else
        {
            var v = Capture(c.CodeQlCmd, "version --format=terse");
            if (!v.Contains(c.CodeQlVersion)) r.Errors.Add($"CodeQL の版が違う（期待 {c.CodeQlVersion} / 実際 {v}）");
        }
        if (!Directory.Exists(c.CodeQlHome)) r.Errors.Add($"CodeQL のフォルダが無い: {c.CodeQlHome}");
        if (!File.Exists(c.BuildEnvCmd)) r.Errors.Add($"ビルド環境の cmd が無い（bat が呼ぶ）: {c.BuildEnvCmd}");
        if (needDvlExe && !File.Exists(c.DvlExe)) r.Errors.Add($"dvl.exe が無い: {c.DvlExe}");
        if (!Directory.Exists(c.DriverDir)) r.Errors.Add($"ドライバのフォルダが無い: {c.DriverDir}");
        else
            foreach (var t in targets)
                if (!File.Exists(Path.Combine(c.DriverDir, t.BuildBat)))
                    r.Errors.Add($"ビルド用の bat が無い: {t.BuildBat}");
        return r;
    }

    // HLK 版フォルダの名前と版の食い違い（Pro の成果物を Subsc のフォルダに入れる事故）を止める。
    public static CheckResult HlkFolder(string hlkDir, BuildTarget t)
    {
        var r = new CheckResult();
        if (!Directory.Exists(hlkDir)) { r.Errors.Add($"HLK 版フォルダが無い: {hlkDir}"); return r; }
        var name = Path.GetFileName(hlkDir.TrimEnd('\\', '/'));
        var hasPro = name.Contains("Pro", StringComparison.OrdinalIgnoreCase);
        var hasSubsc = name.Contains("Subsc", StringComparison.OrdinalIgnoreCase);
        if (hasPro == hasSubsc) r.Errors.Add($"フォルダ名から版が決められない（Pro か Subsc を含めること）: {name}");
        else if (t.IsPro != hasPro) r.Errors.Add($"版が合わない: 対象は {t.Label} だが、フォルダ名は {name}");
        return r;
    }

    // DB の取り違え検査: ソースの場所、Pro/Subsc の違い（token.c の有無）。
    public static CheckResult Database(Config c, BuildTarget t, string dbDir)
    {
        var r = new CheckResult();
        var yml = Path.Combine(dbDir, "codeql-database.yml");
        if (!File.Exists(yml)) { r.Errors.Add($"codeql-database.yml が無い（DB が作れていない）: {dbDir}"); return r; }

        var prefix = File.ReadAllLines(yml).FirstOrDefault(l => l.TrimStart().StartsWith("sourceLocationPrefix:"));
        var want = Path.GetFullPath(c.DriverDir).TrimEnd('\\');
        if (prefix == null || !prefix.Replace("\\\\", "\\").Contains(want, StringComparison.OrdinalIgnoreCase))
            r.Errors.Add($"DB のソースの場所が違う（期待 {want} / yml: {prefix?.Trim() ?? "なし"}）");

        var zip = Path.Combine(dbDir, "src.zip");
        if (!File.Exists(zip)) { r.Errors.Add("src.zip が無い"); return r; }
        using var z = ZipFile.OpenRead(zip);
        var hasToken = z.Entries.Any(e => e.FullName.EndsWith("/token.c", StringComparison.OrdinalIgnoreCase));
        if (t.IsPro && hasToken) r.Errors.Add("Pro なのに token.c が DB に入っている（Subsc のビルドが混ざった可能性）");
        if (!t.IsPro && !hasToken) r.Errors.Add("Subsc なのに token.c が DB に無い（Pro のビルドが混ざった可能性）");
        if (z.Entries.Count == 0) r.Errors.Add("src.zip が空");
        return r;
    }

    public static CheckResult Sarif(string path)
    {
        var r = new CheckResult();
        if (!File.Exists(path)) { r.Errors.Add($"SARIF が無い: {path}"); return r; }
        var len = new FileInfo(path).Length;
        if (len < 1024) { r.Errors.Add($"SARIF が小さすぎる（{len} バイト）"); return r; }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var run = doc.RootElement.GetProperty("runs")[0];
            var tool = run.GetProperty("tool").GetProperty("driver").GetProperty("name").GetString() ?? "";
            if (!tool.Contains("CodeQL", StringComparison.OrdinalIgnoreCase)) r.Errors.Add($"CodeQL の SARIF ではない（tool: {tool}）");
            var n = run.TryGetProperty("results", out var res) ? res.GetArrayLength() : 0;
            if (n == 0) r.Warnings.Add("SARIF の結果が 0 件（欠陥なしなら正常。想定外なら、解析が動いていない）");
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            r.Errors.Add($"SARIF を読めない: {e.Message}");
        }
        return r;
    }

    // DVL の検査。項目名の CPU が対象と合うか、Semmle の項目とチェックサムがあるか。
    public static CheckResult Dvl(string path, string driverName, string arch)
    {
        var r = new CheckResult();
        if (!File.Exists(path)) { r.Errors.Add($"DVL.XML が無い: {path}"); return r; }
        List<string> names;
        try
        {
            names = XDocument.Load(path).Descendants("AssessmentScore")
                .Select(e => (string?)e.Attribute("ScoreName") ?? "").ToList();
        }
        catch (System.Xml.XmlException e) { r.Errors.Add($"DVL.XML を読めない: {e.Message}"); return r; }

        if (names.Count == 0) { r.Errors.Add("DVL.XML に項目が無い"); return r; }
        var prefix = $"{driverName}.{arch}.";
        var other = names.Where(n => !n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (other.Count > 0)
        {
            var known = BuildTarget.All.Select(t => t.Arch).Where(a => !a.Equals(arch, StringComparison.OrdinalIgnoreCase));
            var wrongArch = other.Any(n => known.Any(a => n.StartsWith($"{driverName}.{a}.", StringComparison.OrdinalIgnoreCase)));
            (wrongArch ? r.Errors : r.Warnings).Add($"項目名が {prefix} で始まらない: {other[0]}");
        }
        if (!names.Any(n => n.Contains(".Semmle.Summary"))) r.Errors.Add("Semmle.Summary が無い（SARIF が反映されていない）");
        if (!names.Any(n => n.EndsWith(".General.Checksum"))) r.Errors.Add("General.Checksum が無い");
        if (names.Any(n => n.EndsWith(".CodeAnalysis.NORUN")))
            r.Warnings.Add("CodeAnalysis が NORUN（Code Analysis の結果が入っていない）。VS 拡張で作った DVL は CodeAnalysis.Summary になる");
        return r;
    }
}
