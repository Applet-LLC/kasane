using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>
/// 割り当ての一覧（<see cref="RemapTextFormatter"/>）を文字で見る画面（2026-11-08、GUI 計画 2-2）。読むだけで、書き換えはしない。
/// 「最新にする」で、エディタで開いている今の内容から作り直す。コピーと、.txt への保存ができる。
/// </summary>
public sealed class RemapTextWindow : Window
{
    private readonly Func<string> _build;
    private readonly TextBox _text = new()
    {
        Name = "RemapText",
        IsReadOnly = true,
        // 日本語も半角2文字分の幅で揃う等幅の字体（「->」と「:」の位置を揃えて出すため。Consolas と Meiryo UI の組み合わせでは、日本語の幅が揃わない）。
        FontFamily = new FontFamily("BIZ UDGothic, MS Gothic"),
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        TextWrapping = TextWrapping.NoWrap,
        AcceptsReturn = true,
    };
    private readonly TextBlock _status = new() { Name = "StatusText", Foreground = AppTheme.Palette.SubText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };

    /// <param name="build">一覧の文字を作る（メイン画面の今のプロジェクトから）。</param>
    /// <param name="fileName">保存のときの既定のファイル名。</param>
    public RemapTextWindow(Func<string> build, string fileName)
    {
        _build = build;
        Title = Loc.T("割り当ての一覧", "Assignment List");
        Width = 900;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var note = new TextBlock
        {
            Text = Loc.T("エディタで開いている今の内容です(保存・適用していない変更も含みます)。読む・コピーするための画面で、ここでは書き換えられません。",
                         "This is what the editor has open now (including changes not yet saved or applied). It is for reading and copying; you can't edit here."),
            TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Palette.SubText, Margin = new Thickness(0, 0, 0, 6),
        };

        var refresh = new Button { Name = "RefreshButton", Content = Loc.T("最新にする", "Refresh"), MinWidth = 90, Margin = new Thickness(0, 0, 6, 0) };
        refresh.Click += (_, _) => { Fill(); _status.Text = Loc.T("最新にしました。", "Refreshed."); };
        var copy = new Button { Name = "CopyButton", Content = Loc.T("コピー", "Copy"), MinWidth = 90, Margin = new Thickness(0, 0, 6, 0) };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(_text.Text); _status.Text = Loc.T("クリップボードにコピーしました。", "Copied to the clipboard."); }
            catch (System.Runtime.InteropServices.ExternalException ex) { _status.Text = Loc.T($"コピーできませんでした: {ex.Message}", $"Couldn't copy: {ex.Message}"); }
        };
        var save = new Button { Name = "SaveButton", Content = Loc.T("保存...", "Save..."), MinWidth = 90, Margin = new Thickness(0, 0, 6, 0) };
        save.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = fileName,
                DefaultExt = ".txt",
                Filter = Loc.T("テキスト (*.txt)|*.txt|すべてのファイル (*.*)|*.*", "Text (*.txt)|*.txt|All files (*.*)|*.*"),
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                File.WriteAllText(dlg.FileName, _text.Text, new UTF8Encoding(true)); // メモ帳以外でも日本語が化けないよう、BOM 付き
                _status.Text = Loc.T($"保存しました: {dlg.FileName}", $"Saved: {dlg.FileName}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _status.Text = Loc.T($"保存できませんでした: {ex.Message}", $"Couldn't save: {ex.Message}");
            }
        };
        var close = new Button { Name = "CloseButton", Content = Loc.T("閉じる", "Close"), MinWidth = 90, IsCancel = true };
        close.Click += (_, _) => Close();

        var buttons = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in new[] { refresh, copy, save, close }) right.Children.Add(b);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        buttons.Children.Add(_status);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(note);
        root.Children.Add(buttons);
        root.Children.Add(_text);
        Content = root;
        Fill();
    }

    private void Fill() => _text.Text = _build();
}
