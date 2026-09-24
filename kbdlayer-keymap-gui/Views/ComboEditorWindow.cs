using System.Windows.Controls;
using KbdLayerKeymapGui.Interop;
using KbdLayerKeymapGui.Layout;
using KbdLayerKeymapGui.Model;

namespace KbdLayerKeymapGui.Views;

/// <summary>コンボ（2キー同時押しで別のキーを送る。v1: 2キー限定・出力はKC_*のみ・キーの重複禁止）の編集。</summary>
public sealed class ComboEditorWindow : IdListEditorWindow
{
    private readonly KeymapProject _project;
    private readonly KeyboardLayout _layout;
    private readonly ComboDef?[] _combos = new ComboDef?[KbdLayerClient.MaxComboCount];

    public List<ComboDef> Result { get; private set; } = new();

    public ComboEditorWindow(KeymapProject project, KeyboardLayout layout)
        : base("コンボ", KbdLayerClient.MaxComboCount,
            "コンボは、2つのキーをほぼ同時（タイミング設定の ComboTerm 以内）に押すと、代わりに別のキーを送ります。\n" +
            "制約: メンバーは2キーだけ、出力は単純なキー(KC_*)だけ、同じキーを2つ以上のコンボに使うことはできません。" +
            "コンボは物理キーに対して働くので、どのレイヤーでも有効です。")
    {
        _project = project;
        _layout = layout;
        foreach (var c in project.Combos)
            if (c.Id >= 0 && c.Id < _combos.Length)
                _combos[c.Id] = new ComboDef { Id = c.Id, Slot1 = c.Slot1, Slot2 = c.Slot2, Output = c.Output };
    }

    private string SlotName(int slot) => slot >= 0 && slot < _layout.Slots.Count ? _layout.Slots[slot].PrintedLabel : "?";

    protected override string Summary(int id) =>
        _combos[id] is { } c ? $"{SlotName(c.Slot1)} + {SlotName(c.Slot2)} → {c.Output}" : "";

    protected override void ShowDetail(int id)
    {
        var items = new List<string> { "（未使用）" };
        items.AddRange(_layout.Slots.Select(s => s.Description));
        var m1 = new ComboBox { Width = 300, ItemsSource = items, IsEditable = false };
        var m2 = new ComboBox { Width = 300, ItemsSource = items, IsEditable = false };
        var output = KeycodeBox(_combos[id]?.Output ?? "KC_ESC");
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

        Detail.Children.Add(Row("キー1", m1));
        Detail.Children.Add(Row("キー2", m2));
        Detail.Children.Add(Row("送るキー", output));
    }

    private void Check(ComboDef c, List<string> errors)
    {
        if (c.Slot1 < 0 || c.Slot2 < 0) { errors.Add($"コンボ{c.Id}: キーを2つ選んでください"); return; }
        ProjectService.ComboToEntry(_project, _layout, c, errors);
    }

    protected override bool ValidateAll(out string error)
    {
        var errors = new List<string>();
        var used = new Dictionary<int, int>(); // スロット → コンボID
        foreach (var c in _combos.Where(c => c != null).Select(c => c!))
        {
            Check(c, errors);
            foreach (var slot in new[] { c.Slot1, c.Slot2 })
            {
                if (slot < 0) continue;
                if (used.TryGetValue(slot, out var other))
                    errors.Add($"「{SlotName(slot)}」がコンボ{other}とコンボ{c.Id}の両方に使われています（1つのキーは1つのコンボにしか使えません）");
                else used[slot] = c.Id;
            }
        }
        error = string.Join("\n", errors.Distinct());
        if (errors.Count > 0) return false;
        Result = _combos.Where(c => c != null).Select(c => c!).ToList();
        return true;
    }
}
