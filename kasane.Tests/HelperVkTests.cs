using Kasane.Keycodes;
using Kasane.Model;
using Xunit;

namespace Kasane.Tests;

/// <summary>Sequence の vk 手順（Vk / VkDown / VkUp）と、仮想キーの名前の表。</summary>
public class HelperVkTests
{
    [Theory]
    [InlineData("IME_ON", 0x16)]
    [InlineData("VK_IME_OFF", 0x1A)]
    [InlineData("ime_off", 0x1A)]
    [InlineData("KANJI", 0x19)]
    [InlineData("DBE_HIRAGANA", 0xF2)]
    [InlineData("F1", 0x70)]
    [InlineData("F24", 0x87)]
    [InlineData("0x16", 0x16)]
    [InlineData("22", 22)]
    public void TryResolveKey_AcceptsNamesAndNumbers(string token, int expected)
    {
        Assert.True(VkTable.TryResolveKey(token, out ushort vk));
        Assert.Equal(expected, vk);
    }

    [Theory]
    [InlineData("")]
    [InlineData("NOPE")]
    [InlineData("0")]
    [InlineData("0xFF")]
    [InlineData("300")]
    [InlineData("-5")]
    public void TryResolveKey_RejectsUnknown(string token)
    {
        Assert.False(VkTable.TryResolveKey(token, out _));
    }

    [Fact]
    public void TryResolve_SplitsModifiers()
    {
        Assert.True(VkTable.TryResolve("LSHIFT+RCTRL+F1", out var mods, out ushort vk));
        Assert.Equal(new ushort[] { 0xA0, 0xA3 }, mods);
        Assert.Equal(0x70, vk);
    }

    [Fact]
    public void TryResolve_RejectsNonModifierPrefix()
    {
        Assert.False(VkTable.TryResolve("F1+F2", out _, out _));
        Assert.False(VkTable.TryResolve("SHIFT+", out _, out _));
    }

    [Fact]
    public void Compile_Vk_TapsTheKey()
    {
        var r = SequenceCompiler.Compile(new[] { new HelperStep { Vk = "IME_ON" } });
        Assert.Empty(r.Errors);
        Assert.Equal(new[] { new InputOp(InputKind.Vk, 0x16), new InputOp(InputKind.Vk, 0x16, Up: true) }, r.Ops);
    }

    [Fact]
    public void Compile_Vk_WithModifiers_PressesInOrderAndReleasesInReverse()
    {
        var r = SequenceCompiler.Compile(new[] { new HelperStep { Vk = "LSHIFT+LALT+F1" } });
        Assert.Empty(r.Errors);
        Assert.Equal(new[]
        {
            new InputOp(InputKind.Vk, 0xA0), new InputOp(InputKind.Vk, 0xA4), new InputOp(InputKind.Vk, 0x70),
            new InputOp(InputKind.Vk, 0x70, Up: true), new InputOp(InputKind.Vk, 0xA4, Up: true), new InputOp(InputKind.Vk, 0xA0, Up: true),
        }, r.Ops);
    }

    [Fact]
    public void Compile_VkDownAndUp_AreSeparate()
    {
        var r = SequenceCompiler.Compile(new[] { new HelperStep { VkDown = "CTRL+0x41" }, new HelperStep { VkUp = "CTRL+0x41" } });
        Assert.Empty(r.Errors);
        Assert.Equal(new[]
        {
            new InputOp(InputKind.Vk, 0xA2), new InputOp(InputKind.Vk, 0x41),
            new InputOp(InputKind.Vk, 0x41, Up: true), new InputOp(InputKind.Vk, 0xA2, Up: true),
        }, r.Ops);
    }

    [Fact]
    public void Compile_Vk_Unknown_ReportsError()
    {
        var r = SequenceCompiler.Compile(new[] { new HelperStep { Vk = "NOPE" } });
        Assert.Single(r.Errors);
        Assert.Empty(r.Ops);
    }

    // ---- StepText ----

    [Fact]
    public void StepText_RoundTrips()
    {
        const string text = "vk IME_ON\nvkdown LSHIFT+F1\nvkup LSHIFT+F1\n";
        Assert.True(StepText.TryParse(text, out var steps, out var errors), string.Join(";", errors));
        Assert.Equal("IME_ON", steps[0].Vk);
        Assert.Equal("LSHIFT+F1", steps[1].VkDown);
        Assert.Equal("LSHIFT+F1", steps[2].VkUp);
        Assert.Equal(text.Replace("\n", Environment.NewLine), StepText.Format(steps));
    }

    [Fact]
    public void StepText_RejectsBadVk()
    {
        Assert.False(StepText.TryParse("vk\nvk NOPE\n", out _, out var errors));
        Assert.Equal(2, errors.Count);
    }

    // ---- 実行 ----

    private sealed class Env : IActionEnvironment
    {
        public List<InputOp> Sent = new();
        public void Send(InputOp op) => Sent.Add(op);
        public bool Sleep(int milliseconds) => true;
        public void Shell(string path, string? args, string? workDir) { }
        public void TogglePause() { }
        public void Layer(LayerRequest request) { }
        void IActionEnvironment.Log(string message) { }
    }

    [Fact]
    public void Execute_Vk_SendsVkOps()
    {
        var body = new HelperActionBody { Type = HelperActionBody.Sequence, Steps = new List<HelperStep> { new() { Vk = "IME_OFF" } } };
        var env = new Env();
        new ActionExecutor(env).Execute(new ActionRequest(body, 1, 1, false, false));
        Assert.Equal(new[] { new InputOp(InputKind.Vk, 0x1A), new InputOp(InputKind.Vk, 0x1A, Up: true) }, env.Sent);
    }
}
