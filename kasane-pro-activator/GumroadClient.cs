using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KasaneProActivator;

/// <summary>
/// VerifyAsync が失敗した場合の層別理由。呼び出し側がユーザー/MSIログにどの段階の
/// 問題かを示すために使う。
/// </summary>
public enum VerifyFailureReason
{
    None,
    NetworkError,     // DNS 解決・接続確立に失敗（インターネット未接続 or Gumroad 到達不能）
    Timeout,          // 接続はできたが応答がタイムアウト
    GumroadRejected,  // Gumroad は応答した。ライセンスキー自体が無効/返金済み/台数超過等
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
/// Gumroad v2 ライセンス検証 API クライアント（買い切りモデル用の簡略版）。kbdaddid 買い切り版の license-activator が元。
/// POST https://api.gumroad.com/v2/licenses/verify
///
/// サブスク版 kasane-license-svc/GumroadClient.cs との違い:
///   - サブスクリプション終了判定（subscription_ended_at 等）を削除。買い切りには
///     サブスク終了という概念が無い。
///   - 定期再検証 (incrementUses:false) の分岐を削除。買い切りはインストール実行時の
///     一度だけ検証すればよい。
///   - refunded / chargebacked 判定と、uses によるアクティベート台数上限チェックは維持。
/// </summary>
public sealed class GumroadClient
{
    private const string VerifyUrl = "https://api.gumroad.com/v2/licenses/verify";

    private readonly HttpClient _http;
    private readonly string _productId;
    private readonly string _productPermalink;
    private readonly string _accessToken;
    private readonly int _maxActivations;

    /// <param name="productId">
    /// Gumroad 内部の product_id（URL スラッグ＝permalink とは別物）。/v2/licenses/verify は
    /// この製品では product_permalink だけでは検証できず、product_id が必須（実機検証で確認済み。
    /// permalink を渡すと "That license does not exist for the provided product." で拒否され、
    /// 代わりに product_id を渡すと Gumroad 側が 400 で正しい product_id 値を教えてくれた）。
    /// </param>
    /// <param name="productPermalink">
    /// Gumroad 商品ページ URL のスラッグ部分（例: https://applet.gumroad.com/l/icbqj の "icbqj"）。
    /// リクエストには使わずログ用にのみ保持する。
    /// </param>
    public GumroadClient(string productId, string productPermalink, string accessToken = "", int maxActivations = 3)
    {
        _productId = productId;
        _productPermalink = productPermalink;
        _accessToken = accessToken;
        // キー流出時の被害を抑えるため、同一ライセンスキーの累計 uses（アクティベート回数）に上限を設ける。
        // Gumroad 自体には有効化台数を強制する機能が無いため、返ってくる uses を自前でチェックする。
        _maxActivations = maxActivations;

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>
    /// ライセンスキーを Gumroad で検証する。買い切りモデルでは常に increment_uses_count=true
    /// で呼ぶ（インストール実行という「実際の使用」が発生した時だけ呼ばれるため）。
    /// </summary>
    public async Task<VerifyResult> VerifyAsync(string licenseKey, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["product_id"]           = _productId,
            ["license_key"]          = licenseKey,
            ["increment_uses_count"] = "true",
        };
        if (!string.IsNullOrEmpty(_accessToken))
            form["access_token"] = _accessToken;

        Log.Trace($"VerifyAsync request: productId='{_productId}' productPermalink='{_productPermalink}' key={MaskKey(licenseKey)} hasAccessToken={!string.IsNullOrEmpty(_accessToken)}");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsync(VerifyUrl, new FormUrlEncodedContent(form), ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            Log.Trace($"VerifyAsync timeout: {ex}");
            return new VerifyResult(false, null, null, VerifyFailureReason.Timeout, null);
        }
        catch (HttpRequestException ex)
        {
            bool looksLikeDns = ex.InnerException is SocketException;
            Log.Trace($"VerifyAsync network error (dns/socket={looksLikeDns}): {ex}");
            return new VerifyResult(false, null, null, VerifyFailureReason.NetworkError, null);
        }

        using (resp)
        {
            string body = await resp.Content.ReadAsStringAsync(ct);
            Log.Trace($"VerifyAsync response: status={(int)resp.StatusCode} body={body}");

            if (!resp.IsSuccessStatusCode)
            {
                // 重要: Gumroad は「キーが無効」等の正当な拒否理由でも 2xx 以外のステータスコード
                // （404 等）を返すことがある。ボディが Gumroad の正規レスポンス（success:false + message）
                // としてパースできる場合は GumroadRejected として扱う。パースできない場合のみ ServerError とする。
                var parsed = TryParseGumroadResponse(body);
                if (parsed is not null && parsed.Success == false)
                {
                    Log.Trace($"VerifyAsync: non-2xx ({(int)resp.StatusCode}) but well-formed rejection: message='{parsed.Message}'");
                    return new VerifyResult(false, null, null, VerifyFailureReason.GumroadRejected, parsed.Message);
                }

                Log.Trace($"VerifyAsync: unexpected status {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
                return new VerifyResult(false, null, null, VerifyFailureReason.ServerError, parsed?.Message);
            }

            return ParseResponse(body);
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

    private VerifyResult ParseResponse(string json)
    {
        try
        {
            var root = JsonSerializer.Deserialize<GumroadResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (root?.Success != true || root.Purchase is null)
            {
                Log.Trace($"ParseResponse: success={root?.Success} message='{root?.Message}' purchaseIsNull={root?.Purchase is null}");
                return new VerifyResult(false, null, null, VerifyFailureReason.GumroadRejected, root?.Message);
            }

            var p = root.Purchase;

            // 返金・チャージバックされた買い切りキーは無効とみなす。
            if (p.Refunded == true || p.Chargebacked == true)
            {
                Log.Trace("ParseResponse: refunded or chargebacked");
                return new VerifyResult(false, null, null, VerifyFailureReason.GumroadRejected,
                    "返金またはチャージバックにより、このライセンスキーは無効になりました。", root.Uses);
            }

            // キー流出対策: 累計 uses（=アクティベート台数）が上限を超えていたら拒否する。
            // Uses が返ってこない場合（API仕様変更等）は判定不能として素通しする（fail-open）。
            if (root.Uses is int uses && uses > _maxActivations)
            {
                Log.Trace($"ParseResponse: usage limit exceeded uses={uses} max={_maxActivations}");
                return new VerifyResult(false, null, null, VerifyFailureReason.GumroadRejected,
                    $"ライセンスキーの使用台数が上限（{_maxActivations}台）を超えています（現在{uses}台）。", root.Uses);
            }

            Log.Trace($"ParseResponse: OK email={p.Email} purchaserId={p.PurchaserId} uses={root.Uses}");
            return new VerifyResult(true, p.Email, p.PurchaserId ?? p.Email, VerifyFailureReason.None, null, root.Uses);
        }
        catch (JsonException ex)
        {
            Log.Trace($"ParseResponse: JSON parse failed: {ex.Message} raw={json}");
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

        [JsonPropertyName("message")]
        public string? Message { get; set; }

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
    }
}
