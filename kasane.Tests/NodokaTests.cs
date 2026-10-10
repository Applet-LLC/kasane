using System.Text;
using Kasane.Import.Nodoka;
using Kasane.Keycodes;
using Kasane.Model;
using Kasane.Model.Portable;

namespace Kasane.Tests;

public class NodokaTests
{
    /// <summary>テスト用の最小のキーボード定義（109.nodoka と同じ書き方）。</summary>
    private const string Kbd = @"
def key Esc = 0x01
def key A = 0x1e
def key B = 0x30
def key C = 0x2e
def key D = 0x20
def key X = 0x2d
def key Z = 0x2c
def key N = 0x31
def key Space = 0x39
def key Tab = 0x0f
def key Enter = 0x1c
def key LeftShift LShift = 0x2a
def key LeftControl LControl LCtrl = 0x1d
def key LeftAlt LAlt = 0x38
def key F1 = 0x3b
def key F2 = 0x3c
def key F3 = 0x3d
def key F5 = 0x3f
def key F6 = 0x40
def key ひらがな Hiragana = 0x70
def key Home = E0-0x47
def key Up = E0-0x48
def key Left = E0-0x4b
def key LButton = E1-0x01
";

    private sealed class MemFiles : INodokaFiles
    {
        private readonly Dictionary<string, string> _files;
        public MemFiles(Dictionary<string, string>? files) => _files = files ?? new();
        public (string Path, string Text)? Open(string name, string? includedFrom) =>
            _files.TryGetValue(name, out var t) ? (name, t) : null;
    }

    private static NodokaConversion Run(string text, Dictionary<string, string>? files = null, params string[] symbols) =>
        NodokaImporter.ImportText("main.nodoka", text, symbols, new MemFiles(files));

    private static Dictionary<string, string> Layer(NodokaConversion c, int layer, int keyboard = 0) => c.File.Keyboards[keyboard].Layers![layer].Keys;

    private static string Tok(ushort sc) => KeycodeTable.KeyToken(sc);

    private static List<string> Texts(IEnumerable<NodokaLine> lines, int i) => lines.ElementAt(i).Tokens.Select(t => t.ToString()).ToList();

    private static List<NodokaLine> Tokenize(string text) =>
        NodokaTokenizer.Tokenize(NodokaText.SplitLines(text), (_, msg) => throw new InvalidOperationException(msg));

    // ---- 文字コード ----

    [Fact]
    public void Decode_HandlesBomsUtf8AndShiftJis()
    {
        Assert.Equal("あ", NodokaText.Decode(new byte[] { 0xFF, 0xFE, 0x42, 0x30 }));
        Assert.Equal("あ", NodokaText.Decode(new byte[] { 0xFE, 0xFF, 0x30, 0x42 }));
        Assert.Equal("あ", NodokaText.Decode(new byte[] { 0xEF, 0xBB, 0xBF, 0xE3, 0x81, 0x82 }));
        Assert.Equal("あ", NodokaText.Decode(new byte[] { 0xE3, 0x81, 0x82 }));
        Assert.Equal("あ", NodokaText.Decode(new byte[] { 0x82, 0xA0 })); // CP932（UTF-8 としては不正）
    }

    // ---- トークナイザー ----

    [Fact]
    public void Tokenizer_SplitsPrefixesCommentsAndContinuation()
    {
        var lines = Tokenize("key C-A = Home # comment\nkey A \\\n  = B\n\n# only comment\nkey *S-X=Y");
        Assert.Equal(3, lines.Count);
        Assert.Equal(new[] { "key", "C-", "A", "=", "Home" }, Texts(lines, 0));
        Assert.Equal(1, lines[0].Line);
        Assert.Equal(new[] { "key", "A", "=", "B" }, Texts(lines, 1));
        Assert.Equal(2, lines[1].Line);
        Assert.Equal(new[] { "key", "*", "S-", "X", "=", "Y" }, Texts(lines, 2));
        Assert.Equal(6, lines[2].Line);
    }

    [Fact]
    public void Tokenizer_QuotedStringsNumbersRegexAndEmptyArguments()
    {
        var lines = Tokenize("&SendText(\"hi\" \"there\")\nE0-0x1d 0x1c 010\nwindow W /emacs\\.exe/\n&F(,,)");
        var t0 = lines[0].Tokens;
        Assert.Equal(new[] { "&", "SendText", "(", "hithere", ")" }, t0.Select(t => t.ToString()).ToArray());
        Assert.True(t0[3].Quoted);

        var t1 = lines[1].Tokens;
        Assert.Equal("E0-", t1[0].Text);
        Assert.Equal(0x1d, t1[1].Number);
        Assert.Equal(0x1c, t1[2].Number);
        Assert.Equal(8, t1[3].Number); // 8進

        Assert.True(lines[2].Tokens[2].IsRegexp);
        Assert.Equal(@"emacs\.exe", lines[2].Tokens[2].Text);

        Assert.Equal(9, lines[3].Tokens.Count); // & F ( "" , "" , "" )
        Assert.Equal("", lines[3].Tokens[3].Text);
    }

    [Fact]
    public void Tokenizer_UnicodeKeyNamesAndArrows()
    {
        var lines = Tokenize("def key 半角/全角 漢字 = 0x29\nkey ← = Left");
        Assert.Equal(new[] { "def", "key", "半角/全角", "漢字", "=", "0x29" }, Texts(lines, 0));
        Assert.Equal("←", lines[1].Tokens[1].Text);
    }

    // ---- 読み込み ----

    [Fact]
    public void Parser_EvaluatesIfElseAndDefineAndInclude()
    {
        var c = Run(@"
include ""kbd.nodoka""
define FOO
if (FOO) key *A = *B
if (!FOO) key *A = *C
if (BAR)
  key *B = *D
else
  key *B = *Z
endif
", new() { ["kbd.nodoka"] = Kbd });

        var l0 = Layer(c, 0);
        Assert.Equal(Tok(0x30), l0["SC_001E"]); // A → B
        Assert.Equal(Tok(0x2C), l0["SC_0030"]); // BAR が無いので else 側: B → Z
        Assert.DoesNotContain(c.Lines, l => l.Level == ConvLevel.Skipped);
    }

    [Fact]
    public void Parser_SymbolsFromTheProfileAreHonoured()
    {
        string text = Kbd + "if (USE109) key *A = *B\n";
        Assert.Empty(Layer(Run(text), 0));
        Assert.Single(Layer(Run(text, null, "USE109"), 0));
    }

    [Fact]
    public void Parser_ReportsUnknownLinesWithFileAndLineAndKeepsGoing()
    {
        var c = Run(Kbd + "key *A = *B\nbogus line\nkey *C = *D\n");
        Assert.Equal(2, Layer(c, 0).Count);
        var bad = Assert.Single(c.Lines, l => l.Level == ConvLevel.Skipped && l.Text.Contains("bogus"));
        Assert.Equal("main.nodoka", bad.Src!.File);
        Assert.Equal(Kbd.Split('\n').Length + 1, bad.Src.Line);
    }

    // ---- 変換: キーの置き換え ----

    [Fact]
    public void Convert_PlainRemap_AnyModifiersIsExactAndNoModifiersIsApproximate()
    {
        var c = Run(Kbd + "key *A = *B\nkey C = D\n");

        Assert.Equal(Tok(0x30), Layer(c, 0)["SC_001E"]);
        Assert.Equal(Tok(0x20), Layer(c, 0)["SC_002E"]);
        var approx = Assert.Single(c.Lines, l => l.Level == ConvLevel.Approximated);
        Assert.Contains("修飾キーを押していないとき", approx.Text);
        Assert.Contains("（1件", approx.Text); // *A は含まれない
    }

    [Fact]
    public void Convert_ModifiedOutputUsesModdedKey_AndKeyseqReferenceIsInlined()
    {
        var c = Run(Kbd + "keyseq $T = A-X\nkey *A = S-B\nkey *IC-C-Z = $T\n");

        Assert.Equal($"LSFT({Tok(0x30)})", Layer(c, 0)["SC_001E"]);
        var ko = Assert.Single(c.File.Keyboards[0].KeyOverrides!);
        Assert.Equal("MOD_LCTL", ko.Mods);
        Assert.Equal(Tok(0x2C), ko.Key);
        Assert.Equal($"LALT({Tok(0x2D)})", ko.Replacement);
    }

    [Fact]
    public void Convert_ModifierKeyWithKeyOverride_UsesTheLayer0OutputOfThePhysicalKey()
    {
        // A は B に置き換えられるので、Ctrl+A のキー置換のトリガーは、出力のキー（B）になる。
        var c = Run(Kbd + "key *A = *B\nkey C-A = Home\n");
        var ko = Assert.Single(c.File.Keyboards[0].KeyOverrides!);
        Assert.Equal(Tok(0x30), ko.Key);
        Assert.Equal(Tok(0xE047), ko.Replacement);
    }

    [Fact]
    public void Convert_SubstituteBecomesARemap()
    {
        var c = Run(Kbd + "def subst *C = *D\n");
        Assert.Equal(Tok(0x20), Layer(c, 0)["SC_002E"]);
    }

    // ---- 変換: 修飾キー・レイヤー ----

    [Fact]
    public void Convert_SandS_BecomesModTap()
    {
        var c = Run(Kbd + "mod shift += !!Space\n");
        Assert.Equal($"MT(MOD_LSFT,{Tok(0x39)})", Layer(c, 0)["SC_0039"]);
    }

    [Fact]
    public void Convert_TrueModifierRemapsTheKeyToTheModifier()
    {
        var c = Run(Kbd + "mod ctrl += !Tab\n");
        Assert.Equal(Tok(0x1D), Layer(c, 0)["SC_000F"]);
    }

    [Fact]
    public void Convert_ModifierLayer_M0()
    {
        var c = Run(Kbd + "mod M0 = !Up\nkey M0-Left = Left Up\n");

        Assert.Equal("MO(1)", Layer(c, 0)["SC_E048"]);
        Assert.Equal("MACRO(0)", Layer(c, 1)["SC_E04B"]);
        Assert.Equal(new[] { Tok(0xE04B), Tok(0xE048) }, c.File.Keyboards[0].Macros![0].Keys.ToArray());
    }

    [Fact]
    public void Convert_OneShotM0_UsesLayerTapWithTheKeysOwnOutput()
    {
        var c = Run(Kbd + "mod M0 = !!Space\nkey M0-A = B\n");
        Assert.Equal($"LT(1,{Tok(0x39)})", Layer(c, 0)["SC_0039"]);
        Assert.Equal(Tok(0x30), Layer(c, 1)["SC_001E"]);
    }

    [Fact]
    public void Convert_PrefixKeymapBecomesAOneShotLayer()
    {
        var c = Run(Kbd + "keymap Ctrl-X\nkey *A = *B\nkeymap Global\nkey *X = &Prefix(Ctrl-X)\n");
        Assert.Equal("OSL(1)", Layer(c, 0)["SC_002D"]);
        Assert.Equal(Tok(0x30), Layer(c, 1)["SC_001E"]);
    }

    [Fact]
    public void Convert_UnreferencedKeymapIsNotConverted()
    {
        var c = Run(Kbd + "keymap Lonely\nkey *A = *B\n");
        Assert.Single(c.File.Keyboards[0].Layers!);
        Assert.Contains(c.Lines, l => l.Level == ConvLevel.Info && l.Text.Contains("Lonely"));
    }

    [Fact]
    public void Convert_WindowKeymapBecomesALayerAndAnAppRule()
    {
        var c = Run(Kbd + "window Emacs /emacs\\.exe/ : Global\nkey *A = *B\nwindow Term (/putty/ || /xterm/)\nkey *C = *D\n");

        var rules = c.File.Keyboards[0].Helper!.AppLayerRules;
        Assert.Equal(2, rules.Count);
        // のどかは後に定義した window から調べるので、逆順に並べる
        Assert.Equal("Term", rules[0].Name);
        Assert.Equal("putty", rules[0].Match.NodokaClass);
        Assert.Equal("xterm", rules[0].Match.NodokaTitle);
        Assert.True(rules[0].Match.NodokaOr);
        Assert.Equal("Emacs", rules[1].Name);
        Assert.Equal(@"emacs\.exe", rules[1].Match.NodokaClass);
        Assert.False(rules[1].Match.NodokaOr);
        Assert.Equal(new[] { 1 }, rules[1].Layers);
        Assert.Equal(new[] { 2 }, rules[0].Layers);
        Assert.Equal(Tok(0x30), Layer(c, 1)["SC_001E"]);
        Assert.Equal(Tok(0x20), Layer(c, 2)["SC_002E"]);
    }

    [Fact]
    public void Convert_WindowKeymapInheritsItsNonGlobalParent()
    {
        var c = Run(Kbd + "keymap Base\nkey *A = *B\nwindow W /w/ : Base\nkey *C = *D\n" +
                    "keymap Global\nkey *X = &Prefix(Base)\n");
        var baseLayer = Layer(c, 1);
        Assert.Equal(Tok(0x30), baseLayer["SC_001E"]);
        var w = Layer(c, 2);
        Assert.Equal(Tok(0x30), w["SC_001E"]); // 親の割り当てを引き継ぐ
        Assert.Equal(Tok(0x20), w["SC_002E"]);
    }

    // ---- 変換: 右辺 ----

    [Fact]
    public void Convert_KeyListBecomesAMacro_AndMacrosAreShared()
    {
        var c = Run(Kbd + "key F1 = A B C\nkey F2 = A B C\n");
        Assert.Equal("MACRO(0)", Layer(c, 0)["SC_003B"]);
        Assert.Equal("MACRO(0)", Layer(c, 0)["SC_003C"]);
        Assert.Single(c.File.Keyboards[0].Macros!);
    }

    [Fact]
    public void Convert_ShellExecuteBecomesAHelperLaunch()
    {
        var c = Run(Kbd + "key F1 = &ShellExecute(\"open\", \"calc.exe\", \"\", \"\", \"\")\n");
        var h = c.File.Keyboards[0].Helper!;
        Assert.Equal("HLP(1)", Layer(c, 0)["SC_003B"]);
        Assert.Equal(HelperActionBody.Launch, h.Actions[0].Action.Type);
        Assert.Equal("calc.exe", h.Actions[0].Action.Path);
    }

    [Fact]
    public void Convert_MixedSequenceBecomesAHelperSequence()
    {
        var c = Run(Kbd + "key F2 = C-C &Wait(50) &SendText(\"hi\")\n");
        var action = c.File.Keyboards[0].Helper!.Actions[0].Action;
        Assert.Equal(HelperActionBody.Sequence, action.Type);
        Assert.Equal(3, action.Steps!.Count);
        Assert.Equal($"LCTL({Tok(0x2E)})", action.Steps[0].Key);
        Assert.Equal(50, action.Steps[1].DelayMs);
        Assert.Equal("hi", action.Steps[2].Text);
        Assert.Equal("HLP(1)", Layer(c, 0)["SC_003C"]);
    }

    [Fact]
    public void Convert_ModifierPlusHelperActionBecomesATriggerOnTheLayer0Output()
    {
        var c = Run(Kbd + "key *N = *B\nkey W-N = &ShellExecute(\"open\", \"notepad.exe\", \"\", \"\", \"\")\n");
        var t = Assert.Single(c.File.Keyboards[0].Helper!.Triggers);
        Assert.Equal(Tok(0x30), t.Key);
        Assert.Equal("MOD_LGUI", t.Mods);
        Assert.Equal("notepad.exe", t.Action.Path);
        Assert.Contains(c.Lines, l => l.Level == ConvLevel.Approximated && l.Text.Contains("トリガー"));
    }

    [Fact]
    public void Convert_TogglesAndIgnoreAndTransparent()
    {
        var c = Run(Kbd + "key F1 = &Toggle(Lock0)\nkey F2 = &Ignore\nkey F3 = &Default\nkey L0-A = B\nkey F5 = &Toggle(Lock0, on)\n");
        var l0 = Layer(c, 0);
        Assert.Equal("TG(1)", l0["SC_003B"]);
        Assert.Equal("KC_NO", l0["SC_003C"]);
        Assert.False(l0.ContainsKey("SC_003D")); // そのまま通す
        Assert.Equal(Tok(0x30), Layer(c, 1)["SC_001E"]);
        Assert.Equal("HLP(1)", l0["SC_003F"]);
        var op = c.File.Keyboards[0].Helper!.Actions[0].Action;
        Assert.Equal(HelperActionBody.AppLayer, op.Type);
        Assert.Equal("On", op.LayerOp);
        Assert.Equal(new[] { 1 }, op.Layers);
    }

    [Fact]
    public void Convert_EditNextModifierBecomesOneShotMod()
    {
        var c = Run(Kbd + "key F1 = &EditNextModifier(M-)\n");
        Assert.Equal("OSM(MOD_LALT)", Layer(c, 0)["SC_003B"]);
    }

    // ---- 変換: combo / taphold / tapdance ----

    [Fact]
    public void Convert_ComboTapHoldAndTapDance()
    {
        var c = Run(Kbd + "combo A B = C\ntaphold Space tap=Space hold=LShift\ntapdance Z tap1=Z tap2=X\n" +
                    "taphold Tab tap=Tab hold=Left\n");
        var kb = c.File.Keyboards[0];
        var combo = Assert.Single(kb.Combos!);
        Assert.Equal("SC_001E", combo.Key1);
        Assert.Equal("SC_0030", combo.Key2);
        Assert.Equal(Tok(0x2E), combo.Output);
        Assert.Equal($"MT(MOD_LSFT,{Tok(0x39)})", Layer(c, 0)["SC_0039"]);
        // 修飾キーではない taphold はタップダンスの「タップ / ホールド」（taphold を先に変換するので番号は0）
        Assert.Equal("TAPDANCE(0)", Layer(c, 0)["SC_000F"]);
        Assert.Equal(Tok(0x0F), kb.TapDances![0].SingleTap);
        Assert.Equal(Tok(0xE04B), kb.TapDances[0].SingleHold);
        Assert.Equal("TAPDANCE(1)", Layer(c, 0)["SC_002C"]);
        Assert.Equal(Tok(0x2C), kb.TapDances[1].SingleTap);
        Assert.Equal(Tok(0x2D), kb.TapDances[1].DoubleTap);
    }

    [Fact]
    public void Convert_ThreeKeyComboIsReported()
    {
        var c = Run(Kbd + "combo A B C = D\n");
        Assert.Empty(c.File.Keyboards[0].Combos!);
        Assert.Contains(c.Lines, l => l.Level == ConvLevel.Skipped && l.Text.Contains("3キー"));
    }

    [Fact]
    public void Convert_OptionsBecomeTiming()
    {
        var c = Run(Kbd + "def option TapHoldThreshold = 180\ndef option ComboWindow = 40\ndef option TapDanceTimeout = 250\n" +
                    "def option TapHoldPermissiveHold = on\n");
        var t = c.File.Keyboards[0].Timing!;
        Assert.Equal(180u, t.TapTermMs);
        Assert.Equal(40u, t.ComboTermMs);
        Assert.Equal(250u, t.TapDanceTermMs);
        Assert.Equal(1u, t.TapHoldMode);
    }

    // ---- 変換できないもの ----

    [Fact]
    public void Convert_UnsupportedThingsAreReportedWithFileAndLine()
    {
        string text = Kbd +
            "key F1 = &WindowMaximize\n" +   // 非対応の関数
            "key F2 = LButton\n" +            // マウスの疑似キー
            "key IL-A = B\n" +                // IME の状態
            "key R-*Space = &Ignore\n" +      // キーリピート
            "key F3 = &SendText($Clip)\n" +   // 組み込みの値
            "key *Nope = *A\n" +              // 未定義のキー
            "event prefixed = A\n";
        var c = Run(text);
        Assert.Empty(Layer(c, 0));
        var skipped = c.Lines.Where(l => l.Level == ConvLevel.Skipped).ToList();
        Assert.Equal(7, skipped.Count);
        Assert.All(skipped, l => Assert.NotNull(l.Src));
        Assert.Contains(skipped, l => l.Text.Contains("WindowMaximize"));
        Assert.Contains(skipped, l => l.Text.Contains("疑似キー"));
        Assert.Contains(skipped, l => l.Text.Contains("IL-"));
        Assert.Contains(skipped, l => l.Text.Contains("R-"));
        Assert.Contains(skipped, l => l.Text.Contains("SendText"));
        Assert.Contains(skipped, l => l.Text.Contains("Nope"));
        Assert.Contains(skipped, l => l.Text.Contains("event"));
    }

    [Fact]
    public void Convert_LayerOverflowIsReportedOnce()
    {
        var sb = new StringBuilder(Kbd);
        for (int i = 0; i < 18; i++) sb.AppendLine($"keymap K{i}\nkey *A = *B");
        sb.AppendLine("keymap Global");
        for (int i = 0; i < 18; i++) sb.AppendLine($"key F1 = &Prefix(K{i})");
        var c = Run(sb.ToString());
        Assert.Equal(16, c.File.Keyboards[0].Layers!.Count);
        Assert.Single(c.Lines, l => l.Text.Contains("16枚を超える"));
    }

    // ---- キーボードごと ----

    [Fact]
    public void Convert_KeyboardSpecificRulesGoToThatKeyboardsUniqId()
    {
        var c = Run(Kbd + "def option UnitID = K1 ExtraInfo 0xCDEC0000\nkey *C = *D\nkey K1-*A = *B\n");

        Assert.Equal(2, c.File.Keyboards.Count);
        Assert.Equal(nameof(PortableScope.All), c.File.Scope);
        Assert.Null(c.File.Keyboards[0].UniqId);
        Assert.Equal(new[] { "SC_002E" }, Layer(c, 0, 0).Keys.ToArray()); // 共通
        Assert.Equal("0xCDEC0000", c.File.Keyboards[1].UniqId);
        Assert.Equal(Tok(0x30), Layer(c, 0, 1)["SC_001E"]);
        Assert.Equal(Tok(0x20), Layer(c, 0, 1)["SC_002E"]); // 共通の規則も入る
    }

    [Fact]
    public void Convert_KeyboardRuleWithoutUnitIdIsReported()
    {
        var c = Run(Kbd + "key K2-*A = *B\n");
        Assert.Single(c.File.Keyboards);
        Assert.Contains(c.Lines, l => l.Level == ConvLevel.Skipped && l.Text.Contains("UnitID"));
    }

    // ---- 取り込みの経路 ----

    [Fact]
    public void ConvertedFile_ImportsThroughThePortableImporter()
    {
        var c = Run(Kbd + "key *A = *B\nmod M0 = !Up\nkey M0-Left = Left Up\n");
        var ten = LayoutFixture.TenKey;
        var project = ProjectService.CreateDefault(1, null, ten, true);

        // 10キーには A/B が無いので、無いキーとして報告される。エラーにはならない。
        var res = PortableImporter.Import(c.File.Keyboards[0], project, ten, null, null,
            new ImportOptions { Mode = ImportMode.Append }, layerScope: false);

        Assert.False(res.Report.HasErrors);
        Assert.Contains(res.Report.Lines, l => l.Text.Contains("レイアウトに無いキー"));
    }

    [Fact]
    public void RegistryEntry_IsParsed()
    {
        var p = NodokaRegistry.ParseEntry(@"自分の設定;C:\Users\me\.nodoka;-DUSE109;-DEMACS", true)!;
        Assert.Equal("自分の設定", p.Name);
        Assert.Equal(@"C:\Users\me\.nodoka", p.Path);
        Assert.Equal(new[] { "USE109", "EMACS" }, p.Symbols.ToArray());
        Assert.True(p.IsActive);
        Assert.Null(NodokaRegistry.ParseEntry("no-semicolon", false));
    }
}
