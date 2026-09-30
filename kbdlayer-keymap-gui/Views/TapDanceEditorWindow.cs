using KbdLayerKeymapGui.Interop;
using KbdLayerKeymapGui.Model;

namespace KbdLayerKeymapGui.Views;

/// <summary>タップダンス（TAPDANCE(n)キーのタップ回数・長押しで動作を変える。v1: 3回まで）の編集。</summary>
public sealed class TapDanceEditorWindow : IdListEditorWindow
{
    private readonly TapDanceDef?[] _defs = new TapDanceDef?[KbdLayerClient.MaxTapDanceCount];

    public List<TapDanceDef> Result { get; private set; } = new();

    public TapDanceEditorWindow(IEnumerable<TapDanceDef> tapDances)
        : base("タップダンス", KbdLayerClient.MaxTapDanceCount,
            "タップダンスは、キーマップで TAPDANCE(n) を割り当てたキーを、何回続けてタップしたか・長押ししたかで動作を変えます。\n" +
            "1回・2回・3回タップはキー(KC_*)、LCTL(KC_C) のような修飾付きキー、MACRO(n) のいずれか、" +
            "長押しは KC_*、MT(MOD_LCTL,KC_NO) のような修飾キー、" +
            "LT(1,KC_NO) のようなレイヤーのいずれかを指定します（MT/LTのタップ側のキーは使われません）。" +
            "判定時間はタイミング設定の TapDanceTerm です。4つとも空にすると未使用になります。")
    {
        foreach (var t in tapDances)
            if (t.Id >= 0 && t.Id < _defs.Length)
                _defs[t.Id] = new TapDanceDef { Id = t.Id, SingleTap = t.SingleTap, SingleHold = t.SingleHold, DoubleTap = t.DoubleTap, TripleTap = t.TripleTap };
    }

    protected override string Summary(int id) =>
        _defs[id] is { } t ? $"{t.SingleTap} / 長押し {t.SingleHold} / {t.DoubleTap} / {t.TripleTap}" : "";

    protected override void ShowDetail(int id)
    {
        var d = _defs[id];
        var single = KeycodeBox(d?.SingleTap ?? "");
        var hold = KeycodeBox(d?.SingleHold ?? "");
        var dbl = KeycodeBox(d?.DoubleTap ?? "");
        var triple = KeycodeBox(d?.TripleTap ?? "");

        void Changed()
        {
            var boxes = new[] { single, hold, dbl, triple };
            if (boxes.All(b => b.Text.Trim().Length == 0)) _defs[id] = null;
            else _defs[id] = new TapDanceDef
            {
                Id = id,
                SingleTap = single.Text.Trim(),
                SingleHold = hold.Text.Trim(),
                DoubleTap = dbl.Text.Trim(),
                TripleTap = triple.Text.Trim(),
            };
            RefreshItem(id);
            var errors = new List<string>();
            if (_defs[id] is { } t) ProjectService.TapDanceToEntry(t, errors);
            ShowValidation(errors.Count == 0 ? "OK" : string.Join("\n", errors), errors.Count > 0);
        }
        foreach (var b in new[] { single, hold, dbl, triple }) b.TextChanged += (_, _) => Changed();

        Detail.Children.Add(Row("1回タップ", single));
        Detail.Children.Add(Row("長押し", hold));
        Detail.Children.Add(Row("2回タップ", dbl));
        Detail.Children.Add(Row("3回タップ", triple));
    }

    protected override bool ValidateAll(out string error)
    {
        var errors = new List<string>();
        foreach (var t in _defs.Where(t => t != null)) ProjectService.TapDanceToEntry(t!, errors);
        error = string.Join("\n", errors);
        if (errors.Count > 0) return false;
        Result = _defs.Where(t => t != null).Select(t => t!).ToList();
        return true;
    }
}
