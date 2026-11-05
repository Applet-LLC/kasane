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

    /// <summary>IME のメニューに出す項目（表示, 挿入する行, Id）。方式を書かない行は自動。</summary>
    internal static (string Label, string Line, string Id)[] ImeItems => new (string Label, string Line, string Id)[]
    {
        (Loc.T("IME を ON にする", "Turn IME on"), "ime on", "on"),
        (Loc.T("IME を OFF にする", "Turn IME off"), "ime off", "off"),
        (Loc.T("IME を切り替える", "Toggle IME"), "ime toggle", "toggle"),
    };

    // 方式を固定する行（ime on imm など）は、2026-11-05 にメニューから外した。切り替え方はトレイが決める
    // （ImeRoutePlanner）。書いた行は、以前の設定との互換のために読める。切り替わらないときは vk IME_ON / vk IME_OFF を使う。

    /// <summary>仮想キー（vk）のメニューに出す項目（表示, 挿入する行, Id）。日本語 IME 向けのキー。</summary>
    internal static (string Label, string Line, string Id)[] VkItems => new (string Label, string Line, string Id)[]
    {
        (Loc.T("IME ON (VK_IME_ON)", "IME on (VK_IME_ON)"), "vk IME_ON", "ime_on"),
        (Loc.T("IME OFF (VK_IME_OFF)", "IME off (VK_IME_OFF)"), "vk IME_OFF", "ime_off"),
        (Loc.T("半角/全角 (VK_KANJI)", "Hankaku/Zenkaku (VK_KANJI)"), "vk KANJI", "kanji"),
        (Loc.T("変換 (VK_CONVERT)", "Convert (VK_CONVERT)"), "vk CONVERT", "convert"),
        (Loc.T("無変換 (VK_NONCONVERT)", "Non-convert (VK_NONCONVERT)"), "vk NONCONVERT", "nonconvert"),
        (Loc.T("ひらがな (VK_DBE_HIRAGANA)", "Hiragana (VK_DBE_HIRAGANA)"), "vk DBE_HIRAGANA", "hiragana"),
        (Loc.T("カタカナ (VK_DBE_KATAKANA)", "Katakana (VK_DBE_KATAKANA)"), "vk DBE_KATAKANA", "katakana"),
        (Loc.T("英数 (VK_DBE_ALPHANUMERIC)", "Alphanumeric (VK_DBE_ALPHANUMERIC)"), "vk DBE_ALPHANUMERIC", "alphanumeric"),
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
            var item = new MenuItem { Name = box.Name + "Mouse_" + id, Header = label.Replace("_", "__"), ToolTip = line }; // _ はアクセスキーの印になるので重ねる（VK_IME_ON が消えないように）
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

        var ime = new Button
        {
            Name = box.Name + "Ime",
            Content = Loc.T("IME 操作を挿入 ▾", "Insert IME action ▾"),
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(8, 1, 8, 1),
            ToolTip = Loc.T("IME を ON / OFF にする行を挿入します。今の状態を見て、違うときだけ変えます。切り替え方は、設定の「IME連動」タブの「IME の切り替え方」で選べます。",
                            "Inserts a line that turns the IME on or off. It changes the IME only if the current state differs. Choose how it is switched under \"How to switch the IME\" on the IME link tab in Settings."),
        };
        var imeMenu = new ContextMenu();
        foreach (var (label, line, id) in ImeItems)
        {
            var item = new MenuItem { Name = box.Name + "Ime_" + id, Header = label.Replace("_", "__"), ToolTip = line }; // _ はアクセスキーの印になるので重ねる（VK_IME_ON が消えないように）
            string text = line;
            item.Click += (_, _) => Insert(text);
            imeMenu.Items.Add(item);
        }
        ime.ContextMenu = imeMenu;
        ime.Click += (_, _) =>
        {
            imeMenu.PlacementTarget = ime;
            imeMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            imeMenu.IsOpen = true;
        };

        var vk = new Button
        {
            Name = box.Name + "Vk",
            Content = Loc.T("仮想キーを挿入 ▾", "Insert virtual key ▾"),
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(8, 1, 8, 1),
            ToolTip = Loc.T("スキャンコードを持たない仮想キー(VK_IME_ON など)を送る行を挿入します。ほかの仮想キーは vk 0x70 や vk LSHIFT+F1 のように直接書けます。", "Inserts a line that sends a virtual key that has no scan code (such as VK_IME_ON). Other virtual keys can be written directly, like vk 0x70 or vk LSHIFT+F1."),
        };
        var vkMenu = new ContextMenu();
        foreach (var (label, line, id) in VkItems)
        {
            var item = new MenuItem { Name = box.Name + "Vk_" + id, Header = label.Replace("_", "__"), ToolTip = line }; // _ はアクセスキーの印になるので重ねる（VK_IME_ON が消えないように）
            string text = line;
            item.Click += (_, _) => Insert(text);
            vkMenu.Items.Add(item);
        }
        vk.ContextMenu = vkMenu;
        vk.Click += (_, _) =>
        {
            vkMenu.PlacementTarget = vk;
            vkMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            vkMenu.IsOpen = true;
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        panel.Children.Add(command);
        panel.Children.Add(capture);
        panel.Children.Add(pick);
        panel.Children.Add(mouse);
        panel.Children.Add(ime);
        panel.Children.Add(vk);
        return panel;
    }
}
