using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>
/// 画面の「?」ボタンと F1（2026-11-08、UI の見直し）。画面には説明の段落を出さず、マニュアルの、その画面（タブ・パネル）を説明している節を開く
/// （<see cref="HelpTopics"/> の対応表。節へ飛ぶのは <see cref="ManualLocator.OpenSection"/>）。
/// タブやパネルを切り替える画面では、<c>topic</c> に今のタブ・パネルの名前を返す関数を渡す。
/// </summary>
public static class HelpButton
{
    /// <summary>「?」ボタンを作る。AutomationId は <c>HelpButton</c>。</summary>
    public static Button Create(Func<string> topic)
    {
        var b = new Button
        {
            Name = "HelpButton",
            Content = "?",
            Width = 26,
            MinHeight = 24,
            Padding = new Thickness(0),
            FontWeight = FontWeights.Bold,
            Focusable = false, // 押してもフォーカスを奪わない（入力中の欄から離れないように）
            ToolTip = Loc.T("この画面の説明をマニュアルで開きます (F1)", "Open the manual for this screen (F1)"),
        };
        b.Click += (_, _) => Open(Window.GetWindow(b), topic());
        return b;
    }

    /// <param name="topic">画面の名前（<see cref="HelpTopics"/> の定数）。</param>
    public static Button Create(string topic) => Create(() => topic);

    /// <summary>画面で F1 を押したときに、同じ節を開く。</summary>
    public static void AttachF1(Window window, Func<string> topic)
    {
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.F1 || e.Handled) return;
            e.Handled = true;
            Open(window, topic());
        };
    }

    public static void AttachF1(Window window, string topic) => AttachF1(window, () => topic);

    /// <summary>節を開く。開けなければ、理由を出す。</summary>
    public static void Open(Window? owner, string topic)
    {
        var s = HelpTopics.For(topic);
        string? error = ManualLocator.OpenSection(AppContext.BaseDirectory, s.Page, s.Anchor);
        if (error == null) return;
        if (owner != null) MessageBox.Show(owner, error, AppInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
        else MessageBox.Show(error, AppInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
