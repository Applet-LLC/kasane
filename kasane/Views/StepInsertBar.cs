using System.Windows;
using System.Windows.Controls;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>
/// Sequence の手順を書く複数行の欄に、1行を挿入する補助ボタンの列（キーを押して入力・選ぶ・マウス）。
/// 欄そのもの（TextBox）はそのままで、文字で書くこともできる。挿入するのは、カーソルがある行の次の1行（<see cref="StepInsert"/>）。
/// 各部品の Name は「欄の Name + Command / Capture / Pick / Mouse」（UI Automation 用）。マウスのメニューの項目は「欄の Name + Mouse_ + 内容」。
/// </summary>
public static class StepInsertBar
{
    /// <summary>マウスのメニューに出す項目（表示, 挿入する行）。書式は StepText / HelperMouseButtons が受け付けるもの。</summary>
    internal static (string Label, string Line, string Id)[] MouseItems => new (string Label, string Line, string Id)[]
    {
        (Loc.T("左ボタンをクリック", "Click left button"), "mouse left", "left"),
        (Loc.T("右ボタンをクリック", "Click right button"), "mouse right", "right"),
        (Loc.T("中ボタンをクリック", "Click middle button"), "mouse middle", "middle"),
        (Loc.T("戻るボタン(X1)をクリック", "Click Back button (X1)"), "mouse x1", "x1"),
        (Loc.T("進むボタン(X2)をクリック", "Click Forward button (X2)"), "mouse x2", "x2"),
        ("", "", ""), // 区切り
        (Loc.T("左ボタンを押す", "Press left button"), "mouse left down", "left_down"),
        (Loc.T("左ボタンを離す", "Release left button"), "mouse left up", "left_up"),
        (Loc.T("右ボタンを押す", "Press right button"), "mouse right down", "right_down"),
        (Loc.T("右ボタンを離す", "Release right button"), "mouse right up", "right_up"),
        (Loc.T("中ボタンを押す", "Press middle button"), "mouse middle down", "middle_down"),
        (Loc.T("中ボタンを離す", "Release middle button"), "mouse middle up", "middle_up"),
        ("", "", ""),
        (Loc.T("今の位置から動かす(x y)", "Move from current position (x y)"), "move 10 0", "move"),
        (Loc.T("画面の座標へ動かす(x y)", "Move to screen coordinates (x y)"), "moveto 100 100", "moveto"),
        ("", "", ""),
        (Loc.T("ホイール 上へ1ノッチ", "Wheel: up 1 notch"), "wheel 120", "wheel_up"),
        (Loc.T("ホイール 下へ1ノッチ", "Wheel: down 1 notch"), "wheel -120", "wheel_down"),
        (Loc.T("横ホイール 右へ1ノッチ", "Horizontal wheel: right 1 notch"), "hwheel 120", "hwheel_right"),
        (Loc.T("横ホイール 左へ1ノッチ", "Horizontal wheel: left 1 notch"), "hwheel -120", "hwheel_left"),
    };

    public static StackPanel Create(TextBox box)
    {
        void Insert(string line)
        {
            var (text, caret) = StepInsert.InsertLine(box.Text, box.CaretIndex, line);
            box.Text = text;
            box.CaretIndex = caret;
            box.Focus();
        }

        var command = new ComboBox
        {
            Name = box.Name + "Command",
            Width = 70,
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = Loc.T("押して入力・選ぶで挿入する行の命令。key = 押して離す、down = 押す、up = 離す。", "The command for lines inserted by the capture and pick buttons. key = press and release, down = press, up = release."),
        };
        foreach (var c in new[] { "key", "down", "up" }) command.Items.Add(c);
        command.SelectedIndex = 0;

        var capture = new Button
        {
            Name = box.Name + "Capture",
            Content = Loc.T("キーを押して挿入", "Press a key to insert"),
            Padding = new Thickness(8, 1, 8, 1),
            Focusable = false, // キー操作でボタンが反応して、取り込んだキーが重ならないように
            ToolTip = Loc.T("このボタンを押したあと、入れたいキーを押します(Esc で取消)。Win キーや Alt+Tab は入力できないので「選ぶ...」を使ってください。", "After clicking this button, press the key to insert (Esc cancels). The Win key and Alt+Tab cannot be captured; use \"Pick and insert...\" instead."),
        };
        KeycodeCapture? session = null;
        session = new KeycodeCapture(
            token => Insert($"{command.SelectedItem} {token}"),
            () => capture.Content = Loc.T("キーを押して挿入", "Press a key to insert"));
        capture.Click += (_, _) =>
        {
            if (session.IsActive) { session.Cancel(); return; }
            capture.Content = Loc.T("キーを押してください…", "Press a key…");
            session.Start();
        };
        box.Unloaded += (_, _) => session.Cancel();
        box.Loaded += (_, _) =>
        {
            if (Window.GetWindow(box) is { } w) w.Deactivated += (_, _) => session.Cancel();
        };

        var pick = new Button { Name = box.Name + "Pick", Content = Loc.T("選んで挿入...", "Pick and insert..."), Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(8, 1, 8, 1) };
        pick.Click += (_, _) =>
        {
            session.Cancel();
            var dlg = new KeycodePickerWindow(allowMods: true) { Owner = Window.GetWindow(box) };
            if (dlg.ShowDialog() == true && dlg.ResultToken.Length > 0) Insert($"{command.SelectedItem} {dlg.ResultToken}");
        };

        var mouse = new Button { Name = box.Name + "Mouse", Content = Loc.T("マウス操作を挿入 ▾", "Insert mouse action ▾"), Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(8, 1, 8, 1) };
        var menu = new ContextMenu();
        foreach (var (label, line, id) in MouseItems)
        {
            if (line.Length == 0) { menu.Items.Add(new Separator()); continue; }
            var item = new MenuItem { Name = box.Name + "Mouse_" + id, Header = label, ToolTip = line };
            string text = line;
            item.Click += (_, _) => Insert(text);
            menu.Items.Add(item);
        }
        mouse.ContextMenu = menu;
        mouse.Click += (_, _) =>
        {
            menu.PlacementTarget = mouse;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        panel.Children.Add(command);
        panel.Children.Add(capture);
        panel.Children.Add(pick);
        panel.Children.Add(mouse);
        return panel;
    }
}
