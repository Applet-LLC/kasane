using System.Diagnostics;
using System.Globalization;

namespace KbdLayerLicenseLauncher;

/// <summary>
/// インストーラー起動専用の小さな WinForms ランチャー。
///
/// kbdaddid_subscribe の kbdaddid-license-launcher（Install-kbdaddidLicense.exe）からのフォーク。
/// ライセンスキー入力を持たない点は元と同じ（買い切り版 license-key-launcher と異なる点も踏襲）。
/// ライセンスのアクティベートはインストール後に kbdlayer-license-gui.exe が
/// kbdlayer-license-svc.exe 経由で（Gumroadに対して非同期に）行うため、
/// インストール時点でのキー入力・検証はここでは不要。
///
/// OSの表示言語を自動判定し、対応する言語のMSI（KbdLayerSetup-en-US.msi /
/// -ja-JP.msi）を同じフォルダから選んで msiexec /i で起動する。
/// </summary>
public sealed class MainForm : Form
{
    // ScheduleReboot（Product.wxs）によりドライバインストール後は再起動が予約されるため、
    // msiexec は成功時でも 0 ではなくこれらのコードを返しうる。
    // 3010 = ERROR_SUCCESS_REBOOT_REQUIRED（再起動が必要だが成功）、
    // 1641 = ERROR_SUCCESS_REBOOT_INITIATED（既に再起動を開始した）。
    private const int ErrorSuccessRebootRequired = 3010;
    private const int ErrorSuccessRebootInitiated = 1641;

    private readonly Label _description;
    private readonly Button _installButton;
    private readonly Button _cancelButton;
    private readonly Label _statusLabel;
    private readonly LinkLabel _languageLink;

    private readonly bool _msiMissing;

    public MainForm()
    {
        // OSの表示言語（日本語UI/日本語Windowsなら ja）を既定言語として検出する。
        // ユーザーは右上のリンクでいつでも切り替えられる。
        Strings.IsJapanese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja";

        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Yu Gothic UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        Padding = new Padding(16);
        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
            // アイコン抽出に失敗しても致命的ではないため既定アイコンのまま続行する。
        }

        _languageLink = new LinkLabel
        {
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleRight,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Margin = new Padding(0, 0, 0, 8),
        };
        _languageLink.Click += (_, _) =>
        {
            Strings.IsJapanese = !Strings.IsJapanese;
            ApplyLanguage();
        };

        var languageRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
        };
        languageRow.Controls.Add(_languageLink);

        _description = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            Margin = new Padding(0, 0, 0, 12),
        };

        _statusLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            Margin = new Padding(0, 0, 0, 8),
            ForeColor = Color.DimGray,
            MinimumSize = new Size(420, 32),
        };

        _installButton = new Button
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(100, 30),
            Padding = new Padding(8, 2, 8, 2),
            Margin = new Padding(0, 0, 8, 0),
        };
        _installButton.Click += OnInstallClicked;

        _cancelButton = new Button
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(100, 30),
            Padding = new Padding(8, 2, 8, 2),
            // Margin を明示しないと Button の既定値 Padding(3,3,3,3) が付き、
            // 上マージン 0 の _installButton より 3px 下にずれて見えていた。
            Margin = new Padding(0, 0, 0, 0),
        };
        _cancelButton.Click += (_, _) => Close();

        var buttonRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
        };
        // RightToLeft の FlowLayoutPanel は追加順が右→左に並ぶため、Cancel を先に足すと
        // 見た目は [Install][Cancel] の順になる。
        buttonRow.Controls.Add(_cancelButton);
        buttonRow.Controls.Add(_installButton);

        var root = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            WrapContents = false,
        };
        root.Controls.Add(languageRow);
        root.Controls.Add(_description);
        root.Controls.Add(_statusLabel);
        root.Controls.Add(buttonRow);

        Controls.Add(root);
        AcceptButton = _installButton;
        CancelButton = _cancelButton;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _msiMissing = !File.Exists(GetMsiPath());
        ApplyLanguage();
    }

    /// <summary>Strings.IsJapanese の現在値に合わせて全コントロールの文言を再適用する（言語切替リンク用）。</summary>
    private void ApplyLanguage()
    {
        Text = Strings.WindowTitle;
        _languageLink.Text = Strings.LanguageSwitchLabel;
        _description.Text = Strings.Description;
        _installButton.Text = Strings.InstallButton;
        _cancelButton.Text = Strings.CancelButton;
        ApplySharedButtonSize();

        if (_msiMissing)
        {
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = Strings.MsiNotFoundStatus(MsiFileName);
            _installButton.Enabled = false;
        }
        else
        {
            _statusLabel.Text = string.Empty;
        }
    }

    /// <summary>
    /// 両ボタンのテキスト（言語切替で変わる）に合わせて PreferredSize を測り直し、
    /// 大きい方に揃えた共通サイズを両ボタンへ適用する。AutoSize のままだとボタンごとに
    /// テキスト幅に応じた微妙に異なるサイズが計算され、縦の高さや位置がずれて見えるため、
    /// 一旦 AutoSize で測ってから固定サイズとして適用する。
    /// </summary>
    private void ApplySharedButtonSize()
    {
        foreach (Button b in new[] { _installButton, _cancelButton })
        {
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        }

        Size sharedButtonSize = new(
            Math.Max(_installButton.PreferredSize.Width, _cancelButton.PreferredSize.Width),
            Math.Max(_installButton.PreferredSize.Height, _cancelButton.PreferredSize.Height));

        foreach (Button b in new[] { _installButton, _cancelButton })
        {
            b.AutoSize = false;
            b.Size = sharedButtonSize;
        }
    }

    /// <summary>選択中の言語に対応するMSIファイル名（このランチャーと同じフォルダに置かれる想定）。</summary>
    private static string MsiFileName => Strings.IsJapanese
        ? "KbdLayerSetup-ja-JP.msi"
        : "KbdLayerSetup-en-US.msi";

    private static string GetMsiPath() => Path.Combine(AppContext.BaseDirectory, MsiFileName);

    private async void OnInstallClicked(object? sender, EventArgs e)
    {
        string msiPath = GetMsiPath();
        if (!File.Exists(msiPath))
        {
            MessageBox.Show(this, Strings.MsiNotFoundStatus(MsiFileName), Strings.ErrorTitle,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        SetBusy(true, Strings.InstallingStatus);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                Arguments = $"/i \"{msiPath}\"",
                UseShellExecute = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                throw new InvalidOperationException(Strings.MsiexecStartFailed);
            }

            await process.WaitForExitAsync();

            if (process.ExitCode == 0
                || process.ExitCode == ErrorSuccessRebootRequired
                || process.ExitCode == ErrorSuccessRebootInitiated)
            {
                Close();
                return;
            }

            MessageBox.Show(this, Strings.InstallFailedMessage, Strings.InstallFailedTitle,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Strings.LauncherErrorMessage(ex.Message),
                Strings.ErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private void SetBusy(bool busy, string statusText)
    {
        _installButton.Enabled = !busy && !_msiMissing;
        _cancelButton.Enabled = !busy;
        _statusLabel.ForeColor = Color.DimGray;
        _statusLabel.Text = statusText;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }
}
