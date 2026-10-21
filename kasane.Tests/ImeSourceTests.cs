using System.IO;
using System.Text.Json;
using Kasane.Model;
using Kasane.Tray;
using Xunit;

namespace Kasane.Tests;

public class ImeSourceModeTests
{
    [Theory]
    [InlineData(null, ImeSourceMode.Imm32)]
    [InlineData("", ImeSourceMode.Imm32)]
    [InlineData("Imm32", ImeSourceMode.Imm32)]
    [InlineData("ImmThenTsf", ImeSourceMode.ImmThenTsf)]
    [InlineData("TsfThenImm", ImeSourceMode.TsfThenImm)]
    [InlineData("  tsfthenimm ", ImeSourceMode.TsfThenImm)]
    [InlineData("unknown", ImeSourceMode.Imm32)]
    [InlineData("42", ImeSourceMode.Imm32)] // 数字は名前ではない（範囲外の値を作らない）
    public void Parse(string? text, ImeSourceMode expected) => Assert.Equal(expected, ImeSourceModes.Parse(text));

    [Fact]
    public void UsesTsf_IsFalseOnlyForImm32()
    {
        Assert.False(ImeSourceMode.Imm32.UsesTsf());
        Assert.True(ImeSourceMode.ImmThenTsf.UsesTsf());
        Assert.True(ImeSourceMode.TsfThenImm.UsesTsf());
    }

    [Fact]
    public void TrayConfig_DefaultIsImm32()
    {
        var config = new TrayConfig();
        Assert.Equal("Imm32", config.ImeSource);
        Assert.Equal(ImeSourceMode.Imm32, config.ImeSourceMode);
    }

    [Fact]
    public void TrayConfig_SaveAndLoad_KeepsImeSource_AndDoesNotWriteDerivedProperty()
    {
        string path = Path.Combine(Path.GetTempPath(), "kasane-test-tray-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            new TrayConfig { ImeSource = "TsfThenImm" }.Save(path);
            Assert.Equal(ImeSourceMode.TsfThenImm, TrayConfig.Load(path).ImeSourceMode);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.False(doc.RootElement.TryGetProperty("ImeSourceMode", out _)); // 派生のプロパティは保存しない
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TrayConfig_OldFileWithoutImeSource_GetsDefault()
    {
        string path = Path.Combine(Path.GetTempPath(), "kasane-test-tray-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{ \"ImeLink\": true }");
            var config = TrayConfig.Load(path);
            Assert.True(config.ImeLink);
            Assert.Equal(ImeSourceMode.Imm32, config.ImeSourceMode);
        }
        finally { File.Delete(path); }
    }
}

public class FallbackImeSourceTests
{
    private sealed class Fake : IImeSource
    {
        private readonly ImeState _state;
        public int Calls;
        public Fake(ImeState state) => _state = state;
        public ImeState Read() { Calls++; return _state; }
    }

    private static readonly ImeState Kana = new(true, true, ImeConversion.Native | ImeConversion.FullShape);
    private static readonly ImeState Off = new(true, false, 0);

    [Fact]
    public void FirstKnown_SecondIsNotAsked()
    {
        var first = new Fake(Kana);
        var second = new Fake(Off);
        Assert.Equal(Kana, new FallbackImeSource(first, second).Read());
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public void FirstUnknown_UsesSecond()
    {
        var second = new Fake(Off);
        Assert.Equal(Off, new FallbackImeSource(new Fake(ImeState.Unknown), second).Read());
        Assert.Equal(1, second.Calls);
    }

    [Fact]
    public void BothUnknown_IsUnknown()
    {
        Assert.False(new FallbackImeSource(new Fake(ImeState.Unknown), new Fake(ImeState.Unknown)).Read().Known);
    }

    [Fact]
    public void FirstOff_IsKnown_SoSecondIsNotAsked()
    {
        // OFF も「取れた」値。TSF に聞き直さない。
        var second = new Fake(Kana);
        Assert.Equal(Off, new FallbackImeSource(new Fake(Off), second).Read());
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public void Selector_FollowsModeAndOrder()
    {
        var imm = new Fake(Off);
        var tsf = new Fake(Kana);
        var selector = new ImeSourceSelector(imm, tsf);

        selector.Mode = ImeSourceMode.Imm32;
        Assert.Equal(Off, selector.Read());
        Assert.Equal(0, tsf.Calls); // IMM32 だけのときは TSF を見ない

        selector.Mode = ImeSourceMode.TsfThenImm;
        Assert.Equal(Kana, selector.Read());

        selector.Mode = ImeSourceMode.ImmThenTsf;
        Assert.Equal(Off, selector.Read());
    }

    [Fact]
    public void Describe_OffOnRomajiKanaAndUnknown()
    {
        Assert.Equal("取得できず", ImeSourceSelector.Describe(ImeState.Unknown));
        Assert.Equal("OFF", ImeSourceSelector.Describe(Off));
        Assert.StartsWith("ON ローマ字入力(日本語、", ImeSourceSelector.Describe(new ImeState(true, true, 0x19))); // NATIVE|FULLSHAPE|ROMAN
        Assert.StartsWith("ON カナ入力(日本語、", ImeSourceSelector.Describe(new ImeState(true, true, 0x09)));
        Assert.StartsWith("ON ローマ字入力(半角英数、", ImeSourceSelector.Describe(new ImeState(true, true, 0x10)));
        Assert.Contains("0x19", ImeSourceSelector.Describe(new ImeState(true, true, 0x19)));
    }

    [Fact]
    public void Selector_LogsSourceAndState_OnlyWhenChanged()
    {
        var lines = new List<string>();
        var imm = new Fake(Kana);
        var tsf = new Fake(Off);
        string app = "notepad";
        var selector = new ImeSourceSelector(imm, tsf, lines.Add, () => app) { Mode = ImeSourceMode.ImmThenTsf };

        selector.Read();
        selector.Read(); // 変わらない → 書かない
        Assert.Single(lines);
        Assert.StartsWith("IME: IMM32 ON ", lines[0]);
        Assert.EndsWith("[notepad]", lines[0]);

        app = "chrome"; // 前面のアプリが変わった → 書く
        selector.Read();
        Assert.Equal(2, lines.Count);
        Assert.EndsWith("[chrome]", lines[1]);
    }

    [Fact]
    public void Selector_LogsTsf_WhenImmUnknown_AndDash_WhenBothUnknown()
    {
        var lines = new List<string>();
        var imm = new SwitchableFake();
        var selector = new ImeSourceSelector(imm, new Fake(Off), lines.Add) { Mode = ImeSourceMode.ImmThenTsf };

        selector.Read();
        Assert.Equal("IME: TSF OFF", lines[^1]);

        var bothUnknown = new ImeSourceSelector(new Fake(ImeState.Unknown), new Fake(ImeState.Unknown), lines.Add) { Mode = ImeSourceMode.ImmThenTsf };
        bothUnknown.Read();
        Assert.Equal("IME: - 取得できず", lines[^1]);
    }

    [Fact]
    public void Selector_Imm32Mode_LogsImm32_AndNeverAsksTsf()
    {
        var lines = new List<string>();
        var tsf = new Fake(Kana);
        var selector = new ImeSourceSelector(new Fake(Off), tsf, lines.Add) { Mode = ImeSourceMode.Imm32 };
        selector.Read();
        Assert.Equal("IME: IMM32 OFF", lines[0]);
        Assert.Equal(0, tsf.Calls);
    }

    private sealed class SwitchableFake : IImeSource
    {
        public ImeState Read() => ImeState.Unknown;
    }

    [Fact]
    public void Selector_ImmThenTsf_FallsBackToTsf_WhenImmUnknown()
    {
        var selector = new ImeSourceSelector(new Fake(ImeState.Unknown), new Fake(Kana)) { Mode = ImeSourceMode.ImmThenTsf };
        Assert.Equal(Kana, selector.Read());
    }
}

public class SiriusTests
{
    private static SiriusLayout.Native Native(uint size = 1808, uint version = 1) => new()
    {
        CbSize = size, Version = version, HWndFocus = 0, HWndFront = 8, Conversion = 1064, ImeStatus = 1228,
        SupportTsf = 1248, SupportTsfOpenClose = 1252, SupportTsfConversion = 1256, ProcessId = 1700, IsHooked = 1800,
    };

    [Fact]
    public void Layout_Valid()
    {
        var layout = SiriusLayout.From(Native());
        Assert.NotNull(layout);
        Assert.Equal(1064, layout!.Value.Conversion);
        Assert.Equal(1228, layout.Value.ImeStatus);
        Assert.Equal(1800, layout.Value.IsHooked);
    }

    [Fact]
    public void Layout_RejectsOtherVersion() => Assert.Null(SiriusLayout.From(Native(version: 2)));

    [Fact]
    public void Layout_RejectsZeroSize() => Assert.Null(SiriusLayout.From(Native(size: 0)));

    [Fact]
    public void Layout_RejectsFieldPastEnd()
    {
        // IsHooked（1 バイト）が最後のバイトに収まるぎりぎり／1 バイト足りない。
        Assert.NotNull(SiriusLayout.From(Native(size: 1801)));
        Assert.Null(SiriusLayout.From(Native(size: 1800)));
    }

    [Fact]
    public void Layout_RejectsHugeOffset_WithoutOverflow()
    {
        var n = Native();
        n.Conversion = uint.MaxValue - 1;
        Assert.Null(SiriusLayout.From(n));
    }

    private static SiriusSnapshot Snap(bool hooked = true, bool openClose = true, bool conversion = true, uint open = 1, uint mode = 0x09)
        => new(hooked, 0x1234, openClose, conversion, open, mode);

    [Fact]
    public void Decide_On_WithConversion() =>
        Assert.Equal(new ImeState(true, true, 0x09), SiriusDecoder.Decide(Snap(), focusInForeground: true));

    [Fact]
    public void Decide_Off_DoesNotNeedConversion() =>
        Assert.Equal(new ImeState(true, false, 0), SiriusDecoder.Decide(Snap(open: 0, conversion: false), focusInForeground: true));

    [Fact]
    public void Decide_OnWithoutConversionSupport_IsUnknown() =>
        Assert.False(SiriusDecoder.Decide(Snap(conversion: false), focusInForeground: true).Known);

    [Fact]
    public void Decide_NotHooked_IsUnknown() =>
        Assert.False(SiriusDecoder.Decide(Snap(hooked: false), focusInForeground: true).Known);

    [Fact]
    public void Decide_OpenCloseNotSupported_IsUnknown() =>
        Assert.False(SiriusDecoder.Decide(Snap(openClose: false), focusInForeground: true).Known);

    [Theory]
    [InlineData(false, true, true, TsfReason.NotHooked)]
    [InlineData(true, false, true, TsfReason.OpenCloseUnsupported)]
    [InlineData(true, true, false, TsfReason.FocusNotForeground)]
    public void Decide_ReportsReason(bool hooked, bool openClose, bool inForeground, TsfReason expected)
    {
        var state = SiriusDecoder.Decide(Snap(hooked: hooked, openClose: openClose), inForeground, out var reason);
        Assert.False(state.Known);
        Assert.Equal(expected, reason);
    }

    [Fact]
    public void Decide_OnWithoutConversion_ReportsConversionUnsupported()
    {
        SiriusDecoder.Decide(Snap(conversion: false), true, out var reason);
        Assert.Equal(TsfReason.ConversionUnsupported, reason);
        SiriusDecoder.Decide(Snap(), true, out reason);
        Assert.Equal(TsfReason.Ok, reason);
    }

    [Fact]
    public void Selector_AppendsTsfReason_WhenTsfAskedAndUnknown()
    {
        var lines = new List<string>();
        var tsf = new DiagFake(reasonCode: 5, text: "TSF: テスト理由");
        var selector = new ImeSourceSelector(new StubSource(ImeState.Unknown), tsf, lines.Add) { Mode = ImeSourceMode.ImmThenTsf };
        selector.Read();
        Assert.Equal("IME: - 取得できず (TSF: テスト理由)", lines[0]);

        tsf.ReasonCode = 6; // 理由が変わった → 書く
        tsf.Text = "TSF: 別の理由";
        selector.Read();
        Assert.Equal(2, lines.Count);
        Assert.Contains("別の理由", lines[1]);
    }

    [Fact]
    public void Selector_DoesNotAppendReason_WhenImmSucceeds_AndTsfNotAsked()
    {
        var lines = new List<string>();
        var selector = new ImeSourceSelector(new StubSource(new ImeState(true, true, 0x9)), new DiagFake(5, "x"), lines.Add) { Mode = ImeSourceMode.ImmThenTsf };
        selector.Read();
        Assert.DoesNotContain("TSF:", lines[0]);
    }

    private sealed class StubSource : IImeSource
    {
        private readonly ImeState _state;
        public StubSource(ImeState state) => _state = state;
        public ImeState Read() => _state;
    }

    private sealed class DiagFake : IImeSource, IImeSourceDiagnostics
    {
        public int ReasonCode { get; set; }
        public string Text { get; set; }
        public DiagFake(int reasonCode, string text) { ReasonCode = reasonCode; Text = text; }
        public ImeState Read() => ImeState.Unknown;
        public string DescribeLast() => Text;
    }

    [Fact]
    public void Decide_FocusNotInForeground_IsUnknown() =>
        Assert.False(SiriusDecoder.Decide(Snap(), focusInForeground: false).Known); // 前面のアプリの値ではない
}
