using System.Text.Json;

namespace KasaneWhql;

// 設定。パスは kasane-whql.json で変えられる（無ければ既定値）。DriverDir が空なら、リポジトリの driver\kbdlayer を探す。
internal sealed class Config
{
    public string CodeQlHome { get; set; } = @"E:\codeql-home";
    public string CodeQlCmd { get; set; } = @"E:\codeql-home\codeql-2.20.1\codeql.cmd";
    public string CodeQlVersion { get; set; } = "2.20.1";
    public string QuerySuite { get; set; } = "microsoft/windows-drivers@1.8.0:windows-driver-suites/mustrun.qls";
    public string BuildEnvCmd { get; set; } = @"K:\BuildEnv\SetupBuildEnv.cmd";
    public string DvlExe { get; set; } = @"C:\Program Files (x86)\Windows Kits\10\Tools\dvl\dvl.exe";
    public string DriverDir { get; set; } = "";
    public string DriverName { get; set; } = "kbdlayer";

    public static Config Load()
    {
        var cfg = new Config();
        var path = Path.Combine(AppContext.BaseDirectory, "kasane-whql.json");
        if (File.Exists(path))
            cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(path)) ?? cfg;
        if (string.IsNullOrWhiteSpace(cfg.DriverDir))
            cfg.DriverDir = FindDriverDir() ?? "";
        return cfg;
    }

    // 実行ファイルの場所から上に辿り、kasane.sln のあるフォルダの driver\kbdlayer を返す。
    private static string? FindDriverDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "kasane.sln")))
                return Path.Combine(d.FullName, "driver", "kbdlayer");
        return null;
    }
}
