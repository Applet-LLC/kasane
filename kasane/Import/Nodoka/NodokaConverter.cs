using System.Text;
using Kasane.Interop;
using Kasane.Keycodes;
using Kasane.Model;
using Kasane.Model.Portable;

namespace Kasane.Import.Nodoka;

public enum ConvLevel { Converted, Approximated, Skipped, Info, Error }

public sealed record ConvLine(ConvLevel Level, SourceRef? Src, string Text)
{
    public override string ToString() => (Src != null ? Src + ": " : "") + Text;
}

/// <summary>変換の結果: kasane の交換ファイル（<see cref="File"/>）と、変換できたもの・近似・変換できないものの報告。</summary>
public sealed class NodokaConversion
{
    public PortableFile File { get; init; } = new();
    public List<ConvLine> Lines { get; } = new();
    public int Count(ConvLevel level) => Lines.Count(l => l.Level == level);
}

/// <summary>
/// のどかの設定（<see cref="NodokaSetting"/>）を、kasane の交換モデル（<see cref="PortableKeyboard"/>）とヘルパー設定に変換する。
/// 変換の考え方は docs/IMPORT-NODOKA.md。のどかの規則は「キー × 修飾キーの状態 × キーマップ」の表なので、kasane の
/// レイヤー（キー → 動作）に平らにする: Global はレイヤー0、M0〜M9・L0〜LF・Prefix・window のキーマップは別のレイヤー。
/// 変換できないものは、ファイル名:行 つきで報告して飛ばす。
/// </summary>
public static class NodokaConverter
{
    public static NodokaConversion Convert(NodokaSetting setting, string sourceLabel)
    {
        var lines = new List<ConvLine>();
        foreach (var d in setting.Diagnostics)
        {
            lines.Add(d.Level switch
            {
                DiagLevel.Error => new ConvLine(ConvLevel.Skipped, d.Src, "読めない行: " + d.Text),
                DiagLevel.Warning => new ConvLine(ConvLevel.Info, d.Src, d.Text),
                _ => new ConvLine(ConvLevel.Info, d.Src, d.Text),
            });
        }

        var file = PortableExporter.NewFile(PortableScope.Keyboard);
        file.App = "nodoka import: " + sourceLabel;

        // キーボードごとの規則（K1- 〜 K7-）があるものは、UnitID の ExtraInfo をキーボードの UniqId として別に作る。
        var deviceNumbers = new SortedSet<int>();
        foreach (var n in NodokaRuleScan.DeviceNumbers(setting)) deviceNumbers.Add(n);

        var generic = new TargetBuilder(setting, null, primary: true, lines);
        file.Keyboards.Add(generic.Build(null));

        foreach (int n in deviceNumbers)
        {
            var unit = setting.UnitIds.LastOrDefault(u => u.KeyNumber == n);
            if (unit == null)
            {
                lines.Add(new ConvLine(ConvLevel.Skipped, null,
                    $"K{n}- の条件がありますが、def option UnitID = K{n} ExtraInfo … が無いため、そのキーボードの設定は変換しません"));
                continue;
            }
            var builder = new TargetBuilder(setting, n, primary: false, lines);
            file.Keyboards.Add(builder.Build(unit.ExtraInfo));
        }
        if (file.Keyboards.Count > 1) file.Scope = nameof(PortableScope.All);

        var conversion = new NodokaConversion { File = file };
        // 同じ行が、キーマップごとに（親の割り当てを引き継ぐため）何度も出ることがある。件数と一覧が合うよう、重複を除く。
        conversion.Lines.AddRange(lines.Distinct());
        return conversion;
    }
}

/// <summary>どのキーボード（K1〜K7）向けの規則があるかを調べる。</summary>
internal static class NodokaRuleScan
{
    public static IEnumerable<int> DeviceNumbers(NodokaSetting s)
    {
        var found = new HashSet<int>();
        void Scan(NodokaModifier m)
        {
            for (int n = 1; n <= 7; n++)
                if (m.RequiresPressed(ModType.Keyboard0 + n)) found.Add(n);
        }
        foreach (var km in s.Keymaps)
        {
            foreach (var a in km.Assignments) Scan(a.Mod);
            foreach (var c in km.Combos) Scan(c.Mod);
            foreach (var t in km.TapHolds) Scan(t.Mod);
            foreach (var t in km.TapDances) Scan(t.Mod);
        }
        return found;
    }
}

internal enum PlanKind { Key, Transparent, None, LayerOp, Macro, Helper, Nop, Unsupported }

/// <summary>キーシーケンス（右辺）を、kasane の何にするか。</summary>
internal sealed class Plan
{
    public PlanKind Kind { get; init; }
    /// <summary>Key / LayerOp のトークン（KC_B、LCTL(KC_C)、MO(2) など）。</summary>
    public string Token { get; init; } = "";
    /// <summary>Macro の1タップずつのキー。</summary>
    public List<string> Keys { get; init; } = new();
    public HelperActionBody? Body { get; init; }
    public string Reason { get; init; } = "";
    /// <summary>のどかとの違い（近似した点）。</summary>
    public List<string> Notes { get; } = new();

    public static Plan Unsupported(string reason) => new() { Kind = PlanKind.Unsupported, Reason = reason };
}

internal sealed class TargetBuilder
{
    private readonly NodokaSetting _s;
    private readonly int? _device;
    private readonly bool _primary;
    private readonly List<ConvLine> _out;

    private readonly Dictionary<string, int> _ctx = new(StringComparer.OrdinalIgnoreCase) { ["Global"] = 0 };
    private readonly SortedDictionary<int, Dictionary<ushort, string>> _layers = new() { [0] = new() };
    private readonly List<MacroDef> _macros = new();
    private readonly List<TapDanceDef> _tapDances = new();
    private readonly List<PortableCombo> _combos = new();
    private readonly List<KeyOverrideDef> _overrides = new();
    private readonly HelperConfig _helper = new();
    private readonly Dictionary<string, List<SourceRef>> _approx = new();
    private readonly HashSet<string> _reportedOnce = new();
    private readonly List<(ushort Sc, byte Mods, Plan Plan, SourceRef Src)> _pending = new();

    public TargetBuilder(NodokaSetting s, int? device, bool primary, List<ConvLine> output)
    {
        _s = s;
        _device = device;
        _primary = primary;
        _out = output;
    }

    // ------------------------------------------------------------------
    // 報告
    // ------------------------------------------------------------------

    private string Prefix => _device == null ? "" : $"[K{_device}] ";

    private void Say(ConvLevel level, SourceRef? src, string text, bool deviceSpecific = false)
    {
        if (!_primary && !deviceSpecific) return;
        _out.Add(new ConvLine(level, src, Prefix + text));
    }

    private void Approx(string category, SourceRef src, bool deviceSpecific = false)
    {
        if (!_primary && !deviceSpecific) return;
        if (!_approx.TryGetValue(category, out var list)) _approx[category] = list = new List<SourceRef>();
        list.Add(src);
    }

    private void FlushApprox()
    {
        foreach (var (category, refs) in _approx)
        {
            string where = string.Join(", ", refs.Take(6).Select(r => r.ToString())) + (refs.Count > 6 ? $" ほか{refs.Count - 6}件" : "");
            _out.Add(new ConvLine(ConvLevel.Approximated, refs[0], $"{Prefix}{category}({refs.Count}件: {where})"));
        }
    }

    // ------------------------------------------------------------------
    // 修飾キーの条件
    // ------------------------------------------------------------------

    private sealed record Left(byte Mods, bool AnyMods, int? Device, string? Context, string? Unsupported);

    private static readonly (ModType Type, byte Mask)[] BasicMods =
    {
        (ModType.Control, 0x01), (ModType.Shift, 0x02), (ModType.Alt, 0x04), (ModType.Windows, 0x08),
    };

    private Left AnalyzeLeft(NodokaModifier m)
    {
        byte mods = 0;
        bool any = false;
        foreach (var (type, mask) in BasicMods)
        {
            if (m.RequiresPressed(type)) mods |= mask;
            else if (m.IsDontcare(type)) any = true;
        }

        int? device = null;
        string? context = null;
        string? unsupported = null;
        for (int t = (int)ModType.Up; t < (int)ModType.End; t++)
        {
            var type = (ModType)t;
            if (!m.RequiresPressed(type)) continue;
            if (type >= ModType.Keyboard0 && type <= ModType.Keyboard7)
            {
                int n = type - ModType.Keyboard0;
                if (n == 0) unsupported ??= "K0-(キーボードを特定できないとき)";
                else if (device != null && device != n) unsupported ??= "複数のキーボード(K1- K2- …)の同時指定";
                else device = n;
            }
            else if ((type >= ModType.Mod0 && type <= ModType.Mod9) || (type >= ModType.Lock0 && type <= ModType.LockF))
            {
                string name = type <= ModType.Mod9 ? $"M{type - ModType.Mod0}" : $"L{type - ModType.Lock0:X}";
                if (context != null && context != name) unsupported ??= $"{context}- と {name}- の同時指定";
                else context = name;
            }
            else
            {
                unsupported ??= type switch
                {
                    ModType.Up or ModType.Down => "U-/D-(キーを離したとき・押したときだけ)",
                    ModType.Repeat => "R-(キーリピートのとき)",
                    ModType.ImeLock => "IL-(IME オン)",
                    ModType.ImeComp => "IC-(IME 変換中)",
                    ModType.NumLock or ModType.CapsLock or ModType.ScrollLock or ModType.KanaLock => "NL-/CL-/SL-/KL-(ロックキーの状態)",
                    ModType.Maximized or ModType.Minimized or ModType.MdiMaximized or ModType.MdiMinimized => "MAX-/MIN-/MMAX-/MMIN-(ウィンドウの状態)",
                    ModType.DP => "DP-(2回押し)",
                    ModType.ImeCandi or ModType.Harf or ModType.Katakana or ModType.Native => "IW-/IH-/IK-/IJ-(IME の状態)",
                    _ => "T-/TS-/TL-/TR- など(タッチパッド)",
                };
            }
        }
        return new Left(mods, any, device, context, unsupported);
    }

    private bool AppliesToTarget(Left left) => left.Device == null || left.Device == _device;

    // ------------------------------------------------------------------
    // キー
    // ------------------------------------------------------------------

    private ushort? PhysicalKey(string name, out string? error)
    {
        error = null;
        var key = _s.FindKey(name);
        if (key == null) { error = $"キー「{name}」が定義されていません"; return null; }
        if (key.Enc16 is ushort sc) return sc;
        error = key.ScanCodes.Any(c => (c.Flags & 2) != 0)
            ? $"キー「{name}」はマウス・ゲームパッドなどの疑似キーのため、キーボードのキーとして扱えません"
            : $"キー「{name}」はスキャンコードを1つだけ持つキーではないため、扱えません";
        return null;
    }

    private static byte MaskOf(NodokaModifier m)
    {
        byte mask = 0;
        foreach (var (type, bit) in BasicMods) if (m.RequiresPressed(type)) mask |= bit;
        return mask;
    }

    private static string WithMods(byte mask, string keyToken) => mask == 0 ? keyToken : KeycodeTable.WrapWithMods(mask, keyToken);

    // ------------------------------------------------------------------
    // 右辺 → Plan
    // ------------------------------------------------------------------

    private static void Flatten(KeySeqDef seq, List<SeqItem> items, int depth = 0)
    {
        if (depth > 16) return;
        foreach (var item in seq.Items)
        {
            if (item is SeqNested n) Flatten(n.Seq, items, depth + 1);
            else items.Add(item);
        }
    }

    private static readonly HashSet<string> TransparentFuncs = new(StringComparer.OrdinalIgnoreCase)
        { "Default", "KeymapParent", "KeymapWindow", "OtherWindowClass" };

    private Plan MakePlan(KeySeqDef seq)
    {
        var items = new List<SeqItem>();
        Flatten(seq, items);
        items.RemoveAll(i => i is SeqFunc f && f.Name.Equals("Sync", StringComparison.OrdinalIgnoreCase));
        if (items.Count == 0) return new Plan { Kind = PlanKind.Nop };

        if (items.Count == 1 && items[0] is SeqFunc single)
        {
            if (TransparentFuncs.Contains(single.Name)) return new Plan { Kind = PlanKind.Transparent };
            if (single.Name.Equals("Ignore", StringComparison.OrdinalIgnoreCase) || single.Name.Equals("Undefined", StringComparison.OrdinalIgnoreCase))
                return new Plan { Kind = PlanKind.None };
            var layerOp = LayerFunction(single);
            if (layerOp != null) return layerOp;
        }

        if (items.All(i => i is SeqKey))
        {
            var tokens = new List<string>();
            foreach (var k in items.Cast<SeqKey>())
            {
                if (k.Mod.RequiresPressed(ModType.Up) || k.Mod.RequiresPressed(ModType.Down))
                    return Plan.Unsupported("U-/D-(キーを離す・押すだけ)の指定");
                ushort? sc = PhysicalKey(k.KeyName, out string? err);
                if (sc == null) return Plan.Unsupported(err!);
                tokens.Add(WithMods(MaskOf(k.Mod), KeycodeTable.KeyToken(sc.Value)));
            }
            if (tokens.Count == 1) return new Plan { Kind = PlanKind.Key, Token = tokens[0] };
            if (MacroStepCount(tokens) <= KbdLayerClient.MaxMacroSteps)
            {
                var p = new Plan { Kind = PlanKind.Macro, Keys = tokens };
                p.Notes.Add("複数のキーの列は、最後のキーを押している間だけ押し続ける、という動きにはなりません(押したときに、全部のキーを順に打ちます)");
                return p;
            }
        }
        return BuildSequence(items);
    }

    private static int MacroStepCount(List<string> tokens)
    {
        int steps = 0;
        foreach (var t in tokens)
        {
            if (!KeycodeTable.TryResolve(t, out var a)) return int.MaxValue;
            int mods = a.Type == ActionType.ModdedKey ? System.Numerics.BitOperations.PopCount(a.Param1) : 0;
            steps += 2 + 2 * mods;
        }
        return steps;
    }

    private Plan? LayerFunction(SeqFunc f)
    {
        string name = f.Name;
        var args = f.Args ?? new List<List<Token>>();
        if (name.Equals("Prefix", StringComparison.OrdinalIgnoreCase))
        {
            string? km = args.Count > 0 && args[0].Count == 1 ? args[0][0].Text : null;
            if (km == null) return Plan.Unsupported("&Prefix の引数が読めません");
            var target = _s.FindKeymap(km);
            if (target == null) return Plan.Unsupported($"&Prefix のキーマップ「{km}」がありません");
            int? layer = Alloc("km:" + target.Name, $"キーマップ {target.Name}");
            if (layer == null) return Plan.Unsupported("レイヤーが足りません(16枚まで)");
            var p = new Plan { Kind = PlanKind.LayerOp, Token = $"OSL({layer})" };
            p.Notes.Add("&Prefix は、次の1キーだけを別のキーマップで解釈します(OSL に置き換えました)");
            return p;
        }
        if (name.Equals("Toggle", StringComparison.OrdinalIgnoreCase))
        {
            string? lockName = args.Count > 0 && args[0].Count == 1 ? args[0][0].Text.ToLowerInvariant() : null;
            if (lockName == null || !lockName.StartsWith("lock") || lockName.Length != 5 || !Uri.IsHexDigit(lockName[4]))
                return Plan.Unsupported("&Toggle のロック名が読めません");
            string mode = args.Count > 1 && args[1].Count == 1 ? args[1][0].Text.ToLowerInvariant() : "toggle";
            int? layer = Alloc("L" + char.ToUpperInvariant(lockName[4]), $"ロック {lockName}");
            if (layer == null) return Plan.Unsupported("レイヤーが足りません(16枚まで)");
            if (mode == "toggle") return new Plan { Kind = PlanKind.LayerOp, Token = $"TG({layer})" };
            if (mode is "on" or "off")
            {
                var body = new HelperActionBody
                {
                    Type = HelperActionBody.AppLayer, Layers = new List<int> { layer.Value }, LayerOp = mode == "on" ? "On" : "Off",
                };
                return new Plan { Kind = PlanKind.Helper, Body = body };
            }
            return Plan.Unsupported($"&Toggle の指定「{mode}」が読めません");
        }
        if (name.Equals("EditNextModifier", StringComparison.OrdinalIgnoreCase))
        {
            byte mask = 0;
            foreach (var t in args.SelectMany(a => a))
            {
                string p = t.Text.ToUpperInvariant();
                if (p is "C-") mask |= 0x01;
                else if (p is "S-") mask |= 0x02;
                else if (p is "A-" or "M-") mask |= 0x04;
                else if (p is "W-") mask |= 0x08;
                else return Plan.Unsupported($"&EditNextModifier の修飾子「{t.Text}」は対応しません");
            }
            if (mask == 0) return Plan.Unsupported("&EditNextModifier の修飾子がありません");
            return new Plan { Kind = PlanKind.LayerOp, Token = $"OSM({KeycodeTable.ModMaskToNames(mask)})" };
        }
        return null;
    }

    private Plan BuildSequence(List<SeqItem> items)
    {
        var steps = new List<HelperStep>();
        HelperActionBody? launch = null;
        var notes = new List<string>();
        foreach (var item in items)
        {
            if (item is SeqKey k)
            {
                ushort? sc = PhysicalKey(k.KeyName, out string? err);
                if (sc == null) return Plan.Unsupported(err!);
                string token = WithMods(MaskOf(k.Mod), KeycodeTable.KeyToken(sc.Value));
                if (k.Mod.RequiresPressed(ModType.Down) && !k.Mod.RequiresPressed(ModType.Up)) steps.Add(new HelperStep { Down = token });
                else if (k.Mod.RequiresPressed(ModType.Up) && !k.Mod.RequiresPressed(ModType.Down)) steps.Add(new HelperStep { Up = token });
                else steps.Add(new HelperStep { Key = token });
                continue;
            }
            var f = (SeqFunc)item;
            var args = f.Args ?? new List<List<Token>>();
            switch (f.Name.ToLowerInvariant())
            {
                case "wait":
                {
                    if (!TryInt(args, 0, out int ms)) return Plan.Unsupported("&Wait の時間が読めません");
                    steps.Add(new HelperStep { DelayMs = Math.Clamp(ms, 0, 10000) });
                    break;
                }
                case "sendtext":
                {
                    if (!TryText(args, 0, out string text)) return Plan.Unsupported("&SendText の文字列が読めません($ で始まる組み込みの値は対応しません)");
                    steps.Add(new HelperStep { Text = text });
                    break;
                }
                case "mousemove":
                {
                    if (!TryInt(args, 0, out int dx) || !TryInt(args, 1, out int dy)) return Plan.Unsupported("&MouseMove の引数が読めません");
                    steps.Add(new HelperStep { MoveX = dx, MoveY = dy });
                    break;
                }
                case "mousewheel":
                {
                    if (!TryInt(args, 0, out int delta)) return Plan.Unsupported("&MouseWheel の引数が読めません");
                    steps.Add(new HelperStep { Wheel = delta });
                    break;
                }
                case "vk":
                {
                    var vk = VkStep(args, out string? vkErr);
                    if (vk == null) return Plan.Unsupported(vkErr!);
                    steps.Add(vk);
                    break;
                }
                case "shellexecute":
                {
                    if (!TryText(args, 0, out string op) || !TryText(args, 1, out string file) || !TryText(args, 2, out string prm) || !TryText(args, 3, out string dir))
                        return Plan.Unsupported("&ShellExecute の引数が読めません");
                    if (op.Length > 0 && !op.Equals("open", StringComparison.OrdinalIgnoreCase))
                        notes.Add($"&ShellExecute の操作「{op}」は、通常の起動(open)になります");
                    launch = new HelperActionBody
                    {
                        Type = HelperActionBody.Launch, Path = file,
                        Args = prm.Length > 0 ? prm : null, WorkDir = dir.Length > 0 ? dir : null,
                    };
                    break;
                }
                default:
                    if (LayerFunction(f) != null || TransparentFuncs.Contains(f.Name) || f.Name.Equals("Ignore", StringComparison.OrdinalIgnoreCase))
                        return Plan.Unsupported($"&{f.Name} を、ほかの動作と並べた列には変換できません");
                    return Plan.Unsupported($"&{f.Name} は対応しません");
            }
        }

        HelperActionBody body;
        if (launch != null)
        {
            if (steps.Count > 0) return Plan.Unsupported("&ShellExecute を、キーなどと並べた列には変換できません");
            body = launch;
        }
        else
        {
            body = new HelperActionBody { Type = HelperActionBody.Sequence, Steps = steps };
        }
        var errors = new List<string>();
        HelperSettingsModel.ValidateBody(body, "のどか", errors);
        if (errors.Count > 0) return Plan.Unsupported("ヘルパー動作にできません: " + string.Join(" / ", errors));
        var plan = new Plan { Kind = PlanKind.Helper, Body = body };
        plan.Notes.AddRange(notes);
        return plan;
    }

    private HelperStep? VkStep(List<List<Token>> args, out string? error)
    {
        error = null;
        if (args.Count == 0 || args[0].Count == 0) { error = "&VK の引数が読めません"; return null; }
        string? name = null;
        long? number = null;
        bool up = false, down = false;
        foreach (var t in args[0])
        {
            if (t.Is("E-")) continue;
            if (t.Is("U-")) { up = true; continue; }
            if (t.Is("D-")) { down = true; continue; }
            if (t.IsNumber) number = t.Number;
            else name = t.Text;
        }
        NodokaVKeys.Target? target = null;
        if (name != null) target = NodokaVKeys.Find(name);
        if (target == null && number is long n)
        {
            // 0x30〜0x39 / 0x41〜0x5A は文字キー。ほかの数値は対応しない。
            if (n >= 0x41 && n <= 0x5A) target = NodokaVKeys.Find(((char)n).ToString());
            else if (n >= 0x30 && n <= 0x39) target = NodokaVKeys.Find("_" + (char)n);
            else if (n >= 0x70 && n <= 0x87) target = NodokaVKeys.Find("F" + (n - 0x70 + 1));
        }
        if (target == null) { error = $"&VK({name ?? number?.ToString()}) の仮想キーを、キーボードのキーに対応付けられません"; return null; }
        if (target.MouseButton != null)
            return new HelperStep { Mouse = target.MouseButton, MouseAction = up && !down ? "Up" : down && !up ? "Down" : "Click" };
        string key = KeycodeTable.KeyToken(target.ScanCode!.Value);
        return down && !up ? new HelperStep { Down = key } : up && !down ? new HelperStep { Up = key } : new HelperStep { Key = key };
    }

    private static bool TryInt(List<List<Token>> args, int i, out int value)
    {
        value = 0;
        if (i >= args.Count || args[i].Count != 1 || !args[i][0].IsNumber) return false;
        value = (int)args[i][0].Number;
        return true;
    }

    private static bool TryText(List<List<Token>> args, int i, out string text)
    {
        text = "";
        if (i >= args.Count) return false;
        var a = args[i];
        if (a.Count == 0) return true;
        if (a.Count == 1 && (a[0].IsString || a[0].IsNumber)) { text = a[0].Text; return true; }
        return false;
    }

    // ------------------------------------------------------------------
    // レイヤーの割り当て
    // ------------------------------------------------------------------

    private int? Alloc(string key, string label)
    {
        if (_ctx.TryGetValue(key, out int existing)) return existing;
        if (_ctx.Count >= KbdLayerClient.MaxLayers)
        {
            if (_reportedOnce.Add("layers-full"))
                Say(ConvLevel.Skipped, null, $"レイヤーが{KbdLayerClient.MaxLayers}枚を超えるため、{label} 以降は変換しません");
            return null;
        }
        int index = _ctx.Count;
        _ctx[key] = index;
        _layers[index] = new Dictionary<ushort, string>();
        return index;
    }

    private string HelperToken(HelperActionBody body, string name)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(body);
        var same = _helper.Actions.FirstOrDefault(a => System.Text.Json.JsonSerializer.Serialize(a.Action) == json);
        if (same != null) return $"HLP({same.Id})";
        int id = _helper.Actions.Count + 1;
        _helper.Actions.Add(new HelperAction { Id = id, Name = name, Action = body });
        return $"HLP({id})";
    }

    private string MacroToken(List<string> keys)
    {
        var same = _macros.FirstOrDefault(m => m.Keys.SequenceEqual(keys));
        if (same != null) return $"MACRO({same.Id})";
        if (_macros.Count >= KbdLayerClient.MaxMacroCount) return "";
        int id = _macros.Count;
        _macros.Add(new MacroDef { Id = id, Keys = new List<string>(keys) });
        return $"MACRO({id})";
    }

    /// <summary>Plan を、レイヤーに置くトークンにする（置けなければ null）。HLP は動作を登録する。</summary>
    private string? TokenOf(Plan plan, SourceRef src, string label)
    {
        switch (plan.Kind)
        {
            case PlanKind.Key:
            case PlanKind.LayerOp:
                return plan.Token;
            case PlanKind.Transparent: return "KC_TRNS";
            case PlanKind.None: return "KC_NO";
            case PlanKind.Macro:
            {
                string t = MacroToken(plan.Keys);
                if (t.Length == 0) { Say(ConvLevel.Skipped, src, $"{label}: マクロが{KbdLayerClient.MaxMacroCount}個を超えるため変換しません"); return null; }
                return t;
            }
            case PlanKind.Helper:
                return HelperToken(plan.Body!, label);
            default:
                return null;
        }
    }

    // ------------------------------------------------------------------
    // 変換の本体
    // ------------------------------------------------------------------

    public PortableKeyboard Build(uint? uniqId)
    {
        var referenced = ReferencedKeymaps();

        // 1. キー割り当て（キーマップの定義順。Global が最初）
        foreach (var km in _s.Keymaps)
        {
            int? baseLayer = KeymapLayer(km, referenced);
            if (baseLayer == null) continue;
            foreach (var a in Effective(km, 0))
                ConvertAssignment(km, baseLayer.Value, a);
        }

        // 2. コンボ・タップホールド・タップダンス・修飾キーの定義は Global のもの
        foreach (var km in _s.Keymaps)
        {
            if (km.Kind != KeymapKind.Global) { ReportUnsupportedRules(km); continue; }
            foreach (var c in km.Combos) ConvertCombo(c);
            foreach (var t in km.TapHolds) ConvertTapHold(t);
            foreach (var t in km.TapDances) ConvertTapDance(t);
            foreach (var e in km.Events) Say(ConvLevel.Skipped, e.Src, $"event {e.Event}: 対応しません");
        }
        foreach (var km in _s.Keymaps)
            foreach (var m in km.ModAssigns)
            {
                if (km.Kind != KeymapKind.Global) { Say(ConvLevel.Skipped, m.Src, $"キーマップ {km.Name} の mod 定義は対応しません"); continue; }
                ConvertModAssign(m);
            }
        foreach (var s in _s.Substitutes) ConvertSubstitute(s);

        // 3. 修飾キー付きの割り当て（レイヤー0 の最終的な出力が決まってから）
        ResolvePending();

        // 4. 前面のアプリ（window キーマップ）
        AddWindowRules();

        FlushApprox();
        var kb = new PortableKeyboard
        {
            UniqId = uniqId is uint u ? PortableKeyboard.FormatUniqId(u) : null,
            LayoutName = null,
            Layers = new List<PortableLayer>(),
            Macros = _macros,
            TapDances = _tapDances,
            Combos = _combos,
            KeyOverrides = _overrides,
        };
        int maxLayer = _layers.Keys.Max();
        for (int i = 0; i <= maxLayer; i++)
        {
            var pl = new PortableLayer { Index = i };
            if (_layers.TryGetValue(i, out var keys))
                foreach (var (sc, token) in keys.OrderBy(k => k.Key)) pl.Keys[PortableExporter.ScName(sc)] = token;
            kb.Layers.Add(pl);
        }
        ApplyTiming(kb);
        if (PortableExporter.HasContent(_helper)) kb.Helper = _helper;
        return kb;
    }

    private HashSet<string> ReferencedKeymaps()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Scan(KeySeqDef seq, int depth)
        {
            if (depth > 16) return;
            foreach (var item in seq.Items)
            {
                if (item is SeqNested n) Scan(n.Seq, depth + 1);
                else if (item is SeqFunc f && f.Name.Equals("Prefix", StringComparison.OrdinalIgnoreCase)
                         && f.Args is { Count: > 0 } && f.Args[0].Count == 1)
                    set.Add(f.Args[0][0].Text);
            }
        }
        foreach (var km in _s.Keymaps)
        {
            foreach (var a in km.Assignments) Scan(a.Seq, 0);
            foreach (var t in km.TapHolds) { if (t.Tap != null) Scan(t.Tap, 0); if (t.Hold != null) Scan(t.Hold, 0); }
        }
        return set;
    }

    private int? KeymapLayer(NodokaKeymap km, HashSet<string> referenced)
    {
        if (km.Kind == KeymapKind.Global) return 0;
        if (!km.IsWindow && !referenced.Contains(km.Name))
        {
            if (km.Assignments.Count > 0)
                Say(ConvLevel.Info, km.Src, $"キーマップ {km.Name} は、&Prefix などから呼ばれていないため変換しません({km.Assignments.Count}件の割り当て)");
            return null;
        }
        if (km.IsWindow && km.Assignments.Count == 0 && km.Default == null) { /* 割り当てが無くても、規則だけは作る */ }
        return Alloc("km:" + km.Name, $"キーマップ {km.Name}");
    }

    /// <summary>親（: Parent）の割り当てを先に並べる。親が Global のものは、透過（レイヤー0 に落ちる）なので足さない。</summary>
    private IEnumerable<Assignment> Effective(NodokaKeymap km, int depth)
    {
        if (km.Parent != null && depth < 8 && _s.FindKeymap(km.Parent) is { Kind: not KeymapKind.Global } parent)
            foreach (var a in Effective(parent, depth + 1)) yield return a;
        foreach (var a in km.Assignments) yield return a;
    }

    private void ReportUnsupportedRules(NodokaKeymap km)
    {
        foreach (var c in km.Combos) Say(ConvLevel.Skipped, c.Src, $"キーマップ {km.Name} の combo は対応しません(Global のみ)");
        foreach (var t in km.TapHolds) Say(ConvLevel.Skipped, t.Src, $"キーマップ {km.Name} の taphold は対応しません(Global のみ)");
        foreach (var t in km.TapDances) Say(ConvLevel.Skipped, t.Src, $"キーマップ {km.Name} の tapdance は対応しません(Global のみ)");
        foreach (var e in km.Events) Say(ConvLevel.Skipped, e.Src, $"event {e.Event}: 対応しません");
    }

    private void ConvertAssignment(NodokaKeymap km, int baseLayer, Assignment a)
    {
        var left = AnalyzeLeft(a.Mod);
        bool deviceSpecific = left.Device != null;
        string label = $"key {a.KeyName}";
        if (left.Unsupported != null)
        {
            if (left.Device == null || AppliesToTarget(left)) Say(ConvLevel.Skipped, a.Src, $"{label}: {left.Unsupported} の条件は対応しません", deviceSpecific);
            return;
        }
        if (!AppliesToTarget(left)) return;

        ushort? sc = PhysicalKey(a.KeyName, out string? keyError);
        if (sc == null) { Say(ConvLevel.Skipped, a.Src, $"{label}: {keyError}", deviceSpecific); return; }

        int layer = baseLayer;
        if (left.Context != null)
        {
            if (km.Kind != KeymapKind.Global)
            {
                Say(ConvLevel.Skipped, a.Src, $"{label}: キーマップ {km.Name} の中の {left.Context}- の条件は対応しません", deviceSpecific);
                return;
            }
            int? l = Alloc(left.Context, $"{left.Context}- の割り当て");
            if (l == null) return;
            layer = l.Value;
        }

        var plan = MakePlan(a.Seq);
        if (plan.Kind == PlanKind.Unsupported) { Say(ConvLevel.Skipped, a.Src, $"{label}: {plan.Reason}", deviceSpecific); return; }
        if (plan.Kind == PlanKind.Nop) { Say(ConvLevel.Info, a.Src, $"{label}: 何もしない列のため変換しません", deviceSpecific); return; }

        if (left.Mods != 0)
        {
            // 修飾キーを押しながらのキー: レイヤーには置けない。キー置換か、トリガー（キーフック）にする。
            if (layer != 0) { Say(ConvLevel.Skipped, a.Src, $"{label}: レイヤー0以外での修飾キー付きの割り当ては対応しません", deviceSpecific); return; }
            _pending.Add((sc.Value, left.Mods, plan, a.Src));
            foreach (var n in plan.Notes) Approx(n, a.Src, deviceSpecific);
            return;
        }

        string? token = TokenOf(plan, a.Src, label);
        if (token == null) return;
        if (plan.Kind == PlanKind.Transparent && layer == 0) { Say(ConvLevel.Converted, a.Src, $"{label}: 何もしない(そのまま通す)", deviceSpecific); return; }

        _layers[layer][sc.Value] = token;
        Say(ConvLevel.Converted, a.Src, $"{label} → レイヤー{layer} {PortableExporter.ScName(sc.Value)} = {token}", deviceSpecific);

        if (!left.AnyMods && plan.Kind is PlanKind.Key or PlanKind.Macro or PlanKind.Helper or PlanKind.LayerOp && layer == 0)
            Approx("修飾キーを押していないときだけ、というのどかの条件は、kasane では見分けません(修飾キーを押しても同じに置き換わります)", a.Src, deviceSpecific);
        foreach (var n in plan.Notes) Approx(n, a.Src, deviceSpecific);
    }

    private void ConvertSubstitute(Substitute sub)
    {
        var left = AnalyzeLeft(sub.Mod);
        if (left.Unsupported != null) { Say(ConvLevel.Skipped, sub.Src, $"def subst {sub.KeyName}: {left.Unsupported} の条件は対応しません"); return; }
        if (left.Context != null || left.Device != null) { Say(ConvLevel.Skipped, sub.Src, $"def subst {sub.KeyName}: 条件つきは対応しません"); return; }
        ushort? from = PhysicalKey(sub.KeyName, out string? e1);
        if (from == null) { Say(ConvLevel.Skipped, sub.Src, $"def subst {sub.KeyName}: {e1}"); return; }
        ushort? to = PhysicalKey(sub.To.KeyName, out string? e2);
        if (to == null) { Say(ConvLevel.Skipped, sub.Src, $"def subst {sub.KeyName}: {e2}"); return; }
        var plan = new Plan { Kind = PlanKind.Key, Token = WithMods(MaskOf(sub.To.Mod), KeycodeTable.KeyToken(to.Value)) };
        if (left.Mods != 0) { _pending.Add((from.Value, left.Mods, plan, sub.Src)); return; }
        _layers[0][from.Value] = plan.Token;
        Say(ConvLevel.Converted, sub.Src, $"def subst {sub.KeyName} → レイヤー0 {PortableExporter.ScName(from.Value)} = {plan.Token}");
    }

    // ------------------------------------------------------------------
    // 修飾キー付きの割り当て → キー置換 / トリガー
    // ------------------------------------------------------------------

    private string Layer0Output(ushort sc) => _layers[0].TryGetValue(sc, out var t) ? t : KeycodeTable.KeyToken(sc);

    private void ResolvePending()
    {
        foreach (var (sc, mods, plan, src) in _pending)
        {
            string outKey = Layer0Output(sc);
            if (!KeycodeTable.TryResolve(outKey, out var outAction) || outAction.Type != ActionType.Key)
            {
                Say(ConvLevel.Skipped, src, $"{PortableExporter.ScName(sc)} に修飾キーを付けた割り当て: このキーのレイヤー0 の動作({outKey})が単純なキーではないため、変換しません");
                continue;
            }
            string modNames = KeycodeTable.ModMaskToNames(mods);
            switch (plan.Kind)
            {
                case PlanKind.Key:
                {
                    if (!KeycodeTable.TryResolve(plan.Token, out var rep) || rep.Type is not (ActionType.Key or ActionType.ModdedKey))
                    {
                        Say(ConvLevel.Skipped, src, $"{modNames}+{outKey}: キー置換にできない動作です");
                        break;
                    }
                    if (_overrides.Count >= KbdLayerClient.MaxKeyOverrideCount)
                    {
                        Say(ConvLevel.Skipped, src, $"キー置換が{KbdLayerClient.MaxKeyOverrideCount}個を超えるため、{modNames}+{outKey} は変換しません");
                        break;
                    }
                    var existing = _overrides.FirstOrDefault(o => o.Mods == modNames && o.Key == outKey);
                    if (existing != null) existing.Replacement = plan.Token;
                    else _overrides.Add(new KeyOverrideDef { Id = _overrides.Count, Mods = modNames, Key = outKey, Replacement = plan.Token });
                    Say(ConvLevel.Converted, src, $"{modNames}+{outKey} → キー置換 {plan.Token}");
                    break;
                }
                case PlanKind.Helper:
                {
                    // 押したキー（と修飾キー）を見張って動かす。
                    _helper.Triggers.RemoveAll(t => t.Key == outKey && t.Mods == modNames);
                    _helper.Triggers.Add(new HelperTrigger { Key = outKey, Mods = modNames, Action = plan.Body! });
                    Say(ConvLevel.Converted, src, $"{modNames}+{outKey} → ヘルパーのトリガー");
                    Approx("修飾キーを押しながらの動作を、ヘルパーのトリガー(キーフック)にしました。押したキーはアプリに届かなくなります。トレイが動いている必要があります", src);
                    break;
                }
                default:
                    Say(ConvLevel.Skipped, src, $"{modNames}+{outKey}: 修飾キーを押しながらの「{DescribeKind(plan.Kind)}」は変換できません");
                    break;
            }
        }
    }

    private static string DescribeKind(PlanKind k) => k switch
    {
        PlanKind.Macro => "複数のキーの列",
        PlanKind.LayerOp => "レイヤーの操作",
        PlanKind.Transparent => "そのまま通す",
        PlanKind.None => "何もしない",
        _ => k.ToString(),
    };

    // ------------------------------------------------------------------
    // mod / combo / taphold / tapdance
    // ------------------------------------------------------------------

    private static readonly (ModType Type, string Mod, ushort Left, ushort Right)[] ModKeys =
    {
        (ModType.Shift, "MOD_LSFT", 0x002A, 0x0036), (ModType.Control, "MOD_LCTL", 0x001D, 0xE01D),
        (ModType.Alt, "MOD_LALT", 0x0038, 0xE038), (ModType.Windows, "MOD_LGUI", 0xE05B, 0xE05C),
    };

    private void ConvertModAssign(ModAssign m)
    {
        if (m.KeyName == null) { Say(ConvLevel.Skipped, m.Src, "mod !修飾キー名 の形(すべてのキーの扱いを変える指定)は対応しません"); return; }
        string label = $"mod {m.Target} {(m.Op == AssignOp.Sub ? "-=" : m.Op == AssignOp.Add ? "+=" : "=")} {m.KeyName}";
        if (m.Op == AssignOp.Sub) { Say(ConvLevel.Skipped, m.Src, $"{label}: 修飾キーから外す指定は対応しません"); return; }
        ushort? sc = PhysicalKey(m.KeyName, out string? err);
        if (sc == null) { Say(ConvLevel.Skipped, m.Src, $"{label}: {err}"); return; }

        bool oneShot = m.Mode is AssignMode.OneShot or AssignMode.OneShotRepeatable or AssignMode.OneShot2;
        string own = Layer0Output(sc.Value);

        if (m.Target >= ModType.Mod0 && m.Target <= ModType.Mod9)
        {
            int? layer = Alloc($"M{m.Target - ModType.Mod0}", $"M{m.Target - ModType.Mod0}- の割り当て");
            if (layer == null) return;
            string token = $"MO({layer})";
            if (oneShot && KeycodeTable.TryResolve(own, out var a) && a.Type == ActionType.Key)
                token = $"LT({layer},{own})";
            else if (oneShot)
                Approx("一回だけ有効になる修飾キー(!!)で、キー自体の動作がキーでないものは、押している間だけ有効なレイヤーキー(MO)にしました", m.Src);
            if (m.Mode == AssignMode.Normal)
                Approx("「!」の付かない M0〜M9 の修飾キーは、のどかではキー自体の動作も残りますが、kasane ではレイヤーキー(MO)として働きます", m.Src);
            _layers[0][sc.Value] = token;
            Say(ConvLevel.Converted, m.Src, $"{label} → レイヤー0 {PortableExporter.ScName(sc.Value)} = {token}");
            return;
        }

        var mk = ModKeys.FirstOrDefault(x => x.Type == m.Target);
        if (mk.Mod == null) { Say(ConvLevel.Skipped, m.Src, $"{label}: 対応しない修飾キーです"); return; }
        if (sc == mk.Left || sc == mk.Right) return; // もともとその修飾キー

        string modKey = KeycodeTable.KeyToken(mk.Left);
        if (!oneShot)
        {
            _layers[0][sc.Value] = modKey;
            Say(ConvLevel.Converted, m.Src, $"{label} → レイヤー0 {PortableExporter.ScName(sc.Value)} = {modKey}");
            return;
        }
        if (KeycodeTable.TryResolve(own, out var tap) && tap.Type == ActionType.Key)
        {
            string token = $"MT({mk.Mod},{own})";
            _layers[0][sc.Value] = token;
            Say(ConvLevel.Converted, m.Src, $"{label} → レイヤー0 {PortableExporter.ScName(sc.Value)} = {token}");
            Approx("一回だけ有効になる修飾キー(!!)は、タップならそのキー、押し続けると修飾キー(MT)にしました", m.Src);
        }
        else
        {
            _layers[0][sc.Value] = modKey;
            Say(ConvLevel.Converted, m.Src, $"{label} → レイヤー0 {PortableExporter.ScName(sc.Value)} = {modKey}");
            Approx("一回だけ有効になる修飾キー(!!)で、キー自体の動作がキーでないものは、常に修飾キーにしました", m.Src);
        }
    }

    private bool ComboApplies(NodokaModifier mod, out string? unsupported)
    {
        var left = AnalyzeLeft(mod);
        unsupported = left.Unsupported ?? (left.Context != null ? $"{left.Context}-" : null);
        return unsupported == null && AppliesToTarget(left);
    }

    private void ConvertCombo(ComboRule c)
    {
        if (!ComboApplies(c.Mod, out string? un))
        {
            if (un != null) Say(ConvLevel.Skipped, c.Src, $"combo: {un} の条件は対応しません");
            return;
        }
        if (c.Keys.Count != 2) { Say(ConvLevel.Skipped, c.Src, $"combo: {c.Keys.Count}キーの同時押しは対応しません(2キーのみ)"); return; }
        ushort? a = PhysicalKey(c.Keys[0], out string? e1), b = PhysicalKey(c.Keys[1], out string? e2);
        if (a == null || b == null) { Say(ConvLevel.Skipped, c.Src, $"combo: {e1 ?? e2}"); return; }
        var plan = MakePlan(c.Action);
        if (plan.Kind is not (PlanKind.Key or PlanKind.Macro))
        {
            Say(ConvLevel.Skipped, c.Src, $"combo: 出力は、キーかキーの列だけに対応します({(plan.Kind == PlanKind.Unsupported ? plan.Reason : DescribeKind(plan.Kind))})");
            return;
        }
        string? output = TokenOf(plan, c.Src, "combo");
        if (output == null) return;
        if (_combos.Count >= KbdLayerClient.MaxComboCount) { Say(ConvLevel.Skipped, c.Src, $"コンボが{KbdLayerClient.MaxComboCount}個を超えるため変換しません"); return; }
        if (_combos.Any(x => x.Key1 == PortableExporter.ScName(a.Value) || x.Key2 == PortableExporter.ScName(a.Value)
                             || x.Key1 == PortableExporter.ScName(b.Value) || x.Key2 == PortableExporter.ScName(b.Value)))
        {
            Say(ConvLevel.Skipped, c.Src, "combo: メンバーのキーが、ほかのコンボですでに使われています(kasane では1つのキーは1つのコンボにだけ入れられます)");
            return;
        }
        _combos.Add(new PortableCombo { Id = _combos.Count, Key1 = PortableExporter.ScName(a.Value), Key2 = PortableExporter.ScName(b.Value), Output = output });
        Say(ConvLevel.Converted, c.Src, $"combo {c.Keys[0]} {c.Keys[1]} → {output}");
    }

    private string? SimpleKeyToken(KeySeqDef seq, bool allowMods, bool allowMacro, out string? reason)
    {
        reason = null;
        var plan = MakePlan(seq);
        if (plan.Kind == PlanKind.Key) return plan.Token;
        if (plan.Kind == PlanKind.Macro && allowMacro) return TokenOf(plan, new SourceRef("", 0), "macro");
        reason = plan.Kind == PlanKind.Unsupported ? plan.Reason : DescribeKind(plan.Kind) + "は使えません";
        return null;
    }

    private void ConvertTapHold(TapHoldRule t)
    {
        if (!ComboApplies(t.Mod, out string? un)) { if (un != null) Say(ConvLevel.Skipped, t.Src, $"taphold: {un} の条件は対応しません"); return; }
        ushort? sc = PhysicalKey(t.KeyName, out string? err);
        if (sc == null) { Say(ConvLevel.Skipped, t.Src, $"taphold {t.KeyName}: {err}"); return; }
        string? tap = SimpleKeyToken(t.Tap!, true, true, out string? r1);
        string? hold = SimpleKeyToken(t.Hold!, true, true, out string? r2);
        if (tap == null || hold == null) { Say(ConvLevel.Skipped, t.Src, $"taphold {t.KeyName}: {r1 ?? r2}"); return; }

        // hold が修飾キーそのもの、tap が単純なキーなら MT
        if (KeycodeTable.TryResolve(hold, out var h) && h.Type == ActionType.Key && KeycodeTable.TryResolve(tap, out var tp) && tp.Type == ActionType.Key)
        {
            int bit = Array.IndexOf(KeycodeTable.ModifierScanCodes, h.Param2);
            if (bit >= 0)
            {
                string token = $"MT({KeycodeTable.ModifierNames[bit]},{tap})";
                _layers[0][sc.Value] = token;
                Say(ConvLevel.Converted, t.Src, $"taphold {t.KeyName} → レイヤー0 {PortableExporter.ScName(sc.Value)} = {token}");
                return;
            }
        }
        // それ以外はタップダンス（1回タップ / ホールド）
        if (_tapDances.Count >= KbdLayerClient.MaxTapDanceCount) { Say(ConvLevel.Skipped, t.Src, $"タップダンスが{KbdLayerClient.MaxTapDanceCount}個を超えるため変換しません"); return; }
        var td = new TapDanceDef { Id = _tapDances.Count, SingleTap = tap, SingleHold = hold };
        _tapDances.Add(td);
        _layers[0][sc.Value] = $"TAPDANCE({td.Id})";
        Say(ConvLevel.Converted, t.Src, $"taphold {t.KeyName} → タップダンス{td.Id}(タップ={tap}、ホールド={hold})");
        Approx("修飾キーではない taphold は、タップダンスの「1回タップ / ホールド」にしました", t.Src);
    }

    private void ConvertTapDance(TapDanceRule t)
    {
        if (!ComboApplies(t.Mod, out string? un)) { if (un != null) Say(ConvLevel.Skipped, t.Src, $"tapdance: {un} の条件は対応しません"); return; }
        ushort? sc = PhysicalKey(t.KeyName, out string? err);
        if (sc == null) { Say(ConvLevel.Skipped, t.Src, $"tapdance {t.KeyName}: {err}"); return; }
        var tokens = new string[3] { "", "", "" };
        for (int i = 0; i < 3; i++)
        {
            if (t.Taps[i] == null) continue;
            string? token = SimpleKeyToken(t.Taps[i]!, true, true, out string? reason);
            if (token == null) { Say(ConvLevel.Skipped, t.Src, $"tapdance {t.KeyName}: tap{i + 1} は{reason}"); return; }
            tokens[i] = token;
        }
        if (_tapDances.Count >= KbdLayerClient.MaxTapDanceCount) { Say(ConvLevel.Skipped, t.Src, $"タップダンスが{KbdLayerClient.MaxTapDanceCount}個を超えるため変換しません"); return; }
        var td = new TapDanceDef { Id = _tapDances.Count, SingleTap = tokens[0], DoubleTap = tokens[1], TripleTap = tokens[2] };
        _tapDances.Add(td);
        _layers[0][sc.Value] = $"TAPDANCE({td.Id})";
        Say(ConvLevel.Converted, t.Src, $"tapdance {t.KeyName} → タップダンス{td.Id}");
    }

    // ------------------------------------------------------------------
    // 前面のアプリ・タイミング
    // ------------------------------------------------------------------

    private void AddWindowRules()
    {
        // のどかは、後に定義した window を先に調べる（keymap.cpp の push_front）。kasane は上から順に最初の一致なので、逆順に並べる。
        foreach (var km in _s.Keymaps.Where(k => k.IsWindow).OrderByDescending(k => k.Order))
        {
            if (!_ctx.TryGetValue("km:" + km.Name, out int layer)) continue;
            if (km.ClassRegex.Length == 0 && km.TitleRegex.Length == 0) { Say(ConvLevel.Skipped, km.Src, $"window {km.Name}: ウィンドウの条件が空のため、アプリ別レイヤーの規則にしません"); continue; }
            _helper.AppLayerRules.Add(new AppLayerRule
            {
                Name = km.Name,
                Match = new AppMatch
                {
                    NodokaClass = km.ClassRegex.Length > 0 ? km.ClassRegex : null,
                    NodokaTitle = km.TitleRegex.Length > 0 ? km.TitleRegex : null,
                    NodokaOr = km.Kind == KeymapKind.WindowOr,
                },
                Layers = new List<int> { layer },
            });
            Say(ConvLevel.Converted, km.Src, $"window {km.Name} → レイヤー{layer} とアプリ別レイヤーの規則(のどか互換の照合)");
        }
    }

    private void ApplyTiming(PortableKeyboard kb)
    {
        uint Ms(string name) => _s.Options.TryGetValue(name, out var o) && uint.TryParse(o.Value.Split(' ')[0], out var v) ? v : 0;
        string Opt(string name) => _s.Options.TryGetValue(name, out var o) ? o.Value.Trim().ToLowerInvariant() : "";

        var timing = new PortableTiming
        {
            TapTermMs = Ms("TapHoldThreshold"),
            ComboTermMs = Ms("ComboWindow"),
            TapDanceTermMs = Ms("TapDanceTimeout"),
            QuickTapTermMs = Ms("TapHoldQuickTapTerm"),
        };
        if (Opt("TapHoldPermissiveHold") == "on") timing.TapHoldMode = TapHoldModes.PermissiveHold;
        else if (Opt("TapHoldOnOtherKeyPress") == "on") timing.TapHoldMode = TapHoldModes.HoldOnOtherKey;
        else if (Opt("TapHoldInterrupt") == "tap") timing.TapHoldMode = TapHoldModes.TapPreferred;
        if (timing.TapTermMs + timing.ComboTermMs + timing.TapDanceTermMs + timing.QuickTapTermMs > 0 || timing.TapHoldMode != 0)
        {
            kb.Timing = timing;
            Say(ConvLevel.Converted, null, "タイミング(TapHoldThreshold・ComboWindow・TapDanceTimeout など)を読み込みました");
        }
    }
}
