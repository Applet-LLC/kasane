using System.IO;

namespace Kasane.Model;

public enum HelperScope { Device, Global }

/// <summary>
/// 設定画面で編集するヘルパー設定（2026-10-08、P4）。「このキーボード」の helper.json と、「全キーボード共通」（tray.json の Helper）の
/// 2つを、コピーを編集してから OK のときに書く（キャンセルなら何も書かない）。画面に依存しないので、単体テストできる。
///  - 壊れている helper.json（構文エラーなど）は、黙って上書きしない。そのキーボードの分は、読み取り専用にして理由を見せる。
///  - 共通の分は、渡された <see cref="TrayConfig"/> の Helper に書き戻して保存する。設定画面は IME 連動も同じ TrayConfig に
///    書くので、同じインスタンスを共有して、お互いの変更を消さないようにする。
///  - 何も入っていない設定は、ファイルがまだ無ければ作らない。
/// </summary>
public sealed class HelperSettingsModel
{
    public const int MaxActionId = 65535;

    private readonly uint _uniqId;
    private readonly TrayConfig _trayConfig;
    private readonly string? _baseDirectory;
    private readonly string? _trayConfigPath;
    private string _deviceSnapshot;
    private string _globalSnapshot;

    public HelperSettingsModel(uint uniqId, TrayConfig trayConfig, string? baseDirectory = null, string? trayConfigPath = null)
    {
        _uniqId = uniqId;
        _trayConfig = trayConfig;
        _baseDirectory = baseDirectory;
        _trayConfigPath = trayConfigPath;

        Global = Clone(trayConfig.Helper);
        Device = new HelperConfig();
        if (uniqId != 0)
        {
            if (HelperConfig.TryLoad(HelperConfig.DevicePath(uniqId, baseDirectory), out var loaded, out var error)) Device = Clone(loaded);
            else DeviceLoadError = error;
        }
        _deviceSnapshot = Device.ToJson();
        _globalSnapshot = Global.ToJson();
    }

    /// <summary>このキーボードの helper.json の場所（キーボードが無ければ null）。</summary>
    public string? DevicePath => _uniqId == 0 ? null : HelperConfig.DevicePath(_uniqId, _baseDirectory);

    /// <summary>キーボード（0 = オフラインなど、キーボードが決まらないとき）。</summary>
    public bool HasDevice => _uniqId != 0;

    /// <summary>このキーボードの helper.json が読めなかった理由（読めたなら null）。</summary>
    public string? DeviceLoadError { get; }

    /// <summary>このキーボードの分を編集・保存できるか。</summary>
    public bool DeviceWritable => HasDevice && DeviceLoadError == null;

    public HelperConfig Device { get; }
    public HelperConfig Global { get; }

    public HelperConfig Config(HelperScope scope) => scope == HelperScope.Device ? Device : Global;

    public bool IsDirty(HelperScope scope) =>
        scope == HelperScope.Device ? Device.ToJson() != _deviceSnapshot : Global.ToJson() != _globalSnapshot;

    public bool IsDirty() => (DeviceWritable && IsDirty(HelperScope.Device)) || IsDirty(HelperScope.Global);

    /// <summary>
    /// 新しい動作に使える Id（1〜65535 で、このキーボードの分と共通の分のどちらにも無い最小のもの）。
    /// どちらかで使われている Id を避ける（キーボードごとの Id が共通の Id を隠さないように）。空きが無ければ -1。
    /// </summary>
    public int NextFreeActionId()
    {
        var used = new HashSet<int>(Device.Actions.Select(a => a.Id).Concat(Global.Actions.Select(a => a.Id)));
        for (int id = 1; id <= MaxActionId; id++)
            if (!used.Contains(id)) return id;
        return -1;
    }

    /// <summary>編集中の内容の誤り（保存の前に見せる）。空なら保存してよい。</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (DeviceWritable) ValidateConfig(Device, Loc.T("このキーボード", "this keyboard"), errors);
        ValidateConfig(Global, Loc.T("共通", "shared"), errors);
        return errors;
    }

    /// <summary>変わった分だけ書く。失敗したら false で理由を返す（書けた分は、そのまま残る）。</summary>
    public bool TrySave(out string? error)
    {
        error = null;
        try
        {
            if (DeviceWritable && IsDirty(HelperScope.Device))
            {
                string path = DevicePath!;
                if (!IsEmpty(Device) || File.Exists(path))
                {
                    Device.Save(path);
                    _deviceSnapshot = Device.ToJson();
                }
            }
            if (IsDirty(HelperScope.Global))
            {
                _trayConfig.Helper = IsEmpty(Global) ? null : Global;
                _trayConfig.Save(_trayConfigPath);
                _globalSnapshot = Global.ToJson();
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool IsEmpty(HelperConfig c) =>
        c.AppLayerRules.Count == 0 && c.Actions.Count == 0 && c.Triggers.Count == 0 && c.ImeLayers == null && c.NicolaImeLink == null;

    private static HelperConfig Clone(HelperConfig? c) =>
        c == null ? new HelperConfig() : HelperConfig.FromJson(c.ToJson()) ?? new HelperConfig();

    // ------------------------------------------------------------------
    // 検証
    // ------------------------------------------------------------------

    private static void ValidateConfig(HelperConfig c, string label, List<string> errors)
    {
        var ids = new HashSet<int>();
        foreach (var a in c.Actions)
        {
            string owner = Loc.T($"{label}の動作 {a.Id}", $"Action {a.Id} ({label})") + (string.IsNullOrWhiteSpace(a.Name) ? "" : $"({a.Name})");
            if (a.Id < 0 || a.Id > MaxActionId) errors.Add(Loc.T($"{owner}: Id は 0〜{MaxActionId} です", $"{owner}: Id must be between 0 and {MaxActionId}"));
            else if (!ids.Add(a.Id)) errors.Add(Loc.T($"{owner}: Id が重複しています", $"{owner}: duplicate Id"));
            ValidateBody(a.Action, owner, errors);
        }

        var triggers = new HashSet<(ushort, byte)>();
        foreach (var t in c.Triggers)
        {
            string owner = Loc.T($"{label}のトリガー {t.Key}", $"Trigger {t.Key} ({label})") + (string.IsNullOrWhiteSpace(t.Mods) ? "" : $" + {t.Mods}");
            bool keyOk = Keycodes.KeycodeTable.TryResolveBaseKey(t.Key ?? "", out ushort sc) && sc != 0;
            bool modsOk = TriggerTable.TryParseMods(t.Mods, out byte mods);
            if (!keyOk) errors.Add(Loc.T($"{owner}: キー '{t.Key}' を解決できません(KC_F13 や SC_E06A のような書き方)", $"{owner}: can't resolve key '{t.Key}' (write it like KC_F13 or SC_E06A)"));
            if (!modsOk) errors.Add(Loc.T($"{owner}: 修飾キー '{t.Mods}' を解決できません(MOD_LCTL|MOD_LSFT や ctrl|shift のような書き方)", $"{owner}: can't resolve modifier '{t.Mods}' (write it like MOD_LCTL|MOD_LSFT or ctrl|shift)"));
            if (keyOk && modsOk && !triggers.Add((sc, mods))) errors.Add(Loc.T($"{owner}: 同じキーと修飾キーのトリガーが重複しています", $"{owner}: another trigger has the same key and modifiers"));
            ValidateBody(t.Action, owner, errors);
        }

        if (c.ImeLayers is { } ime)
        {
            if (ime.Layers.Any(l => l is < 0 or >= 16)) errors.Add(Loc.T($"{label}の IME 連動: レイヤーは 0〜15 です", $"IME link ({label}): layers must be between 0 and 15"));
            if (ime.Enabled && ime.Layers.Count == 0) errors.Add(Loc.T($"{label}の IME 連動: 有効にするレイヤーを 1 つ以上選んでください", $"IME link ({label}): choose at least one layer to turn on"));
            if (!string.Equals(ime.Mode, ImeLayerLink.ModeOn, StringComparison.OrdinalIgnoreCase) && !ime.KanaOnly)
                errors.Add(Loc.T($"{label}の IME 連動: Mode '{ime.Mode}' は On / Kana のどちらかです", $"IME link ({label}): Mode '{ime.Mode}' must be On or Kana"));
        }

        int n = 0;
        foreach (var r in c.AppLayerRules)
        {
            n++;
            errors.AddRange(ValidateRule(r, Loc.T($"{label}の規則 {n}", $"Rule {n} ({label})") + (string.IsNullOrWhiteSpace(r.Name) ? "" : $"({r.Name})")));
        }
    }

    /// <summary>動作の中身の誤り。</summary>
    public static void ValidateBody(HelperActionBody body, string owner, List<string> errors)
    {
        switch (body.Type)
        {
            case HelperActionBody.Launch:
            case HelperActionBody.Open:
                if (string.IsNullOrWhiteSpace(body.Path)) errors.Add(Loc.T($"{owner}: {body.Type} の Path が空です", $"{owner}: Path of {body.Type} is empty"));
                break;

            case HelperActionBody.Sequence:
                if ((body.Steps?.Count ?? 0) == 0 && (body.ReleaseSteps?.Count ?? 0) == 0)
                    errors.Add(Loc.T($"{owner}: 手順がありません", $"{owner}: Sequence has no steps"));
                foreach (var e in SequenceCompiler.Compile(body.Steps).Errors) errors.Add($"{owner}: {e}");
                foreach (var e in SequenceCompiler.Compile(body.ReleaseSteps).Errors) errors.Add(Loc.T($"{owner}(離したとき): {e}", $"{owner} (on release): {e}"));
                break;

            case HelperActionBody.TextInput:
                if (string.IsNullOrEmpty(body.Text)) errors.Add(Loc.T($"{owner}: 入力する文字列が空です", $"{owner}: Text is empty"));
                else if (body.Text.Length > HelperActionBody.MaxTextLength)
                    errors.Add(Loc.T($"{owner}: 入力する文字列が長すぎます({HelperActionBody.MaxTextLength} 文字まで)", $"{owner}: Text is too long (up to {HelperActionBody.MaxTextLength} characters)"));
                if (body.ImeOffDuring)
                {
                    if (!ImeNames.TryParseMethod(body.ImeMethod, out _))
                        errors.Add(Loc.T($"{owner}: ImeMethod '{body.ImeMethod}' は Auto / Imm / Tsf / Key / AltKanji のどれかです", $"{owner}: ImeMethod '{body.ImeMethod}' must be Auto, Imm, Tsf, Key or AltKanji"));
                    if (!ImeNames.TryParseOnFail(body.ImeOnFail, out _))
                        errors.Add(Loc.T($"{owner}: ImeOnFail '{body.ImeOnFail}' は Continue / Abort のどちらかです", $"{owner}: ImeOnFail '{body.ImeOnFail}' must be Continue or Abort"));
                }
                break;

            case HelperActionBody.AppLayer:
                if ((body.Layers?.Count ?? 0) == 0) errors.Add(Loc.T($"{owner}: レイヤーを選んでいません", $"{owner}: AppLayer has no layers"));
                else if (body.Layers!.Any(l => l is < 0 or >= 16)) errors.Add(Loc.T($"{owner}: レイヤーは 0〜15 です", $"{owner}: layers must be between 0 and 15"));
                if (!string.IsNullOrWhiteSpace(body.LayerOp) &&
                    !new[] { "on", "off", "toggle", "hold" }.Contains(body.LayerOp.Trim().ToLowerInvariant()))
                    errors.Add(Loc.T($"{owner}: LayerOp '{body.LayerOp}' は On / Off / Toggle / Hold のどれかです", $"{owner}: LayerOp '{body.LayerOp}' must be On, Off, Toggle or Hold"));
                break;

            case HelperActionBody.PauseToggle:
            case HelperActionBody.KillLine:
                break;

            default:
                errors.Add(Loc.T($"{owner}: 種類 '{body.Type}' は未対応です(Launch / Open / Sequence / Text / AppLayer / PauseToggle / KillLine)", $"{owner}: type '{body.Type}' is not supported (Launch / Open / Sequence / Text / AppLayer / PauseToggle / KillLine)"));
                break;
        }
    }

    /// <summary>前面のアプリの規則の誤り。</summary>
    public static List<string> ValidateRule(AppLayerRule rule, string owner)
    {
        var errors = new List<string>();
        if (!rule.Default)
        {
            if (rule.Match.IsEmpty) errors.Add(Loc.T($"{owner}: 照合条件がありません(Default にするか、条件を1つ以上書きます)", $"{owner}: no match conditions (make it Default or add at least one condition)"));
            string? bad = rule.Match.InvalidRegexField;
            if (bad != null) errors.Add(Loc.T($"{owner}: {bad} の正規表現が正しくありません", $"{owner}: invalid regular expression in {bad}"));
        }
        if (rule.Layers.Any(l => l is < 0 or >= 16)) errors.Add(Loc.T($"{owner}: レイヤーは 0〜15 です", $"{owner}: layers must be between 0 and 15"));
        return errors;
    }
}

/// <summary>設定画面の一覧に出す、1行の説明と、一覧の項目の複製（2026-10-08、P4）。</summary>
public static class HelperSummary
{
    /// <param name="full">true なら、手順の中身（key LCTL(KC_C); delay 50 …）と文字列を、省略せずに出す（割り当ての一覧用）。
    /// false は、設定の一覧の 1 行用（手順の数だけ・文字列は 24 文字まで）。</param>
    public static string Body(HelperActionBody b, bool full = false) => b.Type switch
    {
        HelperActionBody.Launch => Loc.T("起動: ", "Launch: ") + (b.Path ?? "") + (string.IsNullOrWhiteSpace(b.Args) ? "" : " " + b.Args),
        HelperActionBody.Open => Loc.T("開く: ", "Open: ") + (b.Path ?? ""),
        HelperActionBody.Sequence when full => Loc.T("操作: ", "Sequence: ") + FlatSteps(b.Steps) +
            ((b.ReleaseSteps?.Count ?? 0) > 0 ? Loc.T(" / 離したとき: ", " / on release: ") + FlatSteps(b.ReleaseSteps) : ""),
        HelperActionBody.Sequence => Loc.T($"操作 {b.Steps?.Count ?? 0} 手順", $"Sequence: {b.Steps?.Count ?? 0} steps") + ((b.ReleaseSteps?.Count ?? 0) > 0 ? Loc.T($" + 離したとき {b.ReleaseSteps!.Count} 手順", $" + on release {b.ReleaseSteps!.Count} steps") : ""),
        HelperActionBody.TextInput => Loc.T("文字列: ", "Text: ") + (full ? Flat(b.Text) : ShortText(b.Text)),
        HelperActionBody.AppLayer => Loc.T($"レイヤー {string.Join(",", b.Layers ?? new List<int>())} を {(string.IsNullOrWhiteSpace(b.LayerOp) ? "On" : b.LayerOp)}", $"Layer {string.Join(",", b.Layers ?? new List<int>())}: {(string.IsNullOrWhiteSpace(b.LayerOp) ? "On" : b.LayerOp)}"),
        HelperActionBody.PauseToggle => Loc.T("一時停止・再開", "Pause / resume"),
        HelperActionBody.KillLine => Loc.T("行末まで切り取り (C-k)", "Kill to end of line (C-k)"),
        _ => Loc.T($"(不明な種類 '{b.Type}')", $"(unknown type '{b.Type}')"),
    } + (b.Repeat ? Loc.T("(繰り返す)", " (repeat)") : "");

    // 一覧に出す文字列（長ければ省略。改行は ↵）。
    private static string ShortText(string? text)
    {
        string t = Flat(text);
        return t.Length <= 24 ? t : t[..24] + "…";
    }

    private static string Flat(string? text) => (text ?? "").Replace("\r", "").Replace("\n", "↵").Replace("\t", "→");

    // 手順を 1 行にする（1 手順ずつ「; 」で区切る）。
    private static string FlatSteps(IEnumerable<HelperStep>? steps) =>
        string.Join("; ", StepText.Format(steps).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public static string Action(HelperAction a, bool full = false) =>
        $"HLP({a.Id})  {(string.IsNullOrWhiteSpace(a.Name) ? "" : a.Name + "  ")}— {Body(a.Action, full)}";

    public static string Trigger(HelperTrigger t, bool full = false)
    {
        string mods = "";
        if (TriggerTable.TryParseMods(t.Mods, out byte m))
        {
            if ((m & TriggerTable.ModCtrl) != 0) mods += "Ctrl+";
            if ((m & TriggerTable.ModShift) != 0) mods += "Shift+";
            if ((m & TriggerTable.ModAlt) != 0) mods += "Alt+";
            if ((m & TriggerTable.ModWin) != 0) mods += "Win+";
        }
        else mods = (t.Mods ?? "") + "+";
        return $"{(string.IsNullOrWhiteSpace(t.Name) ? "" : t.Name + "  ")}{mods}{t.Key}  — {Body(t.Action, full)}";
    }

    /// <summary>トリガーのキー（と修飾キー）を、割り当てのキーコードにする（Ctrl+F13 → LCTL(KC_F13)）。解決できなければ null。</summary>
    public static string? TriggerToken(HelperTrigger t)
    {
        if (!TriggerTable.TryParseMods(t.Mods, out byte m)) return null;
        byte mask = (byte)(((m & TriggerTable.ModCtrl) != 0 ? 0x01 : 0) | ((m & TriggerTable.ModShift) != 0 ? 0x02 : 0) |
                           ((m & TriggerTable.ModAlt) != 0 ? 0x04 : 0) | ((m & TriggerTable.ModWin) != 0 ? 0x08 : 0));
        string token = mask == 0 ? (t.Key ?? "").Trim() : Keycodes.KeycodeTable.WrapWithMods(mask, (t.Key ?? "").Trim());
        return Keycodes.KeycodeTable.TryResolve(token, out _, out _) ? token : null;
    }

    public static string Rule(AppLayerRule r)
    {
        var cond = new List<string>();
        if (r.Default) cond.Add(Loc.T("(既定)", "(default)"));
        else
        {
            var m = r.Match;
            if (!string.IsNullOrEmpty(m.Exe)) cond.Add("exe=" + m.Exe);
            if (!string.IsNullOrEmpty(m.PathRegex)) cond.Add("path~" + m.PathRegex);
            if (!string.IsNullOrEmpty(m.TitleRegex)) cond.Add("title~" + m.TitleRegex);
            if (!string.IsNullOrEmpty(m.Class)) cond.Add("class=" + m.Class);
            if (!string.IsNullOrEmpty(m.NodokaClass)) cond.Add(Loc.T("のどかclass~", "Nodoka class~") + m.NodokaClass);
            if (!string.IsNullOrEmpty(m.NodokaTitle)) cond.Add(Loc.T("のどかtitle~", "Nodoka title~") + m.NodokaTitle);
            if (cond.Count == 0) cond.Add(Loc.T("(条件なし)", "(no conditions)"));
        }
        var act = new List<string>();
        if (r.Layers.Count > 0) act.Add(Loc.T("レイヤー ", "Layer ") + string.Join(",", r.Layers));
        if (r.MapOff) act.Add(Loc.T("リマップOFF", "Remap OFF"));
        if (act.Count == 0) act.Add(Loc.T("何もしない", "Do nothing"));
        string name = string.IsNullOrWhiteSpace(r.Name) ? "" : r.Name + "  ";
        return $"{(r.Enabled ? Loc.T("[有効]", "[On]") : Loc.T("[無効]", "[Off]"))} {name}{string.Join(Loc.T(" かつ ", " and "), cond)} → {string.Join(Loc.T("、", ", "), act)}";
    }

    public static HelperAction CloneAction(HelperAction a) =>
        HelperConfig.FromJson(new HelperConfig { Actions = { a } }.ToJson())!.Actions[0];

    public static HelperTrigger CloneTrigger(HelperTrigger t) =>
        HelperConfig.FromJson(new HelperConfig { Triggers = { t } }.ToJson())!.Triggers[0];

    public static AppLayerRule CloneRule(AppLayerRule r) =>
        HelperConfig.FromJson(new HelperConfig { AppLayerRules = { r } }.ToJson())!.AppLayerRules[0];
}
