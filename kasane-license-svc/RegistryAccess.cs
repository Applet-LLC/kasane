using Microsoft.Win32;

namespace KasaneLicenseSvc;

/// <summary>
/// ドライバが直接読むライセンス関連レジストリ値（HKLM\SYSTEM\CurrentControlSet\Services\kbdlayer\
/// Parameters 配下の License / LicenseToken）の読み書きを担う。サービスは LocalSystem 権限で
/// 動作するため HKLM への書き込みが可能。このキー名・パスは kbdlayer.sys 側の仕様として固定
/// （変更不可）。
///
/// サービス自身の内部状態（SavedLicenseKey 等）は、かつてここ(Services\kasaneLicenseSvc\
/// Parameters)に保存していたが、MSI の MajorUpgrade（上書きインストール）のたびに WiX の
/// ServiceControl Remove="uninstall" が発火してキーごと消えることが実機検証で判明したため、
/// LicenseStateStore.cs（%ProgramData% 配下のファイル）へ移した。詳細はそちらのクラスコメント参照。
/// </summary>
public sealed class RegistryAccess
{
    private const string KbdParams =
        @"SYSTEM\CurrentControlSet\Services\kbdlayer\Parameters";

    private const string CryptographyKey =
        @"SOFTWARE\Microsoft\Cryptography";

    // kbdlayer Parameters 配下（ドライバが直接読む。パス・値名は固定）
    private const string ValLicenseToken = "LicenseToken";
    private const string ValLicense      = "License";

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

    private string ReadStringParam(string keyPath, string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(keyPath);
        return key?.GetValue(name) as string ?? string.Empty;
    }

    private static RegistryKey OpenOrCreateKey(string keyPath) =>
        Registry.LocalMachine.CreateSubKey(keyPath, writable: true)
        ?? throw new InvalidOperationException($"Cannot open registry key: {keyPath}");
}
