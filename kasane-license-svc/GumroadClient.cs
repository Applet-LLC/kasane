using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KasaneLicenseSvc;

/// <summary>
/// VerifyAsync が失敗した場合の層別理由。
/// GUI 側でどの段階の問題かをユーザーに示すために使う。
/// </summary>
public enum VerifyFailureReason
{
    None,
    NetworkError,     // DNS 解決・接続確立に失敗（インターネット未接続 or Gumroad 到達不能）
    Timeout,          // 接続はできたが応答がタイムアウト
    GumroadRejected,  // Gumroad は応答した。ライセンスキー自体が無効/返金済み等
    ServerError,      // Gumroad が 2xx 以外を返した（設定不備・サーバ側の一時的な問題等）
    ParseError,       // 応答 JSON の解析に失敗
}

public readonly record struct VerifyResult(
    bool IsValid,
    string? Email,
    string? UserId,
    VerifyFailureReason FailureReason,
    string? GumroadMessage,
    int? Uses = null);

/// <summary>
/// Gumroad v2 ライセンス検証 API クライアント。
/// POST https://api.gumroad.com/v2/licenses/verify
/// </summary>
public sealed class GumroadClient
{
    private const string VerifyUrl = "https://api.gumroad.com/v2/licenses/verify";

    private readonly HttpClient _http;
    private readonly ILogger<GumroadClient> _logger;
    private readonly string _productId;
    private readonly string _productPermalink;
    private readonly string _accessToken;
    private readonly int _maxActivations;

    public GumroadClient(IConfiguration config, ILogger<GumroadClient> logger)
    {
        _logger = logger;
        // Gumroad の /v2/licenses/verify は product_permalink だけでは検証できない製品があり、
        // その場合 API が 500 で「product_id を使え」と明示的に返す（実測済み）。
        // product_id を必須パラメータとして使用する。permalink はログ用に保持するのみ。
        _productId = config["Gumroad:ProductId"]
            ?? throw new InvalidOperationException("Gumroad:ProductId is not configured");
        _productPermalink = config["Gumroad:ProductPermalink"] ?? string.Empty;
        _accessToken = config["Gumroad:AccessToken"] ?? string.Empty;
        // キー流出時の被害を抑えるため、同一ライセンスキーの累計 uses（アクティベート回数）に上限を設ける。
        // Gumroad 自体には有効化台数を強制する機能が無いため、返ってくる uses を自前でチェックする。
        _maxActivations = config.GetValue<int>("Gumroad:MaxActivations", 100);

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>
    /// ライセンスキーを Gumroad で検証する。
    /// </summary>
    /// <param name="incrementUses">true=初回アクティベート時、false=定期再検証時（uses カウントを増やさない）</param>
    /// <param name="productIdOverride">
    /// 既定（appsettings.json の Gumroad:ProductId）以外の商品と照合したい場合に指定する。
    /// トライアル専用商品（Trial:GumroadProductId）の照合に使う。
    /// </param>
    /// <param name="maxActivationsOverride">
    /// 既定（Gumroad:MaxActivations）以外の上限を使いたい場合に指定する。トライアル商品は
    /// 通常より低い上限（Trial:MaxActivations）で運用するために使う。
    /// </param>
    public async Task<VerifyResult> VerifyAsync(
        string licenseKey, bool incrementUses = true, CancellationToken ct = default,
        string? productIdOverride = null, int? maxActivationsOverride = null)
    {
        string productId = string.IsNullOrEmpty(productIdOverride) ? _productId : productIdOverride;
        int maxActivations = maxActivationsOverride ?? _maxActivations;

        var form = new Dictionary<string, string>
        {
            ["product_id"]           = productId,
            ["license_key"]          = licenseKey,
            ["increment_uses_count"] = incrementUses ? "true" : "false",
        };
        if (!string.IsNullOrEmpty(_accessToken))
            form["access_token"] = _accessToken;

        DebugTrace.Log($"VerifyAsync request: productId='{productId}' permalink='{_productPermalink}' key={MaskKey(licenseKey)} incrementUses={incrementUses} hasAccessToken={!string.IsNullOrEmpty(_accessToken)}");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsync(VerifyUrl, new FormUrlEncodedContent(form), ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient のタイムアウト（呼び出し側のキャンセルではない）
            DebugTrace.Log($"VerifyAsync timeout: {ex}");
            _logger.LogWarning(ex, "Gumroad API request timed out");
            return new VerifyResult(false, null, null, VerifyFailureReason.Timeout, null);
        }
        catch (HttpRequestException ex)
        {
            // DNS 解決失敗・接続拒否・TLS エラー等 = インターネット/Gumroad 到達不能
            bool looksLikeDns = ex.InnerException is SocketException;
            DebugTrace.Log($"VerifyAsync network error (dns/socket={looksLikeDns}): {ex}");
            _logger.LogWarning(ex, "Gumroad API network error");
            return new VerifyResult(false, null, null, VerifyFailureReason.NetworkError, null);
        }

        using (resp)
        {
            string body = await resp.Content.ReadAsStringAsync(ct);
            DebugTrace.Log($"VerifyAsync response: status={(int)resp.StatusCode} body={body}");

            if (!resp.IsSuccessStatusCode)
            {
                // 重要: Gumroad は「キーが無効」等の正当な拒否理由でも 2xx 以外のステータスコード
                // （404 等）を返すことがある（実測済み: 404 + {"success":false,"message":"..."}）。
                // ステータスコードだけで ServerError（一時的失敗扱い、剥奪しない）と決めつけると、
                // 明確に無効化されたキーがいつまでも「Gumroad に接続できません」としか表示されず
                // 実際には無効なライセンスが有効なまま扱われ続けてしまう不具合になる。
                // ボディが Gumroad の正規レスポンス（success:false + message）としてパースできる
                // 場合は GumroadRejected として扱う。パースできない場合のみ本当の ServerError とする。
                var parsed = TryParseGumroadResponse(body);
                if (parsed is not null && parsed.Success == false)
                {
                    DebugTrace.Log($"VerifyAsync: non-2xx ({(int)resp.StatusCode}) but well-formed rejection: message='{parsed.Message}'");
                    _logger.LogWarning("Gumroad rejected license (status={Status}): {Message}",
                        (int)resp.StatusCode, parsed.Message);
                    return new VerifyResult(false, null, null, VerifyFailureReason.GumroadRejected, parsed.Message);
                }

                _logger.LogWarning("Gumroad API returned {Status}: {Body}",
                    (int)resp.StatusCode, body[..Math.Min(200, body.Length)]);
                return new VerifyResult(false, null, null, VerifyFailureReason.ServerError, parsed?.Message);
            }

            return ParseResponse(body, maxActivations);
        }
    }

    private static GumroadResponse? TryParseGumroadResponse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<GumroadResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException) { return null; }
    }

    private static string MaskKey(string key) =>
        key.Length > 4 ? new string('*', key.Length - 4) + key[^4..] : "****";

    private VerifyResult ParseResponse(string json, int maxActivations)
    {
        try
        {
            var root = JsonSerializer.Deserialize<GumroadResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (root?.Success != true || root.Purchase is null)
            {
                DebugTrace.Log($"ParseResponse: success={root?.Success} message='{root?.Message}' purchaseIsNull={root?.Purchase is null}");
                _logger.LogWarning("Gumroad validation failed: success={Success} message={Message}",
                    root?.Success, root?.Message);
                return new VerifyResult(false, null, null, VerifyFailureReason.GumroadRejected, root?.Message);
            }

            var p = root.Purchase;

            // サブスク終了・返金・チャージバック を無効とみなす
            if (p.Refunded == true || p.Chargebacked == true)
            {
                DebugTrace.Log("ParseResponse: refunded or chargebacked");
                _logger.LogWarning("License is refunded or chargebacked");
                return new VerifyResult(false, null, null, VerifyFailureReason.GumroadRejected,
                    "返金またはチャージバックにより、このライセンスキーは無効になりました。", root.Uses);
            }
            // Gumroad 公式ヘルプ(license keys記事)によれば、subscription_ended_at /
            // subscription_cancelled_at / subscription_failed_at はいずれも「実際に会員資格が
            // 終了した日時」（解約操作をした日時ではない）が入り、更新が成立すれば null に戻る。
            // 3つとも同格の「アクセスを止めてよい」シグナルとして扱う必要がある
            // （以前は subscription_ended_at だけを見ていたが、それだと解約(cancelled)や
            // 継続的な支払い失敗(failed)を検知できないケースがあったため統一した）。
            string? terminatedAt = p.SubscriptionEndedAt ?? p.SubscriptionCancelledAt ?? p.SubscriptionFailedAt;
            if (terminatedAt is not null)
            {
                DebugTrace.Log($"ParseResponse: subscription terminated (ended={p.SubscriptionEndedAt} cancelled={p.SubscriptionCancelledAt} failed={p.SubscriptionFailedAt})");
                _logger.LogWarning("Subscription terminated (ended={EndedAt} cancelled={CancelledAt} failed={FailedAt})",
                    p.SubscriptionEndedAt, p.SubscriptionCancelledAt, p.SubscriptionFailedAt);
                return new VerifyResult(false, null, null, VerifyFailureReason.GumroadRejected,
                    "サブスクリプションが終了しました（支払いの失敗または解約）。Gumroadでご確認ください。", root.Uses);
            }

            // キー流出対策: 累計 uses が上限を超えていたら拒否する。
            // Uses が返ってこない場合（API仕様変更等）は判定不能として素通しする（fail-open）。
            if (root.Uses is int uses && uses > maxActivations)
            {
                DebugTrace.Log($"ParseResponse: usage limit exceeded uses={uses} max={maxActivations}");
                _logger.LogWarning("License usage limit exceeded: uses={Uses} max={Max}", uses, maxActivations);
                return new VerifyResult(false, null, null, VerifyFailureReason.GumroadRejected,
                    $"ライセンスキーの使用台数が上限（{maxActivations}台）を超えています（現在{uses}台）。", root.Uses);
            }

            DebugTrace.Log($"ParseResponse: OK email={p.Email} purchaserId={p.PurchaserId} uses={root.Uses}");
            return new VerifyResult(true, p.Email, p.PurchaserId ?? p.Email, VerifyFailureReason.None, null, root.Uses);
        }
        catch (JsonException ex)
        {
            DebugTrace.Log($"ParseResponse: JSON parse failed: {ex.Message} raw={json}");
            _logger.LogWarning(ex, "Failed to parse Gumroad response");
            return new VerifyResult(false, null, null, VerifyFailureReason.ParseError, null);
        }
    }

    // ---------------------------------------------------------------
    // JSON モデル
    // ---------------------------------------------------------------

    private sealed class GumroadResponse
    {
        [JsonPropertyName("success")]
        public bool? Success { get; set; }

        // success=false のとき Gumroad が理由を返す（例: "That license key is invalid for this product."）
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        // このライセンスキーが検証された累計回数（increment_uses_count=true のときインクリメントされる）。
        // 台数制限のチェックに使う。
        [JsonPropertyName("uses")]
        public int? Uses { get; set; }

        [JsonPropertyName("purchase")]
        public GumroadPurchase? Purchase { get; set; }
    }

    private sealed class GumroadPurchase
    {
        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("purchaser_id")]
        public string? PurchaserId { get; set; }

        [JsonPropertyName("refunded")]
        public bool? Refunded { get; set; }

        [JsonPropertyName("chargebacked")]
        public bool? Chargebacked { get; set; }

        [JsonPropertyName("subscription_ended_at")]
        public string? SubscriptionEndedAt { get; set; }

        [JsonPropertyName("subscription_cancelled_at")]
        public string? SubscriptionCancelledAt { get; set; }

        [JsonPropertyName("subscription_failed_at")]
        public string? SubscriptionFailedAt { get; set; }
    }
}
