using Kasane.Interop;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>タップダンス（TAPDANCE(n)キーのタップ回数・長押しで動作を変える。v1: 3回まで）の編集。</summary>
public sealed class TapDanceEditorWindow : IdListEditorWindow
{
    private readonly TapDanceDef?[] _defs = new TapDanceDef?[KbdLayerClient.MaxTapDanceCount];

    public List<TapDanceDef> Result { get; private set; } = new();

    public TapDanceEditorWindow(IEnumerable<TapDanceDef> tapDances, IReadOnlyDictionary<int, string>? names = null)
        : base("タップダンス", KbdLayerClient.MaxTapDanceCount,
            "タップダンスは、キーマップで TAPDANCE(n) を割り当てたキーを、何回続けてタップしたか・長押ししたかで動作を変えます。\n" +
            "1回・2回・3回タップはキー(KC_*)、LCTL(KC_C) のような修飾付きキー、MACRO(n) のいずれか、" +
            "長押しは KC_*、MT(MOD_LCTL,KC_NO) のような修飾キー、LT(1,KC_NO) のようなレイヤー（MT/LTのタップ側のキーは使われません）、" +
            "LCTL(KC_C) のような修飾付きキー（押している間）、LM(1,MOD_LCTL)、MACRO(n)（長押しが決まったときに1回実行）のいずれかを指定します。" +
            "空欄（または KC_NO）にした欄は「動作なし」で、そのタップ回数・長押しでは何も起きません。" +
            "判定時間はタイミング設定の TapDanceTerm です。4つとも空にすると未使用になります。")
    {
        foreach (var t in tapDances)
            if (t.Id >= 0 && t.Id < _defs.Length)
                _defs[t.Id] = new TapDanceDef { Id = t.Id, SingleTap = t.SingleTap, SingleHold = t.SingleHold, DoubleTap = t.DoubleTap, TripleTap = t.TripleTap };
        LoadNames(names);
    }

    protected override string Summary(int id) =>
        _defs[id] is { } t ? $"{t.SingleTap} / 長押し {t.SingleHold} / {t.DoubleTap} / {t.TripleTap}" : "";

    /// <summary>編集中の内容から、使われているタップダンスの一覧を作る（他の画面の「タップダンス...」の選択肢になる）。</summary>
    public IReadOnlyList<MacroInfo> Snapshot()
    {
        var list = new List<MacroInfo>();
        for (int i = 0; i < _defs.Length; i++)
            if (_defs[i] != null || !string.IsNullOrWhiteSpace(Names[i]))
                list.Add(new MacroInfo(i, Names[i], Summary(i), "TAPDANCE"));
        return list;
    }

    // 一覧の行のダブルクリック: キーの割り当て画面のキーコード欄へ TAPDANCE(n) を入れる。
    protected override void OnItemActivated(int id) => InsertIntoOtherWindow(id);

    private void InsertIntoOtherWindow(int id)
    {
        if (_defs[id] == null)
        {
            ShowValidation("中身が空のタップダンスは入れられません。", isError: true);
            return;
        }
        bool ok = MacroInsert.TryInsert($"TAPDANCE({id})", out var reason);
        ShowValidation(ok ? $"TAPDANCE({id}) を入れました。" : reason, isError: !ok);
    }

    protected override void ShowDetail(int id)
    {
        var d = _defs[id];
        var single = KeycodeBox(d?.SingleTap ?? "", "SingleTap");
        var hold = KeycodeBox(d?.SingleHold ?? "", "SingleHold");
        var dbl = KeycodeBox(d?.DoubleTap ?? "", "DoubleTap");
        var triple = KeycodeBox(d?.TripleTap ?? "", "TripleTap");

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

        Detail.Children.Add(Row("1回タップ", KeycodeField.Wrap(single, acceptsMacro: true)));
        Detail.Children.Add(Row("長押し", KeycodeField.Wrap(hold, acceptsMacro: true)));
        Detail.Children.Add(Row("2回タップ", KeycodeField.Wrap(dbl, acceptsMacro: true)));
        Detail.Children.Add(Row("3回タップ", KeycodeField.Wrap(triple, acceptsMacro: true)));

        var insert = new System.Windows.Controls.Button
        {
            Name = "InsertTapDanceButton",
            Content = $"キーの割り当て画面の入力欄に TAPDANCE({id}) を入れる",
            Margin = new System.Windows.Thickness(110, 8, 0, 0),
            Padding = new System.Windows.Thickness(10, 3, 10, 3),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            ToolTip = "キーの割り当て画面のキーコード欄を一度クリックしてから押します（一覧の行をダブルクリックでも同じ）。",
        };
        insert.Click += (_, _) => InsertIntoOtherWindow(id);
        Detail.Children.Add(insert);
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
