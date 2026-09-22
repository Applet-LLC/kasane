using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using KbdLayerLicenseGui.Properties;

namespace KbdLayerLicenseGui;

public partial class MainWindow : Window
{
    // Gumroad 販売ページ（製品購入・サブスク新規登録用）。LayerDriver Subscription商品
    // （appsettings.jsonのGumroad:ProductIdと同じ商品）のpermalink。
    private const string GumroadManageUrl = "https://applet.gumroad.com/l/crijiq";

    private static readonly Brush OkBrush = Brushes.DarkGreen;
    private static readonly Brush NgBrush = Brushes.Crimson;
    private static readonly Brush PendingBrush = Brushes.Gray;

    private bool _suppressLanguageEvent;
    private PipeClient.StatusResponse? _lastStatus;

    public MainWindow()
    {
        InitializeComponent();
        ApplyLocalization();
        Loaded += async (_, _) => await RefreshStatusAsync();
    }

    // 静的な画面文言（ボタン・ラベル・言語コンボ等）を現在の表示言語で再設定する。
    // 動的なステータス表示は、直近に取得済みの状態があればそれを使って再描画する
    // （言語切替のためだけにサービスへ再問い合わせしない）。
    private void ApplyLocalization()
    {
        _suppressLanguageEvent = true;

        BtnRefresh.Content = Strings.Get("Btn_Refresh");
        BtnRefresh.ToolTip = Strings.Get("Btn_Refresh_ToolTip");
        LblLicenseKey.Text = Strings.Get("Label_LicenseKey");
        BtnActivate.Content = Strings.Get("Btn_Activate");
        LblLicenseKeyRegistered.Text = Strings.Get("Label_LicenseKeyRegistered");
        LblPurchase.Text = Strings.Get("Purchase_Label");
        RunLinkGumroad.Text = Strings.Get("Purchase_Link");

        CmbLanguageJa.Content = Strings.Get("Lang_Japanese");
        CmbLanguageEn.Content = Strings.Get("Lang_English");
        CmbLanguage.SelectedItem = LocalizationHelper.CurrentLanguage == "ja" ? CmbLanguageJa : CmbLanguageEn;

        if (_lastStatus is not null)
        {
            UpdateStatusDisplay(_lastStatus);
        }
        else
        {
            TxtStatus.Text = Strings.Get("Status_Checking");
            TxtStatus.Foreground = PendingBrush;
        }

        _suppressLanguageEvent = false;
    }

    private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLanguageEvent) return;
        string language = (CmbLanguage.SelectedItem as ComboBoxItem)?.Tag as string ?? "en";
        if (language == LocalizationHelper.CurrentLanguage) return;

        LocalizationHelper.SetLanguage(language);
        ApplyLocalization();
    }

    private async void BtnActivate_Click(object sender, RoutedEventArgs e)
    {
        string key = TxtLicenseKey.Text.Trim();
        if (string.IsNullOrEmpty(key))
        {
            ShowMessage(Strings.Get("Msg_EnterLicenseKey"), isError: true);
            return;
        }

        SetBusy(true, Strings.Get("Status_Activating"));
        ShowDiagnostics(true);
        SetDiag(TxtDiagService, Strings.Get("Diag_ServiceLabel"), Strings.Get("Diag_Checking"), PendingBrush);
        SetDiag(TxtDiagGumroad, Strings.Get("Diag_GumroadLabel"), Strings.Get("Diag_Dash"), PendingBrush);
        ShowMessage(string.Empty, isError: false);

        try
        {
            var result = await PipeClient.ActivateAsync(key);

            // パイプ経由で応答が返った時点でサービス接続は成功している。
            SetDiag(TxtDiagService, Strings.Get("Diag_ServiceLabel"), Strings.Get("Diag_OK"), OkBrush);

            if (result.Success)
            {
                SetDiag(TxtDiagGumroad, Strings.Get("Diag_GumroadLabel"), Strings.Get("Diag_OK_LicenseValid"), OkBrush);
                ShowMessage(result.IsTrial ? Strings.Get("Msg_TrialActivated") : string.Empty, isError: false);
                await RefreshStatusAsync();
            }
            else
            {
                ApplyGumroadDiagnostic(result.Error);
                ShowMessage(BuildErrorMessage(result.Error, result.GumroadMessage), isError: true);
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or TimeoutException or OperationCanceledException)
        {
            SetDiag(TxtDiagService, Strings.Get("Diag_ServiceLabel"), Strings.Get("Diag_CannotConnect"), NgBrush);
            SetDiag(TxtDiagGumroad, Strings.Get("Diag_GumroadLabel"), Strings.Get("Diag_Dash"), PendingBrush);
            ShowMessage(Strings.Get("Msg_ServiceUnreachable"), isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, Strings.Get("Status_Refreshing"));
        ShowDiagnostics(true);
        SetDiag(TxtDiagService, Strings.Get("Diag_ServiceLabel"), Strings.Get("Diag_Checking"), PendingBrush);
        SetDiag(TxtDiagGumroad, Strings.Get("Diag_GumroadLabel"), Strings.Get("Diag_Dash"), PendingBrush);
        ShowMessage(string.Empty, isError: false);

        try
        {
            var status = await PipeClient.RefreshLicenseAsync();
            SetDiag(TxtDiagService, Strings.Get("Diag_ServiceLabel"), Strings.Get("Diag_OK"), OkBrush);

            switch (status.RefreshOutcome)
            {
                case "Active":
                    SetDiag(TxtDiagGumroad, Strings.Get("Diag_GumroadLabel"), Strings.Get("Diag_OK_LicenseValid"), OkBrush);
                    break;
                case "TrialExpired":
                    // トライアルは Gumroad に問い合わせていないので「接続」欄は中立表示にする。
                    SetDiag(TxtDiagGumroad, Strings.Get("Diag_GumroadLabel"), Strings.Get("Diag_Dash"), PendingBrush);
                    ShowMessage(Strings.Get("Status_TrialExpired"), isError: true);
                    break;
                case "Rejected":
                    SetDiag(TxtDiagGumroad, Strings.Get("Diag_GumroadLabel"), Strings.Get("Diag_OK_LicenseRejected"), OkBrush);
                    ShowMessage(string.IsNullOrEmpty(status.LastRejectionReason)
                        ? Strings.Get("Msg_LicenseRejectedNoReason")
                        : Strings.Format("Msg_LicenseRejectedWithReason", status.LastRejectionReason),
                        isError: true);
                    break;
                case "NoSavedKey":
                    SetDiag(TxtDiagGumroad, Strings.Get("Diag_GumroadLabel"), Strings.Get("Diag_NoSavedKey"), PendingBrush);
                    ShowMessage(Strings.Get("Msg_NoSavedKey"), isError: true);
                    break;
                case "TransientFailure":
                default:
                    SetDiag(TxtDiagGumroad, Strings.Get("Diag_GumroadLabel"), Strings.Get("Diag_Unreachable"), NgBrush);
                    ShowMessage(Strings.Get("Msg_GumroadUnreachable"), isError: true);
                    break;
            }

            UpdateStatusDisplay(status);
        }
        catch (Exception ex) when (ex is System.IO.IOException or TimeoutException or OperationCanceledException)
        {
            SetDiag(TxtDiagService, Strings.Get("Diag_ServiceLabel"), Strings.Get("Diag_CannotConnect"), NgBrush);
            SetDiag(TxtDiagGumroad, Strings.Get("Diag_GumroadLabel"), Strings.Get("Diag_Dash"), PendingBrush);
            ShowMessage(Strings.Get("Msg_ServiceUnreachable"), isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ApplyGumroadDiagnostic(string? error)
    {
        string label = Strings.Get("Diag_GumroadLabel");
        switch (error)
        {
            case "network_error":
                SetDiag(TxtDiagGumroad, label, Strings.Get("Diag_Unreachable"), NgBrush);
                break;
            case "timeout":
                SetDiag(TxtDiagGumroad, label, Strings.Get("Diag_Timeout"), NgBrush);
                break;
            case "server_error":
                SetDiag(TxtDiagGumroad, label, Strings.Get("Diag_ServerError"), NgBrush);
                break;
            case "parse_error":
                SetDiag(TxtDiagGumroad, label, Strings.Get("Diag_ParseError"), NgBrush);
                break;
            case "validation_failed":
                SetDiag(TxtDiagGumroad, label, Strings.Get("Diag_OK_KeyInvalid"), OkBrush);
                break;
            default:
                SetDiag(TxtDiagGumroad, label, Strings.Get("Diag_UnknownError"), NgBrush);
                break;
        }
    }

    private static string BuildErrorMessage(string? error, string? gumroadMessage)
    {
        string baseMsg = error switch
        {
            "validation_failed"   => Strings.Get("Err_ValidationFailed"),
            "missing_license_key" => Strings.Get("Err_MissingKey"),
            "network_error"       => Strings.Get("Err_NetworkError"),
            "timeout"             => Strings.Get("Err_Timeout"),
            "server_error"        => Strings.Get("Err_ServerError"),
            "parse_error"         => Strings.Get("Err_ParseError"),
            _                     => Strings.Format("Err_Generic", error)
        };
        return string.IsNullOrWhiteSpace(gumroadMessage)
            ? baseMsg
            : Strings.Format("Err_DetailsSuffix", baseMsg, gumroadMessage);
    }

    private async Task RefreshStatusAsync()
    {
        ClientDebugLog.Log("RefreshStatusAsync: calling PipeClient.GetStatusAsync()");
        try
        {
            var status = await PipeClient.GetStatusAsync();
            ClientDebugLog.Log($"RefreshStatusAsync: OK active={status.Active} expiry={status.Expiry} user={status.User}");
            UpdateStatusDisplay(status);
        }
        catch (Exception ex)
        {
            ClientDebugLog.Log($"RefreshStatusAsync: EXCEPTION {ex.GetType().FullName}: {ex.Message}\n{ex}");
            TxtStatus.Text = Strings.Get("Status_ServiceNotRunning");
            TxtStatus.Foreground = Brushes.Gray;
            TxtExpiry.Text = string.Empty;
            TxtUser.Text   = string.Empty;
        }
    }

    private void UpdateStatusDisplay(PipeClient.StatusResponse status)
    {
        _lastStatus = status;

        if (status.Active)
        {
            if (status.IsTrial)
            {
                TxtStatus.Text = Strings.Format("Status_TrialActive", status.DaysRemaining);
                TxtStatus.Foreground = Brushes.DarkOrange;
                TxtExpiry.Text = Strings.Format("Status_TrialExpiry", status.Expiry);
            }
            else
            {
                TxtStatus.Text = Strings.Format("Status_Active", status.DaysRemaining);
                TxtStatus.Foreground = Brushes.Green;
                TxtExpiry.Text = Strings.Format("Status_NextRenewal", status.Expiry);
            }
            TxtUser.Text   = string.IsNullOrEmpty(status.User) ? string.Empty : Strings.Format("Status_User", status.User);

            PanelActivateInput.Visibility = Visibility.Collapsed;
            PanelActivated.Visibility = Visibility.Visible;
            TxtActivatedKey.Text = status.MaskedLicenseKey ?? string.Empty;
        }
        else
        {
            if (status.IsTrial)
            {
                // トライアル期限切れ。Gumroad の明示的拒否ではないので LastRejectionReason 分岐より優先する。
                TxtStatus.Text = Strings.Get("Status_TrialExpired");
                TxtStatus.Foreground = Brushes.Crimson;
                TxtExpiry.Text = string.Empty;
            }
            else if (!string.IsNullOrEmpty(status.LastRejectionReason))
            {
                // Gumroad が明示的に拒否した理由が分かっている場合はそれを表示する
                // （支払い滞納・返金・チャージバック・台数超過など）。
                // この理由文字列自体はサービス側から渡される値であり、ここでは翻訳しない。
                TxtStatus.Text = Strings.Get("Status_Deactivated");
                TxtStatus.Foreground = Brushes.Crimson;
                TxtExpiry.Text = status.LastRejectionReason;
            }
            else if (status.HasSavedKey)
            {
                // 過去にこの PC でアクティベートされた形跡はあるが、まだ今回の起動で
                // Gumroad への再確認が済んでいない（PC起動直後にネットワーク未接続だった等）。
                // 「未アクティベート」だと再入力が必要に見えてしまうため、案内文言を分ける。
                TxtStatus.Text = Strings.Get("Status_CannotVerify");
                TxtStatus.Foreground = Brushes.OrangeRed;
                TxtExpiry.Text = Strings.Get("Status_CannotVerifyHint");
            }
            else
            {
                TxtStatus.Text = Strings.Get("Status_NotActivated");
                TxtStatus.Foreground = Brushes.OrangeRed;
                TxtExpiry.Text = string.Empty;
            }
            TxtUser.Text = string.Empty;

            PanelActivateInput.Visibility = Visibility.Visible;
            PanelActivated.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowMessage(string message, bool isError)
    {
        TxtMessage.Text       = message;
        TxtMessage.Foreground = isError ? Brushes.Crimson : Brushes.DarkGreen;
        TxtMessage.Visibility = string.IsNullOrEmpty(message)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowDiagnostics(bool visible) =>
        PanelDiagnostics.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private static void SetDiag(System.Windows.Controls.TextBlock target, string label, string status, Brush color)
    {
        target.Text = $"{label}: {status}";
        target.Foreground = color;
    }

    private void SetBusy(bool busy, string? statusText = null)
    {
        BtnActivate.IsEnabled    = !busy;
        TxtLicenseKey.IsEnabled  = !busy;
        BtnRefresh.IsEnabled     = !busy;
        if (statusText is not null) TxtStatus.Text = statusText;
    }

    private void LinkGumroad_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(GumroadManageUrl) { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }
}
