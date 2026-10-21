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
    private readonly Button _addAction, _editAction, _copyAction, _deleteAction;
    private readonly Button _addTrigger, _editTrigger, _copyTrigger, _deleteTrigger;

    public HelperActionsTab(HelperSettingsModel model, Window owner)
        : base(model, owner, "Helper",
            Loc.T("キーを押して、プログラムの起動、文字列の入力、マウス操作、レイヤーの操作などを実行します。" +
                "「動作」は HLP(Id) を割り当てたキー、「トリガー」は F13〜F24 などのキーで実行され、変更は OK で保存されます。",
                "Press a key to launch a program, type text, control the mouse, switch layers, and more. " +
                "Actions run from keys assigned HLP(Id); triggers run from keys such as F13 to F24. Changes are saved with OK."))
    {
        _addAction = Btn("AddActionButton", Loc.T("追加...", "Add..."));
        _editAction = Btn("EditActionButton", Loc.T("編集...", "Edit..."));
        _copyAction = Btn("CopyActionButton", Loc.T("複製", "Duplicate"));
        _deleteAction = Btn("DeleteActionButton", Loc.T("削除", "Delete"));
        _addTrigger = Btn("AddTriggerButton", Loc.T("追加...", "Add..."));
        _editTrigger = Btn("EditTriggerButton", Loc.T("編集...", "Edit..."));
        _copyTrigger = Btn("CopyTriggerButton", Loc.T("複製", "Duplicate"));
        _deleteTrigger = Btn("DeleteTriggerButton", Loc.T("削除", "Delete"));

        Stack.Children.Add(new TextBlock { Text = Loc.T("動作(HLP(Id) のキーから呼ぶ)", "Actions (called from HLP(Id) keys)"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
        Stack.Children.Add(_actions);
        Stack.Children.Add(Buttons(_addAction, _editAction, _copyAction, _deleteAction));
        Stack.Children.Add(new TextBlock { Text = Loc.T("トリガー(F13 などのキー。キーフックで受ける)", "Triggers (keys such as F13, received by a key hook)"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        Stack.Children.Add(_triggers);
        Stack.Children.Add(Buttons(_addTrigger, _editTrigger, _copyTrigger, _deleteTrigger));
        Stack.Children.Add(new TextBlock { Text = Loc.T("HLP (Helper): キーに割り当てると、そのキーを押したときに「動作」を実行します。", "HLP (Helper): when assigned to a key, pressing that key runs the action."), TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Palette.SubText, Margin = new Thickness(0, 16, 0, 0) });

        _addAction.Click += (_, _) => AddAction();
        _editAction.Click += (_, _) => EditAction();
        _copyAction.Click += (_, _) => CopyAction();
        _deleteAction.Click += (_, _) => DeleteAction();
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
        copy.Name = string.IsNullOrWhiteSpace(copy.Name) ? null : copy.Name + "(複製)";
        Config.Actions.Insert(i + 1, copy);
        Refresh(i + 1, _triggers.SelectedIndex);
    }

    private void DeleteAction()
    {
        int i = _actions.SelectedIndex;
        if (!Editable || i < 0 || i >= Config.Actions.Count) return;
        if (!Confirm(Loc.T($"HLP({Config.Actions[i].Id}) の動作を削除しますか？\nこの Id をキーに割り当てている場合、そのキーは何もしなくなります。",
            $"Delete the action HLP({Config.Actions[i].Id})?\nIf this Id is assigned to a key, that key will do nothing."))) return;
        Config.Actions.RemoveAt(i);
        Refresh(Math.Min(i, Config.Actions.Count - 1), _triggers.SelectedIndex);
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
