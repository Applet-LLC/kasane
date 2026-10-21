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
        Text = $"{AppInfo.DisplayName} のバージョン情報";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(640, 232); // 幅は、インストール先のフルパス（長い）が1行で収まる広さ。高さは、下で、最後の行に合わせて決め直す
        Icon = TrayIcons.Load();

        Controls.Add(new Label { Name = "AboutName", Text = AppInfo.DisplayName, Font = new Font(Font.FontFamily, 18f, FontStyle.Bold), AutoSize = true, Location = new Point(22, 16) });
        Controls.Add(new Label { Name = "AboutVersion", Text = $"バージョン {AppInfo.VersionText}", AutoSize = true, Location = new Point(24, 62) });
        Controls.Add(new Label { Name = "AboutBuildTime", Text = $"ビルド日時 {AppInfo.BuildTimeText}", AutoSize = true, Location = new Point(24, 86) });
        // 実行しているファイルの、インストール先を含むフルパス（長くても折り返して、選んでコピーできる）。
        Controls.Add(new TextBox
        {
            Name = "AboutPath", Text = AppInfo.ExePath, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = BackColor,
            ForeColor = SystemColors.GrayText, Multiline = true, Location = new Point(24, 112), Size = new Size(592, 36), ScrollBars = ScrollBars.None,
        });
        var link = new LinkLabel { Name = "AboutWebsite", Text = AppInfo.Website, AutoSize = true, Location = new Point(24, 160) };
        link.LinkClicked += (_, _) => OpenWebsite();
        Controls.Add(link);
        var copyright = new Label { Name = "AboutCopyright", Text = AppInfo.Copyright, ForeColor = SystemColors.GrayText, AutoSize = true, Location = new Point(24, 184) };
        Controls.Add(copyright);

        // OK ボタンは、右端に置き、下端を最後の行（Copyright）の下端にそろえる。高さは、文字の下が切れないよう、少し高くする。
        const int okHeight = 32;
        int bottom = copyright.Top + copyright.PreferredHeight;
        var ok = new Button { Name = "AboutOk", Text = "OK", Size = new Size(80, okHeight), Location = new Point(640 - 24 - 80, bottom - okHeight) };
        ok.Click += (_, _) => Close(); // Show() で開く（非モーダル）ので、DialogResult では閉じない
        Controls.Add(ok);
        AcceptButton = ok;
        ActiveControl = ok; // 最初に、パスの欄を選択状態にしない
        ClientSize = new Size(640, bottom + 22);
        CancelButton = ok;
        TrayTheme.Apply(this); // 色のモード（ダークなら暗くする）
    }
}
