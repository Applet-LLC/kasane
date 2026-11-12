namespace KasaneLauncher;

/// <summary>
/// ランチャーUIの日英文言。.resx + サテライトアセンブリではなくコード内テーブルにした理由:
/// このプロジェクトは PublishSingleFile=true / IncludeAllContentForSelfExtract=false で
/// publish するため、サテライトリソースdllが単一exeに正しく同梱されない（または実行時に
/// 別ファイル展開を要求する）リスクがある。文言数も少ないため、単純な静的プロパティで済ませる
/// （kbdaddid-license-launcher と同じ方針）。
/// </summary>
internal static class Strings
{
    public static bool IsJapanese { get; set; }

    public static string LanguageSwitchLabel => IsJapanese ? "English" : "日本語";

#if KASANE_PRO
    public static string WindowTitle => IsJapanese ? "かさね Pro セットアップ" : "kasane Pro Setup";

    public static string Description => IsJapanese
        ? "かさね Pro(買い切り版)をインストールします。\nGumroad で購入したライセンスキーを入力してください。キーの確認はインストール時に1回だけ行い、インターネット接続が必要です。"
        : "This installs kasane Pro (the one-time purchase edition).\nEnter the license key you purchased on Gumroad. The key is checked once during installation, so an internet connection is required.";

    public static string KeyLabel => IsJapanese ? "ライセンスキー:" : "License key:";

    public static string KeyEmptyMessage => IsJapanese
        ? "ライセンスキーを入力してください。"
        : "Enter your license key.";

    public static string KeyInvalidFormatMessage => IsJapanese
        ? "ライセンスキーに使えない文字が含まれています。Gumroad のメールまたはライブラリに表示されたキーを、そのまま貼り付けてください。"
        : "The license key contains characters that are not allowed. Paste the key exactly as shown in your Gumroad email or library.";

    public static string VerifyRejectedMessage(string? detail) => IsJapanese
        ? "ライセンスキーが無効です。キーを確かめて、もう一度入力してください。"
            + (string.IsNullOrWhiteSpace(detail) ? "" : $"\n\n詳細: {detail}")
            + "\n\n購入ページ: https://applet.gumroad.com/l/icbqj"
        : "The license key is not valid. Check the key and enter it again."
            + (string.IsNullOrWhiteSpace(detail) ? "" : $"\n\nDetails: {detail}")
            + "\n\nPurchase page: https://applet.gumroad.com/l/icbqj";

    public static string VerifyNetworkMessage => IsJapanese
        ? "インターネットに接続できないため、ライセンスキーを確認できませんでした。接続を確かめて、もう一度お試しください。"
        : "The license key could not be checked because the internet connection is unavailable. Check the connection and try again.";

    public static string VerifyMissingKeyMessage => IsJapanese
        ? "ライセンスキーが渡されませんでした。もう一度、キーを入力してください。"
        : "No license key was passed to the installer. Enter the key again.";

    public static string VerifyConfigErrorMessage => IsJapanese
        ? "インストーラーの設定に問題があります。サポート担当者に問い合わせてください。"
        : "The installer is not configured correctly. Contact support.";

    public static string VerifyFailedTitle => IsJapanese ? "ライセンスキーを確認できません" : "License Key Check Failed";
#else
    public static string WindowTitle => IsJapanese ? "かさね Subscription セットアップ" : "kasane Subscription Setup";

    public static string Description => IsJapanese
        ? "かさね サブスクリプションライセンス版をインストールします。\nライセンスキーの入力は、インストール完了後に表示される「License Manager」から行います。"
        : "This installs the kasane subscription license edition.\nYou'll enter your license key afterward, from the License Manager it installs.";
#endif

    /// <summary>二重起動したときに、先のウィンドウを探すための、両言語のタイトル。</summary>
    public static string[] AllWindowTitles
    {
        get
        {
            bool saved = IsJapanese;
            try
            {
                IsJapanese = true;
                string ja = WindowTitle;
                IsJapanese = false;
                return new[] { ja, WindowTitle };
            }
            finally
            {
                IsJapanese = saved;
            }
        }
    }

    public static string InstallButton => IsJapanese ? "インストール" : "Install";

    public static string CancelButton => IsJapanese ? "キャンセル" : "Cancel";

    public static string MsiNotFoundStatus(string msiFileName) => IsJapanese
        ? $"{msiFileName} が見つかりません。このランチャーと同じフォルダに配置してください。"
        : $"{msiFileName} not found. Place it in the same folder as this launcher.";

    public static string InstallingStatus => IsJapanese
        ? "インストール中です。しばらくお待ちください…"
        : "Installing. Please wait...";

    public static string MsiexecStartFailed => IsJapanese
        ? "msiexec.exe の起動に失敗しました。"
        : "Failed to start msiexec.exe.";

    public static string LauncherErrorMessage(string exceptionMessage) => IsJapanese
        ? $"インストーラーの起動中にエラーが発生しました: {exceptionMessage}"
        : $"An error occurred while starting the installer: {exceptionMessage}";

    public static string ErrorTitle => IsJapanese ? "エラー" : "Error";

#if KASANE_PRO
    public static string InstallFailedMessage => IsJapanese
        ? "インストールに失敗しました。\n\n詳しくは次のログを確認してください:\n  %TEMP%\\MSI*.LOG(msiexec の詳細ログ)\n  C:\\Windows\\Temp\\kasane-pro-activator.log(ライセンスキーの確認)"
        : "Installation failed.\n\nCheck the following logs for details:\n  %TEMP%\\MSI*.LOG (msiexec verbose log)\n  C:\\Windows\\Temp\\kasane-pro-activator.log (license key check)";
#else
    public static string InstallFailedMessage => IsJapanese
        ? "インストールに失敗しました。\n\n詳しくは次のログを確認してください:\n  %TEMP%\\MSI*.LOG(msiexec の詳細ログ)\n  %ProgramData%\\kbdlayer-license\\debug.log\n  %ProgramData%\\kbdlayer-license\\gui-debug.log"
        : "Installation failed.\n\nCheck the following logs for details:\n  %TEMP%\\MSI*.LOG (msiexec verbose log)\n  %ProgramData%\\kbdlayer-license\\debug.log\n  %ProgramData%\\kbdlayer-license\\gui-debug.log";
#endif

    public static string InstallFailedTitle => IsJapanese ? "インストール失敗" : "Installation Failed";
}
