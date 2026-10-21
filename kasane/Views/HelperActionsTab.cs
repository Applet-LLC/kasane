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
            "キーで、プログラムの起動・文字列の入力・マウス操作・レイヤーの操作などを実行します。" +
            "「動作」は、キー割り当て画面で HLP(Id) を割り当てたキーで実行されます。" +
            "「トリガー」は、F13〜F24 などのキーを、トレイが監視して実行します。" +
            "変更は、OK を押すと保存されます(管理者権限は要りません)。トレイが数秒で読み直します。")
    {
        _addAction = Btn("AddActionButton", "追加...");
        _editAction = Btn("EditActionButton", "編集...");
        _copyAction = Btn("CopyActionButton", "複製");
        _deleteAction = Btn("DeleteActionButton", "削除");
        _addTrigger = Btn("AddTriggerButton", "追加...");
        _editTrigger = Btn("EditTriggerButton", "編集...");
        _copyTrigger = Btn("CopyTriggerButton", "複製");
        _deleteTrigger = Btn("DeleteTriggerButton", "削除");

        Stack.Children.Add(new TextBlock { Text = "動作(HLP(Id) のキーから呼ぶ)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
        Stack.Children.Add(_actions);
        Stack.Children.Add(Buttons(_addAction, _editAction, _copyAction, _deleteAction));
        Stack.Children.Add(new TextBlock { Text = "トリガー(F13 などのキー。キーフックで受ける)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        Stack.Children.Add(_triggers);
        Stack.Children.Add(Buttons(_addTrigger, _editTrigger, _copyTrigger, _deleteTrigger));

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
        if (id < 0) { MessageBox.Show(Owner, "使える Id がありません。", "動作の追加"); return; }
        var dlg = new HelperActionEditorWindow("動作の追加", HelperEditMode.Action, id, null, null, null,
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
        var dlg = new HelperActionEditorWindow("動作の編集", HelperEditMode.Action, a.Id, a.Name, null, null,
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
        if (id < 0) { MessageBox.Show(Owner, "使える Id がありません。", "動作の複製"); return; }
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
        if (!Confirm($"HLP({Config.Actions[i].Id}) の動作を削除しますか？\nこの Id をキーに割り当てている場合、そのキーは何もしなくなります。")) return;
        Config.Actions.RemoveAt(i);
        Refresh(Math.Min(i, Config.Actions.Count - 1), _triggers.SelectedIndex);
    }

    private bool IdInUse(int id, int exceptIndex) =>
        Config.Actions.Where((a, index) => index != exceptIndex).Any(a => a.Id == id);

    // ---- トリガー ----

    private void AddTrigger()
    {
        var dlg = new HelperActionEditorWindow("トリガーの追加", HelperEditMode.Trigger, 0, null, "KC_F13", null,
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
        var dlg = new HelperActionEditorWindow("トリガーの編集", HelperEditMode.Trigger, 0, null, t.Key, t.Mods,
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
        if (!Confirm("このトリガーを削除しますか？")) return;
        Config.Triggers.RemoveAt(i);
        Refresh(_actions.SelectedIndex, Math.Min(i, Config.Triggers.Count - 1));
    }
}
