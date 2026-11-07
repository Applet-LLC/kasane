using System.Windows;
using System.Windows.Controls;
using Kasane.Model;

namespace Kasane.Views;

/// <summary>
/// 削除の確認（2026-11-06、GUI 計画 1-3）。削除するものを使っている場所の一覧と、「使っている場所の割り当ても外す」（既定でオン）を出す。
/// 使っている場所が無いときは、一覧とチェックを出さない。<see cref="Unassign"/> は「削除」を押したときのチェックの状態。
/// </summary>
public sealed class DeleteConfirmWindow : Window
{
    private readonly CheckBox _unassign;

    /// <summary>使っている場所の割り当ても外すか（使っている場所が無ければ false）。</summary>
    public bool Unassign => _unassign.IsChecked == true && _unassign.Visibility == Visibility.Visible;

    /// <param name="what">削除するもの（例: 「マクロ3「挨拶」」）。</param>
    /// <param name="references">使っている場所の説明。</param>
    /// <param name="note">一覧の下に添える補足（ほかのキーボードは調べられない、など。無ければ null）。</param>
    /// <param name="commitNote">いつ決まるかの一文（null なら「編集画面で OK か適用を押したとき」）。</param>
    public DeleteConfirmWindow(string what, IReadOnlyList<string> references, string? note = null, string? commitNote = null)
    {
        Title = Loc.T("削除の確認", "Confirm Delete");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var stack = new StackPanel { Margin = new Thickness(14) };
        stack.Children.Add(new TextBlock { Text = Loc.T($"{what}を削除しますか?", $"Delete {what}?"), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });

        _unassign = new CheckBox
        {
            Name = "UnassignCheck",
            IsChecked = true,
            Margin = new Thickness(0, 8, 0, 0),
            Content = new TextBlock
            {
                Text = Loc.T("使っている場所の割り当ても外す(キーの割り当ては ▽ 透過に戻ります。レイヤー0では、刻印のキーとして動きます)",
                             "Also remove it where it is used (key assignments go back to ▽ transparent; on layer 0 the key works as its printed key)"),
                TextWrapping = TextWrapping.Wrap, Width = 500,
            },
        };
        var keepNote = new TextBlock
        {
            Text = Loc.T("外さないと、使っている場所は残り、そのキーを押しても何もしなくなります。", "If you don't, the places stay, and pressing those keys does nothing."),
            TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Palette.SubText, Margin = new Thickness(20, 2, 0, 0), Visibility = Visibility.Collapsed,
        };
        _unassign.Checked += (_, _) => keepNote.Visibility = Visibility.Collapsed;
        _unassign.Unchecked += (_, _) => keepNote.Visibility = Visibility.Visible;

        if (references.Count > 0)
        {
            stack.Children.Add(new TextBlock { Text = Loc.T($"次の {references.Count} か所で使われています:", $"It is used in {references.Count} place(s):"), Margin = new Thickness(0, 8, 0, 2) });
            var list = new ListBox { Name = "ReferenceList", MaxHeight = 180 };
            foreach (var r in references) list.Items.Add(r);
            stack.Children.Add(list);
            stack.Children.Add(_unassign);
            stack.Children.Add(keepNote);
        }
        else
        {
            _unassign.Visibility = Visibility.Collapsed;
            stack.Children.Add(new TextBlock { Text = Loc.T("どこにも使われていません。", "It is not used anywhere."), Foreground = AppTheme.Palette.SubText, Margin = new Thickness(0, 6, 0, 0) });
        }
        if (note != null)
            stack.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Palette.SubText, Margin = new Thickness(0, 8, 0, 0) });
        stack.Children.Add(new TextBlock
        {
            Text = commitNote ?? Loc.T("削除は、編集画面で OK か適用を押したときに決まります。", "The deletion takes effect when you press OK or Apply in the editor."),
            TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Palette.SubText, Margin = new Thickness(0, 8, 0, 0),
        });

        var delete = new Button { Name = "DeleteButton", Content = Loc.T("削除", "Delete"), Width = 90, IsDefault = true };
        delete.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Name = "CancelButton", Content = Loc.T("キャンセル", "Cancel"), Width = 90, IsCancel = true, Margin = new Thickness(6, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(delete);
        buttons.Children.Add(cancel);
        stack.Children.Add(buttons);
        Content = stack;
    }
}
