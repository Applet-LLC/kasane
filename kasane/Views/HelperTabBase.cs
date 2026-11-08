using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>
/// 設定画面の「ヘルパー動作」「アプリ別レイヤー」タブの共通部分（2026-10-08、P4）: 範囲（このキーボード／全キーボード共通）の
/// 切り替えと、一覧に添えるボタンの部品。編集は <see cref="HelperSettingsModel"/> のコピーに対して行い、書くのは設定画面のOKのとき。
/// </summary>
internal abstract class HelperTabBase
{
    protected readonly HelperSettingsModel Model;
    protected readonly Window Owner;
    protected readonly StackPanel Stack = new();
    private readonly RadioButton _deviceRadio;
    private readonly RadioButton _globalRadio;
    private readonly TextBlock _scopeInfo = new() { TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Palette.ErrorText, Margin = new Thickness(0, 4, 0, 0) };

    protected HelperScope Scope { get; private set; }

    // タブの先頭の説明の段落は出さない（2026-11-08、UI の見直し。説明は設定ダイアログの「?」で開くマニュアル）。
    // どのタブにも共通する「このキーボードの設定が先」だけを、範囲のツールチップにする。
    protected HelperTabBase(HelperSettingsModel model, Window owner, string idPrefix)
    {
        Model = model;
        Owner = owner;

        string scopeTip = Loc.T("このキーボードの設定が、全キーボード共通の設定より先に使われます。変更は OK で保存します",
                                "This keyboard's settings are used before the shared ones. Changes are saved with OK");
        _deviceRadio = new RadioButton { Name = idPrefix + "ScopeDevice", Content = Loc.T("このキーボード", "This keyboard"), GroupName = idPrefix + "Scope", Margin = new Thickness(0, 0, 16, 0), ToolTip = scopeTip };
        _globalRadio = new RadioButton { Name = idPrefix + "ScopeGlobal", Content = Loc.T("全キーボード共通", "All keyboards (shared)"), GroupName = idPrefix + "Scope", ToolTip = scopeTip };
        var scopeBar = new StackPanel { Orientation = Orientation.Horizontal };
        scopeBar.Children.Add(new TextBlock { Text = Loc.T("対象:", "Target:"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        scopeBar.Children.Add(_deviceRadio);
        scopeBar.Children.Add(_globalRadio);
        Stack.Children.Add(scopeBar);
        Stack.Children.Add(_scopeInfo);

        _deviceRadio.IsEnabled = model.HasDevice;
        Scope = model.HasDevice ? HelperScope.Device : HelperScope.Global;
        (Scope == HelperScope.Device ? _deviceRadio : _globalRadio).IsChecked = true;
        _deviceRadio.Checked += (_, _) => SwitchScope(HelperScope.Device);
        _globalRadio.Checked += (_, _) => SwitchScope(HelperScope.Global);
    }

    public UIElement Root => Stack;

    /// <summary>今の範囲を編集できるか（キーボードの helper.json が壊れているときは、そのキーボードの分は編集できない）。</summary>
    protected bool Editable => Scope == HelperScope.Global || Model.DeviceWritable;

    protected HelperConfig Config => Model.Config(Scope);

    /// <summary>一覧と、ボタンの有効・無効を作り直す。</summary>
    protected abstract void Refresh();

    /// <summary>派生クラスが部品を作り終えたら呼ぶ。</summary>
    protected void Init()
    {
        UpdateScopeInfo();
        Refresh();
    }

    private void SwitchScope(HelperScope scope)
    {
        if (Scope == scope) return;
        Scope = scope;
        UpdateScopeInfo();
        Refresh();
    }

    private void UpdateScopeInfo()
    {
        if (!Model.HasDevice) _scopeInfo.Text = Loc.T("キーボードが決まっていないため、全キーボード共通の設定だけ編集できます。", "No keyboard is selected, so only the shared settings for all keyboards can be edited.");
        else if (Scope == HelperScope.Device && Model.DeviceLoadError != null)
            _scopeInfo.Text = Loc.T(($"このキーボードの helper.json を読めませんでした({Model.DeviceLoadError})。" +
                                     $"この分は編集できません。ファイルを直すか削除してから、開き直してください。\n{Model.DevicePath}"),
                                    ($"Could not read helper.json for this keyboard ({Model.DeviceLoadError}). " +
                                     $"This part cannot be edited. Fix or delete the file, then reopen.\n{Model.DevicePath}"));
        else _scopeInfo.Text = "";
    }

    protected static Button Btn(string name, string text, double width = 80) =>
        new() { Name = name, Content = text, Width = width, Margin = new Thickness(0, 0, 6, 0) };

    protected static ListBox ListBoxOf(string name, double height) =>
        new() { Name = name, Height = height, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 4, 0, 4) };

    protected static StackPanel Buttons(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var b in buttons) row.Children.Add(b);
        return row;
    }

    protected bool Confirm(string message) =>
        MessageBox.Show(Owner, message, Loc.T("確認", "Confirm"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
}
