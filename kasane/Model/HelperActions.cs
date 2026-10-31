using Kasane.Keycodes;

namespace Kasane.Model;

// ヘルパー動作（P3、2026-10-08）の、Windows に依存しない部分。手順の展開（SequenceCompiler）、キーフックのトリガーの引き当て
// （TriggerTable）、HLP(id) の引き当て（HelperActionResolver）、動作の実行（ActionExecutor。実際の入力・起動は
// IActionEnvironment の向こう）。テストでは偽の IActionEnvironment を使う。

public enum InputKind { Key, Unicode, MouseButton, MouseMove, Wheel, HWheel, Delay, Ime, Vk }

/// <summary>IME をどうするか。OffRemember / Restore は Text の ImeOffDuring 用（OFF にする前の状態を覚え、あとで戻す）。</summary>
public enum ImeTarget { Off, On, Toggle, OffRemember, Restore }

/// <summary>IME の状態の読み書きの方式。Auto 以外は固定で、失敗しても別の方式へは切り替えない。</summary>
public enum ImeMethod { Auto, Imm, Tsf, Key, AltKanji }

public static class ImeNames
{
    public static bool TryParseTarget(string? text, out ImeTarget target)
    {
        target = ImeTarget.Off;
        switch ((text ?? "").Trim().ToLowerInvariant())
        {
            case "on": target = ImeTarget.On; return true;
            case "off": target = ImeTarget.Off; return true;
            case "toggle": target = ImeTarget.Toggle; return true;
            default: return false;
        }
    }

    /// <summary>空・null は Auto。</summary>
    public static bool TryParseMethod(string? text, out ImeMethod method)
    {
        method = ImeMethod.Auto;
        switch ((text ?? "").Trim().ToLowerInvariant())
        {
            case "" or "auto": return true;
            case "imm" or "imm32": method = ImeMethod.Imm; return true;
            case "tsf": method = ImeMethod.Tsf; return true;
            case "key": method = ImeMethod.Key; return true;
            case "altkanji" or "alt-kanji" or "alt": method = ImeMethod.AltKanji; return true;
            default: return false;
        }
    }

    /// <summary>失敗したときの扱い。空・null・"continue" は続行（abort = false）。</summary>
    public static bool TryParseOnFail(string? text, out bool abort)
    {
        abort = false;
        switch ((text ?? "").Trim().ToLowerInvariant())
        {
            case "" or "continue": return true;
            case "abort": abort = true; return true;
            default: return false;
        }
    }

    public static string MethodName(ImeMethod method) => method switch
    {
        ImeMethod.Imm => "imm",
        ImeMethod.Tsf => "tsf",
        ImeMethod.Key => "key",
        ImeMethod.AltKanji => "altkanji",
        _ => "auto",
    };
}

/// <summary>
/// 展開した入力1件。
///  - Key: Value = スキャンコード（Enc16。上位が E0 なら拡張キー）、Up。
///  - Unicode: Value = UTF-16 の1文字、Up。
///  - MouseButton: Value = <see cref="HelperMouseButtons"/>、Up。
///  - MouseMove: Value = x、Value2 = y、Flag = 絶対座標（仮想デスクトップの画面座標）。
///  - Wheel / HWheel: Value = 量（120 で1ノッチ）。
///  - Delay: Value = ミリ秒。
///  - Ime: Value = <see cref="ImeTarget"/>、Value2 = <see cref="ImeMethod"/>、Flag = 失敗したらこの動作の残りをやめる。
///  - Vk: Value = 仮想キーコード（VK_*）、Up。スキャンコードを持たないキー（VK_IME_ON など）用で、常に SendInput で送る。
/// </summary>
public readonly record struct InputOp(InputKind Kind, int Value = 0, int Value2 = 0, bool Up = false, bool Flag = false);

public static class HelperMouseButtons
{
    public const int Left = 0, Right = 1, Middle = 2, X1 = 3, X2 = 4;

    public static bool TryParse(string? name, out int button)
    {
        button = -1;
        switch ((name ?? "").Trim().ToLowerInvariant())
        {
            case "left" or "l": button = Left; break;
            case "right" or "r": button = Right; break;
            case "middle" or "m": button = Middle; break;
            case "x1": button = X1; break;
            case "x2": button = X2; break;
        }
        return button >= 0;
    }
}

public sealed record CompileResult(IReadOnlyList<InputOp> Ops, IReadOnlyList<string> Errors);

/// <summary>手順（<see cref="HelperStep"/>）を、入力の列に展開する。</summary>
public static class SequenceCompiler
{
    public const int MaxOps = 2000;
    public const int MaxDelayMs = 10_000;
    public const int MaxTotalDelayMs = 60_000;

    private const ushort EnterScan = 0x001C;
    private const ushort TabScan = 0x000F;

    public static CompileResult Compile(IEnumerable<HelperStep>? steps)
    {
        var ops = new List<InputOp>();
        var errors = new List<string>();
        long totalDelay = 0;
        int index = 0;

        foreach (var step in steps ?? Enumerable.Empty<HelperStep>())
        {
            index++;
            if (ops.Count > MaxOps) { errors.Add(Loc.T($"手順が多すぎます({MaxOps} 件まで)", $"Too many steps (up to {MaxOps})")); break; }
            string at = Loc.T($"手順{index}: ", $"Step {index}: ");

            if (!string.IsNullOrEmpty(step.Key))
            {
                if (TryResolveKey(step.Key, out byte mods, out ushort sc)) Tap(ops, mods, sc);
                else errors.Add(at + Loc.T($"Key '{step.Key}' を解決できません", $"Cannot resolve Key '{step.Key}'"));
            }
            if (!string.IsNullOrEmpty(step.Down))
            {
                if (TryResolveKey(step.Down, out byte mods, out ushort sc)) Press(ops, mods, sc);
                else errors.Add(at + Loc.T($"Down '{step.Down}' を解決できません", $"Cannot resolve Down '{step.Down}'"));
            }
            if (!string.IsNullOrEmpty(step.Up))
            {
                if (TryResolveKey(step.Up, out byte mods, out ushort sc)) Release(ops, mods, sc);
                else errors.Add(at + Loc.T($"Up '{step.Up}' を解決できません", $"Cannot resolve Up '{step.Up}'"));
            }
            if (!string.IsNullOrEmpty(step.Vk))
            {
                if (VkTable.TryResolve(step.Vk, out var mods, out ushort vk)) { PressVk(ops, mods, vk); ReleaseVk(ops, mods, vk); }
                else errors.Add(at + Loc.T($"Vk '{step.Vk}' を解決できません", $"Cannot resolve Vk '{step.Vk}'"));
            }
            if (!string.IsNullOrEmpty(step.VkDown))
            {
                if (VkTable.TryResolve(step.VkDown, out var mods, out ushort vk)) PressVk(ops, mods, vk);
                else errors.Add(at + Loc.T($"VkDown '{step.VkDown}' を解決できません", $"Cannot resolve VkDown '{step.VkDown}'"));
            }
            if (!string.IsNullOrEmpty(step.VkUp))
            {
                if (VkTable.TryResolve(step.VkUp, out var mods, out ushort vk)) ReleaseVk(ops, mods, vk);
                else errors.Add(at + Loc.T($"VkUp '{step.VkUp}' を解決できません", $"Cannot resolve VkUp '{step.VkUp}'"));
            }
            if (!string.IsNullOrEmpty(step.Text)) AddText(ops, step.Text);
            if (step.DelayMs is int delay)
            {
                int d = Math.Clamp(delay, 0, MaxDelayMs);
                if (d != delay) errors.Add(at + Loc.T($"DelayMs {delay} は 0〜{MaxDelayMs} に丸めました", $"DelayMs {delay} was clamped to 0-{MaxDelayMs}"));
                totalDelay += d;
                if (totalDelay > MaxTotalDelayMs) { errors.Add(Loc.T($"待ち時間の合計が長すぎます({MaxTotalDelayMs}ms まで)", $"The total delay is too long (up to {MaxTotalDelayMs} ms)")); break; }
                if (d > 0) ops.Add(new InputOp(InputKind.Delay, d));
            }
            if (!string.IsNullOrEmpty(step.Mouse))
            {
                if (!HelperMouseButtons.TryParse(step.Mouse, out int button)) errors.Add(at + Loc.T($"Mouse '{step.Mouse}' を解決できません", $"Cannot resolve Mouse '{step.Mouse}'"));
                else
                {
                    string action = (step.MouseAction ?? "Click").Trim().ToLowerInvariant();
                    if (action == "click") { ops.Add(new InputOp(InputKind.MouseButton, button)); ops.Add(new InputOp(InputKind.MouseButton, button, Up: true)); }
                    else if (action == "down") ops.Add(new InputOp(InputKind.MouseButton, button));
                    else if (action == "up") ops.Add(new InputOp(InputKind.MouseButton, button, Up: true));
                    else errors.Add(at + Loc.T($"MouseAction '{step.MouseAction}' を解決できません(Click / Down / Up)", $"Cannot resolve MouseAction '{step.MouseAction}' (Click / Down / Up)"));
                }
            }
            if (step.MoveX != null || step.MoveY != null)
            {
                if (step.Absolute && (step.MoveX == null || step.MoveY == null))
                    errors.Add(at + Loc.T("Absolute のときは MoveX と MoveY の両方が必要です", "Absolute requires both MoveX and MoveY"));
                else ops.Add(new InputOp(InputKind.MouseMove, step.MoveX ?? 0, step.MoveY ?? 0, Flag: step.Absolute));
            }
            if (step.Wheel is int wheel && wheel != 0) ops.Add(new InputOp(InputKind.Wheel, wheel));
            if (step.HWheel is int hwheel && hwheel != 0) ops.Add(new InputOp(InputKind.HWheel, hwheel));
            if (!string.IsNullOrEmpty(step.Ime))
            {
                if (!ImeNames.TryParseTarget(step.Ime, out var target)) errors.Add(at + Loc.T($"Ime '{step.Ime}' を解決できません(On / Off / Toggle)", $"Cannot resolve Ime '{step.Ime}' (On / Off / Toggle)"));
                else if (!ImeNames.TryParseMethod(step.ImeMethod, out var method)) errors.Add(at + Loc.T($"ImeMethod '{step.ImeMethod}' を解決できません(Auto / Imm / Tsf / Key / AltKanji)", $"Cannot resolve ImeMethod '{step.ImeMethod}' (Auto / Imm / Tsf / Key / AltKanji)"));
                else if (!ImeNames.TryParseOnFail(step.ImeOnFail, out bool abort)) errors.Add(at + Loc.T($"ImeOnFail '{step.ImeOnFail}' を解決できません(Continue / Abort)", $"Cannot resolve ImeOnFail '{step.ImeOnFail}' (Continue / Abort)"));
                else ops.Add(new InputOp(InputKind.Ime, (int)target, (int)method, Flag: abort));
            }
        }
        return new CompileResult(ops, errors);
    }

    /// <summary>キーコード文字列を、修飾子マスク（KBDLAYER_MOD_*）とスキャンコードにする。単純キーと修飾付きキー（LCTL(KC_C) など）だけ。</summary>
    public static bool TryResolveKey(string token, out byte mods, out ushort scanCode)
    {
        mods = 0;
        scanCode = 0;
        if (!KeycodeTable.TryResolve(token, out var action)) return false;
        switch (action.Type)
        {
            case ActionType.Key when action.Param2 != 0:
                scanCode = action.Param2;
                return true;
            case ActionType.ModdedKey when action.Param2 != 0:
                mods = action.Param1;
                scanCode = action.Param2;
                return true;
            default:
                return false;
        }
    }

    private static void Press(List<InputOp> ops, byte mods, ushort sc)
    {
        for (int bit = 0; bit < 8; bit++)
            if ((mods & (1 << bit)) != 0) ops.Add(new InputOp(InputKind.Key, KeycodeTable.ModifierScanCodes[bit]));
        ops.Add(new InputOp(InputKind.Key, sc));
    }

    private static void Release(List<InputOp> ops, byte mods, ushort sc)
    {
        ops.Add(new InputOp(InputKind.Key, sc, Up: true));
        for (int bit = 7; bit >= 0; bit--)
            if ((mods & (1 << bit)) != 0) ops.Add(new InputOp(InputKind.Key, KeycodeTable.ModifierScanCodes[bit], Up: true));
    }

    private static void PressVk(List<InputOp> ops, ushort[] mods, ushort vk)
    {
        foreach (ushort m in mods) ops.Add(new InputOp(InputKind.Vk, m));
        ops.Add(new InputOp(InputKind.Vk, vk));
    }

    private static void ReleaseVk(List<InputOp> ops, ushort[] mods, ushort vk)
    {
        ops.Add(new InputOp(InputKind.Vk, vk, Up: true));
        for (int i = mods.Length - 1; i >= 0; i--) ops.Add(new InputOp(InputKind.Vk, mods[i], Up: true));
    }

    private static void Tap(List<InputOp> ops, byte mods, ushort sc)
    {
        for (int bit = 0; bit < 8; bit++)
            if ((mods & (1 << bit)) != 0) ops.Add(new InputOp(InputKind.Key, KeycodeTable.ModifierScanCodes[bit]));
        ops.Add(new InputOp(InputKind.Key, sc));
        Release(ops, mods, sc);
        // Release は先にキーを離すので、ここで足したキーの押下と対になる。
    }

    private static void AddText(List<InputOp> ops, string text)
    {
        foreach (char c in text)
        {
            if (c == '\r') continue;
            if (c == '\n') Tap(ops, 0, EnterScan);
            else if (c == '\t') Tap(ops, 0, TabScan);
            else
            {
                ops.Add(new InputOp(InputKind.Unicode, c));
                ops.Add(new InputOp(InputKind.Unicode, c, Up: true));
            }
        }
    }
}

/// <summary>キーフックで受ける、キー＋修飾キー → 動作の表。キーボードごとの分を先に、共通の分を後に引く。</summary>
public sealed class TriggerTable
{
    public const byte ModCtrl = 1, ModShift = 2, ModAlt = 4, ModWin = 8;

    public static readonly TriggerTable Empty = new(new Dictionary<(ushort, byte), List<Entry>>(), Array.Empty<string>());

    private sealed record Entry(int DeviceHash16, HelperActionBody Body); // DeviceHash16 = -1 は共通

    private readonly Dictionary<(ushort Scan, byte Mods), List<Entry>> _map;

    private TriggerTable(Dictionary<(ushort, byte), List<Entry>> map, IReadOnlyList<string> errors)
    {
        _map = map;
        Errors = errors;
    }

    /// <summary>読めなかったトリガー（ログ・エディタの検証用）。</summary>
    public IReadOnlyList<string> Errors { get; }

    public bool IsEmpty => _map.Count == 0;

    /// <summary>修飾キー名（"MOD_LSFT|MOD_LCTL"、"CTRL|SHIFT" など。左右は区別しない）→ 汎用のマスク。空は 0。</summary>
    public static bool TryParseMods(string? text, out byte mods)
    {
        mods = 0;
        if (string.IsNullOrWhiteSpace(text)) return true;
        foreach (var raw in text.Split('|', ',', '+'))
        {
            string part = raw.Trim().ToUpperInvariant();
            if (part.Length == 0) continue;
            int idx = Array.IndexOf(KeycodeTable.ModifierNames, part);
            if (idx >= 0) { mods |= (byte)(1 << (idx & 3)); continue; }
            switch (part)
            {
                case "CTRL" or "CONTROL" or "CTL": mods |= ModCtrl; break;
                case "SHIFT" or "SFT": mods |= ModShift; break;
                case "ALT": mods |= ModAlt; break;
                case "WIN" or "GUI": mods |= ModWin; break;
                default: return false;
            }
        }
        return true;
    }

    public static TriggerTable Build(HelperConfigSet config)
    {
        var map = new Dictionary<(ushort, byte), List<Entry>>();
        var errors = new List<string>();

        void Add(HelperConfig cfg, int hash16, string owner)
        {
            foreach (var t in cfg.Triggers)
            {
                if (!KeycodeTable.TryResolveBaseKey(t.Key ?? "", out ushort sc) || sc == 0)
                {
                    errors.Add(Loc.T($"{owner}: トリガーのキー '{t.Key}' を解決できません", $"{owner}: Cannot resolve trigger key '{t.Key}'"));
                    continue;
                }
                if (!TryParseMods(t.Mods, out byte mods))
                {
                    errors.Add(Loc.T($"{owner}: トリガー {t.Key} の Mods '{t.Mods}' を解決できません", $"{owner}: Cannot resolve Mods '{t.Mods}' of trigger {t.Key}"));
                    continue;
                }
                if (!map.TryGetValue((sc, mods), out var list)) map[(sc, mods)] = list = new List<Entry>();
                list.Add(new Entry(hash16, t.Action));
            }
        }

        foreach (var kv in config.PerDevice.OrderBy(kv => kv.Key))
            Add(kv.Value, (int)(kv.Key >> 16), kv.Key.ToString("X8"));
        if (config.Global != null) Add(config.Global, -1, Loc.T("共通", "Global"));
        return new TriggerTable(map, errors);
    }

    /// <summary>
    /// キー・修飾キー・キーボード（ExtraInfo の上位16bit。0 は不明）から動作を引く。キーボードごとの分 → 共通の分の順。
    /// 修飾キーは、押している分と過不足なく一致したものだけ。
    /// </summary>
    public HelperActionBody? Lookup(ushort scanCode, byte mods, ushort deviceHash16)
    {
        if (!_map.TryGetValue((scanCode, mods), out var list)) return null;
        if (deviceHash16 != 0)
        {
            foreach (var e in list)
                if (e.DeviceHash16 == deviceHash16) return e.Body;
        }
        foreach (var e in list)
            if (e.DeviceHash16 < 0) return e.Body;
        return null;
    }

    public bool HasKey(ushort scanCode) => _map.Keys.Any(k => k.Scan == scanCode);
}

/// <summary>HLP(id) のイベント → 動作。そのキーボードの Actions → 共通の Actions の順に探す。</summary>
public static class HelperActionResolver
{
    public static HelperActionBody? Resolve(HelperConfigSet config, uint uniqId, int id)
    {
        if (config.PerDevice.TryGetValue(uniqId, out var device))
        {
            var a = device.Actions.FirstOrDefault(x => x.Id == id);
            if (a != null) return a.Action;
        }
        return config.Global?.Actions.FirstOrDefault(x => x.Id == id)?.Action;
    }
}

/// <summary>ドライバのヘルパーイベントの時刻（TimeMs）の扱い。</summary>
public static class HelperEventTiming
{
    /// <summary>押下のイベントを、これより古ければ実行しない（ミリ秒）。</summary>
    public const long StaleMs = 5000;

    /// <summary>
    /// イベントが古すぎるか。TimeMs はドライバの時刻（起動からのミリ秒を 32bit に丸めたもの）で、ユーザーモードの
    /// <c>Environment.TickCount64</c>（約15.6ms刻み）より数〜十数 ms 先に進むことがある。そのため、32bit の差を
    /// 符号付きで見る（負 = 新しい）。符号なしで引くと、少し先の時刻が 4294967284 付近に巻き戻って「古い」と判定され、
    /// 押下がほぼ毎回捨てられる。32bit のままなので、起動から 49.7 日を超えて巻き戻っても正しい。
    /// </summary>
    public static bool IsStale(long tickCount64, uint timeMs)
    {
        int age = unchecked((int)((uint)tickCount64 - timeMs));
        return age > StaleMs;
    }
}

public enum LayerOp { On, Off, Toggle, Hold }

public static class LayerOps
{
    public static LayerOp Parse(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "off" => LayerOp.Off,
        "toggle" => LayerOp.Toggle,
        "hold" => LayerOp.Hold,
        _ => LayerOp.On,
    };
}

/// <summary>
/// アプリ管理レイヤーを操作する要求。対象のキーボードは、UniqId が分かれば それ、分からず DeviceHash16（ExtraInfo の上位16bit）が
/// 分かればそのハッシュのキーボード、どちらも分からなければレイヤーマップのある全キーボード。
/// </summary>
public sealed record LayerRequest(uint UniqId, ushort DeviceHash16, IReadOnlyList<int> Layers, LayerOp Op);

/// <summary>
/// 動作1件の実行依頼（キーの押下・オートリピート・解放ごとに1件）。
/// <paramref name="SuppressKeys"/>: Sequence の実行の間だけ OS から隠す、物理的に押されている修飾キー（<see cref="KeycodeTable.ModifierScanCodes"/> の並びのビット。
/// トリガーの修飾キーのうち、押している側）。0 は隠さない。<paramref name="Done"/>: 隠した実行が終わった（または捨てられた）ときに呼ぶ。
/// </summary>
public sealed record ActionRequest(HelperActionBody Body, uint UniqId, ushort DeviceHash16, bool IsBreak, bool IsRepeat,
    byte SuppressKeys = 0, Action? Done = null);

/// <summary>動作の実行が頼る外の世界（実際の入力・起動・一時停止・レイヤー）。</summary>
public interface IActionEnvironment
{
    void Send(InputOp op);
    /// <summary>待つ。止めるよう頼まれていたら false（手順を打ち切る）。</summary>
    bool Sleep(int milliseconds);
    void Shell(string path, string? args, string? workDir);
    void TogglePause();
    void Layer(LayerRequest request);
    void Log(string message);
    /// <summary>
    /// 前面のアプリの IME を <paramref name="target"/> にする（今の状態を見て、違うときだけ変える）。<paramref name="method"/> が Auto 以外なら、
    /// その方式だけを使う。変えられなかった（状態を読めない・反映されない等）ときは false（理由は <see cref="Log"/> に書く）。
    /// </summary>
    bool SetIme(ImeTarget target, ImeMethod method) => true;
    /// <summary>いま物理的に押されている修飾キー（<see cref="KeycodeTable.ModifierScanCodes"/> の並びのビット）。隠した修飾キーを戻すかの判断に使う。</summary>
    byte PhysicalKeys() => 0;
}

/// <summary>動作を実行する。1つのスレッドから順に呼ぶこと。</summary>
public sealed class ActionExecutor
{
    private readonly IActionEnvironment _env;

    public ActionExecutor(IActionEnvironment env) => _env = env;

    /// <summary>
    /// キーを離したときに、実行することがあるか（無ければ、解放を実行の列に積まない）。
    /// <see cref="HelperActionBody.ReleaseSteps"/> のある Sequence と、Hold の AppLayer だけ。
    /// </summary>
    public static bool NeedsRelease(HelperActionBody body) => body.Type switch
    {
        HelperActionBody.Sequence => body.ReleaseSteps is { Count: > 0 },
        HelperActionBody.AppLayer => LayerOps.Parse(body.LayerOp) == LayerOp.Hold,
        _ => false,
    };

    public void Execute(ActionRequest r)
    {
        var body = r.Body;
        if (r.IsRepeat && !body.Repeat) return; // オートリピートは、Repeat を選んだ動作だけ

        switch (body.Type)
        {
            case HelperActionBody.Launch:
            case HelperActionBody.Open:
                if (r.IsBreak) return;
                if (string.IsNullOrWhiteSpace(body.Path)) { _env.Log(Loc.T($"{body.Type}: Path が空です", $"{body.Type}: Path is empty")); return; }
                _env.Shell(Environment.ExpandEnvironmentVariables(body.Path),
                           body.Type == HelperActionBody.Launch ? Expand(body.Args) : null, Expand(body.WorkDir));
                break;

            case HelperActionBody.Sequence:
                if (!r.IsBreak && r.SuppressKeys != 0) RunSuppressed(r, body.Steps);
                else RunSteps(r.IsBreak ? body.ReleaseSteps : body.Steps);
                break;

            case HelperActionBody.TextInput:
            {
                if (r.IsBreak) return;
                var steps = TextSteps(body);
                var wrap = ImeWrapOf(body);
                if (r.SuppressKeys != 0) RunSuppressed(r, steps, wrap);
                else RunSteps(steps, wrap);
                break;
            }

            case HelperActionBody.AppLayer:
            {
                var op = LayerOps.Parse(body.LayerOp);
                var layers = (body.Layers ?? new List<int>()).Where(l => l is >= 0 and < 16).Distinct().ToList();
                if (layers.Count == 0)
                {
                    if (!r.IsBreak) _env.Log(Loc.T("AppLayer: Layers が空です", "AppLayer: Layers is empty"));
                    return;
                }
                if (op == LayerOp.Hold)
                {
                    if (r.IsRepeat) return;
                    _env.Layer(new LayerRequest(r.UniqId, r.DeviceHash16, layers, r.IsBreak ? LayerOp.Off : LayerOp.On));
                }
                else if (!r.IsBreak)
                {
                    _env.Layer(new LayerRequest(r.UniqId, r.DeviceHash16, layers, op));
                }
                break;
            }

            case HelperActionBody.PauseToggle:
                if (!r.IsBreak) _env.TogglePause();
                break;

            default:
                if (!r.IsBreak) _env.Log(Loc.T($"未対応の動作の種類: '{body.Type}'", $"Unsupported action type: '{body.Type}'"));
                break;
        }
    }

    /// <summary>TextInput の文字列を、1つの text 手順にする（空なら null）。</summary>
    private static List<HelperStep>? TextSteps(HelperActionBody body) =>
        string.IsNullOrEmpty(body.Text) ? null : new List<HelperStep> { new() { Text = body.Text } };

    /// <summary>Text の「入力の間だけ IME を OFF」の指定（<see cref="HelperActionBody.ImeOffDuring"/>）。</summary>
    private sealed record ImeWrap(ImeMethod Method, bool Abort);

    private ImeWrap? ImeWrapOf(HelperActionBody body)
    {
        if (!body.ImeOffDuring) return null;
        if (!ImeNames.TryParseMethod(body.ImeMethod, out var method))
        {
            _env.Log(Loc.T($"Text: ImeMethod '{body.ImeMethod}' を解決できないため、Auto にします", $"Text: Cannot resolve ImeMethod '{body.ImeMethod}', using Auto"));
            method = ImeMethod.Auto;
        }
        if (!ImeNames.TryParseOnFail(body.ImeOnFail, out bool abort))
        {
            _env.Log(Loc.T($"Text: ImeOnFail '{body.ImeOnFail}' を解決できないため、Continue にします", $"Text: Cannot resolve ImeOnFail '{body.ImeOnFail}', using Continue"));
            abort = false;
        }
        return new ImeWrap(method, abort);
    }

    private static string? Expand(string? s) => string.IsNullOrEmpty(s) ? null : Environment.ExpandEnvironmentVariables(s);

    // Alt・Win を離す／戻すと、単独で押して離したことになり、メニューやスタートが開く。間に入れるキー（F24。QMK の DUMMY_MOD_NEUTRALIZER と同じ考え）。
    private const ushort NeutralizerScanCode = 0x0076;
    private const byte AltWinKeys = 0xCC; // ModifierScanCodes の LALT(2)・LGUI(3)・RALT(6)・RGUI(7)

    /// <summary>
    /// 手順の間だけ、トリガーの修飾キー（物理的に押している分）を OS から隠す。終わったら、まだ押している分だけ戻す。
    /// 手順の中で例外や打ち切りがあっても、必ず戻す。
    /// </summary>
    private void RunSuppressed(ActionRequest r, List<HelperStep>? steps, ImeWrap? wrap = null)
    {
        byte keys = r.SuppressKeys;
        try
        {
            NeutralizeIfNeeded(keys);
            SendMods(keys, up: true);
            RunSteps(steps, wrap);
        }
        finally
        {
            byte restore = (byte)(keys & _env.PhysicalKeys());
            SendMods(restore, up: false);
            NeutralizeIfNeeded(restore);
            r.Done?.Invoke();
        }
    }

    private void SendMods(byte keys, bool up)
    {
        for (int bit = 0; bit < KeycodeTable.ModifierScanCodes.Length; bit++)
            if ((keys & (1 << bit)) != 0)
                _env.Send(new InputOp(InputKind.Key, KeycodeTable.ModifierScanCodes[bit], Up: up));
    }

    private void NeutralizeIfNeeded(byte keys)
    {
        if ((keys & AltWinKeys) == 0) return;
        _env.Send(new InputOp(InputKind.Key, NeutralizerScanCode));
        _env.Send(new InputOp(InputKind.Key, NeutralizerScanCode, Up: true));
    }

    private void RunSteps(List<HelperStep>? steps, ImeWrap? wrap = null)
    {
        if (steps == null || steps.Count == 0) return;
        var compiled = SequenceCompiler.Compile(steps);
        foreach (var e in compiled.Errors) _env.Log("Sequence: " + e);
        bool restore = false;
        try
        {
            if (wrap != null)
            {
                bool ok = _env.SetIme(ImeTarget.OffRemember, wrap.Method);
                restore = true; // 戻せるものが無ければ、環境側が何もしない
                if (!ok && wrap.Abort)
                {
                    _env.Log(Loc.T("Text: IME を OFF にできなかったため、入力をやめました", "Text: Could not turn the IME off, so the input was cancelled"));
                    return;
                }
            }
            foreach (var op in compiled.Ops)
            {
                if (op.Kind == InputKind.Delay)
                {
                    if (!_env.Sleep(op.Value)) return;
                }
                else if (op.Kind == InputKind.Ime)
                {
                    if (!_env.SetIme((ImeTarget)op.Value, (ImeMethod)op.Value2) && op.Flag)
                    {
                        _env.Log(Loc.T("Sequence: IME を切り替えられなかったため、残りをやめました", "Sequence: Could not switch the IME, so the rest was cancelled"));
                        return;
                    }
                }
                else _env.Send(op);
            }
        }
        finally
        {
            if (restore) _env.SetIme(ImeTarget.Restore, wrap!.Method);
        }
    }
}
