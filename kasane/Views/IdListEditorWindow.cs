using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kasane.Model;

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

    /// <summary>
    /// 番号ごとの「無効」（定義は残すが、キーボードへ適用しない）。画面の「無効にする」で変わる。派生クラスは、コンストラクターで
    /// <see cref="LoadDisabled"/> を呼んで写し、Result を作るときに、この値を定義の Disabled に入れる。
    /// </summary>
    protected readonly bool[] Disabled;

    /// <summary>プロジェクトの定義の「無効」を、編集用の配列に写す。派生クラスのコンストラクターから呼ぶ。</summary>
    protected void LoadDisabled(int id, bool disabled)
    {
        if (id >= 0 && id < Disabled.Length) Disabled[id] = disabled;
    }

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
        Disabled = new bool[count];
        Title = title;
        // 右側に「押して入力」「選ぶ...」「マクロ...」のボタンが並ぶので、横幅に余裕を持たせる（狭いとボタンが隠れる）。
        Width = 1120;
        MinWidth = 900;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var apply = new Button { Name = "ApplyButton", Content = Loc.T("適用", "Apply"), Width = 90, Margin = new Thickness(0, 0, 24, 0) }; // OKとは少し間を空ける
        apply.Click += (_, _) => { if (Commit()) ShowValidation(Loc.T("適用しました。", "Applied."), isError: false); };
        var ok = new Button { Name = "OkButton", Content = "OK", IsDefault = true, Width = 90 };
        ok.Click += (_, _) => { if (Commit() && ConfirmPendingInsert()) Close(); };
        // 別の画面から戻ったとき、入れ先の欄の状態が変わっているので、「入れる」ボタンの強調を見直す。
        Activated += (_, _) => UpdateInsertHint();
        // 非モーダルなので、IsCancel（ShowDialog 用）ではなく、自分で閉じる。Esc も同じ。
        var cancel = new Button { Name = "CancelButton", Content = Loc.T("キャンセル", "Cancel"), Width = 90, Margin = new Thickness(6, 0, 0, 0) };
        cancel.Click += (_, _) => Close();
        PreviewKeyDown += (_, e) =>
        {
            // 「押して入力」の取り込み中の Esc は、取り込みを終えるためのもの。画面は閉じない。
            if (e.Key == System.Windows.Input.Key.Escape && !e.Handled)
            {
                e.Handled = true;
                if (!KeycodeCapture.EscapeBelongsToCapture) Close();
            }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(apply);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var right = new StackPanel();
        right.Children.Add(new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Palette.SubText, Margin = new Thickness(0, 0, 0, 10) });
        right.Children.Add(Detail);
        right.Children.Add(_validation);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_list, Dock.Left);
        root.Children.Add(buttons);
        root.Children.Add(_list);
        root.Children.Add(new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        // 一覧の行のダブルクリック。派生クラスが使い道を決める（マクロの画面は、他の画面の入力欄へ挿入する）。
        _list.MouseDoubleClick += (_, e) =>
        {
            if (_list.SelectedIndex >= 0 && e.OriginalSource is FrameworkElement { DataContext: string }) OnItemActivated(_list.SelectedIndex);
        };
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedIndex < 0) return;
            _detailId = _list.SelectedIndex;
            Detail.Children.Clear();
            AddNameRow(_list.SelectedIndex);
            ShowDetail(_list.SelectedIndex);
            ShowValidation("", false);
        };
        Loaded += (_, _) =>
        {
            for (int i = 0; i < _count; i++) _list.Items.Add(ItemText(i));
            _loaded = true;
            _list.SelectedIndex = _pendingSelect is int id && id >= 0 && id < _count ? id : 0;
            if (_list.SelectedItem != null) _list.ScrollIntoView(_list.SelectedItem);
        };
    }

    private bool _loaded;
    private int? _pendingSelect;

    /// <summary>
    /// 番号 <paramref name="id"/> を選んで、右側にその中身を出す（2026-11-05、GUI 計画 1-2。割り当て画面・右クリックの「開く」から）。
    /// 開く前（Show の前）に呼べば、開いたときに選ぶ。範囲外の番号は先頭。
    /// </summary>
    public void ShowId(int id)
    {
        if (id < 0 || id >= _count) id = 0;
        if (!_loaded) { _pendingSelect = id; return; }
        _list.SelectedIndex = -1; // 同じ番号を選び直しても、右側を作り直すように
        _list.SelectedIndex = id;
        _list.ScrollIntoView(_list.SelectedItem);
    }

    /// <summary>最初の空き番号（中身も名前も無い番号）。無ければ -1。</summary>
    protected int FreeId()
    {
        for (int i = 0; i < _count; i++)
            if (string.IsNullOrEmpty(Summary(i)) && string.IsNullOrWhiteSpace(Names[i])) return i;
        return -1;
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
        Detail.Children.Add(Row(Loc.T("名前(任意)", "Name (optional)"), box));
        {
            // 中身がある番号だけ押せる（中身の編集で、押せるかが変わる。RefreshItem）。チェックを付けると、書いた内容は残したまま、
            // キーボードへは適用しない。
            var disabled = new CheckBox
            {
                Name = "ItemDisabled",
                Content = Loc.T("無効にする(内容は残して、キーボードには適用しない)", "Disable (keep the content, but do not apply it to the keyboard)"),
                IsChecked = Disabled[id],
                IsEnabled = !string.IsNullOrEmpty(Summary(id)),
                Margin = new Thickness(110, 0, 0, 6),
            };
            _disabledBox = disabled;
            // Click ではなく Checked/Unchecked（UI Automation の Toggle でも起きる）。
            RoutedEventHandler changed = (_, _) =>
            {
                Disabled[id] = disabled.IsChecked == true;
                RefreshItem(id);
                if (Disabled[id]) ShowValidation("", false);
                else OnItemEnabled(id); // 有効に戻したときは、その項目を検証して、結果を出す
            };
            disabled.Checked += changed;
            disabled.Unchecked += changed;
            Detail.Children.Add(disabled);
        }
        if (string.IsNullOrEmpty(Summary(id)))
            Detail.Children.Add(new TextBlock
            {
                Text = Loc.T("中身が空の番号は未使用です。名前だけ先に付けておけます(中身を入れるまで、キーボードには適用されません)。",
                             "A number with no content is unused. You can set a name ahead of time (it is not applied to the keyboard until you add content)."),
                Foreground = AppTheme.Palette.SubText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
            });
    }

    private string ItemText(int id)
    {
        var s = Summary(id);
        var name = Names[id];
        string body = string.IsNullOrEmpty(s) ? Loc.T("(未使用)", "(unused)") : s;
        if (Disabled[id] && !string.IsNullOrEmpty(s)) body = Loc.T("[無効] ", "[Disabled] ") + body;
        return string.IsNullOrEmpty(name) ? $"{id,2}: {body}" : $"{id,2}: {name} — {body}";
    }

    /// <summary>派生クラスが編集内容を変えたら呼ぶ（一覧の表示を更新する）。</summary>
    protected void RefreshItem(int id)
    {
        if (id >= 0 && id < _list.Items.Count) _list.Items[id] = ItemText(id);
        // 右側に出している番号なら、「無効にする」を押せるかを、中身に合わせる（一覧の行の入れ替えで、選択が外れることがある）。
        if (_disabledBox != null && id == _detailId) _disabledBox.IsEnabled = !string.IsNullOrEmpty(Summary(id));
    }

    // --- 他の画面の入力欄へ MACRO(n)・TAPDANCE(n) を入れる（マクロ・タップダンスの画面） ---

    /// <summary>この画面が入れるトークンの種類（"MACRO"・"TAPDANCE"）。入れない画面は null。</summary>
    protected virtual string? InsertKind => null;

    /// <summary>右側の「入れる」ボタン。派生クラスが、作ったときに設定する。</summary>
    protected Button? InsertButton { get; set; }

    /// <summary>この画面で、最後に中身を編集した番号。まだ編集していなければ -1。</summary>
    protected int LastEditedId { get; private set; } = -1;

    /// <summary>派生クラスが、中身を編集したときに呼ぶ。「入れる」ボタンの強調を更新する。</summary>
    protected void NoteEdited(int id)
    {
        LastEditedId = id;
        UpdateInsertHint();
    }

    /// <summary>
    /// 最後に編集した定義（中身があり、無効でない）の MACRO(n)・TAPDANCE(n) を、まだ入れ先の欄に入れていなければ、そのトークン。
    /// 入れ先が無い、または、すでに入っていれば null。
    /// </summary>
    protected string? PendingInsertToken()
    {
        if (InsertKind == null || LastEditedId < 0 || Disabled[LastEditedId] || string.IsNullOrEmpty(Summary(LastEditedId))) return null;
        string token = $"{InsertKind}({LastEditedId})";
        return MacroInsert.NeedsInsert(token) ? token : null;
    }

    /// <summary>まだ入れていないトークンがあるとき、「入れる」ボタンを強調する（太字と色）。入れたら、元に戻す。</summary>
    protected void UpdateInsertHint()
    {
        if (InsertButton == null) return;
        bool pending = PendingInsertToken() != null;
        InsertButton.FontWeight = pending ? FontWeights.Bold : FontWeights.Normal;
        if (pending) InsertButton.Background = AppTheme.Palette.KeyChanged;
        else InsertButton.ClearValue(Control.BackgroundProperty);
    }

    // OK で閉じる前に、編集した定義を入れ先にまだ入れていなければ、入れるかを聞く。false なら、閉じない。
    private bool ConfirmPendingInsert()
    {
        string? token = PendingInsertToken();
        if (token == null) return true;
        var answer = MessageBox.Show(this,
            Loc.T($"{token} を、他の画面の入力欄にまだ入れていません。入れてから閉じますか?\n\n「はい」: 入れて閉じる\n「いいえ」: 入れずに閉じる\n「キャンセル」: 閉じない",
                  $"{token} has not been inserted into the field in the other window yet. Insert it before closing?\n\nYes: insert and close\nNo: close without inserting\nCancel: do not close"),
            Kasane.Model.AppInfo.DisplayName, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.No) return true;
        if (MacroInsert.TryInsert(token, out var reason)) return true;
        ShowValidation(reason, isError: true);
        return false;
    }

    private CheckBox? _disabledBox;
    private int _detailId = -1;

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

    /// <summary>「無効にする」のチェックを外したとき（有効に戻したとき）。その項目を検証して、<see cref="ShowValidation"/> に出す。</summary>
    protected virtual void OnItemEnabled(int id) { }

    /// <summary>一覧の行をダブルクリックしたとき（既定では何もしない）。</summary>
    protected virtual void OnItemActivated(int id) { }

    protected abstract string Summary(int id);
    protected abstract void ShowDetail(int id);
    protected abstract bool ValidateAll(out string error);
}
