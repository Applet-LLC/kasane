using System.Diagnostics;
using System.Text;
using KbdLayerKeymapGui.Interop;
using KbdLayerKeymapGui.Model;

namespace KbdLayerKeymapGui.Tests;

/// <summary>
/// NicolaService（NicolaTables.cpp の C# 移植）のテスト。CLI（KeymapTool.exe --dump-nicola）がビルド済みなら、
/// 同じ nicola.json から作った blob の中身が CLI と一致することも確かめる。
/// </summary>
public class NicolaServiceTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "driver", "KeymapTool", "samples", "nicola.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static string SamplePath => Path.Combine(RepoRoot(), "driver", "KeymapTool", "samples", "nicola.json");

    private static byte[] Build(NicolaDef def, List<string>? warnings = null)
    {
        var errors = new List<string>();
        var blob = NicolaService.BuildBlob(def, warnings ?? new List<string>(), errors);
        Assert.True(errors.Count == 0, string.Join("\n", errors));
        return blob!;
    }

    /// <summary>blob の文字キー index の plane（0=単独,1=親指左,2=親指右）のステップを "25v 25^" の形で返す。</summary>
    private static string Steps(byte[] blob, int index, int plane)
    {
        int off = KbdLayerClient.NicolaHeaderSize + index * KbdLayerClient.NicolaKeyEntrySize + 4 + plane * 52;
        int count = blob[off];
        var parts = new List<string>();
        for (int s = 0; s < count; s++)
        {
            int p = off + 4 + s * 4;
            parts.Add($"{BitConverter.ToUInt16(blob, p):X}{(blob[p + 2] != 0 ? '^' : 'v')}");
        }
        return string.Join(" ", parts);
    }

    private static ushort Source(byte[] blob, int index) =>
        BitConverter.ToUInt16(blob, KbdLayerClient.NicolaHeaderSize + index * KbdLayerClient.NicolaKeyEntrySize);

    [Fact]
    public void Standard_Matches_Sample_Json()
    {
        var fromSample = NicolaService.FromJson(File.ReadAllText(SamplePath));
        Assert.Equal(Build(NicolaService.CreateStandard()), Build(fromSample));
    }

    [Fact]
    public void Standard_Romaji_Header_And_Steps()
    {
        var warnings = new List<string>();
        var blob = Build(NicolaService.CreateStandard(), warnings);
        Assert.Equal(KbdLayerClient.NicolaHeaderSize + 32 * KbdLayerClient.NicolaKeyEntrySize, blob.Length);
        Assert.Equal(1u, BitConverter.ToUInt32(blob, 0));     // Version
        Assert.Equal(1u, BitConverter.ToUInt32(blob, 4));     // Enabled
        Assert.Equal(1u, BitConverter.ToUInt32(blob, 8));     // Layer
        Assert.Equal(0x7B, BitConverter.ToUInt16(blob, 12));  // KC_MHEN
        Assert.Equal(0x79, BitConverter.ToUInt16(blob, 14));  // KC_HENK
        Assert.Equal(100u, BitConverter.ToUInt32(blob, 28));  // OverlapRatioPercent
        Assert.Equal(32u, BitConverter.ToUInt32(blob, 40));   // KeyCount

        // KC_W（左手側の2番目）: 単独「か」= ka、親指左（同じ側）「え」= e、親指右（反対側）「が」= ga
        Assert.Equal(0x11, Source(blob, 1));
        Assert.Equal("25v 25^ 1Ev 1E^", Steps(blob, 1, 0));
        Assert.Equal("12v 12^", Steps(blob, 1, 1));
        Assert.Equal("22v 22^ 1Ev 1E^", Steps(blob, 1, 2));
        // KC_Y（右手側の先頭 = 16番目）: 親指右（同じ側）「よ」= yo、親指左（反対側）「ぱ」= pa
        Assert.Equal(0x15, Source(blob, 15));
        Assert.Equal("15v 15^ 18v 18^", Steps(blob, 15, 2));
        Assert.Equal("19v 19^ 1Ev 1E^", Steps(blob, 15, 1));

        // ローマ字入力では単独の ゛ ゜ は打てない（CLIと同じく、空にして警告する）
        Assert.Equal(4, warnings.Count);
        Assert.Equal("", Steps(blob, 21, 0)); // KC_RBRC 単独「゛」
    }

    [Fact]
    public void JisKana_Dakuten_And_Shifted_Kana()
    {
        var def = NicolaService.CreateStandard();
        def.Output = "jis-kana";
        var warnings = new List<string>();
        var blob = Build(def, warnings);
        Assert.Empty(warnings); // JISかな入力では ゛ ゜ も打てる
        // KC_W: 親指右「が」= か(0x14) + ゛(0x1A)
        Assert.Equal("14v 14^ 1Av 1A^", Steps(blob, 1, 2));
        // KC_Q: 親指左「ぁ」= Shift + 0x04
        Assert.Equal("2Av 4v 4^ 2A^", Steps(blob, 0, 1));
        // KC_A: 親指右「ヴ」= う(0x05) + ゛
        Assert.Equal("5v 5^ 1Av 1A^", Steps(blob, 5, 2));
    }

    [Fact]
    public void Json_RoundTrip_Keeps_Everything()
    {
        var def = NicolaService.CreateStandard();
        def.Output = "jis-kana";
        def.Enabled = false;
        def.Layer = 2;
        def.ThumbLeft = "KC_SPC";
        def.ThumbLeftTap = "KC_SPC";
        def.OnEnter = "KC_ESC";
        def.SimulTermMs = 80;
        def.OverlapRatio = 0.75;
        def.MinOverlapMs = 20;
        def.ContinuousShift = true;
        def.Keys[3].ThumbRight = "";

        var back = NicolaService.FromJson(NicolaService.ToJson(def));
        Assert.Equal(Build(def), Build(back));
        Assert.Equal(def.Keys.Select(k => (k.Key, k.RightHand, k.Single, k.ThumbLeft, k.ThumbRight)),
                     back.Keys.Select(k => (k.Key, k.RightHand, k.Single, k.ThumbLeft, k.ThumbRight)));
        Assert.False(back.Enabled);
        Assert.True(back.ContinuousShift);
        Assert.Equal(0.75, back.OverlapRatio);
    }

    [Theory]
    [InlineData("thumb-same")]
    [InlineData("char-is-thumb")]
    [InlineData("duplicate")]
    [InlineData("layer")]
    [InlineData("simul")]
    [InlineData("output")]
    [InlineData("bad-key")]
    [InlineData("too-many-steps")]
    public void Invalid_Definitions_Are_Rejected(string which)
    {
        var def = NicolaService.CreateStandard();
        switch (which)
        {
            case "thumb-same": def.ThumbRight = def.ThumbLeft; break;
            case "char-is-thumb": def.Keys[0].Key = "KC_MHEN"; break;
            case "duplicate": def.Keys[1].Key = def.Keys[0].Key; break;
            case "layer": def.Layer = KbdLayerClient.MaxLayers; break;
            case "simul": def.SimulTermMs = 0; break;
            case "output": def.Output = "kana"; break;
            case "bad-key": def.ThumbLeft = "MO(1)"; break;
            case "too-many-steps": def.Keys[0].Single = "っっっ"; break; // xtu ×3 = 18ステップ
        }
        var errors = new List<string>();
        Assert.Null(NicolaService.BuildBlob(def, new List<string>(), errors));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void FromJson_Rejects_Malformed()
    {
        Assert.Throws<InvalidDataException>(() => NicolaService.FromJson("[1]"));
        Assert.Throws<InvalidDataException>(() => NicolaService.FromJson("{\"left\": {\"KC_Q\": []}}"));
        Assert.Throws<InvalidDataException>(() => NicolaService.FromJson("{\"left\": {\"KC_Q\": [1]}}"));
        Assert.Throws<InvalidDataException>(() => NicolaService.FromJson("{ not json"));
    }

    // ------------------------------------------------------------------
    // CLI との照合（KeymapTool.exe --dump-nicola）
    // ------------------------------------------------------------------

    private static string? FindKeymapTool()
    {
        foreach (var config in new[] { "Release", "Debug" })
        {
            var p = Path.Combine(RepoRoot(), "driver", "KeymapTool", "x64", config, "KeymapTool.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>NicolaTables::PrintBlob と同じ形式で、文字キーの行だけを作る。</summary>
    private static List<string> DumpKeyLines(byte[] blob)
    {
        var lines = new List<string>();
        int count = (int)BitConverter.ToUInt32(blob, 40);
        string[] names = { "single", "thumbL", "thumbR" };
        for (int i = 0; i < count; i++)
        {
            var sb = new StringBuilder($"    [{i,2}] 0x{Source(blob, i):X4}");
            for (int p = 0; p < 3; p++)
            {
                string steps = Steps(blob, i, p);
                sb.Append($"  {names[p]}:").Append(steps.Length == 0 ? " -" : " " + steps);
            }
            lines.Add(sb.ToString());
        }
        return lines;
    }

    [Theory]
    [InlineData("romaji")]
    [InlineData("jis-kana")]
    public void Matches_KeymapTool_DumpNicola(string output)
    {
        var exe = FindKeymapTool();
        if (exe == null) return; // CLIが未ビルドなら照合しない（tools/Check-NicolaTables.ps1 の表の照合は別にある）

        var def = NicolaService.FromJson(File.ReadAllText(SamplePath));
        def.Output = output;
        var json = Path.Combine(Path.GetTempPath(), $"nicola-check-{output}-{Environment.ProcessId}.json");
        NicolaService.Save(def, json);
        try
        {
            var psi = new ProcessStartInfo(exe, $"--dump-nicola \"{json}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            Assert.Equal(0, proc.ExitCode);

            var cliLines = stdout.Replace("\r", "").Split('\n').Where(l => l.StartsWith("    [")).ToList();
            var blob = Build(def);
            Assert.Contains($"BlobLength={blob.Length} ", stdout);
            Assert.Equal(cliLines, DumpKeyLines(blob));
        }
        finally { File.Delete(json); }
    }
}
