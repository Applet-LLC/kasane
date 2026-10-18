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
        if (DeviceWritable) ValidateConfig(Device, "このキーボード", errors);
        ValidateConfig(Global, "共通", errors);
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

    public static bool IsEmpty(HelperConfig c) => c.AppLayerRules.Count == 0 && c.Actions.Count == 0 && c.Triggers.Count == 0;

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
            string owner = $"{label}の動作 {a.Id}" + (string.IsNullOrWhiteSpace(a.Name) ? "" : $"（{a.Name}）");
            if (a.Id < 0 || a.Id > MaxActionId) errors.Add($"{owner}: Id は 0〜{MaxActionId} です");
            else if (!ids.Add(a.Id)) errors.Add($"{owner}: Id が重複しています");
            ValidateBody(a.Action, owner, errors);
        }

        var triggers = new HashSet<(ushort, byte)>();
        foreach (var t in c.Triggers)
        {
            string owner = $"{label}のトリガー {t.Key}" + (string.IsNullOrWhiteSpace(t.Mods) ? "" : $" + {t.Mods}");
            bool keyOk = Keycodes.KeycodeTable.TryResolveBaseKey(t.Key ?? "", out ushort sc) && sc != 0;
            bool modsOk = TriggerTable.TryParseMods(t.Mods, out byte mods);
            if (!keyOk) errors.Add($"{owner}: キー '{t.Key}' を解決できません（KC_F13 や SC_E06A のような書き方）");
            if (!modsOk) errors.Add($"{owner}: 修飾キー '{t.Mods}' を解決できません（MOD_LCTL|MOD_LSFT や ctrl|shift のような書き方）");
            if (keyOk && modsOk && !triggers.Add((sc, mods))) errors.Add($"{owner}: 同じキーと修飾キーのトリガーが重複しています");
            ValidateBody(t.Action, owner, errors);
        }

        int n = 0;
        foreach (var r in c.AppLayerRules)
        {
            n++;
            errors.AddRange(ValidateRule(r, $"{label}の規則 {n}" + (string.IsNullOrWhiteSpace(r.Name) ? "" : $"（{r.Name}）")));
        }
    }

    /// <summary>動作の中身の誤り。</summary>
    public static void ValidateBody(HelperActionBody body, string owner, List<string> errors)
    {
        switch (body.Type)
        {
            case HelperActionBody.Launch:
            case HelperActionBody.Open:
                if (string.IsNullOrWhiteSpace(body.Path)) errors.Add($"{owner}: {body.Type} の Path が空です");
                break;

            case HelperActionBody.Sequence:
                if ((body.Steps?.Count ?? 0) == 0 && (body.ReleaseSteps?.Count ?? 0) == 0)
                    errors.Add($"{owner}: Sequence に手順がありません");
                foreach (var e in SequenceCompiler.Compile(body.Steps).Errors) errors.Add($"{owner}: {e}");
                foreach (var e in SequenceCompiler.Compile(body.ReleaseSteps).Errors) errors.Add($"{owner}（離したとき）: {e}");
                break;

            case HelperActionBody.TextInput:
                if (string.IsNullOrEmpty(body.Text)) errors.Add($"{owner}: Text の文字列が空です");
                else if (body.Text.Length > HelperActionBody.MaxTextLength)
                    errors.Add($"{owner}: Text の文字列が長すぎます（{HelperActionBody.MaxTextLength} 文字まで）");
                break;

            case HelperActionBody.AppLayer:
                if ((body.Layers?.Count ?? 0) == 0) errors.Add($"{owner}: AppLayer のレイヤーが空です");
                else if (body.Layers!.Any(l => l is < 0 or >= 16)) errors.Add($"{owner}: レイヤーは 0〜15 です");
                if (!string.IsNullOrWhiteSpace(body.LayerOp) &&
                    !new[] { "on", "off", "toggle", "hold" }.Contains(body.LayerOp.Trim().ToLowerInvariant()))
                    errors.Add($"{owner}: LayerOp '{body.LayerOp}' は On / Off / Toggle / Hold のどれかです");
                break;

            case HelperActionBody.PauseToggle:
                break;

            default:
                errors.Add($"{owner}: 種類 '{body.Type}' は未対応です（Launch / Open / Sequence / Text / AppLayer / PauseToggle）");
                break;
        }
    }

    /// <summary>前面のアプリの規則の誤り。</summary>
    public static List<string> ValidateRule(AppLayerRule rule, string owner)
    {
        var errors = new List<string>();
        if (!rule.Default)
        {
            if (rule.Match.IsEmpty) errors.Add($"{owner}: 照合条件がありません（Default にするか、条件を1つ以上書きます）");
            string? bad = rule.Match.InvalidRegexField;
            if (bad != null) errors.Add($"{owner}: {bad} の正規表現が正しくありません");
        }
        if (rule.Layers.Any(l => l is < 0 or >= 16)) errors.Add($"{owner}: レイヤーは 0〜15 です");
        return errors;
    }
}

/// <summary>設定画面の一覧に出す、1行の説明と、一覧の項目の複製（2026-10-08、P4）。</summary>
public static class HelperSummary
{
    public static string Body(HelperActionBody b) => b.Type switch
    {
        HelperActionBody.Launch => "起動: " + (b.Path ?? "") + (string.IsNullOrWhiteSpace(b.Args) ? "" : " " + b.Args),
        HelperActionBody.Open => "開く: " + (b.Path ?? ""),
        HelperActionBody.Sequence => $"操作 {b.Steps?.Count ?? 0} 手順" + ((b.ReleaseSteps?.Count ?? 0) > 0 ? $" + 離したとき {b.ReleaseSteps!.Count} 手順" : ""),
        HelperActionBody.TextInput => "文字列: " + ShortText(b.Text),
        HelperActionBody.AppLayer => $"レイヤー {string.Join(",", b.Layers ?? new List<int>())} を {(string.IsNullOrWhiteSpace(b.LayerOp) ? "On" : b.LayerOp)}",
        HelperActionBody.PauseToggle => "一時停止・再開",
        _ => $"（不明な種類 '{b.Type}'）",
    } + (b.Repeat ? "（繰り返す）" : "");

    // 一覧に出す文字列（長ければ省略。改行は ↵）。
    private static string ShortText(string? text)
    {
        string t = (text ?? "").Replace("\r", "").Replace("\n", "↵").Replace("\t", "→");
        return t.Length <= 24 ? t : t[..24] + "…";
    }

    public static string Action(HelperAction a) =>
        $"HLP({a.Id})  {(string.IsNullOrWhiteSpace(a.Name) ? "" : a.Name + "  ")}— {Body(a.Action)}";

    public static string Trigger(HelperTrigger t)
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
        return $"{mods}{t.Key}  — {Body(t.Action)}";
    }

    public static string Rule(AppLayerRule r)
    {
        var cond = new List<string>();
        if (r.Default) cond.Add("（既定）");
        else
        {
            var m = r.Match;
            if (!string.IsNullOrEmpty(m.Exe)) cond.Add("exe=" + m.Exe);
            if (!string.IsNullOrEmpty(m.PathRegex)) cond.Add("path~" + m.PathRegex);
            if (!string.IsNullOrEmpty(m.TitleRegex)) cond.Add("title~" + m.TitleRegex);
            if (!string.IsNullOrEmpty(m.Class)) cond.Add("class=" + m.Class);
            if (!string.IsNullOrEmpty(m.NodokaClass)) cond.Add("のどかclass~" + m.NodokaClass);
            if (!string.IsNullOrEmpty(m.NodokaTitle)) cond.Add("のどかtitle~" + m.NodokaTitle);
            if (cond.Count == 0) cond.Add("（条件なし）");
        }
        var act = new List<string>();
        if (r.Layers.Count > 0) act.Add("レイヤー " + string.Join(",", r.Layers));
        if (r.MapOff) act.Add("リマップOFF");
        if (act.Count == 0) act.Add("何もしない");
        string name = string.IsNullOrWhiteSpace(r.Name) ? "" : r.Name + "  ";
        return $"{(r.Enabled ? "[有効]" : "[無効]")} {name}{string.Join(" かつ ", cond)} → {string.Join("、", act)}";
    }

    public static HelperAction CloneAction(HelperAction a) =>
        HelperConfig.FromJson(new HelperConfig { Actions = { a } }.ToJson())!.Actions[0];

    public static HelperTrigger CloneTrigger(HelperTrigger t) =>
        HelperConfig.FromJson(new HelperConfig { Triggers = { t } }.ToJson())!.Triggers[0];

    public static AppLayerRule CloneRule(AppLayerRule r) =>
        HelperConfig.FromJson(new HelperConfig { AppLayerRules = { r } }.ToJson())!.AppLayerRules[0];
}
