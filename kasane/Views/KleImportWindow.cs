using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kasane.Keycodes;
using Kasane.Layout;

namespace Kasane.Views;

/// <summary>
/// 「KLEから追加」。Keyboard Layout Editor の JSON（Raw data / Download JSON）を読み、各キーの既定キーコードを推定して見せる。
/// 推定できなかった・違うキーは、一覧で直せる。名前をつけて、利用者のレイアウトのフォルダ（<see cref="LayoutFolders.UserDirectory"/>）に保存する。
/// 保存したら <see cref="SavedPath"/> に、そのファイルのパスが入る。
/// </summary>
public sealed class KleImportWindow : Window
{
    private const double Scale = 34;

    private sealed class Row : INotifyPropertyChanged
    {
        public int Slot { get; init; }
        public string Number => $"#{Slot + 1}";
        public string Label { get; init; } = "";
        public string Note { get; init; } = "";
        public KleKeyResult Source { get; init; } = null!;

        private string _keycode = "";
        public string Keycode
        {
            get => _keycode;
            set { if (_keycode != value) { _keycode = value; Changed(nameof(Keycode)); Changed(nameof(StatusText)); Changed(nameof(Status)); } }
        }

        // 利用者が直したら「手で指定」。もとの推定と同じなら、もとの状態。
        public KleKeyStatus Status => Keycode.Length == 0 ? KleKeyStatus.Unassigned
            : Keycode == (Source.Keycode ?? "") ? Source.Status : KleKeyStatus.Confirmed;
        public bool EditedByUser => Keycode != (Source.Keycode ?? "");
        public string StatusText => EditedByUser ? (Keycode.Length == 0 ? "未割り当て（手で消した）" : "手で指定") : Status switch
        {
            KleKeyStatus.Confirmed => "確定",
            KleKeyStatus.Inferred => "推定",
            _ => "未割り当て",
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly TextBox _file = new() { Name = "KleFileBox", Width = 440 };
    private readonly TextBox _name = new() { Name = "KleNameBox", Width = 200 };
    private readonly Canvas _canvas = new() { Name = "KleCanvas", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly DataGrid _grid = new() { Name = "KleKeyGrid", AutoGenerateColumns = false, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column, MinWidth = 330 };
    private readonly TextBlock _summary = new() { Name = "KleSummary", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 2) };
    private readonly TextBlock _warnings = new() { Name = "KleWarnings", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkOrange, Margin = new Thickness(0, 0, 0, 4) };
    private readonly Button _pick = new() { Name = "KlePickKeycodeButton", Content = "一覧から選ぶ...", Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 2, 8, 2) };
    private readonly Button _save = new() { Name = "KleSaveButton", Content = "保存して使う", IsDefault = true, Width = 120, IsEnabled = false };

    private readonly Dictionary<int, Button> _keyBoxes = new();
    private List<Row> _rows = new();
    private KleImportResult? _result;
    private string _json = "";

    /// <summary>保存したレイアウトのファイル（保存しなかったら null）。</summary>
    public string? SavedPath { get; private set; }
    public string? SavedName { get; private set; }

    public KleImportWindow()
    {
        Title = "KLE の配列を取り込む";
        Width = 1120;
        Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var choose = new Button { Name = "KleChooseFileButton", Content = "ファイルを選ぶ...", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
        choose.Click += (_, _) => ChooseFile();
        var load = new Button { Name = "KleLoadButton", Content = "読み込む", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
        load.Click += (_, _) => LoadFromBox();
        _file.KeyDown += (_, e) => { if (e.Key == Key.Enter) LoadFromBox(); };
        var fileRow = new StackPanel { Orientation = Orientation.Horizontal };
        fileRow.Children.Add(new TextBlock { Text = "ファイル:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        fileRow.Children.Add(_file);
        fileRow.Children.Add(choose);
        fileRow.Children.Add(load);

        _name.TextChanged += (_, _) => UpdateSave();
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        nameRow.Children.Add(new TextBlock { Text = "名前:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        nameRow.Children.Add(_name);
        nameRow.Children.Add(new TextBlock { Text = "（英数字とハイフン。レイアウトの一覧にこの名前で出ます）", Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });

        var help = new TextBlock
        {
            Text = "keyboard-layout-editor.com で作った配列の JSON（Raw data）を読み込みます。各キーの既定のキーコードは、ラベルと位置から推定します。" +
                   "黄色は推定、赤は未割り当て（選ばなくても保存できますが、キャリブレーションが要ります）です。キーコードは、一覧で書き換えるか、「一覧から選ぶ」で選べます。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 6),
        };

        var top = new StackPanel();
        top.Children.Add(help);
        top.Children.Add(fileRow);
        top.Children.Add(nameRow);
        top.Children.Add(_summary);
        top.Children.Add(_warnings);

        _grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new System.Windows.Data.Binding(nameof(Row.Number)), IsReadOnly = true, Width = 44 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "ラベル", Binding = new System.Windows.Data.Binding(nameof(Row.Label)), IsReadOnly = true, Width = 110 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "キーコード", Binding = new System.Windows.Data.Binding(nameof(Row.Keycode)) { Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.LostFocus }, Width = 130 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "状態", Binding = new System.Windows.Data.Binding(nameof(Row.StatusText)), IsReadOnly = true, Width = 90 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "メモ", Binding = new System.Windows.Data.Binding(nameof(Row.Note)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.LoadingRow += (_, e) => ColorRow(e.Row);
        _grid.CellEditEnding += Grid_CellEditEnding;
        _grid.SelectionChanged += (_, _) => HighlightSelected();
        _pick.Click += (_, _) => PickKeycode();

        var right = new DockPanel { Margin = new Thickness(10, 0, 0, 0) };
        DockPanel.SetDock(_pick, Dock.Bottom);
        right.Children.Add(_pick);
        right.Children.Add(_grid);

        var left = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _canvas };

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 380 });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        body.Children.Add(left);
        body.Children.Add(right);

        _save.Click += (_, _) => Save();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(_save);
        buttons.Children.Add(new Button { Name = "KleCancelButton", Content = "キャンセル", IsCancel = true, Width = 90, Margin = new Thickness(6, 0, 0, 0) });

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(buttons);
        root.Children.Add(body);
        Content = root;
    }

    /// <summary>ファイルを指定して開く（UIA の試験や、ドラッグ＆ドロップの入り口用）。読めなければ、理由を出して false。</summary>
    public bool LoadFile(string path)
    {
        string json;
        KleImportResult result;
        try
        {
            json = File.ReadAllText(path);
            result = KleImporter.Analyze(json);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"読み込めませんでした: {ex.Message}", Title);
            return false;
        }
        _json = json;
        _result = result;
        _file.Text = path;
        if (!KleImporter.IsValidName(_name.Text)) _name.Text = SuggestName(path);

        _rows = result.Keys.Select(k => new Row { Slot = k.Slot, Label = k.Label, Note = k.Note, Source = k, Keycode = k.Keycode ?? "" }).ToList();
        foreach (var r in _rows) r.PropertyChanged += (_, _) => { RefreshKeyBox(r); UpdateSummary(); };
        _grid.ItemsSource = _rows;
        DrawKeyboard();
        UpdateSummary();
        _warnings.Text = string.Join("\n", result.Warnings);
        UpdateSave();
        return true;
    }

    private static string SuggestName(string path)
    {
        string n = Path.GetFileNameWithoutExtension(path);
        if (n.StartsWith("kle-", StringComparison.OrdinalIgnoreCase)) n = n[4..];
        var chars = n.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        string s = new string(chars).Trim('-');
        if (s.Length > 32) s = s[..32];
        return KleImporter.IsValidName(s) ? s : "my-layout";
    }

    private void LoadFromBox()
    {
        string path = _file.Text.Trim().Trim('"');
        if (path.Length == 0) { MessageBox.Show(this, "ファイルを選ぶか、パスを書いてください。", Title); return; }
        LoadFile(path);
    }

    private void ChooseFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "KLE JSON (*.json)|*.json", Title = "KLE の配列（JSON）を開く" };
        if (dlg.ShowDialog(this) == true) LoadFile(dlg.FileName);
    }

    // ---- 図と一覧 ----

    private static Brush StatusBrush(KleKeyStatus s) => s switch
    {
        KleKeyStatus.Confirmed => new SolidColorBrush(Color.FromRgb(0xD5, 0xEE, 0xD5)),
        KleKeyStatus.Inferred => new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0xB8)),
        _ => new SolidColorBrush(Color.FromRgb(0xF6, 0xC9, 0xC9)),
    };

    private void ColorRow(DataGridRow row)
    {
        if (row.Item is Row r) row.Background = StatusBrush(r.Status);
    }

    private void DrawKeyboard()
    {
        _canvas.Children.Clear();
        _keyBoxes.Clear();
        if (_result == null) return;
        double maxX = 0, maxY = 0;
        foreach (var r in _rows)
        {
            var k = r.Source;
            var box = new Button
            {
                Name = $"KleKey{k.Slot + 1}",
                Width = Math.Max(8, k.W * Scale - 3),
                Height = Math.Max(8, k.H * Scale - 3),
                Padding = new Thickness(0),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Background = StatusBrush(r.Status),
                ToolTip = $"#{k.Slot + 1} {k.Label}",
                Content = new TextBlock { Text = ShortText(r), FontSize = 10, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2), VerticalAlignment = VerticalAlignment.Center },
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(box, $"KleKey{k.Slot + 1}");
            System.Windows.Automation.AutomationProperties.SetName(box, $"#{k.Slot + 1} {k.Label}");
            Canvas.SetLeft(box, k.X * Scale + 4);
            Canvas.SetTop(box, k.Y * Scale + 4);
            int slot = k.Slot;
            box.Click += (_, _) => SelectRow(slot);
            _canvas.Children.Add(box);
            _keyBoxes[k.Slot] = box;
            maxX = Math.Max(maxX, (k.X + k.W) * Scale);
            maxY = Math.Max(maxY, (k.Y + k.H) * Scale);
        }
        _canvas.Width = maxX + 12;
        _canvas.Height = maxY + 12;
    }

    private static string ShortText(Row r)
    {
        string kc = r.Keycode.StartsWith("KC_", StringComparison.Ordinal) ? r.Keycode[3..] : r.Keycode;
        return r.Label.Length > 0 ? $"{(r.Label.Length > 8 ? r.Label[..8] : r.Label)}\n{kc}" : kc;
    }

    private void RefreshKeyBox(Row r)
    {
        if (_keyBoxes.TryGetValue(r.Slot, out var box))
        {
            box.Background = StatusBrush(r.Status);
            if (box.Content is TextBlock t) t.Text = ShortText(r);
        }
        if (_grid.ItemContainerGenerator.ContainerFromItem(r) is DataGridRow row) ColorRow(row);
    }

    private void SelectRow(int slot)
    {
        var row = _rows.FirstOrDefault(r => r.Slot == slot);
        if (row == null) return;
        _grid.SelectedItem = row;
        _grid.ScrollIntoView(row);
    }

    private void HighlightSelected()
    {
        foreach (var (slot, box) in _keyBoxes)
        {
            bool selected = _grid.SelectedItem is Row r && r.Slot == slot;
            box.BorderBrush = selected ? Brushes.RoyalBlue : Brushes.Gray;
            box.BorderThickness = new Thickness(selected ? 3 : 1);
        }
    }

    private void UpdateSummary()
    {
        int total = _rows.Count;
        int confirmed = _rows.Count(r => r.Status == KleKeyStatus.Confirmed);
        int inferred = _rows.Count(r => r.Status == KleKeyStatus.Inferred);
        int unassigned = _rows.Count(r => r.Status == KleKeyStatus.Unassigned);
        _summary.Text = total == 0 ? "" : $"{total} キー: 確定 {confirmed}、推定 {inferred}、未割り当て {unassigned}"
            + (_result is { IsJis: true } ? "（日本語配列として読みました）" : "");
    }

    // ---- キーコードの編集 ----

    private void Grid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not Row row || e.EditingElement is not TextBox tb) return;
        string text = tb.Text.Trim();
        if (text.Length > 0 && !KeycodeTable.TryResolveBaseKey(text, out _))
        {
            MessageBox.Show(this, $"「{text}」はキーコードではありません。KC_A、KC_F1、KC_LSHIFT、SC_E06A のような、1 つのキーの名前を書いてください。", Title);
            tb.Text = row.Keycode; // 元に戻す
            return;
        }
        tb.Text = text;
        Dispatcher.BeginInvoke(new Action(UpdateSave));
    }

    private void PickKeycode()
    {
        if (_grid.SelectedItem is not Row row) { MessageBox.Show(this, "一覧のキーを選んでから押してください。", Title); return; }
        var slot = new KeySlot { Index = row.Slot, RawLabel = row.Label, X = row.Source.X, Y = row.Source.Y, W = row.Source.W, H = row.Source.H };
        var dlg = new KeyAssignDialog(slot, 0, row.Keycode.Length > 0 ? row.Keycode : "KC_NO", 1) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if (!KeycodeTable.TryResolveBaseKey(dlg.ResultToken, out _))
        {
            MessageBox.Show(this, $"「{dlg.ResultToken}」は、1 つのキーの名前ではありません。レイアウトの既定には、キーの名前（KC_A など）を選んでください。", Title);
            return;
        }
        row.Keycode = dlg.ResultToken;
        UpdateSave();
    }

    // ---- 保存 ----

    private void UpdateSave() =>
        _save.IsEnabled = _result is { CanSave: true } && KleImporter.IsValidName(_name.Text.Trim());

    private void Save()
    {
        string name = _name.Text.Trim();
        if (_result is not { CanSave: true }) return;
        if (!KleImporter.IsValidName(name)) { MessageBox.Show(this, "名前は、英数字とハイフンで、32 文字までにしてください。", Title); return; }
        if (LayoutFolders.IsBundledName(name))
        {
            MessageBox.Show(this, $"「{name}」は、同梱のレイアウトの名前です。別の名前にしてください。", Title);
            return;
        }
        string path = LayoutFolders.UserFilePath(name);
        if (File.Exists(path) &&
            MessageBox.Show(this, $"「{name}」は、すでにあります。上書きしますか。", Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        try
        {
            var map = _rows.Where(r => r.Keycode.Length > 0).ToDictionary(r => r.Slot, r => r.Keycode);
            string text = KleImporter.Apply(_json, map);
            Directory.CreateDirectory(LayoutFolders.UserDirectory);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            try { KeyboardLayout.Load(tmp); }              // 保存する前に、レイアウトとして読めることを確かめる
            catch { File.Delete(tmp); throw; }
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this, $"保存できませんでした: {ex.Message}", Title);
            return;
        }
        SavedPath = path;
        SavedName = KeyboardLayout.NameFromPath(path);
        DialogResult = true;
    }
}
