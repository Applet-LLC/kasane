using System.Windows;
using System.Windows.Controls;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>
/// キーコードを文字で入力する欄に、入力の補助ボタンを付ける。
///  - 「押して入力」: 次に押したキーのキーコード名を入れる（<see cref="KeycodeCapture"/>）。
///  - 「選ぶ...」: 画面上のキーボードや一覧から選ぶ（<see cref="KeycodePickerWindow"/>）。
/// 欄そのもの（TextBox）はそのまま残るので、文字で書くこともできる。ボタンの Name は「欄の Name + Capture / Pick」（UI Automation 用）。
/// </summary>
public static class KeycodeField
{
    /// <param name="append">true なら、欄の末尾に空白区切りで足す（マクロのように複数のキーを並べる欄）。false なら置き換える。</param>
    /// <param name="allowMods">選ぶ画面に修飾キー（Ctrl+C のような形）のチェックを出す。</param>
    /// <param name="acceptsMacro">true なら、MACRO(n) を入れられる欄（コンボ・タップダンス・キー置換の出力など）。「マクロ...」ボタンを足し、
    /// マクロの編集画面の「他の画面の入力欄に挿入」の入れ先にもなる（<see cref="MacroInsert"/>）。</param>
    public static StackPanel Wrap(TextBox box, bool append = false, bool allowMods = true, bool acceptsMacro = false)
    {
        void Put(string token) =>
            box.Text = append && box.Text.Trim().Length > 0 ? box.Text.TrimEnd() + " " + token : token;

        var capture = new Button
        {
            Name = box.Name.Length > 0 ? box.Name + "Capture" : "",
            Content = Loc.T("押して入力", "Press to enter"),
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(8, 1, 8, 1),
            Focusable = false, // キー操作でボタンが反応して、取り込んだキーが重ならないように
            ToolTip = Loc.T("このボタンを押したあと、入力したいキーを押します(Esc で取消)。Win キーや Alt+Tab は入力できないので「選ぶ...」を使ってください。",
                            "After clicking this button, press the key you want to enter (Esc to cancel). The Win key and Alt+Tab cannot be entered, so use \"Pick...\" for those."),
        };
        KeycodeCapture? session = null;
        // 末尾に足していく欄（マクロ）は、取り込みを続けて、何個でも続けて入れられる（Esc かボタンをもう一度押すと終わる）。
        session = new KeycodeCapture(
            token => Put(token),
            () => { capture.Content = Loc.T("押して入力", "Press to enter"); box.Focus(); }) { Continuous = append };
        capture.Click += (_, _) =>
        {
            if (session.IsActive) { session.Cancel(); return; }
            capture.Content = append ? Loc.T("キーを押す(Esc で終了)", "Press keys (Esc to finish)") : Loc.T("キーを押してください…", "Press a key...");
            session.Start();
        };
        box.Unloaded += (_, _) => session.Cancel();
        box.Loaded += (_, _) =>
        {
            if (Window.GetWindow(box) is { } w) w.Deactivated += (_, _) => session.Cancel();
        };

        var pick = new Button
        {
            Name = box.Name.Length > 0 ? box.Name + "Pick" : "",
            Content = Loc.T("選ぶ...", "Pick..."),
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(8, 1, 8, 1),
        };
        pick.Click += (_, _) =>
        {
            session.Cancel();
            if (append)
            {
                // 足していく欄（マクロ）: 「追加」で欄の末尾に足し、画面は開いたまま（続けて選べる）。「閉じる」で終わる。
                new KeycodePickerWindow(allowMods, onAdd: Put) { Owner = Window.GetWindow(box) }.ShowDialog();
                return;
            }
            var dlg = new KeycodePickerWindow(allowMods) { Owner = Window.GetWindow(box) };
            if (dlg.ShowDialog() == true && dlg.ResultToken.Length > 0) Put(dlg.ResultToken);
        };

        // 「×」: 欄を空にする（2026-11-06、GUI 計画 1-3）。空の欄の意味は欄ごと（タップダンスなら「動作なし」）なので、確認は出さない。
        var clear = new Button
        {
            Name = box.Name.Length > 0 ? box.Name + "Clear" : "",
            Content = "×",
            Width = 26,
            Padding = new Thickness(0), // テーマの既定の余白（左右 10）のままだと、幅 26 では字が隠れる（2026-11-07 の実機確認 X1）
            Margin = new Thickness(2, 0, 0, 0),
            Focusable = false,
            ToolTip = Loc.T("欄を空にします。", "Clears the field."),
        };
        clear.Click += (_, _) =>
        {
            session.Cancel();
            box.Clear();
            box.Focus();
        };
        clear.IsEnabled = box.Text.Length > 0;
        box.TextChanged += (_, _) => clear.IsEnabled = box.Text.Length > 0;

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(box);
        panel.Children.Add(clear);
        panel.Children.Add(capture);
        panel.Children.Add(pick);

        if (acceptsMacro)
        {
            MacroInsert.Register(box, Put);
            var macro = new Button
            {
                Name = box.Name.Length > 0 ? box.Name + "Macro" : "",
                Content = Loc.T("マクロ...", "Macro..."),
                Margin = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(8, 1, 8, 1),
                ToolTip = Loc.T("マクロの一覧(番号・名前・中身)から選んで、MACRO(n) を入れます。", "Pick from the macro list (number, name, content) to insert MACRO(n)."),
            };
            macro.Click += (_, _) =>
            {
                session.Cancel();
                var dlg = new MacroPickerWindow { Owner = Window.GetWindow(box) };
                if (dlg.ShowDialog() == true && dlg.Result is { } m) Put(m.Token);
            };
            panel.Children.Add(macro);
        }
        return panel;
    }
}
