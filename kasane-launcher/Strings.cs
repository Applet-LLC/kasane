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

    public static string WindowTitle => IsJapanese ? "かさね Subscription セットアップ" : "kasane Subscription Setup";

    public static string Description => IsJapanese
        ? "かさね サブスクリプションライセンス版をインストールします。\nライセンスキーの入力は、インストール完了後に表示される「License Manager」から行います。"
        : "This installs the kasane subscription license edition.\nYou'll enter your license key afterward, from the License Manager it installs.";

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

    public static string InstallFailedMessage => IsJapanese
        ? "インストールに失敗しました。\n\n詳しくは次のログを確認してください:\n  %TEMP%\\MSI*.LOG（msiexec の詳細ログ）\n  %ProgramData%\\kbdlayer-license\\debug.log\n  %ProgramData%\\kbdlayer-license\\gui-debug.log"
        : "Installation failed.\n\nCheck the following logs for details:\n  %TEMP%\\MSI*.LOG (msiexec verbose log)\n  %ProgramData%\\kbdlayer-license\\debug.log\n  %ProgramData%\\kbdlayer-license\\gui-debug.log";

    public static string InstallFailedTitle => IsJapanese ? "インストール失敗" : "Installation Failed";
}
