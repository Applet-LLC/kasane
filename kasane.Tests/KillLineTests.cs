using Kasane.Model;

namespace Kasane.Tests;

/// <summary>ヘルパー動作 KillLine（Emacs の C-k。のどかの &amp;EmacsEditKillLine と同じ連結）。</summary>
public class KillLineTests
{
    /// <summary>
    /// 偽の環境。Ctrl+X を送ると、<see cref="NextCut"/> の文字列がクリップボードに入る（null は何も切り取れない）。
    /// 送ったキー手順を <see cref="Keys"/> に記録する。
    /// </summary>
    private sealed class Env : IActionEnvironment
    {
        public string? Clipboard;
        public uint Seq;
        public long Now = 1000;
        public long Window = 1;
        public bool Activity;
        public byte Held;
        public byte Physical;
        public string? NextCut;
        public List<InputOp> Sent = new();
        public List<string> ClipboardWrites = new();

        public void Send(InputOp op)
        {
            Sent.Add(op);
            // Ctrl+X の X（スキャンコード 0x2D）の押下で切り取る
            if (op.Kind == InputKind.Key && op.Value == 0x002D && !op.Up && NextCut != null)
            {
                Clipboard = NextCut;
                Seq++;
            }
        }
        public bool Sleep(int milliseconds) { Now += milliseconds; return true; }
        public void Shell(string path, string? args, string? workDir) { }
        public void TogglePause() { }
        public void Layer(LayerRequest request) { }
        void IActionEnvironment.Log(string message) { }
        byte IActionEnvironment.PhysicalKeys() => Physical;
        byte IActionEnvironment.HeldModifiers() => Held;
        bool IActionEnvironment.TryGetClipboardText(out string text) { text = Clipboard ?? ""; return Clipboard != null; }
        bool IActionEnvironment.SetClipboardText(string? text)
        {
            Clipboard = string.IsNullOrEmpty(text) ? null : text;
            Seq++;
            ClipboardWrites.Add(text ?? "");
            return true;
        }
        uint IActionEnvironment.ClipboardSequence() => Seq;
        long IActionEnvironment.TickMs() => Now;
        long IActionEnvironment.ForegroundWindow() => Window;
        bool IActionEnvironment.TakeKeyActivity() { bool v = Activity; Activity = false; return v; }
    }

    private static readonly HelperActionBody Body = new() { Type = HelperActionBody.KillLine };

    private static void Kill(ActionExecutor ex, Env env, string? cut)
    {
        env.NextCut = cut;
        ex.Execute(new ActionRequest(Body, 0, 0, IsBreak: false, IsRepeat: false));
    }

    // 送ったキーのスキャンコード（押下だけ）。
    private static List<int> Downs(Env env) => env.Sent.Where(o => o.Kind == InputKind.Key && !o.Up).Select(o => o.Value).ToList();

    [Fact]
    public void MidLine_CutsToEndAndPutsItOnTheClipboard()
    {
        var env = new Env();
        var ex = new ActionExecutor(env);
        Kill(ex, env, "hello");

        Assert.Equal(new[] { 0x2A, 0xE04F, 0x1D, 0x2D }, Downs(env));
        Assert.Equal("hello", env.Clipboard);
    }

    [Fact]
    public void AtEndOfLine_AddsNewlineAndSendsDelete()
    {
        var env = new Env();
        var ex = new ActionExecutor(env);
        Kill(ex, env, null);

        Assert.Equal("\r\n", env.Clipboard);
        Assert.Equal(0xE053, Downs(env).Last()); // Delete
    }

    [Fact]
    public void TrailingNewline_IsChompedAndRestoredWithReturnLeft()
    {
        var env = new Env();
        var ex = new ActionExecutor(env);
        Kill(ex, env, "abc\r\n");

        Assert.Equal("abc", env.Clipboard);
        var downs = Downs(env);
        Assert.Equal(new[] { 0x1C, 0xE04B }, downs.TakeLast(2).ToArray()); // Return, Left
    }

    [Fact]
    public void ConsecutiveKills_AreConcatenated()
    {
        var env = new Env();
        var ex = new ActionExecutor(env);
        Kill(ex, env, "one");
        Kill(ex, env, null);       // 行末: 改行
        Kill(ex, env, "two");

        Assert.Equal("one\r\ntwo", env.Clipboard);
    }

    [Fact]
    public void DifferentClipboard_StartsOver()
    {
        var env = new Env();
        var ex = new ActionExecutor(env);
        Kill(ex, env, "one");
        env.Clipboard = "copied elsewhere"; // ユーザーが別にコピーした
        Kill(ex, env, "two");

        Assert.Equal("two", env.Clipboard);
    }

    [Fact]
    public void OtherKeyBetween_StartsOver()
    {
        var env = new Env();
        var ex = new ActionExecutor(env);
        Kill(ex, env, "one");
        env.Activity = true;
        Kill(ex, env, "two");

        Assert.Equal("two", env.Clipboard);
    }

    [Fact]
    public void ForegroundChange_StartsOver()
    {
        var env = new Env();
        var ex = new ActionExecutor(env);
        Kill(ex, env, "one");
        env.Window = 2;
        Kill(ex, env, "two");

        Assert.Equal("two", env.Clipboard);
    }

    [Fact]
    public void After60Seconds_StartsOver()
    {
        var env = new Env();
        var ex = new ActionExecutor(env);
        Kill(ex, env, "one");
        env.Now += KillLineState.ExpireMs + 1;
        Kill(ex, env, "two");

        Assert.Equal("two", env.Clipboard);
    }

    [Fact]
    public void KeysPressedDuringRun_DoNotBreakTheChain()
    {
        var env = new Env();
        var ex = new ActionExecutor(env);
        Kill(ex, env, "one");
        Kill(ex, env, "two");

        Assert.Equal("onetwo", env.Clipboard);
    }

    [Fact]
    public void HeldCtrl_IsReleasedDuringTheCutAndRestoredIfStillDown()
    {
        var env = new Env { Held = 0x01, Physical = 0x01 }; // 左 Ctrl
        var ex = new ActionExecutor(env);
        Kill(ex, env, "x");

        // 先頭は Ctrl の解放、最後は Ctrl の押し直し
        Assert.Equal(new InputOp(InputKind.Key, 0x1D, Up: true), env.Sent.First());
        Assert.Equal(new InputOp(InputKind.Key, 0x1D), env.Sent.Last());
    }

    [Fact]
    public void HeldCtrl_ReleasedByUserMeanwhile_IsNotRestored()
    {
        var env = new Env { Held = 0x01, Physical = 0x00 };
        var ex = new ActionExecutor(env);
        Kill(ex, env, "x");

        Assert.NotEqual(new InputOp(InputKind.Key, 0x1D), env.Sent.Last());
    }

    [Fact]
    public void ReleaseAndRepeat_DoNothing()
    {
        var env = new Env();
        var ex = new ActionExecutor(env);
        ex.Execute(new ActionRequest(Body, 0, 0, IsBreak: true, IsRepeat: false));
        ex.Execute(new ActionRequest(Body, 0, 0, IsBreak: false, IsRepeat: true));

        Assert.Empty(env.Sent);
        Assert.Empty(env.ClipboardWrites);
    }

    [Fact]
    public void Validate_AcceptsKillLine()
    {
        var errors = new List<string>();
        HelperSettingsModel.ValidateBody(Body, "x", errors);
        Assert.Empty(errors);
    }
}
