using Kasane.Interop;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>タップダンス（TAPDANCE(n)キーのタップ回数・長押しで動作を変える。v1: 3回まで）の編集。</summary>
public sealed class TapDanceEditorWindow : IdListEditorWindow
{
    private readonly TapDanceDef?[] _defs = new TapDanceDef?[KbdLayerClient.MaxTapDanceCount];

    public List<TapDanceDef> Result { get; private set; } = new();

    public TapDanceEditorWindow(IEnumerable<TapDanceDef> tapDances, IReadOnlyDictionary<int, string>? names = null)
        : base(Loc.T("タップダンス", "Tap dance"), KbdLayerClient.MaxTapDanceCount,
            Loc.T(
                "タップダンスは、キーマップで TAPDANCE(n) を割り当てたキーを、何回続けてタップしたか・長押ししたかで動作を変えます。\n" +
                "1回・2回・3回タップはキー(KC_*)、LCTL(KC_C) のような修飾付きキー、MACRO(n) のいずれか、" +
                "長押しは KC_*、MT(MOD_LCTL,KC_NO) のような修飾キー、LT(1,KC_NO) のようなレイヤー(MT/LTのタップ側のキーは使われません)、" +
                "LCTL(KC_C) のような修飾付きキー(押している間)、LM(1,MOD_LCTL)、MACRO(n)(長押しが決まったときに1回実行)のいずれかを指定します。" +
                "空欄(または KC_NO)にした欄は「動作なし」で、そのタップ回数・長押しでは何も起きません。" +
                "判定時間はタイミング設定の TapDanceTerm です。4つとも空にすると未使用になります。\n" +
                "「無効にする」にチェックを付けると、書いた内容を残したまま、キーボードには適用されません。",
                "A tap dance changes the action depending on how many times in a row you tap a key assigned TAPDANCE(n) in the keymap, or whether you hold it.\n" +
                "For 1, 2, and 3 taps, specify a key (KC_*), a key with modifiers such as LCTL(KC_C), or MACRO(n). " +
                "For a hold, specify KC_*, a modifier key such as MT(MOD_LCTL,KC_NO), a layer such as LT(1,KC_NO) (the tap-side key of MT/LT is not used), " +
                "a key with modifiers such as LCTL(KC_C) (while held), LM(1,MOD_LCTL), or MACRO(n) (runs once when the hold is recognized). " +
                "A field left blank (or KC_NO) means “no action”: nothing happens for that tap count or hold. " +
                "The timing is TapDanceTerm in the timing settings. If all four are blank, the tap dance is unused.\n" +
                "If you check \"Disable\", the content is kept but not applied to the keyboard."))
    {
        foreach (var t in tapDances)
            if (t.Id >= 0 && t.Id < _defs.Length)
            {
                _defs[t.Id] = new TapDanceDef { Id = t.Id, SingleTap = t.SingleTap, SingleHold = t.SingleHold, DoubleTap = t.DoubleTap, TripleTap = t.TripleTap };
                LoadDisabled(t.Id, t.Disabled);
            }
        LoadNames(names);
    }

    protected override string? InsertKind => "TAPDANCE";

    protected override RefKind? ReferenceKind => RefKind.TapDance;
    protected override void ClearItem(int id) => _defs[id] = null;
    protected override void CopyItem(int from, int to) =>
        _defs[to] = _defs[from] is { } t ? new TapDanceDef { Id = to, SingleTap = t.SingleTap, SingleHold = t.SingleHold, DoubleTap = t.DoubleTap, TripleTap = t.TripleTap } : null;
    protected override void SwapItems(int a, int b)
    {
        (_defs[a], _defs[b]) = (_defs[b], _defs[a]);
        if (_defs[a] != null) _defs[a]!.Id = a;
        if (_defs[b] != null) _defs[b]!.Id = b;
    }

    // ほかの画面でマクロを削除して参照を外したとき: この画面の欄からも外す（全部空になれば未使用）。
    public override void ReferencesRemoved(RefKind kind, int id)
    {
        for (int i = 0; i < _defs.Length; i++)
        {
            if (_defs[i] is not { } t) continue;
            int cleared = ProjectService.ClearTapDanceRefs(t, kind, id);
            // 定義の実体はプロジェクトと共有していることがあり（先に適用したとき）、プロジェクト側で先に空にされていることもあるので、空かどうかは別に見る。
            if (ProjectService.IsEmptyTapDance(t)) _defs[i] = null;
            else if (cleared == 0) continue;
            RefreshItem(i);
        }
        ShowId(Math.Max(0, CurrentId));
    }

    /// <summary>
    /// 最初の空き番号を選ぶ。<paramref name="singleTap"/> があれば、1回タップに入れたタップダンスを作る（ほかの欄は利用者が書く）。
    /// 作ったときは、「入れる」ボタンを強調する（キーの割り当て画面に TAPDANCE(n) を入れ忘れないように）。空き番号が無ければ -1。
    /// </summary>
    public int StartNew(string? singleTap)
    {
        int id = FreeId();
        if (id < 0) return -1;
        if (!string.IsNullOrWhiteSpace(singleTap))
        {
            _defs[id] = new TapDanceDef { Id = id, SingleTap = singleTap.Trim(), SingleHold = "", DoubleTap = "", TripleTap = "" };
            RefreshItem(id);
            NoteEdited(id);
        }
        ShowId(id);
        return id;
    }

    protected override string Summary(int id) =>
        _defs[id] is { } t ? Loc.T($"{t.SingleTap} / 長押し {t.SingleHold} / {t.DoubleTap} / {t.TripleTap}", $"{t.SingleTap} / hold {t.SingleHold} / {t.DoubleTap} / {t.TripleTap}") : "";

    /// <summary>編集中の内容から、使われているタップダンスの一覧を作る（他の画面の「タップダンス...」の選択肢になる）。</summary>
    public IReadOnlyList<MacroInfo> Snapshot()
    {
        var list = new List<MacroInfo>();
        for (int i = 0; i < _defs.Length; i++)
            if (_defs[i] != null || !string.IsNullOrWhiteSpace(Names[i]))
                list.Add(new MacroInfo(i, Names[i], Summary(i), "TAPDANCE", Disabled[i]));
        return list;
    }

    // 一覧の行のダブルクリック: キーの割り当て画面のキーコード欄へ TAPDANCE(n) を入れる。
    protected override void OnItemActivated(int id) => InsertIntoOtherWindow(id);

    private void InsertIntoOtherWindow(int id)
    {
        if (_defs[id] == null)
        {
            ShowValidation(Loc.T("中身が空のタップダンスは入れられません。", "An empty tap dance cannot be inserted."), isError: true);
            return;
        }
        bool ok = MacroInsert.TryInsert($"TAPDANCE({id})", out var reason);
        UpdateInsertHint();
        ShowValidation(ok ? Loc.T($"TAPDANCE({id}) を入れました。", $"Inserted TAPDANCE({id}).") : reason, isError: !ok);
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
            NoteEdited(id);
            var errors = new List<string>();
            if (_defs[id] is { } t) ProjectService.TapDanceToEntry(t, errors);
            ShowValidation(errors.Count == 0 ? "OK" : string.Join("\n", errors), errors.Count > 0);
        }
        foreach (var b in new[] { single, hold, dbl, triple }) b.TextChanged += (_, _) => Changed();

        Detail.Children.Add(Row(Loc.T("1回タップ", "Single tap"), KeycodeField.Wrap(single, acceptsMacro: true)));
        Detail.Children.Add(Row(Loc.T("長押し", "Hold"), KeycodeField.Wrap(hold, acceptsMacro: true)));
        Detail.Children.Add(Row(Loc.T("2回タップ", "Double tap"), KeycodeField.Wrap(dbl, acceptsMacro: true)));
        Detail.Children.Add(Row(Loc.T("3回タップ", "Triple tap"), KeycodeField.Wrap(triple, acceptsMacro: true)));

        var insert = new System.Windows.Controls.Button
        {
            Name = "InsertTapDanceButton",
            Content = Loc.T($"キーの割り当て画面の入力欄に TAPDANCE({id}) を入れる", $"Insert TAPDANCE({id}) into the key assignment field"),
            Margin = new System.Windows.Thickness(110, 8, 0, 0),
            Padding = new System.Windows.Thickness(10, 3, 10, 3),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            ToolTip = Loc.T("キーの割り当て画面のキーコード欄を一度クリックしてから押します(一覧の行をダブルクリックでも同じ)。", "Click the keycode field in the key assignment dialog once, then press this (double-clicking a row in the list does the same)."),
        };
        insert.Click += (_, _) => InsertIntoOtherWindow(id);
        Detail.Children.Add(insert);
        InsertButton = insert;
        UpdateInsertHint();
    }

    protected override void OnItemEnabled(int id)
    {
        var errors = new List<string>();
        if (_defs[id] is { } t) ProjectService.TapDanceToEntry(t, errors);
        ShowValidation(errors.Count == 0 ? "OK" : string.Join("\n", errors), errors.Count > 0);
    }

    protected override bool ValidateAll(out string error)
    {
        var errors = new List<string>();
        // 無効のものは、検証しない。
        foreach (var t in _defs.Where(t => t != null && !Disabled[t.Id])) ProjectService.TapDanceToEntry(t!, errors);
        error = string.Join("\n", errors);
        if (errors.Count > 0) return false;
        Result = _defs.Where(t => t != null).Select(t => { t!.Disabled = Disabled[t.Id]; return t; }).ToList();
        return true;
    }
}
