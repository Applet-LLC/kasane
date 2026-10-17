using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Kasane.Views;

/// <summary>
/// マクロ・コンボ・タップダンス編集画面の共通部分: 左にID一覧、右に選択中IDの編集欄、下にOK/キャンセル。
/// 編集内容は派生クラスが作業用コピーに持ち、「適用」またはOKで検証が通ったときだけ呼び出し元へ返す
/// （<see cref="Applied"/>）。同時に開ける別ウィンドウ（非モーダル）として使う。「適用」は画面を閉じずに反映し、OKは反映して閉じ、
/// キャンセル・×は反映せずに閉じる。
/// </summary>
public abstract class IdListEditorWindow : Window
{
    private readonly ListBox _list = new() { Name = "IdList", Width = 300, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock _validation = new() { Name = "ValidationText", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    protected readonly StackPanel Detail = new();
    private readonly int _count;

    /// <summary>
    /// 番号ごとの利用者がつけた名前（任意）。中身が空（未使用）の番号にも付けられる。画面の名前欄（<c>ItemName</c>）で変えると、この配列が変わる。
    /// OKで検証が通ると、<see cref="ResultNames"/> に入る。
    /// </summary>
    protected readonly string?[] Names;

    /// <summary>「適用」またはOKで確定した名前の表（番号 → 名前）。名前が1つも無ければ null。</summary>
    public Dictionary<int, string>? ResultNames { get; private set; }

    /// <summary>
    /// 検証が通って、編集内容を確定したとき（「適用」・OK）に呼ぶ。呼び出し側は、このときの
    /// 派生クラスの Result と <see cref="ResultNames"/> をプロジェクトへ取り込む。
    /// </summary>
    public event Action? Applied;

    /// <summary>プロジェクトの名前の表を、編集用の配列に写す。派生クラスのコンストラクターから呼ぶ。</summary>
    protected void LoadNames(IReadOnlyDictionary<int, string>? names)
    {
        if (names == null) return;
        foreach (var (id, name) in names)
            if (id >= 0 && id < Names.Length) Names[id] = name;
    }

    // UI Automationでの要素特定用（WPFの既定AutomationPeerはx:Name/Nameを
    // AutomationIdとして使うため、コード生成のコントロールにも明示的に設定しておく）。
    protected IdListEditorWindow(string title, int count, string help)
    {
        _count = count;
        Names = new string?[count];
        Title = title;
        Width = 900;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var apply = new Button { Name = "ApplyButton", Content = "適用", Width = 90, Margin = new Thickness(0, 0, 24, 0) }; // OKとは少し間を空ける
        apply.Click += (_, _) => { if (Commit()) ShowValidation("適用しました。", isError: false); };
        var ok = new Button { Name = "OkButton", Content = "OK", IsDefault = true, Width = 90 };
        ok.Click += (_, _) => { if (Commit()) Close(); };
        // 非モーダルなので、IsCancel（ShowDialog 用）ではなく、自分で閉じる。Esc も同じ。
        var cancel = new Button { Name = "CancelButton", Content = "キャンセル", Width = 90, Margin = new Thickness(6, 0, 0, 0) };
        cancel.Click += (_, _) => Close();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && !e.Handled) { e.Handled = true; Close(); }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(apply);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var right = new StackPanel();
        right.Children.Add(new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 10) });
        right.Children.Add(Detail);
        right.Children.Add(_validation);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_list, Dock.Left);
        root.Children.Add(buttons);
        root.Children.Add(_list);
        root.Children.Add(new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        // 一覧の行のダブルクリック。派生クラスが使い道を決める（マクロの画面は、他の画面の入力欄へ挿入する）。
        _list.MouseDoubleClick += (_, e) =>
        {
            if (_list.SelectedIndex >= 0 && e.OriginalSource is FrameworkElement { DataContext: string }) OnItemActivated(_list.SelectedIndex);
        };
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedIndex < 0) return;
            Detail.Children.Clear();
            AddNameRow(_list.SelectedIndex);
            ShowDetail(_list.SelectedIndex);
            ShowValidation("", false);
        };
        Loaded += (_, _) =>
        {
            for (int i = 0; i < _count; i++) _list.Items.Add(ItemText(i));
            _list.SelectedIndex = 0;
        };
    }

    // 検証し、通れば名前の表を確定して Applied を起こす（「適用」・OK 共通）。通らなければ画面にエラーを出して false。
    private bool Commit()
    {
        if (!ValidateAll(out var error))
        {
            ShowValidation(error, isError: true);
            return false;
        }
        var names = new Dictionary<int, string>();
        for (int i = 0; i < Names.Length; i++)
            if (!string.IsNullOrWhiteSpace(Names[i])) names[i] = Names[i]!;
        ResultNames = names.Count > 0 ? names : null;
        Applied?.Invoke();
        return true;
    }

    // 右側の先頭に、名前の欄を出す（番号は変わらない。名前は一覧とドキュメントでの見分けのためだけ）。
    private void AddNameRow(int id)
    {
        var box = new TextBox { Name = "ItemName", Text = Names[id] ?? "", Width = 300 };
        box.TextChanged += (_, _) =>
        {
            Names[id] = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();
            RefreshItem(id);
        };
        Detail.Children.Add(Row("名前（任意）", box));
        if (string.IsNullOrEmpty(Summary(id)))
            Detail.Children.Add(new TextBlock
            {
                Text = "中身が空の番号は未使用です。名前だけ先に付けておけます（中身を入れるまで、キーボードには適用されません）。",
                Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
            });
    }

    private string ItemText(int id)
    {
        var s = Summary(id);
        var name = Names[id];
        string body = string.IsNullOrEmpty(s) ? "（未使用）" : s;
        return string.IsNullOrEmpty(name) ? $"{id,2}: {body}" : $"{id,2}: {name} — {body}";
    }

    /// <summary>派生クラスが編集内容を変えたら呼ぶ（一覧の表示を更新する）。</summary>
    protected void RefreshItem(int id)
    {
        if (id >= 0 && id < _list.Items.Count) _list.Items[id] = ItemText(id);
    }

    protected void ShowValidation(string text, bool isError)
    {
        _validation.Foreground = isError ? Brushes.Firebrick : Brushes.DarkGreen;
        _validation.Text = text;
    }

    protected static UIElement Row(string label, UIElement input)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(input);
        return row;
    }

    /// <summary><paramref name="name"/>を渡すとUI Automationでの要素特定用に<c>Name</c>を設定する
    /// （例: "SingleTap"）。省略時は無名（呼び出し側が複数箇所で使い回す場合など）。</summary>
    protected static TextBox KeycodeBox(string text, string? name = null) =>
        new() { Name = name ?? "", Text = text, Width = 300, FontFamily = new FontFamily("Consolas") };

    /// <summary>一覧の行をダブルクリックしたとき（既定では何もしない）。</summary>
    protected virtual void OnItemActivated(int id) { }

    protected abstract string Summary(int id);
    protected abstract void ShowDetail(int id);
    protected abstract bool ValidateAll(out string error);
}
