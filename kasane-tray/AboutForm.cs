using Kasane.Model;

namespace Kasane.Tray;

/// <summary>バージョン情報（トレイのメニューから開く。エディタにも同じ内容のダイアログがある）。UI Automation 用に Name を付ける。</summary>
internal sealed class AboutForm : Form
{
    private static AboutForm? _current;

    /// <summary>開いていれば前面へ、なければ開く（メニューを何度押しても1つだけ）。</summary>
    public static void ShowSingle()
    {
        if (_current is { IsDisposed: false })
        {
            _current.Activate();
            return;
        }
        _current = new AboutForm();
        _current.FormClosed += (_, _) => _current = null;
        _current.Show();
    }

    // 既定のブラウザーで、会社のサイトを開く。開けなくても（ブラウザーが無いなど）、何もしない。
    private static void OpenWebsite()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppInfo.Website) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { /* ブラウザーを起動できない */ }
    }

    private AboutForm()
    {
        Name = "AboutForm";
        Text = Loc.T($"{AppInfo.DisplayName} のバージョン情報", $"About {AppInfo.DisplayName}");
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = TrayIcons.Load();

        // 並べ方は、位置の数値ではなく、レイアウトの部品（縦に並べる）に任せる。以前は、位置を直接書いたため、
        // 画面の拡大率が高いと、大きい字の「かさね」の下に、次の行が重なって、上が隠れた。
        var content = new FlowLayoutPanel
        {
            Name = "AboutContent", FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty,
        };
        content.Controls.Add(new Label { Name = "AboutName", Text = AppInfo.DisplayName, Font = new Font(Font.FontFamily, 18f, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
        content.Controls.Add(new Label { Name = "AboutVersion", Text = Loc.T($"バージョン {AppInfo.VersionText}", $"Version {AppInfo.VersionText}"), AutoSize = true, Margin = new Padding(2, 0, 0, 4) });
        content.Controls.Add(new Label { Name = "AboutBuildTime", Text = Loc.T($"ビルド日時 {AppInfo.BuildTimeText}", $"Build time {AppInfo.BuildTimeText}"), AutoSize = true, Margin = new Padding(2, 0, 0, 4) });
        // 実行しているファイルの、インストール先を含むフルパス（長くても折り返して、選んでコピーできる）。
        content.Controls.Add(new TextBox
        {
            Name = "AboutPath", Text = AppInfo.ExePath, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = BackColor,
            ForeColor = SystemColors.GrayText, Multiline = true, Size = new Size(560, 36), ScrollBars = ScrollBars.None, Margin = new Padding(2, 0, 0, 8),
        });
        var link = new LinkLabel { Name = "AboutWebsite", Text = AppInfo.Website, AutoSize = true, Margin = new Padding(2, 0, 0, 4) };
        link.LinkClicked += (_, _) => OpenWebsite();
        content.Controls.Add(link);
        content.Controls.Add(new Label { Name = "AboutCopyright", Text = AppInfo.Copyright, ForeColor = SystemColors.GrayText, AutoSize = true, Margin = new Padding(2, 0, 0, 0) });

        // OK ボタンは、右下に置く（下端は、最後の行 Copyright の下端と同じ。行の高さが、その行を決める）。文字の下が切れないよう、少し高くする。
        var ok = new Button { Name = "AboutOk", Text = "OK", Size = new Size(80, 32), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Margin = new Padding(16, 0, 0, 0) };
        ok.Click += (_, _) => Close(); // Show() で開く（非モーダル）ので、DialogResult では閉じない

        // アイコンの絵は、右上に置く。
        using var logoIcon = TrayIcons.Load(new Size(128, 128));
        var logo = new PictureBox
        {
            Name = "AboutLogo", Image = logoIcon.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(72, 72),
            Anchor = AnchorStyles.Top | AnchorStyles.Right, Margin = new Padding(16, 0, 0, 12),
        };

        var layout = new TableLayoutPanel
        {
            Name = "AboutLayout", ColumnCount = 2, RowCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(24, 18, 24, 22),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(content, 0, 0);
        layout.SetRowSpan(content, 2);
        layout.Controls.Add(logo, 1, 0);
        layout.Controls.Add(ok, 1, 1);

        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = ok;
        ActiveControl = ok; // 最初に、パスの欄を選択状態にしない
        TrayTheme.Apply(this); // 色のモード（ダークなら暗くする）
    }
}
