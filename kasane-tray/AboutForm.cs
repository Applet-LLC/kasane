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
        ClientSize = new Size(320, 170);
        Icon = TrayIcons.Load();

        Controls.Add(new Label { Name = "AboutName", Text = AppInfo.DisplayName, Font = new Font(Font.FontFamily, 18f, FontStyle.Bold), AutoSize = true, Location = new Point(22, 16) });
        Controls.Add(new Label { Name = "AboutVersion", Text = $"バージョン {AppInfo.VersionText}", AutoSize = true, Location = new Point(24, 62) });
        Controls.Add(new Label { Name = "AboutBuild", Text = $"ビルド {AppInfo.BuildText}", ForeColor = SystemColors.GrayText, AutoSize = true, Location = new Point(24, 86) });
        Controls.Add(new Label { Name = "AboutPublisher", Text = AppInfo.Publisher, ForeColor = SystemColors.GrayText, AutoSize = true, Location = new Point(24, 112) });

        var ok = new Button { Name = "AboutOk", Text = "OK", DialogResult = DialogResult.OK, Size = new Size(80, 28), Location = new Point(222, 128) };
        Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = ok;
    }
}
