using Microsoft.Win32;

namespace KbdLayerLicenseSvc;

/// <summary>
/// ライセンス関連レジストリ値の読み書きを担う。
/// サービスは LocalSystem 権限で動作するため HKLM への書き込みが可能。
///
/// 保存先を 2 つに分けている:
///   - HKLM\...\Services\kbdlayer\Parameters
///       License / LicenseToken のみ。kbdlayer.sys が直接読むため、
///       このキー名・パスは仕様として固定（変更不可）。
///   - HKLM\...\Services\kbdlayerLicenseSvc\Parameters
///       SavedLicenseKey / SavedUserEmail。サービス自身の再起動時リカバリ用の内部状態。
///
/// 注意（重要）: kbdlayer 側のキーに SavedLicenseKey 等を混在させてはいけない。
/// DriverManager.exe の UninstallDriver() は kbdlayer サービスに対して DeleteService() を呼ぶが、
/// これは Services\kbdlayer の登録レジストリキー全体（Parameters サブキーごと）を削除する。
/// MSI のメジャーアップグレードのたびに kbdlayer ドライバは uninstall→install されるため、
/// もし SavedLicenseKey をそこに置いていると MSI 再インストールのたびに消失し、
/// サービス再起動時に「保存済みキーなし」として再アクティベートが必要になってしまう
/// （kbdaddidで実際に発生した不具合を踏まえた設計。2026-07-21 修正）。
/// kbdlayerLicenseSvc 自身のサービスキーは WiX の ServiceInstall/ServiceControl で管理されており、
/// DriverManager.exe のような DeleteService は呼ばれないため、この問題が起きない。
/// </summary>
public sealed class RegistryAccess
{
    private const string KbdParams =
        @"SYSTEM\CurrentControlSet\Services\kbdlayer\Parameters";

    private const string SvcParams =
        @"SYSTEM\CurrentControlSet\Services\kbdlayerLicenseSvc\Parameters";

    private const string CryptographyKey =
        @"SOFTWARE\Microsoft\Cryptography";

    // kbdlayer Parameters 配下（ドライバが直接読む。パス・値名は固定）
    private const string ValLicenseToken = "LicenseToken";
    private const string ValLicense      = "License";

    // kbdlayerLicenseSvc Parameters 配下（サービス自身の内部状態。ドライバ再インストールの影響を受けない）
    private const string ValLicenseKey   = "SavedLicenseKey";   // サービスが保存する Gumroad キー
    private const string ValUserEmail    = "SavedUserEmail";    // GUI 表示用メールアドレス
    private const string ValLastRejectionReason = "LastRejectionReason"; // 直近の無効化理由（GUI表示用）
    private const string ValIsTrial      = "IsTrial";           // 直近のアクティベートがトライアル商品によるものか（REG_DWORD 0/1）

    // ---------------------------------------------------------------

    public string ReadMachineGuid()
    {
        using var key = Registry.LocalMachine.OpenSubKey(CryptographyKey);
        return key?.GetValue("MachineGuid") as string ?? string.Empty;
    }

    // ---------------------------------------------------------------

    /// <summary>kbdlayer ドライバが読む LicenseToken と License DWORD を書く。</summary>
    public void WriteLicenseToken(string token)
    {
        using var key = OpenOrCreateKey(KbdParams);
        key.SetValue(ValLicenseToken, token, RegistryValueKind.String);
        key.SetValue(ValLicense, 1, RegistryValueKind.DWord);
    }

    /// <summary>ライセンス無効化：LicenseToken を削除し License を 0 にする。</summary>
    public void ClearLicense()
    {
        using var key = OpenOrCreateKey(KbdParams);
        try { key.DeleteValue(ValLicenseToken, throwOnMissingValue: false); } catch { /* ignore */ }
        key.SetValue(ValLicense, 0, RegistryValueKind.DWord);
    }

    /// <summary>ドライバが読む署名済みトークン文字列をそのまま読み返す。
    /// トライアルライセンスはサービス再起動後、Gumroad に問い合わせずトークン自体の
    /// exp（有効期限）を読み取って復元するために使う。</summary>
    public string ReadLicenseToken() =>
        ReadStringParam(KbdParams, ValLicenseToken);

    // ---------------------------------------------------------------

    /// <summary>再起動後も再検証できるよう Gumroad ライセンスキーを保存する（サービス自身のキーへ）。</summary>
    public void WriteLicenseKey(string licenseKey) =>
        WriteStringParam(SvcParams, ValLicenseKey, licenseKey);

    public string ReadLicenseKey() =>
        ReadStringParam(SvcParams, ValLicenseKey);

    public void WriteUserEmail(string email) =>
        WriteStringParam(SvcParams, ValUserEmail, email);

    public string ReadUserEmail() =>
        ReadStringParam(SvcParams, ValUserEmail);

    /// <summary>
    /// 直近でライセンスが無効化された理由（Gumroad からの明示的な拒否のみ。
    /// ネットワーク未到達等の一時的失敗では書き込まない）を保存する。
    /// GUI が「未アクティベート」の理由をユーザーに説明するために使う。
    /// </summary>
    public void WriteLastRejectionReason(string reason) =>
        WriteStringParam(SvcParams, ValLastRejectionReason, reason);

    public string ReadLastRejectionReason() =>
        ReadStringParam(SvcParams, ValLastRejectionReason);

    /// <summary>再アクティベート成功時に、古い無効化理由が残らないよう消す。</summary>
    public void ClearLastRejectionReason()
    {
        using var key = OpenOrCreateKey(SvcParams);
        try { key.DeleteValue(ValLastRejectionReason, throwOnMissingValue: false); } catch { /* ignore */ }
    }

    /// <summary>直近のアクティベートがトライアル専用商品によるものかを保存する。
    /// 本番キーでアクティベートすればここが false に上書きされ、通常の Gumroad 定期
    /// 再検証（RefreshAsync）が再開される。</summary>
    public void WriteIsTrial(bool isTrial)
    {
        using var key = OpenOrCreateKey(SvcParams);
        key.SetValue(ValIsTrial, isTrial ? 1 : 0, RegistryValueKind.DWord);
    }

    public bool ReadIsTrial()
    {
        using var key = Registry.LocalMachine.OpenSubKey(SvcParams);
        return (key?.GetValue(ValIsTrial) as int?) == 1;
    }

    // ---------------------------------------------------------------

    private void WriteStringParam(string keyPath, string name, string value)
    {
        using var key = OpenOrCreateKey(keyPath);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    private string ReadStringParam(string keyPath, string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(keyPath);
        return key?.GetValue(name) as string ?? string.Empty;
    }

    private static RegistryKey OpenOrCreateKey(string keyPath) =>
        Registry.LocalMachine.CreateSubKey(keyPath, writable: true)
        ?? throw new InvalidOperationException($"Cannot open registry key: {keyPath}");
}
