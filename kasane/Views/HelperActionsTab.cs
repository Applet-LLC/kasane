using System.Windows;
using System.Windows.Controls;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>
/// 設定画面の「ヘルパー動作」タブ（2026-10-08、P4）。HLP(id) のキーから呼ぶ動作（Actions）と、F13〜F24 などのキーフックで受ける
/// トリガー（Triggers）の一覧と、追加・編集・複製・削除。
/// </summary>
internal sealed class HelperActionsTab : HelperTabBase
{
    private readonly ListBox _actions = ListBoxOf("ActionsList", 100);
    private readonly ListBox _triggers = ListBoxOf("TriggersList", 100);
    private readonly Button _addAction, _editAction, _copyAction, _deleteAction, _upAction, _downAction;
    private readonly Button _addTrigger, _editTrigger, _copyTrigger, _deleteTrigger;
    private readonly KeymapProject? _project;
    private readonly Kasane.Layout.KeyboardLayout? _layout;

    /// <summary>
    /// 削除のときに「使っている場所の割り当ても外す」を選んだ HLP の番号（2026-11-06、GUI 計画 1-3）。設定画面が、OK で保存できたあと、
    /// プロジェクトから外す（<see cref="ProjectService.RemoveReferences"/>）。キャンセルなら外さない。
    /// </summary>
    public List<int> PendingUnassign { get; } = new();

    /// <param name="project">このキーボードのプロジェクト（削除の確認で、HLP(n) を使っている割り当てを調べる。null なら調べない）。</param>
    public HelperActionsTab(HelperSettingsModel model, Window owner, KeymapProject? project = null, Kasane.Layout.KeyboardLayout? layout = null)
        : base(model, owner, "Helper")
    {
        _project = project;
        _layout = layout;
        _addAction = Btn("AddActionButton", Loc.T("追加...", "Add..."));
        _upAction = Btn("MoveActionUpButton", Loc.T("上へ", "Up"), 56);
        _downAction = Btn("MoveActionDownButton", Loc.T("下へ", "Down"), 56);
        _editAction = Btn("EditActionButton", Loc.T("編集...", "Edit..."));
        _copyAction = Btn("CopyActionButton", Loc.T("複製", "Duplicate"));
        _deleteAction = Btn("DeleteActionButton", Loc.T("削除", "Delete"));
        _addTrigger = Btn("AddTriggerButton", Loc.T("追加...", "Add..."));
        _editTrigger = Btn("EditTriggerButton", Loc.T("編集...", "Edit..."));
        _copyTrigger = Btn("CopyTriggerButton", Loc.T("複製", "Duplicate"));
        _deleteTrigger = Btn("DeleteTriggerButton", Loc.T("削除", "Delete"));

        Stack.Children.Add(new TextBlock { Text = Loc.T("動作", "Actions"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0),
                                           ToolTip = Loc.T("HLP(Id) を割り当てたキーで動きます", "Run from keys assigned HLP(Id)") });
        Stack.Children.Add(_actions);
        Stack.Children.Add(Buttons(_addAction, _editAction, _copyAction, _deleteAction, _upAction, _downAction));
        Stack.Children.Add(new TextBlock { Text = Loc.T("トリガー", "Triggers"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0),
                                           ToolTip = Loc.T("F13 などのキーで動きます(トレイのキーフックで受けます)", "Run from keys such as F13 (received by the tray's key hook)") });
        Stack.Children.Add(_triggers);
        Stack.Children.Add(Buttons(_addTrigger, _editTrigger, _copyTrigger, _deleteTrigger));

        _addAction.Click += (_, _) => AddAction();
        _editAction.Click += (_, _) => EditAction();
        _copyAction.Click += (_, _) => CopyAction();
        _deleteAction.Click += (_, _) => DeleteAction();
        _upAction.Click += (_, _) => MoveAction(-1);
        _downAction.Click += (_, _) => MoveAction(+1);
        _actions.MouseDoubleClick += (_, _) => EditAction();
        _actions.SelectionChanged += (_, _) => UpdateButtons();
        _addTrigger.Click += (_, _) => AddTrigger();
        _editTrigger.Click += (_, _) => EditTrigger();
        _copyTrigger.Click += (_, _) => CopyTrigger();
        _deleteTrigger.Click += (_, _) => DeleteTrigger();
        _triggers.MouseDoubleClick += (_, _) => EditTrigger();
        _triggers.SelectionChanged += (_, _) => UpdateButtons();
        Init();
    }

    protected override void Refresh() => Refresh(_actions.SelectedIndex, _triggers.SelectedIndex);

    private void Refresh(int actionIndex, int triggerIndex)
    {
        _actions.Items.Clear();
        foreach (var a in Config.Actions) _actions.Items.Add(HelperSummary.Action(a));
        _triggers.Items.Clear();
        foreach (var t in Config.Triggers) _triggers.Items.Add(HelperSummary.Trigger(t));
        if (actionIndex >= 0 && actionIndex < _actions.Items.Count) _actions.SelectedIndex = actionIndex;
        if (triggerIndex >= 0 && triggerIndex < _triggers.Items.Count) _triggers.SelectedIndex = triggerIndex;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool actionSelected = Editable && _actions.SelectedIndex >= 0;
        bool triggerSelected = Editable && _triggers.SelectedIndex >= 0;
        _addAction.IsEnabled = _addTrigger.IsEnabled = Editable;
        _editAction.IsEnabled = _copyAction.IsEnabled = _deleteAction.IsEnabled = actionSelected;
        _upAction.IsEnabled = actionSelected && _actions.SelectedIndex > 0;
        _downAction.IsEnabled = actionSelected && _actions.SelectedIndex < Config.Actions.Count - 1;
        _editTrigger.IsEnabled = _copyTrigger.IsEnabled = _deleteTrigger.IsEnabled = triggerSelected;
    }

    // ---- 動作 ----

    private void AddAction()
    {
        int id = Model.NextFreeActionId();
        if (id < 0) { MessageBox.Show(Owner, Loc.T("使える Id がありません。", "No Id is available."), Loc.T("動作の追加", "Add Action")); return; }
        var dlg = new HelperActionEditorWindow(Loc.T("動作の追加", "Add Action"), HelperEditMode.Action, id, null, null, null,
            new HelperActionBody { Type = HelperActionBody.Launch }, otherId => IdInUse(otherId, -1)) { Owner = Owner };
        if (dlg.ShowDialog() != true) return;
        Config.Actions.Add(new HelperAction { Id = dlg.ResultId, Name = dlg.ResultName, Action = dlg.ResultBody });
        Refresh(Config.Actions.Count - 1, _triggers.SelectedIndex);
    }

    private void EditAction()
    {
        int i = _actions.SelectedIndex;
        if (!Editable || i < 0 || i >= Config.Actions.Count) return;
        var a = Config.Actions[i];
        var dlg = new HelperActionEditorWindow(Loc.T("動作の編集", "Edit Action"), HelperEditMode.Action, a.Id, a.Name, null, null,
            HelperSummary.CloneAction(a).Action, otherId => IdInUse(otherId, i)) { Owner = Owner };
        if (dlg.ShowDialog() != true) return;
        a.Id = dlg.ResultId;
        a.Name = dlg.ResultName;
        a.Action = dlg.ResultBody;
        Refresh(i, _triggers.SelectedIndex);
    }

    private void CopyAction()
    {
        int i = _actions.SelectedIndex;
        if (!Editable || i < 0 || i >= Config.Actions.Count) return;
        int id = Model.NextFreeActionId();
        if (id < 0) { MessageBox.Show(Owner, Loc.T("使える Id がありません。", "No Id is available."), Loc.T("動作の複製", "Duplicate Action")); return; }
        var copy = HelperSummary.CloneAction(Config.Actions[i]);
        copy.Id = id;
        copy.Name = string.IsNullOrWhiteSpace(copy.Name) ? null : copy.Name + Loc.T("(複製)", " (copy)");
        Config.Actions.Insert(i + 1, copy);
        Refresh(i + 1, _triggers.SelectedIndex);
    }

    private void DeleteAction()
    {
        int i = _actions.SelectedIndex;
        if (!Editable || i < 0 || i >= Config.Actions.Count) return;
        var action = Config.Actions[i];
        int id = action.Id;
        // HLP(n) は、このキーボード用 → 共通の順に引く。もう一方の範囲に同じ Id があれば、キーはそちらを実行し続けるので、割り当ては外さない。
        var other = Scope == HelperScope.Device ? HelperScope.Global : HelperScope.Device;
        bool otherHas = (other == HelperScope.Global || Model.HasDevice) && Model.Config(other).Actions.Any(a => a.Id == id);
        var refs = _project == null || otherHas ? new List<string>()
            : ProjectService.FindReferences(_project, RefKind.Helper, id).Select(r => ProjectService.DescribeReference(_project, _layout, r)).ToList();
        var notes = new List<string>();
        if (otherHas)
            notes.Add(other == HelperScope.Global
                ? Loc.T($"全キーボード共通にも HLP({id}) の動作があるので、HLP({id}) のキーは、そちらを実行します。", $"A shared HLP({id}) action also exists, so HLP({id}) keys will run that one.")
                : Loc.T($"このキーボード用にも HLP({id}) の動作があるので、このキーボードの HLP({id}) のキーは、そちらを実行します。", $"This keyboard also has an HLP({id}) action, so this keyboard's HLP({id}) keys will run that one."));
        if (Scope == HelperScope.Global)
            notes.Add(Loc.T("全キーボード共通の動作です。ほかのキーボードの割り当ては確認できません(このキーボードの分だけ外します)。",
                            "This is a shared action. Assignments on other keyboards can't be checked (only this keyboard's are removed)."));
        string what = string.IsNullOrWhiteSpace(action.Name) ? $"HLP({id})" : Loc.T($"HLP({id})「{action.Name}」", $"HLP({id}) \"{action.Name}\"");
        var dlg = new DeleteConfirmWindow(what, refs, notes.Count > 0 ? string.Join("\n", notes) : null) { Owner = Owner };
        if (dlg.ShowDialog() != true) return;
        Config.Actions.RemoveAt(i);
        if (dlg.Unassign) PendingUnassign.Add(id);
        Refresh(Math.Min(i, Config.Actions.Count - 1), _triggers.SelectedIndex);
    }

    // 並べ替え: 一覧の表示の順だけが変わる（Id は変わらないので、HLP(n) の割り当てはそのまま）。
    private void MoveAction(int delta)
    {
        int i = _actions.SelectedIndex, j = i + delta;
        if (!Editable || i < 0 || j < 0 || j >= Config.Actions.Count) return;
        (Config.Actions[i], Config.Actions[j]) = (Config.Actions[j], Config.Actions[i]);
        Refresh(j, _triggers.SelectedIndex);
    }

    private bool IdInUse(int id, int exceptIndex) =>
        Config.Actions.Where((a, index) => index != exceptIndex).Any(a => a.Id == id);

    // ---- トリガー ----

    private void AddTrigger()
    {
        var dlg = new HelperActionEditorWindow(Loc.T("トリガーの追加", "Add Trigger"), HelperEditMode.Trigger, 0, null, "KC_F13", null,
            new HelperActionBody { Type = HelperActionBody.Launch }) { Owner = Owner };
        if (dlg.ShowDialog() != true) return;
        Config.Triggers.Add(new HelperTrigger { Key = dlg.ResultKey, Mods = dlg.ResultMods, Action = dlg.ResultBody });
        Refresh(_actions.SelectedIndex, Config.Triggers.Count - 1);
    }

    private void EditTrigger()
    {
        int i = _triggers.SelectedIndex;
        if (!Editable || i < 0 || i >= Config.Triggers.Count) return;
        var t = Config.Triggers[i];
        var dlg = new HelperActionEditorWindow(Loc.T("トリガーの編集", "Edit Trigger"), HelperEditMode.Trigger, 0, null, t.Key, t.Mods,
            HelperSummary.CloneTrigger(t).Action) { Owner = Owner };
        if (dlg.ShowDialog() != true) return;
        t.Key = dlg.ResultKey;
        t.Mods = dlg.ResultMods;
        t.Action = dlg.ResultBody;
        Refresh(_actions.SelectedIndex, i);
    }

    private void CopyTrigger()
    {
        int i = _triggers.SelectedIndex;
        if (!Editable || i < 0 || i >= Config.Triggers.Count) return;
        Config.Triggers.Insert(i + 1, HelperSummary.CloneTrigger(Config.Triggers[i]));
        Refresh(_actions.SelectedIndex, i + 1);
    }

    private void DeleteTrigger()
    {
        int i = _triggers.SelectedIndex;
        if (!Editable || i < 0 || i >= Config.Triggers.Count) return;
        if (!Confirm(Loc.T("このトリガーを削除しますか？", "Delete this trigger?"))) return;
        Config.Triggers.RemoveAt(i);
        Refresh(_actions.SelectedIndex, Math.Min(i, Config.Triggers.Count - 1));
    }
}
