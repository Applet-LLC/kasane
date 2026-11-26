namespace KasaneWhql;

// 4 本（Subsc/Pro × x64/ARM64）の違いは、すべてこの表で表す。ほかのコードに版や CPU の分岐を書かない。
internal sealed record BuildTarget(
    string Edition,      // "subsc" | "pro"
    string Arch,         // "x64" | "arm64"
    string BuildBat,     // driver\kbdlayer 直下の bat
    string DbFolder,     // E:\codeql-home 直下
    string SarifName,    // E:\codeql-home 直下の SARIF
    string HlkArchDir)   // HLK 版フォルダ内の CPU ごとのフォルダ
{
    public bool IsPro => Edition == "pro";
    public string Label => $"{(IsPro ? "Pro" : "Subsc")} {Arch}";

    public static readonly BuildTarget[] All =
    {
        new("subsc", "x64",   "build_codeql_x64.bat",       "databases-x64",       "layerDriver-x64.sarif",       "x64"),
        new("subsc", "arm64", "build_codeql_arm64.bat",     "databases-arm64",     "layerDriver-arm64.sarif",     "ARM64"),
        new("pro",   "x64",   "build_codeql_x64_pro.bat",   "databases-x64-pro",   "layerDriver-x64-pro.sarif",   "x64"),
        new("pro",   "arm64", "build_codeql_arm64_pro.bat", "databases-arm64-pro", "layerDriver-arm64-pro.sarif", "ARM64"),
    };
}
