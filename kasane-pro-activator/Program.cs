using Microsoft.Win32;

namespace KasaneProActivator;

/// <summary>
/// kasane Pro（買い切り版）のインストーラーから、deferred CustomAction として一度だけ
/// 呼び出される検証ツール。Gumroad にライセンスキーを問い合わせ、有効なら終了コード 0、
/// 無効なら 0 以外を返す（Return="check" なので、0 以外ならインストール全体が巻き戻る）。
///
/// ライセンスキーの受け渡しは HKLM\SOFTWARE\kasane-pro-setup\PendingLicenseKey 経由。
/// MSI（setup/Product.wxs）の CMP_LicenseKeyRegistry が、標準アクション WriteRegistryValues で
/// LICENSEKEY プロパティの値をこの値に書く。deferred CustomAction の ExeCommand="[~]" は
/// 実機で常に空文字列になったため、レジストリ経由にしている（kbdaddid 買い切り版で確認済み）。
/// 読み取ったらすぐに値を消す（一度読んだら消費済み）。
///
/// 検証結果は HKLM\SOFTWARE\kasane-pro-setup\LastVerifyOutcome / LastVerifyMessage に書く。
/// この exe は SYSTEM の deferred CustomAction なので、Session 0 分離により、自分では
/// ダイアログを出せない。ランチャー（Install-kasane-pro.exe）が msiexec の終了を待ってから
/// この値を読み、原因に合わせた案内を表示し、キーごと消す。
/// outcome は "REJECTED" | "NETWORK" | "MISSING_KEY" | "CONFIG_ERROR"。
///
/// 手動テスト用に、コマンドライン引数でキーを渡すこともできる（レジストリは読まない）。
/// 常駐しない。インストール後の再検証もしない。
/// </summary>
internal static class Program
{
    // Gumroad 内部の product_id。この製品は product_permalink だけでは検証できず、
    // /v2/licenses/verify が 400 で「product_id を使え」と教えてくれた値（実機確認済み）。
    private const string GumroadProductId = "MKU8Dk41HTZmbENIlY0HQg==";

    // Gumroad 商品ページ URL のスラッグ（https://applet.gumroad.com/l/icbqj の "icbqj"）。
    // リクエストには使わず、ログ用にだけ持つ。
    private const string GumroadProductPermalink = "icbqj";

    // 非公開製品の場合のみ必要。公開製品なら空文字のままでよい。
    private const string GumroadAccessToken = "";

    // 1 ライセンスキーあたりの最大アクティベート（検証成功）回数。
    // 低すぎると、PC の買い替えや再インストールなどの正当な利用で上限に達する。
    // 100 回までは使い回しを許し、それを超える規模の流出だけを弾く。
    private const int MaxActivations = 100;

    private const string RegistryKeyPath = @"SOFTWARE\kasane-pro-setup";
    private const string PendingKeyValueName = "PendingLicenseKey";
    private const string OutcomeValueName = "LastVerifyOutcome";
    private const string MessageValueName = "LastVerifyMessage";

    private static async Task<int> Main(string[] args)
    {
        string? licenseKey = args.Length >= 1 && !string.IsNullOrWhiteSpace(args[0])
            ? args[0].Trim()
            : ReadAndConsumePendingLicenseKey();

        Log.Trace($"Main: source={(args.Length >= 1 ? "argv" : "registry")} keyPresent={!string.IsNullOrEmpty(licenseKey)}");

        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            Console.Error.WriteLine("Usage: kasane-pro-activator.exe [license-key]");
            Log.Trace($"Main: no license key found (argv or HKLM\\{RegistryKeyPath}\\{PendingKeyValueName})");
            WriteVerifyOutcome("MISSING_KEY", "ライセンスキーが入力されていません。");
            return 2;
        }

        Log.Trace($"Main: verifying license key (len={licenseKey.Length})");

        if (GumroadProductId.StartsWith("REPLACE_WITH_", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("kasane-pro-activator is not configured: GumroadProductId is a placeholder.");
            Log.Trace("Main: GumroadProductId placeholder not replaced; aborting");
            WriteVerifyOutcome("CONFIG_ERROR", "インストーラーの設定に問題があります（GumroadProductId 未設定）。");
            return 3;
        }

        var client = new GumroadClient(GumroadProductId, GumroadProductPermalink, GumroadAccessToken, MaxActivations);

        VerifyResult result;
        try
        {
            result = await client.VerifyAsync(licenseKey);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ライセンス確認中に予期しないエラーが発生しました: {ex.Message}");
            Log.Trace($"Main: unexpected exception: {ex}");
            WriteVerifyOutcome("NETWORK", "ライセンス確認中に予期しないエラーが発生しました。");
            return 11;
        }

        if (!result.IsValid)
        {
            // GumroadRejected（キー自体が無効/返金済み/台数超過）と、それ以外（接続不可・
            // タイムアウト・サーバー障害・応答解析失敗）を区別する。前者は「再購入をお勧めします」、
            // 後者は「後日お試しください」という別の案内を launcher 側で出し分けるため。
            bool isRejection = result.FailureReason == VerifyFailureReason.GumroadRejected;
            string reason = result.FailureReason switch
            {
                VerifyFailureReason.NetworkError => "インターネットに接続できないため、ライセンスキーを確認できませんでした。",
                VerifyFailureReason.Timeout => "Gumroad サーバーへの接続がタイムアウトしました。",
                VerifyFailureReason.GumroadRejected => result.GumroadMessage ?? "ライセンスキーが無効です。",
                VerifyFailureReason.ServerError => "Gumroad サーバーでエラーが発生しました。しばらくしてから再度お試しください。",
                VerifyFailureReason.ParseError => "Gumroad からの応答を解析できませんでした。",
                _ => "ライセンスキーの確認に失敗しました。",
            };
            Console.Error.WriteLine(reason);
            Log.Trace($"Main: verification failed reason={result.FailureReason} message={result.GumroadMessage}");
            WriteVerifyOutcome(isRejection ? "REJECTED" : "NETWORK", reason);
            return isRejection ? 10 : 11;
        }

        Console.WriteLine($"License verified. Purchaser: {result.Email ?? "(unknown)"}");
        Log.Trace($"Main: verification OK email={result.Email} userId={result.UserId} uses={result.Uses}");
        ClearVerifyOutcome();
        return 0;
    }

    /// <summary>
    /// HKLM\SOFTWARE\kasane-pro-setup\PendingLicenseKey を読み取り、即座に削除する（一度読んだら消費済み）。
    /// 値が無い、または読み取り/削除に失敗した場合は null を返す。
    /// </summary>
    private static string? ReadAndConsumePendingLicenseKey()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath, writable: true);
            if (key is null)
            {
                Log.Trace($"ReadAndConsumePendingLicenseKey: key HKLM\\{RegistryKeyPath} does not exist");
                return null;
            }

            string? value = key.GetValue(PendingKeyValueName) as string;
            if (!string.IsNullOrWhiteSpace(value))
            {
                try
                {
                    key.DeleteValue(PendingKeyValueName, throwOnMissingValue: false);
                }
                catch (Exception ex)
                {
                    Log.Trace($"ReadAndConsumePendingLicenseKey: failed to delete value after reading: {ex.Message}");
                }
            }
            return value;
        }
        catch (Exception ex)
        {
            Log.Trace($"ReadAndConsumePendingLicenseKey: unexpected error: {ex}");
            return null;
        }
    }

    /// <summary>
    /// Install-kasane-pro が msiexec の終了後に読み取れるよう、検証結果を
    /// HKLM\SOFTWARE\kasane-pro-setup に書き込む。outcome は "REJECTED" | "NETWORK" | "MISSING_KEY" | "CONFIG_ERROR"。
    /// </summary>
    private static void WriteVerifyOutcome(string outcome, string message)
    {
        try
        {
            using RegistryKey key = Registry.LocalMachine.CreateSubKey(RegistryKeyPath, writable: true)
                ?? throw new InvalidOperationException($"Cannot open/create HKLM\\{RegistryKeyPath}");
            key.SetValue(OutcomeValueName, outcome);
            key.SetValue(MessageValueName, message);
        }
        catch (Exception ex)
        {
            Log.Trace($"WriteVerifyOutcome: failed to write outcome='{outcome}': {ex.Message}");
        }
    }

    /// <summary>検証成功時、前回までの失敗記録が残らないよう消す。</summary>
    private static void ClearVerifyOutcome()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath, writable: true);
            key?.DeleteValue(OutcomeValueName, throwOnMissingValue: false);
            key?.DeleteValue(MessageValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Trace($"ClearVerifyOutcome: failed: {ex.Message}");
        }
    }
}
