using System.IO;
using System.Text.RegularExpressions;

namespace Kasane.Import.Nodoka;

/// <summary>include の探し方（nodoka4 の getHomeDirectories と同じ順: 設定ファイルのフォルダ → %NODOKA% → %HOME% → %USERPROFILE% → %HOMEDRIVE%%HOMEPATH%）。</summary>
public sealed class NodokaDiskFiles : INodokaFiles
{
    private readonly List<string> _dirs = new();

    public NodokaDiskFiles(string mainFile, IEnumerable<string>? extraDirs = null)
    {
        void Add(string? d)
        {
            if (!string.IsNullOrWhiteSpace(d) && !_dirs.Contains(d, StringComparer.OrdinalIgnoreCase)) _dirs.Add(d);
        }
        Add(Path.GetDirectoryName(Path.GetFullPath(mainFile)));
        Add(Environment.GetEnvironmentVariable("NODOKA"));
        Add(Environment.GetEnvironmentVariable("HOME"));
        Add(Environment.GetEnvironmentVariable("USERPROFILE"));
        string? drive = Environment.GetEnvironmentVariable("HOMEDRIVE"), path = Environment.GetEnvironmentVariable("HOMEPATH");
        if (drive != null && path != null) Add(drive + path);
        foreach (var d in extraDirs ?? Enumerable.Empty<string>()) Add(d);
    }

    public (string Path, string Text)? Open(string name, string? includedFrom)
    {
        var candidates = new List<string>();
        if (Path.IsPathRooted(name)) candidates.Add(name);
        else foreach (var d in _dirs) candidates.Add(Path.Combine(d, name));
        foreach (var c in candidates)
        {
            try
            {
                if (File.Exists(c)) return (c, NodokaText.Decode(File.ReadAllBytes(c)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }
}

/// <summary>のどかの設定一覧（レジストリ HKCU\Software\appletkan\nodoka の .nodoka0, .nodoka1, … = "表示名;パス;-DSYM;…"）の1件。</summary>
public sealed record NodokaProfile(string Name, string Path, IReadOnlyList<string> Symbols, bool IsActive);

public static class NodokaRegistry
{
    private const string Key = @"Software\appletkan\nodoka";

    public static List<NodokaProfile> ListProfiles()
    {
        var list = new List<NodokaProfile>();
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key);
            if (key == null) return list;
            int active = key.GetValue(".nodokaIndex") is int i ? i : 0;
            foreach (var name in key.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                var m = Regex.Match(name, @"^\.nodoka(\d+)$", RegexOptions.IgnoreCase);
                if (!m.Success || key.GetValue(name) is not string entry) continue;
                var p = ParseEntry(entry, int.Parse(m.Groups[1].Value) == active);
                if (p != null) list.Add(p);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // 読めなければ、一覧は空（ファイルを直接選べる）
        }
        return list;
    }

    /// <summary>"表示名;パス;-DSYM1;-DSYM2" を読む。形が違えば null。</summary>
    public static NodokaProfile? ParseEntry(string entry, bool active)
    {
        var parts = entry.Split(';');
        if (parts.Length < 2) return null;
        var symbols = new List<string>();
        foreach (var opt in parts.Skip(2))
        {
            string o = opt.Trim();
            if (o.StartsWith("-D", StringComparison.Ordinal) && o.Length > 2) symbols.Add(o[2..]);
        }
        return new NodokaProfile(parts[0], parts[1], symbols, active);
    }
}

public static class NodokaImporter
{
    /// <summary>
    /// 設定ファイルを読んで変換する。<paramref name="files"/> が null なら、実ファイルから include を探す。
    /// ファイルが読めなければ、報告にエラーを入れた空の結果を返す。
    /// </summary>
    public static NodokaConversion ImportFile(string path, IEnumerable<string> symbols, INodokaFiles? files = null)
    {
        string text;
        try { text = NodokaText.Decode(File.ReadAllBytes(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var failed = new NodokaConversion { File = new Kasane.Model.Portable.PortableFile() };
            failed.Lines.Add(new ConvLine(ConvLevel.Error, null, $"{path} を読めません: {ex.Message}"));
            return failed;
        }
        return ImportText(path, text, symbols, files ?? new NodokaDiskFiles(path));
    }

    public static NodokaConversion ImportText(string path, string text, IEnumerable<string> symbols, INodokaFiles files)
    {
        var parser = new NodokaParser(files, symbols);
        var setting = parser.Load(path, text);
        return NodokaConverter.Convert(setting, System.IO.Path.GetFileName(path));
    }
}
