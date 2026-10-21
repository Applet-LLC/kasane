using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kasane.Layout;

namespace Kasane.Views;

/// <summary>
/// キーを選ぶための、画面上のキーボード（同梱のKLEレイアウト。US104・JP109・10key）。キーをクリックすると、そのキーの
/// 標準のキーコード（KC_A など）が <see cref="KeySelected"/> で届く。出力キーを文字で入力しなくても選べるようにするための部品。
/// キーの AutomationId は "Key_&lt;スロット番号&gt;"（KeyboardCanvas と同じ）。
/// </summary>
public sealed class SoftKeyboardPanel : StackPanel
{
    private static Brush SelectedBrush => AppTheme.Palette.KeySelected;

    private readonly KeyboardCanvas _canvas = new();
    private readonly ComboBox _layoutCombo = new() { Name = "SoftKeyboardLayout", Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly List<string> _layoutFiles = new();
    private KeySlot? _selected;

    /// <summary>
    /// いま編集しているキーボードのレイアウトファイル（MainWindow が、プロジェクトを開くたびに設定する）。
    /// パネルは、最初にこのレイアウトを出す（US104 のキーボードを編集中なら US104、テンキーなら 10key）。
    /// 同梱のものでなければ（ユーザー自作の KLE）、一覧に足す。設定されていないときは、言語から JP109 か US104 を選ぶ。
    /// </summary>
    public static string? CurrentLayoutFile { get; set; }

    /// <summary>キーがクリックされた（キーコード名。既定のキーコードが無いキーは来ない）。</summary>
    public event Action<string>? KeySelected;

    public SoftKeyboardPanel()
    {
        Name = "SoftKeyboard";

        foreach (var f in KeyboardLayout.EnumerateBundledLayoutFiles())
        {
            _layoutFiles.Add(f);
            _layoutCombo.Items.Add(KeyboardLayout.NameFromPath(f));
        }
        int current = IndexOfFile(CurrentLayoutFile);
        if (current < 0 && CurrentLayoutFile != null && File.Exists(CurrentLayoutFile))
        {
            _layoutFiles.Add(CurrentLayoutFile);
            _layoutCombo.Items.Add(KeyboardLayout.NameFromPath(CurrentLayoutFile));
            current = _layoutFiles.Count - 1;
        }
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(new TextBlock { Text = "配列:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        header.Children.Add(_layoutCombo);
        header.Children.Add(new TextBlock
        {
            Text = "キーをクリックして選びます（キーコード名はUS配列基準です）。",
            Foreground = AppTheme.Palette.SubText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
        });
        Children.Add(header);
        Children.Add(new ScrollViewer
        {
            Content = _canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        _canvas.KeyClicked += slot =>
        {
            if (slot.DefaultKeycode == null) return;
            _selected = slot;
            _canvas.Render(_canvas.Layout, VisualFor); // 選択の強調を描き直す
            KeySelected?.Invoke(slot.DefaultKeycode);
        };
        _layoutCombo.SelectionChanged += (_, _) => LoadSelectedLayout();

        if (current < 0)
        {
            // 編集中のレイアウトが分からないときは、日本語環境なら JP109、そうでなければ US104。
            string preferred = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja" ? "JP109" : "US104";
            current = _layoutCombo.Items.IndexOf(preferred);
        }
        _layoutCombo.SelectedIndex = current >= 0 ? current : (_layoutCombo.Items.Count > 0 ? 0 : -1);
    }

    private int IndexOfFile(string? path)
    {
        if (path == null) return -1;
        string full = Path.GetFullPath(path);
        return _layoutFiles.FindIndex(f => string.Equals(Path.GetFullPath(f), full, StringComparison.OrdinalIgnoreCase));
    }

    private void LoadSelectedLayout()
    {
        _selected = null;
        int i = _layoutCombo.SelectedIndex;
        if (i < 0 || i >= _layoutFiles.Count) { _canvas.Render(null, VisualFor); return; }
        try { _canvas.Render(KeyboardLayout.Load(_layoutFiles[i]), VisualFor); }
        catch (Exception ex) when (ex is System.IO.IOException or System.Text.Json.JsonException)
        {
            _canvas.Render(null, VisualFor);
        }
    }

    private KeyVisual VisualFor(KeySlot slot)
    {
        bool selectable = slot.DefaultKeycode != null;
        bool selected = _selected != null && _selected.Index == slot.Index;
        Brush bg = selected ? SelectedBrush : KeyboardCanvas.DefaultKeyBrush;
        return new KeyVisual(
            slot.MainLegend.Length > 0 ? slot.MainLegend : slot.PrintedLabel,
            bg,
            selected ? AppTheme.Palette.KeySelectedBorder : KeyboardCanvas.DefaultBorderBrush,
            selected ? 2 : 1,
            selectable ? slot.DefaultKeycode : slot.Description,
            selectable ? null : AppTheme.Palette.KeyDisabledText);
    }
}
