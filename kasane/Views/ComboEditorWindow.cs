using System.Windows.Controls;
using Kasane.Interop;
using Kasane.Layout;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>コンボ（2キー同時押しで別のキーを送る。v1: 2キー限定・出力はKC_*のみ・キーの重複禁止）の編集。</summary>
public sealed class ComboEditorWindow : IdListEditorWindow
{
    private readonly KeymapProject _project;
    private readonly KeyboardLayout _layout;
    private readonly ComboDef?[] _combos = new ComboDef?[KbdLayerClient.MaxComboCount];

    public List<ComboDef> Result { get; private set; } = new();

    public ComboEditorWindow(KeymapProject project, KeyboardLayout layout)
        : base(Loc.T("コンボ", "Combo"), KbdLayerClient.MaxComboCount,
            Loc.T(("コンボは、2つのキーをほぼ同時(タイミング設定の ComboTerm 以内)に押すと、代わりに別のキーを送ります。\n" +
                   "出力は、キー(KC_*)、LCTL(KC_Z) のような修飾付きキー(押している間)、MACRO(n)(成立したときに1回実行)のいずれかです。" +
                   "制約: メンバーは2キーだけで、同じキーを2つ以上のコンボには使えません。" +
                   "コンボは物理キーに対して働くので、どのレイヤーでも有効です。\n" +
                   "「無効にする」にチェックを付けると、書いた内容を残したまま、キーボードには適用されません(メンバーのキーは、ほかのコンボに使えます)。"),
                  ("A combo sends a different key when two keys are pressed at almost the same time (within ComboTerm in the timing settings).\n" +
                   "The output is one of: a key (KC_*), a key with modifiers such as LCTL(KC_Z) (held while the combo is pressed), or MACRO(n) (runs once when the combo fires). " +
                   "Limits: a combo has exactly two member keys, and a key can't be used in more than one combo. " +
                   "Combos work on physical keys, so they apply on every layer.\n" +
                   "If you check \"Disable\", the content is kept but not applied to the keyboard (its member keys can be used in other combos).")))
    {
        _project = project;
        _layout = layout;
        foreach (var c in project.Combos)
            if (c.Id >= 0 && c.Id < _combos.Length)
            {
                _combos[c.Id] = new ComboDef { Id = c.Id, Slot1 = c.Slot1, Slot2 = c.Slot2, Output = c.Output };
                LoadDisabled(c.Id, c.Disabled);
            }
        LoadNames(project.ComboNames);
    }

    /// <summary>
    /// 最初の空き番号に、キー1 を <paramref name="slot"/> にしたコンボを作って選ぶ（キー2 と送るキーは利用者が選ぶ。送るキーの既定は KC_ESC）。
    /// プロジェクトは、適用・OK まで変わらない。空き番号が無ければ -1。
    /// </summary>
    public int StartNew(int slot)
    {
        int id = FreeId();
        if (id < 0) return -1;
        _combos[id] = new ComboDef { Id = id, Slot1 = slot, Slot2 = -1, Output = "KC_ESC" };
        RefreshItem(id);
        ShowId(id);
        return id;
    }

    protected override bool CanReorder => true;
    protected override void ClearItem(int id) => _combos[id] = null;
    protected override void CopyItem(int from, int to) =>
        _combos[to] = _combos[from] is { } c ? new ComboDef { Id = to, Slot1 = c.Slot1, Slot2 = c.Slot2, Output = c.Output } : null;
    protected override void SwapItems(int a, int b)
    {
        (_combos[a], _combos[b]) = (_combos[b], _combos[a]);
        if (_combos[a] != null) _combos[a]!.Id = a;
        if (_combos[b] != null) _combos[b]!.Id = b;
    }

    // 複製したコンボは、元と同じキーを使うので、無効にしておく（1つのキーは、有効なコンボ1つにしか使えない）。
    protected override void OnDuplicated(int id) => SetDisabled(id, true);

    protected override string? DuplicatedNote(int id) =>
        Loc.T($"コンボ{id}に複製しました。同じキーは1つのコンボにしか使えないので、無効にしてあります。キーを変えてから、「無効にする」を外してください。",
              $"Duplicated to combo {id}. A key can be used in only one combo, so it is disabled. Change its keys, then clear \"Disable\".");

    // ほかの画面でマクロを削除して参照を外したとき: 出力がそのマクロのコンボを無効にする（プロジェクトと同じ規則）。
    public override void ReferencesRemoved(RefKind kind, int id)
    {
        for (int i = 0; i < _combos.Length; i++)
            if (_combos[i] is { } c && !Disabled[i] && ProjectService.RefersTo(c.Output, kind, id)) SetDisabled(i, true);
        ShowId(Math.Max(0, CurrentId));
    }

    private string SlotName(int slot) => slot >= 0 && slot < _layout.Slots.Count ? _layout.Slots[slot].PrintedLabel : "?";

    protected override string Summary(int id) =>
        _combos[id] is { } c ? $"{SlotName(c.Slot1)} + {SlotName(c.Slot2)} → {c.Output}" : "";

    protected override void ShowDetail(int id)
    {
        var items = new List<string> { Loc.T("(未使用)", "(unused)") };
        items.AddRange(_layout.Slots.Select(s => s.Description));
        var m1 = new ComboBox { Name = "Member1Combo", Width = 300, ItemsSource = items, IsEditable = false };
        var m2 = new ComboBox { Name = "Member2Combo", Width = 300, ItemsSource = items, IsEditable = false };
        var output = KeycodeBox(_combos[id]?.Output ?? "KC_ESC", "OutputBox");
        m1.SelectedIndex = _combos[id] is { } c1 ? c1.Slot1 + 1 : 0;
        m2.SelectedIndex = _combos[id] is { } c2 ? c2.Slot2 + 1 : 0;

        void Changed()
        {
            if (m1.SelectedIndex <= 0 && m2.SelectedIndex <= 0) _combos[id] = null;
            else _combos[id] = new ComboDef { Id = id, Slot1 = m1.SelectedIndex - 1, Slot2 = m2.SelectedIndex - 1, Output = output.Text.Trim() };
            RefreshItem(id);
            var errors = new List<string>();
            if (_combos[id] is { } c) Check(c, errors);
            ShowValidation(errors.Count == 0 ? "OK" : string.Join("\n", errors), errors.Count > 0);
        }
        m1.SelectionChanged += (_, _) => Changed();
        m2.SelectionChanged += (_, _) => Changed();
        output.TextChanged += (_, _) => Changed();

        Detail.Children.Add(Row(Loc.T("キー1", "Key 1"), m1));
        Detail.Children.Add(Row(Loc.T("キー2", "Key 2"), m2));
        Detail.Children.Add(Row(Loc.T("送るキー", "Output key"), KeycodeField.Wrap(output, acceptsMacro: true)));
    }

    private void Check(ComboDef c, List<string> errors)
    {
        if (c.Slot1 < 0 || c.Slot2 < 0) { errors.Add(Loc.T($"コンボ{c.Id}: キーを2つ選んでください", $"Combo {c.Id}: select two keys")); return; }
        ProjectService.ComboToEntry(_project, _layout, c, errors);
    }

    // 「無効にする」のチェックを外したとき: その項目を検証し、ほかの有効なコンボとキーが重ならないかも調べる。
    protected override void OnItemEnabled(int id)
    {
        var errors = new List<string>();
        if (_combos[id] is { } c)
        {
            Check(c, errors);
            foreach (var other in _combos.Where(o => o != null && o.Id != id && !Disabled[o.Id]).Select(o => o!))
                foreach (var slot in new[] { c.Slot1, c.Slot2 })
                    if (slot >= 0 && (other.Slot1 == slot || other.Slot2 == slot))
                        errors.Add(Loc.T($"「{SlotName(slot)}」がコンボ{other.Id}にも使われています(1つのキーは1つのコンボにしか使えません)", $"\"{SlotName(slot)}\" is also used in combo {other.Id} (a key can be used in only one combo)"));
        }
        ShowValidation(errors.Count == 0 ? "OK" : string.Join("\n", errors.Distinct()), errors.Count > 0);
    }

    private List<ComboDef> ResultList() =>
        _combos.Where(c => c != null).Select(c => { c!.Disabled = Disabled[c.Id]; return c; }).ToList();

    protected override bool ValidateAll(out string error)
    {
        var errors = new List<string>();
        var used = new Dictionary<int, int>(); // スロット → コンボID
        // 無効のコンボは、検証しない。メンバーのキーも、重複の検査に含めない。
        foreach (var c in _combos.Where(c => c != null && !Disabled[c.Id]).Select(c => c!))
        {
            Check(c, errors);
            foreach (var slot in new[] { c.Slot1, c.Slot2 })
            {
                if (slot < 0) continue;
                if (used.TryGetValue(slot, out var other))
                    errors.Add(Loc.T($"「{SlotName(slot)}」がコンボ{other}とコンボ{c.Id}の両方に使われています(1つのキーは1つのコンボにしか使えません)", $"\"{SlotName(slot)}\" is used in both combo {other} and combo {c.Id} (a key can be used in only one combo)"));
                else used[slot] = c.Id;
            }
        }
        error = string.Join("\n", errors.Distinct());
        if (errors.Count > 0) return false;
        Result = ResultList();
        return true;
    }
}
