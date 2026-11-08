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
    /// <summary>「?」ボタンと F1 で開くマニュアルの節（<see cref="HelpTopics"/>）。編集画面ごとに上書きする。</summary>
    protected virtual string HelpTopic => HelpTopics.EditorsCommon;

    protected IdListEditorWindow(string title, int count)
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
        var okCancel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        okCancel.Children.Add(apply);
        okCancel.Children.Add(ok);
        okCancel.Children.Add(cancel);
        // 説明の段落は画面に出さず、「?」（左下）と F1 でマニュアルの節を開く（2026-11-08、UI の文言を削る作業）。
        var buttons = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = false };
        var help = HelpButton.Create(() => HelpTopic);
        DockPanel.SetDock(help, Dock.Left);
        DockPanel.SetDock(okCancel, Dock.Right);
        buttons.Children.Add(help);
        buttons.Children.Add(okCancel);
        HelpButton.AttachF1(this, () => HelpTopic);

        var right = new StackPanel();
        right.Children.Add(Detail);
        right.Children.Add(_validation);

        // 一覧の下の操作（2026-11-06、GUI 計画 1-3）: 削除・複製と、並べ替え（番号をほかから参照されない、コンボ・キー置換だけ）。
        _delete = new Button { Name = "DeleteItemButton", Content = Loc.T("削除", "Delete"), MinWidth = 56, Margin = new Thickness(0, 0, 4, 0),
                               ToolTip = Loc.T("選んでいる番号の中身と名前を消します", "Clears the content and name of the selected number") };
        _duplicate = new Button { Name = "DuplicateItemButton", Content = Loc.T("複製", "Duplicate"), MinWidth = 56, Margin = new Thickness(0, 0, 4, 0),
                                  ToolTip = Loc.T("選んでいる番号の中身を、空いている番号に写します。", "Copies the selected number to a free number.") };
        _up = new Button { Name = "MoveUpButton", Content = Loc.T("上へ", "Up"), MinWidth = 52, Margin = new Thickness(8, 0, 4, 0) };
        _down = new Button { Name = "MoveDownButton", Content = Loc.T("下へ", "Down"), MinWidth = 52 }; // 幅は字に合わせる（英語の Duplicate が切れないように。2026-11-07 の実機確認 E1）
        _delete.Click += (_, _) => DeleteSelected();
        _duplicate.Click += (_, _) => DuplicateSelected();
        _up.Click += (_, _) => MoveSelected(-1);
        _down.Click += (_, _) => MoveSelected(+1);
        var listButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 10, 0) };
        listButtons.Children.Add(_delete);
        listButtons.Children.Add(_duplicate);
        listButtons.Children.Add(_up);
        listButtons.Children.Add(_down);
        var left = new DockPanel();
        DockPanel.SetDock(listButtons, Dock.Bottom);
        left.Children.Add(listButtons);
        left.Children.Add(_list);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(buttons);
        root.Children.Add(left);
        root.Children.Add(new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        // 一覧の行のダブルクリック。派生クラスが使い道を決める（マクロの画面は、他の画面の入力欄へ挿入する）。
        _list.MouseDoubleClick += (_, e) =>
        {
            if (_list.SelectedIndex >= 0 && e.OriginalSource is FrameworkElement { DataContext: string }) OnItemActivated(_list.SelectedIndex);
        };
        _list.SelectionChanged += (_, _) =>
        {
            // 中身を書くと一覧の行を差し替えるので、選択が外れる（-1）。右側に出している番号（_detailId）はそのまま。
            if (_list.SelectedIndex < 0) return;
            _detailId = _list.SelectedIndex;
            UpdateListButtons();
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

    // ---- 一覧の操作（削除・複製・並べ替え。2026-11-06、GUI 計画 1-3） ----

    private readonly Button _delete, _duplicate, _up, _down;

    /// <summary>番号 <paramref name="id"/> の中身を消す（未使用にする）。名前と無効は基底が消す。</summary>
    protected abstract void ClearItem(int id);

    /// <summary>番号 <paramref name="from"/> の中身を <paramref name="to"/> に写す（名前と無効は基底が写す）。</summary>
    protected abstract void CopyItem(int from, int to);

    /// <summary>番号 <paramref name="a"/> と <paramref name="b"/> の中身を入れ替える（名前と無効は基底が入れ替える）。</summary>
    protected abstract void SwapItems(int a, int b);

    /// <summary>
    /// 並べ替え（上へ・下へ）と、削除で後ろを詰めることができるか。番号がキーマップから参照されない、コンボとキー置換だけ true。
    /// マクロとタップダンスは、番号が MACRO(n) / TAPDANCE(n) として書かれているので、番号を動かさない。
    /// </summary>
    protected virtual bool CanReorder => false;

    /// <summary>削除の確認で、使っている場所を調べる種類（マクロ・タップダンス）。番号を参照されないものは null。</summary>
    protected virtual RefKind? ReferenceKind => null;

    /// <summary>確認の文に出す種類の名前（「マクロ」など）。</summary>
    protected virtual string KindLabel => Title;

    /// <summary>複製したあと（複製先の番号）。コンボは、同じキーが重なるので無効にする。</summary>
    protected virtual void OnDuplicated(int id) { }

    /// <summary>
    /// 使っている場所の説明を返す（メイン画面が設定する。プロジェクトのキーマップと定義を調べる）。null なら調べない。
    /// </summary>
    public Func<RefKind, int, IReadOnlyList<string>>? ReferenceProvider { get; set; }

    /// <summary>
    /// 削除のときに「使っている場所の割り当ても外す」を選んだ番号。メイン画面が、適用・OK のときにプロジェクトから参照を外し、空にする。
    /// キャンセルで閉じれば、何も外さない。
    /// </summary>
    public List<(RefKind Kind, int Id)> PendingUnassign { get; } = new();

    /// <summary>
    /// ほかの編集画面で、番号 <paramref name="id"/> への参照が外されたとき（メイン画面が呼ぶ）。この画面の作業用コピーからも外す
    /// （コンボ・キー置換は無効にし、タップダンスは欄を空にする）。プロジェクトと同じ規則（<see cref="ProjectService.RemoveReferences"/>）。
    /// </summary>
    public virtual void ReferencesRemoved(RefKind kind, int id) { }

    /// <summary>派生クラスが、無効の状態を変えるとき（参照を外して無効にしたときなど）。</summary>
    protected void SetDisabled(int id, bool disabled)
    {
        if (id < 0 || id >= Disabled.Length) return;
        Disabled[id] = disabled;
        RefreshItem(id);
    }

    /// <summary>一覧で選んでいる番号（無ければ -1）。</summary>
    protected int CurrentId => _detailId;

    private bool HasItem(int id) => !string.IsNullOrEmpty(Summary(id)) || !string.IsNullOrWhiteSpace(Names[id]);

    private string ItemLabel(int id) =>
        string.IsNullOrWhiteSpace(Names[id]) ? Loc.T($"{KindLabel}{id}", $"{KindLabel} {id}") : Loc.T($"{KindLabel}{id}「{Names[id]}」", $"{KindLabel} {id} \"{Names[id]}\"");

    private void UpdateListButtons()
    {
        int i = _detailId; // 一覧の選択は、中身を書くと外れるので、右側に出している番号で決める
        _delete.IsEnabled = _duplicate.IsEnabled = i >= 0 && HasItem(i);
        _up.Visibility = _down.Visibility = CanReorder ? Visibility.Visible : Visibility.Collapsed;
        _up.IsEnabled = CanReorder && i > 0;
        _down.IsEnabled = CanReorder && i >= 0 && i < _count - 1;
    }

    private void SwapAll(int a, int b)
    {
        SwapItems(a, b);
        (Names[a], Names[b]) = (Names[b], Names[a]);
        (Disabled[a], Disabled[b]) = (Disabled[b], Disabled[a]);
        RefreshItem(a);
        RefreshItem(b);
    }

    private void DeleteSelected()
    {
        int id = _detailId;
        if (id < 0 || !HasItem(id)) return;
        var refs = ReferenceKind is RefKind k && ReferenceProvider != null ? ReferenceProvider(k, id) : Array.Empty<string>();
        var dlg = new DeleteConfirmWindow(ItemLabel(id), refs, CanReorder
            ? Loc.T("後ろの番号は、1つずつ前に詰めます。", "The numbers after it move up by one.")
            : Loc.T("番号は詰めません(空いた番号は未使用になります)。", "Numbers are not shifted (the number becomes unused).")) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        ClearItem(id);
        Names[id] = null;
        Disabled[id] = false;
        RefreshItem(id);
        if (dlg.Unassign && ReferenceKind is RefKind kind) PendingUnassign.Add((kind, id));
        if (CanReorder)
            for (int i = id; i < _count - 1; i++) SwapAll(i, i + 1); // 空いた番号を末尾へ送る（後ろを前に詰める）
        ShowId(id);
        ShowValidation(Loc.T("削除しました。OK か適用で決まります。", "Deleted. Press OK or Apply to keep the change."), isError: false);
    }

    private void DuplicateSelected()
    {
        int from = _detailId;
        if (from < 0 || !HasItem(from)) return;
        int to = FreeId();
        if (to < 0)
        {
            ShowValidation(Loc.T("空いている番号がありません。", "There is no free number."), isError: true);
            return;
        }
        CopyItem(from, to);
        Names[to] = string.IsNullOrWhiteSpace(Names[from]) ? null : Names[from] + Loc.T("(複製)", " (copy)");
        Disabled[to] = Disabled[from];
        OnDuplicated(to);
        RefreshItem(to);
        ShowId(to); // 選び直すと検証の欄が空になるので、補足はこのあとに出す（2026-11-07 の実機確認 L5）
        if (DuplicatedNote(to) is { } note) ShowValidation(note, isError: false);
    }

    /// <summary>複製したあとに、検証の欄に出す補足（無ければ null）。</summary>
    protected virtual string? DuplicatedNote(int id) => null;

    private void MoveSelected(int delta)
    {
        int id = _detailId, other = id + delta;
        if (!CanReorder || id < 0 || other < 0 || other >= _count) return;
        SwapAll(id, other);
        ShowId(other);
    }

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
                Content = Loc.T("無効にする", "Disable"),
                ToolTip = Loc.T("内容は残して、キーボードには適用しません", "Keeps the content, but does not apply it to the keyboard"),
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
        // 中身が空の番号は未使用（一覧に「(未使用)」と出る）。その説明はマニュアル（9 章の共通の操作）に置く。
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
        if (id == _detailId) UpdateListButtons(); // 中身を書いた・消したので、削除・複製を押せるかが変わる
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
        _validation.Foreground = isError ? AppTheme.Palette.ErrorText : AppTheme.Palette.OkText;
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
