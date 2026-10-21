using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kasane.Import.Nodoka;
using Kasane.Model.Portable;
using Microsoft.Win32;

namespace Kasane.Views;

/// <summary>
/// のどか（nodoka4）の設定ファイルを読み込んで、kasane の設定に変換する（2026-10-09、P6）。ここでは変換して結果を見せるだけで、
/// 何も書き換えない。「この内容で取り込む」を押すと、変換結果を交換ファイルの形（<see cref="Result"/>）で返し、
/// インポートの画面が、項目や行き先を選んで取り込む（ほかのインポートと同じ経路）。
/// 変換できたもの・近似したもの・変換できないものは、ファイル名:行 つきで一覧に出す。
/// </summary>
public sealed class NodokaImportWindow : Window
{
    private readonly ComboBox _profiles = new() { Name = "NodokaProfileCombo", Width = 460 };
    private readonly TextBox _path = new() { Name = "NodokaPath", Width = 460, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBox _symbols = new() { Name = "NodokaSymbols", Width = 460, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button _convert = new() { Name = "NodokaConvertButton", Content = "変換する", Width = 100, Margin = new Thickness(0, 8, 8, 0) };
    private readonly Button _accept = new() { Name = "NodokaAcceptButton", Content = "この内容で取り込む", Width = 150, Margin = new Thickness(0, 8, 8, 0), IsEnabled = false };
    private readonly TextBlock _summary = new() { Name = "NodokaSummary", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) };
    private readonly ComboBox _filter = new() { Name = "NodokaFilter", Width = 160 };
    private readonly ListBox _list = new() { Name = "NodokaLines", FontFamily = new FontFamily("Consolas"), MinHeight = 200 };
    private NodokaConversion? _conversion;

    /// <summary>変換結果（「この内容で取り込む」を押したときだけ）。</summary>
    public PortableFile? Result { get; private set; }

    /// <summary>何の設定を変換したか（インポート画面に出す名前）。</summary>
    public string SourceLabel { get; private set; } = "";

    private sealed record Profile(NodokaProfile? Value, string Text)
    {
        public override string ToString() => Text;
    }

    public NodokaImportWindow()
    {
        Title = "のどかの設定を取り込む";
        Width = 760;
        Height = 700;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new DockPanel { Margin = new Thickness(10) };

        var top = new StackPanel();
        top.Children.Add(new TextBlock
        {
            Text = "のどかの設定ファイル（.nodoka）を、kasane の設定に変換します。のどかの設定一覧から選ぶか、ファイルを選んでください。" +
                   "include しているファイルも読みます。変換できないものは、ファイル名と行番号つきで下に出ます。この画面では何も書き換えません。",
            TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Palette.SubText, Margin = new Thickness(0, 0, 0, 8),
        });
        top.Children.Add(new TextBlock { Text = "のどかの設定一覧:" });
        top.Children.Add(_profiles);

        var pathRow = new StackPanel { Orientation = Orientation.Horizontal };
        pathRow.Children.Add(_path);
        var pick = new Button { Name = "NodokaPickButton", Content = "ファイルを選ぶ...", Width = 120, Margin = new Thickness(8, 4, 0, 0) };
        pick.Click += (_, _) => PickFile();
        pathRow.Children.Add(pick);
        top.Children.Add(new TextBlock { Text = "設定ファイル:", Margin = new Thickness(0, 8, 0, 0) });
        top.Children.Add(pathRow);

        top.Children.Add(new TextBlock { Text = "シンボル（-D。空白かカンマで区切る。例: USE109 EMACS）:", Margin = new Thickness(0, 8, 0, 0) });
        top.Children.Add(_symbols);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        _convert.Click += (_, _) => Convert();
        _accept.Click += (_, _) => Accept();
        buttons.Children.Add(_convert);
        buttons.Children.Add(_accept);
        var cancel = new Button { Name = "NodokaCancelButton", Content = "キャンセル", IsCancel = true, Width = 90, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(cancel);
        top.Children.Add(buttons);
        top.Children.Add(_summary);

        var filterRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        filterRow.Children.Add(new TextBlock { Text = "表示:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        foreach (var item in new[] { "すべて", "変換できないもの", "近似したもの", "変換できたもの", "情報" }) _filter.Items.Add(item);
        _filter.SelectedIndex = 1;
        _filter.SelectionChanged += (_, _) => RefreshList();
        filterRow.Children.Add(_filter);
        top.Children.Add(filterRow);

        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_list);
        Content = root;

        _profiles.SelectionChanged += (_, _) =>
        {
            if (_profiles.SelectedItem is Profile { Value: { } p })
            {
                _path.Text = ResolveProfilePath(p);
                _symbols.Text = string.Join(" ", p.Symbols);
            }
        };
        Loaded += (_, _) => LoadProfiles();
    }

    private static string ResolveProfilePath(NodokaProfile p)
    {
        if (!string.IsNullOrWhiteSpace(p.Path) && Path.IsPathRooted(p.Path)) return p.Path;
        // パスが空・相対のときは、のどかと同じように、ホームフォルダなどから探す（見つからなければ、名前のまま見せる）。
        string name = string.IsNullOrWhiteSpace(p.Path) ? "dot.nodoka" : p.Path;
        foreach (var dir in new[] { Environment.GetEnvironmentVariable("NODOKA"), Environment.GetEnvironmentVariable("HOME"), Environment.GetEnvironmentVariable("USERPROFILE") })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            string candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return name;
    }

    private void LoadProfiles()
    {
        _profiles.Items.Clear();
        var list = NodokaRegistry.ListProfiles();
        _profiles.Items.Add(new Profile(null, list.Count == 0 ? "（のどかの設定が見つかりません。ファイルを選んでください）" : "（選んでください）"));
        foreach (var p in list) _profiles.Items.Add(new Profile(p, (p.IsActive ? "★ " : "") + $"{p.Name}  {p.Path}"));
        int active = list.FindIndex(p => p.IsActive);
        _profiles.SelectedIndex = active >= 0 ? active + 1 : 0;
    }

    private void PickFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "のどかの設定ファイル",
            InitialDirectory = DialogDefaults.DataFolder,
            ClientGuid = DialogDefaults.Nodoka,
            Filter = "のどかの設定 (*.nodoka)|*.nodoka|すべてのファイル|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true) _path.Text = dlg.FileName;
    }

    private void Convert()
    {
        string path = _path.Text.Trim();
        if (path.Length == 0) { _summary.Text = "設定ファイルを選んでください。"; return; }
        var symbols = _symbols.Text.Split(new[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.StartsWith("-D", StringComparison.Ordinal) ? s[2..] : s).ToList();
        try
        {
            _conversion = NodokaImporter.ImportFile(path, symbols);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _conversion = null;
            _summary.Text = $"変換できませんでした: {ex.Message}";
            _accept.IsEnabled = false;
            return;
        }
        SourceLabel = Path.GetFileName(path);
        int ok = _conversion.Count(ConvLevel.Converted), approx = _conversion.Count(ConvLevel.Approximated);
        int skipped = _conversion.Count(ConvLevel.Skipped) + _conversion.Count(ConvLevel.Error);
        var kb = _conversion.File.Keyboards;
        _summary.Text = $"変換できた {ok} 件、近似 {approx} 件、変換できない {skipped} 件。" +
                        (kb.Count > 0 ? $"（レイヤー{kb[0].Layers?.Count ?? 0}枚、マクロ{kb[0].Macros?.Count ?? 0}、コンボ{kb[0].Combos?.Count ?? 0}、" +
                                         $"タップダンス{kb[0].TapDances?.Count ?? 0}、キー置換{kb[0].KeyOverrides?.Count ?? 0}、" +
                                         $"ヘルパー動作{kb[0].Helper?.Actions.Count ?? 0}）" : "") +
                        (kb.Count > 1 ? $" キーボードごとの設定が {kb.Count - 1} 台分あります。" : "");
        _accept.IsEnabled = !_conversion.Lines.Any(l => l.Level == ConvLevel.Error) && kb.Count > 0;
        _filter.SelectedIndex = skipped > 0 ? 1 : 0;
        RefreshList();
    }

    private void RefreshList()
    {
        _list.Items.Clear();
        if (_conversion == null) return;
        foreach (var l in _conversion.Lines)
        {
            bool show = _filter.SelectedIndex switch
            {
                1 => l.Level is ConvLevel.Skipped or ConvLevel.Error,
                2 => l.Level == ConvLevel.Approximated,
                3 => l.Level == ConvLevel.Converted,
                4 => l.Level == ConvLevel.Info,
                _ => true,
            };
            if (!show) continue;
            string mark = l.Level switch
            {
                ConvLevel.Converted => "[変換]",
                ConvLevel.Approximated => "[近似]",
                ConvLevel.Skipped => "[未対応]",
                ConvLevel.Error => "[エラー]",
                _ => "[情報]",
            };
            // 絶対パスで始まると理由が右へ隠れるので、ファイル名だけを見せる。
            string where = l.Src != null ? $"{Path.GetFileName(l.Src.File)}:{l.Src.Line}: " : "";
            _list.Items.Add($"{mark} {where}{l.Text}");
        }
        if (_list.Items.Count == 0) _list.Items.Add("（該当する項目はありません）");
    }

    private void Accept()
    {
        if (_conversion == null) return;
        Result = _conversion.File;
        DialogResult = true;
    }
}
