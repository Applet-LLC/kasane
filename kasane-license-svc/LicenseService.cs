using System.Text.Json;

namespace KasaneLicenseSvc;

/// <summary>
/// ライセンス管理の中核ロジック。
/// Worker（定期再検証）と PipeServer（GUI IPC）の両方から呼ばれる。
/// </summary>
public sealed class LicenseService
{
    private readonly ILogger<LicenseService> _logger;
    private readonly GumroadClient _gumroad;
    private readonly TokenSigner _signer;
    private readonly RegistryAccess _registry;
    private readonly LicenseStateStore _state;
    private readonly IConfiguration _config;

    // インメモリ状態（スレッドセーフのため lock で保護）
    private readonly object _lock = new();
    private bool _isActive;
    private string? _licenseKey;
    private string? _userEmail;
    private DateTimeOffset _tokenExpiry;
    // 直近で Gumroad が明示的にキーを拒否した理由（未アクティベート表示の説明用）。
    // サービス起動時にレジストリから復元するので、再起動後も GUI に表示され続ける。
    private string? _lastRejectionReason;
    // 直近のアクティベートがトライアル専用商品によるものか。true の間は RefreshAsync が
    // Gumroad に一切問い合わせない（トライアルキーに対応する Gumroad 側の有料アカウントは
    // 存在しないため）。
    private bool _isTrial;

    // トークン有効期限の設定値（デフォルト 7 日）
    private int TokenExpiryDays =>
        _config.GetValue<int>("TokenExpiryDays", 7);

    // トライアル専用商品の設定（appsettings.json の Trial セクション）。
    // GumroadProductId が未設定ならトライアル機能自体を無効化する（フォールバックしない）。
    private string? TrialGumroadProductId => _config["Trial:GumroadProductId"];
    private bool HasTrialProduct => !string.IsNullOrEmpty(TrialGumroadProductId);
    private int TrialMaxActivations => _config.GetValue<int>("Trial:MaxActivations", 5);
    private int TrialDays => _config.GetValue<int>("Trial:Days", 6);

    public LicenseService(
        ILogger<LicenseService> logger,
        GumroadClient gumroad,
        TokenSigner signer,
        RegistryAccess registry,
        LicenseStateStore state,
        IConfiguration config)
    {
        _logger = logger;
        _gumroad = gumroad;
        _signer = signer;
        _registry = registry;
        _state = state;
        _config = config;

        string savedReason = _state.ReadLastRejectionReason();
        _lastRejectionReason = string.IsNullOrEmpty(savedReason) ? null : savedReason;
    }

    // ---------------------------------------------------------------
    // 状態読み取り（スレッドセーフ）
    // ---------------------------------------------------------------

    public LicenseStatus GetStatus()
    {
        lock (_lock)
        {
            int days = _isActive
                ? Math.Max(0, (int)(_tokenExpiry - DateTimeOffset.UtcNow).TotalDays)
                : 0;
            string? maskedKey = _isActive && _licenseKey is not null ? MaskKey(_licenseKey) : null;

            // 過去にアクティベートされた形跡があるか（インメモリ未確定でも、再起動を跨いで
            // 残るレジストリの SavedLicenseKey を見れば分かる）。PC起動直後にネットワーク未接続で
            // まだ RefreshAsync が一度も成功していない場合、_isActive は false のままだが
            // 「未アクティベート」ではなく「再確認待ち」であることを GUI に伝えるために使う。
            bool hasSavedKey = _isActive
                || !string.IsNullOrEmpty(_licenseKey)
                || !string.IsNullOrEmpty(_state.ReadLicenseKey());

            return new LicenseStatus(_isActive, _userEmail, _tokenExpiry, days, maskedKey, _lastRejectionReason, hasSavedKey, _isTrial);
        }
    }

    // ---------------------------------------------------------------
    // アクティベート
    // ---------------------------------------------------------------

    public async Task<ActivateResult> ActivateAsync(string licenseKey, CancellationToken ct)
    {
        DebugTrace.Log($"ActivateAsync: received key={MaskKey(licenseKey)} len={licenseKey.Length}");
        _logger.LogInformation("Activating license key {Key}", MaskKey(licenseKey));

        var result = await _gumroad.VerifyAsync(licenseKey, incrementUses: true, ct);

        if (!result.IsValid)
        {
            // 本番商品としては見つからなかった（＝そのキーはこの product_id には存在しない）場合に
            // 限り、トライアル専用商品としても照合してみる。ネットワーク不調等の一時的失敗では
            // そもそも Gumroad に到達できていないので、別 product_id で試しても無意味であり
            // 試みない（既存のエラーメッセージをそのまま返す）。
            if (result.FailureReason == VerifyFailureReason.GumroadRejected && HasTrialProduct)
            {
                DebugTrace.Log("ActivateAsync: primary product rejected, trying trial product");
                var trialResult = await _gumroad.VerifyAsync(
                    licenseKey, incrementUses: true, ct, TrialGumroadProductId, TrialMaxActivations);

                if (trialResult.IsValid)
                {
                    DebugTrace.Log($"ActivateAsync: trial validation OK email={trialResult.Email} userId={trialResult.UserId}");
                    return ApplyLicense(licenseKey, trialResult.Email, trialResult.UserId, TrialDays, isTrial: true);
                }

                DebugTrace.Log($"ActivateAsync: trial validation also failed reason={trialResult.FailureReason} message={trialResult.GumroadMessage}");
                _logger.LogWarning("Trial validation also failed for key {Key}: reason={Reason} message={Message}",
                    MaskKey(licenseKey), trialResult.FailureReason, trialResult.GumroadMessage);
                string trialErrorCode = ToErrorCode(trialResult.FailureReason);
                // トライアル商品としても明示的に拒否された場合のみ理由を保存する
                // （例: 使用台数上限超過＝このトライアルキーは既に使用済み）。
                ClearInternal(trialResult.FailureReason == VerifyFailureReason.GumroadRejected ? trialResult.GumroadMessage : null);
                return new ActivateResult(false, trialErrorCode, trialResult.GumroadMessage, null, default);
            }

            string errorCode = ToErrorCode(result.FailureReason);
            DebugTrace.Log($"ActivateAsync: Gumroad validation failed reason={result.FailureReason} message={result.GumroadMessage} -> {errorCode}");
            _logger.LogWarning("Gumroad validation failed for key {Key}: reason={Reason} message={Message}",
                MaskKey(licenseKey), result.FailureReason, result.GumroadMessage);
            // Gumroad が明示的に拒否した場合のみ理由を保存する（ネットワーク不調等では保存しない）。
            ClearInternal(result.FailureReason == VerifyFailureReason.GumroadRejected ? result.GumroadMessage : null);
            return new ActivateResult(false, errorCode, result.GumroadMessage, null, default);
        }

        DebugTrace.Log($"ActivateAsync: Gumroad validation OK email={result.Email} userId={result.UserId}");
        return ApplyLicense(licenseKey, result.Email, result.UserId, TokenExpiryDays, isTrial: false);
    }

    private static string ToErrorCode(VerifyFailureReason reason) => reason switch
    {
        VerifyFailureReason.NetworkError    => "network_error",
        VerifyFailureReason.Timeout         => "timeout",
        VerifyFailureReason.GumroadRejected => "validation_failed",
        VerifyFailureReason.ServerError     => "server_error",
        VerifyFailureReason.ParseError      => "parse_error",
        _                                   => "unknown_error",
    };

    // ---------------------------------------------------------------
    // 定期再検証（Worker から呼ばれる）
    // ---------------------------------------------------------------

    /// <summary>
    /// 起動時・定期チェックで保存済みキーを再検証する。
    /// キーが保存されていない場合は何もしない。
    /// 戻り値は Worker 側でリトライ要否を判断するために使う
    /// （TransientFailure のときだけ、時間をおいて再試行する価値がある）。
    /// </summary>
    public async Task<RefreshOutcome> RefreshAsync(CancellationToken ct)
    {
        bool inMemoryKeyPresent;
        string savedKey;
        bool isTrial;
        DateTimeOffset tokenExpiry;
        lock (_lock)
        {
            inMemoryKeyPresent = !string.IsNullOrEmpty(_licenseKey);
            savedKey = _licenseKey ?? string.Empty;
            isTrial = _isTrial;
            tokenExpiry = _tokenExpiry;
        }

        if (string.IsNullOrEmpty(savedKey))
        {
            savedKey = _state.ReadLicenseKey();
            isTrial = _state.ReadIsTrial();
            // トライアルは Gumroad に再確認できないため、サービス再起動直後で in-memory 状態が
            // 無い場合、ドライバ用に保存済みの署名トークン自体から exp を読み取って復元する。
            tokenExpiry = TryDecodeTokenExpiry(_registry.ReadLicenseToken()) ?? default;
            _logger.LogInformation(
                "RefreshAsync: no in-memory key, read from registry SavedLicenseKey. found={Found} key={Key} isTrial={IsTrial}",
                !string.IsNullOrEmpty(savedKey), string.IsNullOrEmpty(savedKey) ? "(empty)" : MaskKey(savedKey), isTrial);
            if (string.IsNullOrEmpty(savedKey))
            {
                _logger.LogWarning("RefreshAsync: no saved license key anywhere (in-memory or registry); skipping refresh, staying inactive");
                DebugTrace.Log("RefreshAsync: no saved key found, early return");
                return RefreshOutcome.NoSavedKey;
            }
        }
        else
        {
            _logger.LogInformation("RefreshAsync: using in-memory key (from earlier Activate in this process run)");
        }

        if (isTrial)
        {
            // トライアルキーには対応する Gumroad の有料アカウントが存在しないため、定期リフレッシュで
            // Gumroad に問い合わせることは絶対にしない（問い合わせれば「商品が存在しない」等で
            // 誤ってライセンスが剥奪されてしまう）。ドライバは署名済みトークンの exp を完全オフラインで
            // 検証しているので、ここではサービス側の内部状態をそれに合わせて整合させるだけでよい。
            bool active = tokenExpiry > DateTimeOffset.UtcNow;
            DebugTrace.Log($"RefreshAsync: trial license, skipping Gumroad entirely. expiry={tokenExpiry:O} active={active}");
            _logger.LogInformation("RefreshAsync: trial license, expiry={Expiry} active={Active}", tokenExpiry, active);

            if (!active)
            {
                // ドライバ側も exp で独自に弾くため必須ではないが、GUI/レジストリの状態を
                // 早期に整合させるため、期限切れが判明した時点で明示的に無効化しておく。
                _registry.ClearLicense();
            }

            lock (_lock)
            {
                _isActive = active;
                _licenseKey = savedKey;
                _tokenExpiry = tokenExpiry;
                _isTrial = true;
            }

            return active ? RefreshOutcome.Active : RefreshOutcome.TrialExpired;
        }

        _logger.LogInformation("Refreshing license for key {Key} (source={Source})",
            MaskKey(savedKey), inMemoryKeyPresent ? "memory" : "registry");

        var result = await _gumroad.VerifyAsync(savedKey, incrementUses: false, ct);
        if (result.IsValid)
        {
            ApplyLicense(savedKey, result.Email, result.UserId, TokenExpiryDays, isTrial: false);
            _logger.LogInformation("RefreshAsync: result -> ACTIVE");
            return RefreshOutcome.Active;
        }
        else if (result.FailureReason is VerifyFailureReason.NetworkError or VerifyFailureReason.Timeout
                                       or VerifyFailureReason.ServerError or VerifyFailureReason.ParseError)
        {
            // Gumroad に到達できなかっただけの一時的な失敗ではライセンスを剥奪しない。
            // 既存トークンが有効な間はそのまま使えるので、次回の定期チェックに委ねる。
            // 注意: この分岐では in-memory _isActive は変更しない。フレッシュな service
            // プロセス起動直後にこの分岐へ来た場合、_isActive は初期値 false のままになる
            // （レジストリ License=1 は温存されるが GUI 上は「未アクティベート」に見える）。
            // -> これを軽減するため、Worker 側で TransientFailure 時はバックオフ付きリトライを行う。
            DebugTrace.Log($"RefreshAsync: transient failure ({result.FailureReason}), keeping current in-memory state as-is");
            _logger.LogWarning(
                "RefreshAsync: result -> KEEPING STATE AS-IS (reason={Reason}, message={Message}). " +
                "Current in-memory _isActive is UNCHANGED by this branch -- if this is a fresh service " +
                "start, _isActive is still false even though registry License may still be 1.",
                result.FailureReason, result.GumroadMessage);
            return RefreshOutcome.TransientFailure;
        }
        else
        {
            // Gumroad が明示的に拒否した（支払い滞納・返金・台数超過等）。理由を保存して GUI に表示する。
            DebugTrace.Log($"RefreshAsync: Gumroad rejected key, clearing license (message={result.GumroadMessage})");
            _logger.LogWarning("RefreshAsync: result -> INACTIVE (Gumroad rejected key, message={Message}); clearing license",
                result.GumroadMessage);
            ClearInternal(result.GumroadMessage);
            return RefreshOutcome.Rejected;
        }
    }

    // ---------------------------------------------------------------
    // 内部ヘルパー
    // ---------------------------------------------------------------

    private ActivateResult ApplyLicense(string licenseKey, string? email, string? userId, int expiryDays, bool isTrial)
    {
        var expiry = DateTimeOffset.UtcNow.AddDays(expiryDays);
        long expUnix = expiry.ToUnixTimeSeconds();
        string machineGuid = _registry.ReadMachineGuid();
        string token = _signer.CreateToken(userId ?? email ?? "unknown", machineGuid, expUnix);

        _registry.WriteLicenseToken(token);
        _state.WriteLicenseKey(licenseKey);
        if (!string.IsNullOrEmpty(email))
            _state.WriteUserEmail(email);
        // 本番キーでアクティベートすれば false で上書きされ、通常の Gumroad 定期再検証が再開される。
        _state.WriteIsTrial(isTrial);
        // 再アクティベートに成功したので、古い無効化理由は表示され続けないよう消す。
        _state.ClearLastRejectionReason();

        lock (_lock)
        {
            _isActive = true;
            _licenseKey = licenseKey;
            _userEmail = email;
            _tokenExpiry = expiry;
            _lastRejectionReason = null;
            _isTrial = isTrial;
        }

        _logger.LogInformation("License applied. expiry={Expiry} email={Email} isTrial={IsTrial}", expiry, email, isTrial);
        return new ActivateResult(true, null, null, email, expiry, isTrial);
    }

    /// <summary>
    /// ライセンスを無効化する。
    /// </summary>
    /// <param name="rejectionReason">
    /// Gumroad が明示的に拒否した場合の理由。GUI表示用にレジストリへ保存する。
    /// ネットワーク不調等の一時的失敗ではこのメソッド自体を呼ばない（呼び出し側で分岐済み）ため、
    /// null を渡すのは「理由不明の無効化」のときのみ。
    /// </param>
    private void ClearInternal(string? rejectionReason = null)
    {
        _registry.ClearLicense();
        if (rejectionReason is not null)
        {
            _state.WriteLastRejectionReason(rejectionReason);
        }

        lock (_lock)
        {
            _isActive = false;
            _userEmail = null;
            _tokenExpiry = default;
            if (rejectionReason is not null)
            {
                _lastRejectionReason = rejectionReason;
            }
            // licenseKey は保持し続ける（次回起動時の再試行のため）
        }
    }

    private static string MaskKey(string key) =>
        key.Length > 4 ? new string('*', key.Length - 4) + key[^4..] : "****";

    /// <summary>
    /// 署名済みトークン文字列（&lt;base64url(payload)&gt;.&lt;base64url(sig)&gt;）から、
    /// 署名検証はせず payload 部分の "exp" だけを読み取る。トライアルライセンスは
    /// サービス再起動後に Gumroad へ再確認できないため、これが有効期限の唯一の情報源になる。
    /// このトークンは直前にこのサービス自身が発行したものであり、改めて署名検証する必要はない
    /// （ドライバ側が毎回独立に署名検証するので、なりすましトークンが実害を持つことはない）。
    /// </summary>
    private static DateTimeOffset? TryDecodeTokenExpiry(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        try
        {
            int dot = token.IndexOf('.');
            if (dot < 0) return null;

            string payloadB64 = token[..dot].Replace('-', '+').Replace('_', '/');
            switch (payloadB64.Length % 4)
            {
                case 2: payloadB64 += "=="; break;
                case 3: payloadB64 += "="; break;
            }

            byte[] payloadBytes = Convert.FromBase64String(payloadB64);
            using var doc = JsonDocument.Parse(payloadBytes);
            if (doc.RootElement.TryGetProperty("exp", out var expEl) && expEl.TryGetInt64(out long expUnix))
                return DateTimeOffset.FromUnixTimeSeconds(expUnix);
        }
        catch
        {
            // 壊れている/想定外の形式のトークンは「有効期限不明」として扱う（呼び出し側で default 扱い）。
        }
        return null;
    }
}

public sealed record ActivateResult(
    bool Success,
    string? Error,
    string? GumroadMessage,
    string? UserEmail,
    DateTimeOffset Expiry,
    bool IsTrial = false);

public sealed record LicenseStatus(
    bool Active,
    string? Email,
    DateTimeOffset Expiry,
    int DaysRemaining,
    string? MaskedLicenseKey,
    string? LastRejectionReason = null,
    bool HasSavedKey = false,
    bool IsTrial = false);

/// <summary>
/// RefreshAsync の結果種別。Worker はこの値でリトライすべきかを判断する。
/// TransientFailure のみリトライの価値がある（PC起動直後のネットワーク未確立など）。
/// </summary>
public enum RefreshOutcome
{
    /// <summary>保存済みキーが見つからなかった（未アクティベート状態）。リトライ不要。</summary>
    NoSavedKey,
    /// <summary>Gumroad 検証成功、ライセンス適用済み。</summary>
    Active,
    /// <summary>ネットワーク到達不能・タイムアウト等の一時的失敗。時間をおいてリトライする価値がある。</summary>
    TransientFailure,
    /// <summary>Gumroad がキーを明確に拒否した（返金・失効等）。リトライ不要。</summary>
    Rejected,
    /// <summary>トライアルライセンスの有効期限（ローカルのトークン exp）が過ぎた。リトライ不要。</summary>
    TrialExpired,
}
