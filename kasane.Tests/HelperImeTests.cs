using Kasane.Model;
using Xunit;

namespace Kasane.Tests;

/// <summary>Sequence の IME ステップ（Ime / ImeMethod / ImeOnFail）と、Text の ImeOffDuring。</summary>
public class HelperImeTests
{
    private sealed class Env : IActionEnvironment
    {
        public List<string> Log = new();
        public List<InputOp> Sent = new();
        public List<(ImeTarget Target, ImeMethod Method)> ImeCalls = new();
        /// <summary>SetIme の結果。Sent との前後関係を見るため、呼び出しは Events にも記録する。</summary>
        public bool ImeResult = true;
        public List<string> Events = new();

        public void Send(InputOp op) { Sent.Add(op); Events.Add("send"); }
        public bool Sleep(int milliseconds) => true;
        public void Shell(string path, string? args, string? workDir) { }
        public void TogglePause() { }
        public void Layer(LayerRequest request) { }
        void IActionEnvironment.Log(string message) => Log.Add(message);
        public bool SetIme(ImeTarget target, ImeMethod method)
        {
            ImeCalls.Add((target, method));
            Events.Add("ime:" + target);
            return ImeResult;
        }
    }

    private static ActionRequest Press(HelperActionBody b) => new(b, 1, 1, false, false);

    // ---- Compile ----

    [Fact]
    public void Compile_ImeStep_BecomesImeOp()
    {
        var r = SequenceCompiler.Compile(new[] { new HelperStep { Ime = "Off", ImeMethod = "Tsf", ImeOnFail = "Abort" } });
        Assert.Empty(r.Errors);
        var op = Assert.Single(r.Ops);
        Assert.Equal(InputKind.Ime, op.Kind);
        Assert.Equal((int)ImeTarget.Off, op.Value);
        Assert.Equal((int)ImeMethod.Tsf, op.Value2);
        Assert.True(op.Flag);
    }

    [Fact]
    public void Compile_ImeStep_DefaultsToAutoAndContinue()
    {
        var r = SequenceCompiler.Compile(new[] { new HelperStep { Ime = "on" } });
        var op = Assert.Single(r.Ops);
        Assert.Equal((int)ImeTarget.On, op.Value);
        Assert.Equal((int)ImeMethod.Auto, op.Value2);
        Assert.False(op.Flag);
    }

    [Theory]
    [InlineData("maybe", null, null)]
    [InlineData("on", "ouija", null)]
    [InlineData("on", null, "retry")]
    public void Compile_ImeStep_ReportsBadNames(string ime, string? method, string? onFail)
    {
        var r = SequenceCompiler.Compile(new[] { new HelperStep { Ime = ime, ImeMethod = method, ImeOnFail = onFail } });
        Assert.Single(r.Errors);
        Assert.Empty(r.Ops);
    }

    // ---- StepText ----

    [Fact]
    public void StepText_RoundTrips_Ime()
    {
        var steps = new List<HelperStep>
        {
            new() { Ime = "off" },
            new() { Ime = "on", ImeMethod = "tsf" },
            new() { Ime = "toggle", ImeMethod = "altkanji", ImeOnFail = "abort" },
        };
        string text = StepText.Format(steps);
        Assert.Equal("ime off\nime on tsf\nime toggle altkanji abort\n", text.Replace("\r\n", "\n"));

        Assert.True(StepText.TryParse(text, out var parsed, out var errors), string.Join("; ", errors));
        Assert.Equal(StepText.Format(steps), StepText.Format(parsed));
        Assert.Equal("tsf", parsed[1].ImeMethod);
        Assert.Equal("abort", parsed[2].ImeOnFail);
    }

    [Theory]
    [InlineData("ime")]
    [InlineData("ime maybe")]
    [InlineData("ime on ouija")]
    [InlineData("ime on key tsf")]
    public void StepText_Parse_RejectsBadIme(string line)
    {
        Assert.False(StepText.TryParse(line, out _, out var errors));
        Assert.Single(errors);
    }

    // ---- Executor: Sequence ----

    [Fact]
    public void Sequence_Ime_CallsEnvironmentWithTheFixedMethod_InOrder()
    {
        var env = new Env();
        var body = new HelperActionBody
        {
            Type = HelperActionBody.Sequence,
            Steps = new() { new() { Ime = "off", ImeMethod = "key" }, new() { Key = "KC_A" } },
        };
        new ActionExecutor(env).Execute(Press(body));

        Assert.Equal((ImeTarget.Off, ImeMethod.Key), Assert.Single(env.ImeCalls));
        Assert.Equal("ime:Off", env.Events[0]);
        Assert.Contains(env.Sent, o => o.Kind == InputKind.Key); // IME の op は Send に来ない
        Assert.DoesNotContain(env.Sent, o => o.Kind == InputKind.Ime);
    }

    [Fact]
    public void Sequence_ImeFailure_Continues_ByDefault()
    {
        var env = new Env { ImeResult = false };
        var body = new HelperActionBody
        {
            Type = HelperActionBody.Sequence,
            Steps = new() { new() { Ime = "off", ImeMethod = "imm" }, new() { Key = "KC_A" } },
        };
        new ActionExecutor(env).Execute(Press(body));
        Assert.Contains(env.Sent, o => o.Kind == InputKind.Key);
    }

    [Fact]
    public void Sequence_ImeFailure_Aborts_WhenRequested()
    {
        var env = new Env { ImeResult = false };
        var body = new HelperActionBody
        {
            Type = HelperActionBody.Sequence,
            Steps = new() { new() { Ime = "off", ImeMethod = "imm", ImeOnFail = "abort" }, new() { Key = "KC_A" } },
        };
        new ActionExecutor(env).Execute(Press(body));
        Assert.Empty(env.Sent);
        Assert.Single(env.Log);
    }

    // ---- Executor: Text + ImeOffDuring ----

    [Fact]
    public void Text_ImeOffDuring_TurnsOffBeforeAndRestoresAfter()
    {
        var env = new Env();
        var body = new HelperActionBody { Type = HelperActionBody.TextInput, Text = "ab", ImeOffDuring = true, ImeMethod = "imm" };
        new ActionExecutor(env).Execute(Press(body));

        Assert.Equal("ime:OffRemember", env.Events.First());
        Assert.Equal("ime:Restore", env.Events.Last());
        Assert.Equal(new[] { ImeMethod.Imm, ImeMethod.Imm }, env.ImeCalls.Select(c => c.Method).ToArray());
        Assert.Equal(4, env.Sent.Count(o => o.Kind == InputKind.Unicode));
    }

    [Fact]
    public void Text_WithoutImeOffDuring_DoesNotTouchTheIme()
    {
        var env = new Env();
        new ActionExecutor(env).Execute(Press(new HelperActionBody { Type = HelperActionBody.TextInput, Text = "ab" }));
        Assert.Empty(env.ImeCalls);
    }

    [Fact]
    public void Text_ImeOffDuring_FailureContinues_ByDefault_AndStillRestores()
    {
        var env = new Env { ImeResult = false };
        var body = new HelperActionBody { Type = HelperActionBody.TextInput, Text = "a", ImeOffDuring = true };
        new ActionExecutor(env).Execute(Press(body));

        Assert.Equal(2, env.Sent.Count(o => o.Kind == InputKind.Unicode));
        Assert.Equal(ImeTarget.Restore, env.ImeCalls.Last().Target);
    }

    [Fact]
    public void Text_ImeOffDuring_FailureAborts_WhenRequested_AndStillRestores()
    {
        var env = new Env { ImeResult = false };
        var body = new HelperActionBody { Type = HelperActionBody.TextInput, Text = "a", ImeOffDuring = true, ImeMethod = "tsf", ImeOnFail = "Abort" };
        new ActionExecutor(env).Execute(Press(body));

        Assert.Empty(env.Sent);
        Assert.Equal(ImeTarget.Restore, env.ImeCalls.Last().Target);
    }

    [Fact]
    public void Json_RoundTrips_ImeFields()
    {
        const string json = """
        { "Actions": [ { "Id": 1, "Name": "t", "Action": { "Type": "Text", "Text": "x", "ImeOffDuring": true, "ImeMethod": "Key", "ImeOnFail": "Abort" } },
                       { "Id": 2, "Name": "s", "Action": { "Type": "Sequence", "Steps": [ { "Ime": "On", "ImeMethod": "Tsf" } ] } } ] }
        """;
        var cfg = HelperConfig.FromJson(json)!;
        Assert.True(cfg.Actions[0].Action.ImeOffDuring);
        Assert.Equal("Key", cfg.Actions[0].Action.ImeMethod);
        Assert.Equal("Tsf", cfg.Actions[1].Action.Steps![0].ImeMethod);
        Assert.Equal(cfg.ToJson(), HelperConfig.FromJson(cfg.ToJson())!.ToJson());
    }
}
