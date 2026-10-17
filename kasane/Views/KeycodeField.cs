using System.Windows;
using System.Windows.Controls;

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
            Content = "押して入力",
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(8, 1, 8, 1),
            Focusable = false, // キー操作でボタンが反応して、取り込んだキーが重ならないように
            ToolTip = "このボタンを押したあと、入力したいキーを押します（Esc で取消）。Win キーや Alt+Tab は取れないので「選ぶ...」を使ってください。",
        };
        KeycodeCapture? session = null;
        session = new KeycodeCapture(
            token => Put(token),
            () => { capture.Content = "押して入力"; box.Focus(); });
        capture.Click += (_, _) =>
        {
            if (session.IsActive) { session.Cancel(); return; }
            capture.Content = "キーを押してください…";
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
            Content = "選ぶ...",
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(8, 1, 8, 1),
        };
        pick.Click += (_, _) =>
        {
            session.Cancel();
            var dlg = new KeycodePickerWindow(allowMods) { Owner = Window.GetWindow(box) };
            if (dlg.ShowDialog() == true && dlg.ResultToken.Length > 0) Put(dlg.ResultToken);
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(box);
        panel.Children.Add(capture);
        panel.Children.Add(pick);

        if (acceptsMacro)
        {
            MacroInsert.Register(box, Put);
            var macro = new Button
            {
                Name = box.Name.Length > 0 ? box.Name + "Macro" : "",
                Content = "マクロ...",
                Margin = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(8, 1, 8, 1),
                ToolTip = "マクロの一覧（番号・名前・中身）から選んで、MACRO(n) を入れます。",
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
