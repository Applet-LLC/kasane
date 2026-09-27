using System.Text.Json;

namespace KbdLayerLicenseSvc;

/// <summary>
/// サービス自身の内部状態（SavedLicenseKey / SavedUserEmail / LastRejectionReason / IsTrial）を
/// %ProgramData%\kbdlayer-license\license-svc\licensestate.json に永続化する。
///
/// 背景（重要、2026-09-27 修正）: 以前はこれらを HKLM\SYSTEM\CurrentControlSet\Services\
/// kbdlayerLicenseSvc\Parameters レジストリキーに保存していた。RegistryAccess.cs の旧コメントは
/// 「kbdlayerLicenseSvc 自身のサービスキーは WiX の ServiceInstall/ServiceControl で管理されており、
/// DriverManager.exe のような DeleteService は呼ばれないため（ドライバのような）消失は起きない」と
/// 説明していたが、これは実機検証の結果誤りだと判明した: Component GUID を固定していても、MSI の
/// MajorUpgrade（上書きインストール）のたびに ServiceControl Remove="uninstall" が実際に発火し、
/// Services\kbdlayerLicenseSvc キーごと削除→再作成されていた（debug.log 上でサービス再起動直後に
/// 一貫して "no saved key found" になっていたことを実機で確認済み）。ドライバ側は DriverManager.exe が
/// SetupInstallServicesFromInfSectionW で既存サービスを明示的に上書き更新しているのに対し、
/// ライセンスサービス側は WiX の宣言的な ServiceInstall/ServiceControl に処理を任せきりで、
/// 同等の保護が効いていなかった。
///
/// TokenSigner.cs の signingkey.dat と同じ %ProgramData% 配下は、どの WiX Component からも
/// 一切書き込み・削除の対象にされていないため、MSI のアップグレードをまたいで確実に永続化できる。
/// </summary>
public sealed class LicenseStateStore
{
    private static readonly string StateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "kbdlayer-license", "license-svc");
    private static readonly string StatePath = Path.Combine(StateDir, "licensestate.json");
    private static readonly object s_lock = new();

    private sealed class State
    {
        public string? LicenseKey { get; set; }
        public string? UserEmail { get; set; }
        public string? LastRejectionReason { get; set; }
        public bool IsTrial { get; set; }
    }

    public string ReadLicenseKey() => Load().LicenseKey ?? string.Empty;

    public void WriteLicenseKey(string licenseKey)
    {
        lock (s_lock)
        {
            var state = Load();
            state.LicenseKey = licenseKey;
            Save(state);
        }
    }

    public string ReadUserEmail() => Load().UserEmail ?? string.Empty;

    public void WriteUserEmail(string email)
    {
        lock (s_lock)
        {
            var state = Load();
            state.UserEmail = email;
            Save(state);
        }
    }

    /// <summary>
    /// 直近でライセンスが無効化された理由（Gumroad からの明示的な拒否のみ。
    /// ネットワーク未到達等の一時的失敗では書き込まない）。GUI が「未アクティベート」の
    /// 理由をユーザーに説明するために使う。
    /// </summary>
    public string ReadLastRejectionReason() => Load().LastRejectionReason ?? string.Empty;

    public void WriteLastRejectionReason(string reason)
    {
        lock (s_lock)
        {
            var state = Load();
            state.LastRejectionReason = reason;
            Save(state);
        }
    }

    /// <summary>再アクティベート成功時に、古い無効化理由が残らないよう消す。</summary>
    public void ClearLastRejectionReason()
    {
        lock (s_lock)
        {
            var state = Load();
            state.LastRejectionReason = null;
            Save(state);
        }
    }

    /// <summary>直近のアクティベートがトライアル専用商品によるものかを保存する。
    /// 本番キーでアクティベートすればここが false に上書きされ、通常の Gumroad 定期
    /// 再検証（RefreshAsync）が再開される。</summary>
    public void WriteIsTrial(bool isTrial)
    {
        lock (s_lock)
        {
            var state = Load();
            state.IsTrial = isTrial;
            Save(state);
        }
    }

    public bool ReadIsTrial() => Load().IsTrial;

    // ---------------------------------------------------------------

    private static State Load()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                string json = File.ReadAllText(StatePath);
                return JsonSerializer.Deserialize<State>(json) ?? new State();
            }
        }
        catch
        {
            // 壊れている場合は「未アクティベート」相当として扱う（フェイルセーフ）。
        }
        return new State();
    }

    private static void Save(State state)
    {
        Directory.CreateDirectory(StateDir);
        string json = JsonSerializer.Serialize(state);
        File.WriteAllText(StatePath, json);
    }
}
