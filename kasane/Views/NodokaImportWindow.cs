using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kasane.Import.Nodoka;
using Kasane.Model;
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
    private readonly Button _convert = new() { Name = "NodokaConvertButton", Content = Loc.T("変換する", "Convert"), Width = 100, Margin = new Thickness(0, 8, 8, 0) };
    private readonly Button _accept = new() { Name = "NodokaAcceptButton", Content = Loc.T("この内容で取り込む", "Import this"), Width = 150, Margin = new Thickness(0, 8, 8, 0), IsEnabled = false };
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
        Title = Loc.T("のどかの設定を取り込む", "Import Nodoka settings");
        Width = 760;
        Height = 700;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new DockPanel { Margin = new Thickness(10) };

        var top = new StackPanel();
        top.Children.Add(new TextBlock
        {
            Text = Loc.T(
                ("のどかの設定ファイル(.nodoka)を、kasane の設定に変換します。のどかの設定一覧から選ぶか、ファイルを選んでください。" +
                 "include しているファイルも読みます。変換できないものは、ファイル名と行番号つきで下に出ます。この画面では何も書き換えません。"),
                ("Converts a Nodoka settings file (.nodoka) into kasane settings. Choose one from the Nodoka settings list, or pick a file. " +
                 "Included files are read too. Anything that cannot be converted is listed below with its file name and line number. Nothing is changed on this screen.")),
            TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Palette.SubText, Margin = new Thickness(0, 0, 0, 8),
        });
        top.Children.Add(new TextBlock { Text = Loc.T("のどかの設定一覧:", "Nodoka settings list:") });
        top.Children.Add(_profiles);

        var pathRow = new StackPanel { Orientation = Orientation.Horizontal };
        pathRow.Children.Add(_path);
        var pick = new Button { Name = "NodokaPickButton", Content = Loc.T("ファイルを選ぶ...", "Browse..."), Width = 120, Margin = new Thickness(8, 4, 0, 0) };
        pick.Click += (_, _) => PickFile();
        pathRow.Children.Add(pick);
        top.Children.Add(new TextBlock { Text = Loc.T("設定ファイル:", "Settings file:"), Margin = new Thickness(0, 8, 0, 0) });
        top.Children.Add(pathRow);

        top.Children.Add(new TextBlock { Text = Loc.T("シンボル(-D。空白かカンマで区切る。例: USE109 EMACS):", "Symbols (-D; separated by spaces or commas, e.g. USE109 EMACS):"), Margin = new Thickness(0, 8, 0, 0) });
        top.Children.Add(_symbols);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        _convert.Click += (_, _) => Convert();
        _accept.Click += (_, _) => Accept();
        buttons.Children.Add(_convert);
        buttons.Children.Add(_accept);
        var cancel = new Button { Name = "NodokaCancelButton", Content = Loc.T("キャンセル", "Cancel"), IsCancel = true, Width = 90, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(cancel);
        top.Children.Add(buttons);
        top.Children.Add(_summary);

        var filterRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        filterRow.Children.Add(new TextBlock { Text = Loc.T("表示:", "Show:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        foreach (var item in new[] { Loc.T("すべて", "All"), Loc.T("変換できないもの", "Not converted"), Loc.T("近似したもの", "Approximated"), Loc.T("変換できたもの", "Converted"), Loc.T("情報", "Info") }) _filter.Items.Add(item);
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
        _profiles.Items.Add(new Profile(null, list.Count == 0 ? Loc.T("(のどかの設定が見つかりません。ファイルを選んでください)", "(No Nodoka settings found. Choose a file.)") : Loc.T("(選んでください)", "(Choose one)")));
        foreach (var p in list) _profiles.Items.Add(new Profile(p, (p.IsActive ? "★ " : "") + $"{p.Name}  {p.Path}"));
        int active = list.FindIndex(p => p.IsActive);
        _profiles.SelectedIndex = active >= 0 ? active + 1 : 0;
    }

    private void PickFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.T("のどかの設定ファイル", "Nodoka settings file"),
            InitialDirectory = DialogDefaults.DataFolder,
            ClientGuid = DialogDefaults.Nodoka,
            Filter = Loc.T("のどかの設定 (*.nodoka)|*.nodoka|すべてのファイル|*.*", "Nodoka settings (*.nodoka)|*.nodoka|All files|*.*"),
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true) _path.Text = dlg.FileName;
    }

    private void Convert()
    {
        string path = _path.Text.Trim();
        if (path.Length == 0) { _summary.Text = Loc.T("設定ファイルを選んでください。", "Choose a settings file."); return; }
        var symbols = _symbols.Text.Split(new[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.StartsWith("-D", StringComparison.Ordinal) ? s[2..] : s).ToList();
        try
        {
            _conversion = NodokaImporter.ImportFile(path, symbols);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _conversion = null;
            _summary.Text = Loc.T($"変換できませんでした: {ex.Message}", $"Could not convert: {ex.Message}");
            _accept.IsEnabled = false;
            return;
        }
        SourceLabel = Path.GetFileName(path);
        int ok = _conversion.Count(ConvLevel.Converted), approx = _conversion.Count(ConvLevel.Approximated);
        int skipped = _conversion.Count(ConvLevel.Skipped) + _conversion.Count(ConvLevel.Error);
        var kb = _conversion.File.Keyboards;
        _summary.Text = Loc.T(
            ($"変換できた {ok} 件、近似 {approx} 件、変換できない {skipped} 件。" +
                        (kb.Count > 0 ? $"(レイヤー{kb[0].Layers?.Count ?? 0}枚、マクロ{kb[0].Macros?.Count ?? 0}、コンボ{kb[0].Combos?.Count ?? 0}、" +
                                         $"タップダンス{kb[0].TapDances?.Count ?? 0}、キー置換{kb[0].KeyOverrides?.Count ?? 0}、" +
                                         $"ヘルパー動作{kb[0].Helper?.Actions.Count ?? 0})" : "") +
                        (kb.Count > 1 ? $" キーボードごとの設定が {kb.Count - 1} 台分あります。" : "")),
            ($"{ok} converted, {approx} approximated, {skipped} not converted." +
                        (kb.Count > 0 ? $" ({kb[0].Layers?.Count ?? 0} layers, {kb[0].Macros?.Count ?? 0} macros, {kb[0].Combos?.Count ?? 0} combos, " +
                                         $"{kb[0].TapDances?.Count ?? 0} tap dances, {kb[0].KeyOverrides?.Count ?? 0} key overrides, " +
                                         $"{kb[0].Helper?.Actions.Count ?? 0} helper actions)" : "") +
                        (kb.Count > 1 ? $" There are per-keyboard settings for {kb.Count - 1} keyboard(s)." : "")));
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
                ConvLevel.Converted => Loc.T("[変換]", "[Converted]"),
                ConvLevel.Approximated => Loc.T("[近似]", "[Approx]"),
                ConvLevel.Skipped => Loc.T("[未対応]", "[Unsupported]"),
                ConvLevel.Error => Loc.T("[エラー]", "[Error]"),
                _ => Loc.T("[情報]", "[Info]"),
            };
            // 絶対パスで始まると理由が右へ隠れるので、ファイル名だけを見せる。
            string where = l.Src != null ? $"{Path.GetFileName(l.Src.File)}:{l.Src.Line}: " : "";
            _list.Items.Add($"{mark} {where}{l.Text}");
        }
        if (_list.Items.Count == 0) _list.Items.Add(Loc.T("(該当する項目はありません)", "(No matching items)"));
    }

    private void Accept()
    {
        if (_conversion == null) return;
        Result = _conversion.File;
        DialogResult = true;
    }
}
