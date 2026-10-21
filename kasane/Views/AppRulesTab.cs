using System.Windows;
using System.Windows.Controls;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>
/// 設定画面の「アプリ別レイヤー」タブ（2026-10-08、P4）。前面のアプリに応じて、アプリ管理レイヤーを立てる・リマップをOFFにする規則の
/// 一覧と、追加・編集・複製・削除・順序の入れ替え・有効/無効の切り替え。規則は上から順に見て、最初に一致したものを使う
/// （「既定」の規則は、どれにも一致しなかったときに使う）。
/// </summary>
internal sealed class AppRulesTab : HelperTabBase
{
    private readonly ListBox _rules = ListBoxOf("RulesList", 230);
    private readonly Button _add, _edit, _copy, _delete, _up, _down, _toggle;

    public AppRulesTab(HelperSettingsModel model, Window owner)
        : base(model, owner, "Rules",
            "前面のアプリに応じて、アプリ管理レイヤーを自動で立てるか、そのキーボードのリマップをOFF(素通し)にします。" +
            "規則は上から順に見て最初に一致したものを使い、キーボードごとの規則が共通の規則より先です。変更は OK で保存されます。")
    {
        _add = Btn("AddRuleButton", "追加...");
        _edit = Btn("EditRuleButton", "編集...");
        _copy = Btn("CopyRuleButton", "複製");
        _delete = Btn("DeleteRuleButton", "削除");
        _up = Btn("UpRuleButton", "上へ", 60);
        _down = Btn("DownRuleButton", "下へ", 60);
        _toggle = Btn("ToggleRuleButton", "有効/無効", 90);

        Stack.Children.Add(_rules);
        Stack.Children.Add(Buttons(_add, _edit, _copy, _delete, _up, _down, _toggle));

        _add.Click += (_, _) => Add();
        _edit.Click += (_, _) => Edit();
        _copy.Click += (_, _) => Copy();
        _delete.Click += (_, _) => Delete();
        _up.Click += (_, _) => Move(-1);
        _down.Click += (_, _) => Move(1);
        _toggle.Click += (_, _) => Toggle();
        _rules.MouseDoubleClick += (_, _) => Edit();
        _rules.SelectionChanged += (_, _) => UpdateButtons();
        Init();
    }

    protected override void Refresh() => Refresh(_rules.SelectedIndex);

    private void Refresh(int index)
    {
        _rules.Items.Clear();
        foreach (var r in Config.AppLayerRules) _rules.Items.Add(HelperSummary.Rule(r));
        if (index >= 0 && index < _rules.Items.Count) _rules.SelectedIndex = index;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        int i = _rules.SelectedIndex;
        bool selected = Editable && i >= 0;
        _add.IsEnabled = Editable;
        _edit.IsEnabled = _copy.IsEnabled = _delete.IsEnabled = _toggle.IsEnabled = selected;
        _up.IsEnabled = selected && i > 0;
        _down.IsEnabled = selected && i < Config.AppLayerRules.Count - 1;
    }

    private bool Selected(out int i)
    {
        i = _rules.SelectedIndex;
        return Editable && i >= 0 && i < Config.AppLayerRules.Count;
    }

    private void Add()
    {
        if (!Editable) return;
        var dlg = new AppRuleEditorWindow(new AppLayerRule { Enabled = true }) { Owner = Owner };
        if (dlg.ShowDialog() != true) return;
        Config.AppLayerRules.Add(dlg.Result);
        Refresh(Config.AppLayerRules.Count - 1);
    }

    private void Edit()
    {
        if (!Selected(out int i)) return;
        var dlg = new AppRuleEditorWindow(HelperSummary.CloneRule(Config.AppLayerRules[i])) { Owner = Owner };
        if (dlg.ShowDialog() != true) return;
        Config.AppLayerRules[i] = dlg.Result;
        Refresh(i);
    }

    private void Copy()
    {
        if (!Selected(out int i)) return;
        var copy = HelperSummary.CloneRule(Config.AppLayerRules[i]);
        copy.Name = string.IsNullOrWhiteSpace(copy.Name) ? null : copy.Name + "(複製)";
        Config.AppLayerRules.Insert(i + 1, copy);
        Refresh(i + 1);
    }

    private void Delete()
    {
        if (!Selected(out int i)) return;
        if (!Confirm("この規則を削除しますか？")) return;
        Config.AppLayerRules.RemoveAt(i);
        Refresh(Math.Min(i, Config.AppLayerRules.Count - 1));
    }

    private void Move(int delta)
    {
        if (!Selected(out int i)) return;
        int j = i + delta;
        if (j < 0 || j >= Config.AppLayerRules.Count) return;
        (Config.AppLayerRules[i], Config.AppLayerRules[j]) = (Config.AppLayerRules[j], Config.AppLayerRules[i]);
        Refresh(j);
    }

    private void Toggle()
    {
        if (!Selected(out int i)) return;
        Config.AppLayerRules[i].Enabled = !Config.AppLayerRules[i].Enabled;
        Refresh(i);
    }
}
