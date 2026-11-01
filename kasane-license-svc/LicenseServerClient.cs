using System.Net;
using System.Net.Http.Json;
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
    NetworkError,     // DNS 解決・接続確立に失敗（インターネット未接続 or ライセンスサーバー到達不能）
    Timeout,          // 接続はできたが応答がタイムアウト
    GumroadRejected,  // ライセンスサーバーが応答した。ライセンスキー自体が無効/返金済み等（Gumroad の判定）
    ServerError,      // ライセンスサーバーが 2xx/403 以外を返した（Gumroad 側の一時的な問題等）
    ParseError,       // 応答 JSON の解析に失敗
}

/// <param name="Token">署名済みトークン(成功時のみ)。署名はライセンスサーバーだけが行う。</param>
/// <param name="ExpiresAt">トークンの有効期限(Unix 秒)。成功時のみ。</param>
public readonly record struct VerifyResult(
    bool IsValid,
    string? Email,
    string? UserId,
    VerifyFailureReason FailureReason,
    string? GumroadMessage,
    string? Token = null,
    long ExpiresAt = 0,
    bool IsTrial = false);

/// <summary>
/// ライセンスサーバー(worker/license-signer)のクライアント。
/// POST {Url}/v1/license {"licenseKey","machineGuid","mode":"activate"|"refresh"}
/// サーバーが Gumroad でキーを確認し、有効なら署名済みトークンを返す。署名鍵はこのサービスには無い。
/// </summary>
public sealed class LicenseServerClient
{
    // 本番のライセンスサーバーの URL。appsettings.json の LicenseServer:Url で上書きできる(試験用)。
    private const string DefaultUrl = "https://REPLACE-WITH-LICENSE-SERVER-URL";

    private readonly HttpClient _http;
    private readonly ILogger<LicenseServerClient> _logger;
    private readonly string _endpoint;

    public LicenseServerClient(IConfiguration config, ILogger<LicenseServerClient> logger)
    {
        _logger = logger;
        string baseUrl = (config["LicenseServer:Url"] ?? DefaultUrl).TrimEnd('/');
        _endpoint = baseUrl + "/v1/license";
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <param name="activate">true=アクティベート(利用回数を増やす。トライアルも試す)、false=定期再検証</param>
    public async Task<VerifyResult> VerifyAsync(
        string licenseKey, string machineGuid, bool activate, CancellationToken ct = default)
    {
        if (_endpoint.Contains("REPLACE-WITH", StringComparison.Ordinal))
        {
            _logger.LogError("LicenseServer:Url is not configured");
            return new VerifyResult(false, null, null, VerifyFailureReason.ServerError, null);
        }

        var request = new { licenseKey, machineGuid, mode = activate ? "activate" : "refresh" };
        DebugTrace.Log($"VerifyAsync request: key={MaskKey(licenseKey)} mode={request.mode}");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsJsonAsync(_endpoint, request, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            DebugTrace.Log($"VerifyAsync timeout: {ex}");
            _logger.LogWarning(ex, "License server request timed out");
            return new VerifyResult(false, null, null, VerifyFailureReason.Timeout, null);
        }
        catch (HttpRequestException ex)
        {
            DebugTrace.Log($"VerifyAsync network error: {ex}");
            _logger.LogWarning(ex, "License server network error");
            return new VerifyResult(false, null, null, VerifyFailureReason.NetworkError, null);
        }

        using (resp)
        {
            string body = await resp.Content.ReadAsStringAsync(ct);
            // トークンはログに残さない。
            DebugTrace.Log($"VerifyAsync response: status={(int)resp.StatusCode} bodyLength={body.Length}");

            ServerResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<ServerResponse>(body);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse license server response (status={Status})", (int)resp.StatusCode);
                return new VerifyResult(false, null, null,
                    resp.IsSuccessStatusCode ? VerifyFailureReason.ParseError : VerifyFailureReason.ServerError, null);
            }

            if (resp.StatusCode == HttpStatusCode.OK && parsed?.Success == true && !string.IsNullOrEmpty(parsed.Token))
            {
                return new VerifyResult(true, parsed.Email, parsed.UserId, VerifyFailureReason.None, null,
                    parsed.Token, parsed.ExpiresAt, parsed.IsTrial);
            }

            // Gumroad が、このキーを明確に拒否した(返金・失効・台数超過など)。
            if (resp.StatusCode == HttpStatusCode.Forbidden && parsed?.Reason == "rejected")
            {
                _logger.LogWarning("License rejected: {Message}", parsed.Message);
                return new VerifyResult(false, null, null, VerifyFailureReason.GumroadRejected, parsed.Message);
            }

            // それ以外は一時的な失敗として扱う(ライセンスを剥奪しない)。
            _logger.LogWarning("License server returned {Status} reason={Reason}", (int)resp.StatusCode, parsed?.Reason);
            return new VerifyResult(false, null, null, VerifyFailureReason.ServerError, parsed?.Message);
        }
    }

    private static string MaskKey(string key) =>
        key.Length > 4 ? new string('*', key.Length - 4) + key[^4..] : "****";

    private sealed class ServerResponse
    {
        [JsonPropertyName("success")] public bool? Success { get; set; }
        [JsonPropertyName("reason")] public string? Reason { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
        [JsonPropertyName("userId")] public string? UserId { get; set; }
        [JsonPropertyName("isTrial")] public bool IsTrial { get; set; }
        [JsonPropertyName("expiresAt")] public long ExpiresAt { get; set; }
    }
}
