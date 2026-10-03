using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KasaneLicense;

/// <summary>
/// KasaneLicenseSvc との名前付きパイプ IPC クライアント。
/// </summary>
public static class PipeClient
{
    private const string PipeName = "kbdlayer-license";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions s_opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>ライセンスキーをサービスへ送りアクティベートする。</summary>
    public static async Task<ActivateResponse> ActivateAsync(string licenseKey)
    {
        var req = new { action = "activate", license_key = licenseKey };
        var raw = await SendRequestAsync(JsonSerializer.Serialize(req, s_opts));
        return JsonSerializer.Deserialize<ActivateResponse>(raw, s_opts)
               ?? new ActivateResponse { Success = false, Error = "parse_error" };
    }

    /// <summary>サービスから現在のライセンス状態を取得する（キャッシュのみ、Gumroad へは問い合わせない）。</summary>
    public static async Task<StatusResponse> GetStatusAsync()
    {
        var req = new { action = "status" };
        var raw = await SendRequestAsync(JsonSerializer.Serialize(req, s_opts));
        return JsonSerializer.Deserialize<StatusResponse>(raw, s_opts)
               ?? new StatusResponse { Active = false };
    }

    /// <summary>
    /// 保存済みキーで即座に Gumroad 再検証を行い、最新状態を取得する。
    /// ネットワーク未接続時などに自動リフレッシュが失敗した状態から、
    /// ユーザーが「ライセンスを再確認」ボタンで手動復帰するために使う。
    /// </summary>
    public static async Task<StatusResponse> RefreshLicenseAsync()
    {
        var req = new { action = "refresh" };
        var raw = await SendRequestAsync(JsonSerializer.Serialize(req, s_opts));
        return JsonSerializer.Deserialize<StatusResponse>(raw, s_opts)
               ?? new StatusResponse { Active = false };
    }

    private static async Task<string> SendRequestAsync(string requestJson)
    {
        ClientDebugLog.Log($"SendRequestAsync: request='{requestJson}'");

        using var pipe = new NamedPipeClientStream(
            ".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        using var cts = new CancellationTokenSource(ConnectTimeout);
        try
        {
            ClientDebugLog.Log("SendRequestAsync: connecting...");
            await pipe.ConnectAsync(cts.Token);
            ClientDebugLog.Log($"SendRequestAsync: connected, IsConnected={pipe.IsConnected}");
        }
        catch (Exception ex)
        {
            ClientDebugLog.Log($"SendRequestAsync: CONNECT FAILED {ex.GetType().FullName}: {ex.Message}");
            throw;
        }

        using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);

        try
        {
            ClientDebugLog.Log("SendRequestAsync: writing request line");
            await writer.WriteLineAsync(requestJson);
            ClientDebugLog.Log("SendRequestAsync: write completed OK, reading response line");
        }
        catch (Exception ex)
        {
            ClientDebugLog.Log($"SendRequestAsync: WRITE FAILED {ex.GetType().FullName}: {ex.Message}");
            throw;
        }

        try
        {
            string response = await reader.ReadLineAsync() ?? "{}";
            ClientDebugLog.Log($"SendRequestAsync: response='{response}'");
            return response;
        }
        catch (Exception ex)
        {
            ClientDebugLog.Log($"SendRequestAsync: READ FAILED {ex.GetType().FullName}: {ex.Message}");
            throw;
        }
    }

    // ---------------------------------------------------------------
    // 応答モデル
    // ---------------------------------------------------------------

    public sealed class ActivateResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        // Gumroad が返した具体的な理由（あれば）。エラーコードだけでは分からない詳細をユーザーに示す。
        [JsonPropertyName("gumroad_message")]
        public string? GumroadMessage { get; set; }

        [JsonPropertyName("expiry")]
        public string? Expiry { get; set; }

        [JsonPropertyName("user")]
        public string? User { get; set; }

        // トライアル専用商品での照合によって成立したアクティベートかどうか。
        [JsonPropertyName("is_trial")]
        public bool IsTrial { get; set; }
    }

    public sealed class StatusResponse
    {
        [JsonPropertyName("active")]
        public bool Active { get; set; }

        [JsonPropertyName("expiry")]
        public string? Expiry { get; set; }

        [JsonPropertyName("days_remaining")]
        public int? DaysRemaining { get; set; }

        [JsonPropertyName("user")]
        public string? User { get; set; }

        [JsonPropertyName("masked_license_key")]
        public string? MaskedLicenseKey { get; set; }

        // "refresh" 要求のときだけ設定される: NoSavedKey / Active / TransientFailure / Rejected
        [JsonPropertyName("refresh_outcome")]
        public string? RefreshOutcome { get; set; }

        // 未アクティベート時、直近で Gumroad が明示的に拒否した理由（支払い滞納・返金・台数超過等）。
        [JsonPropertyName("last_rejection_reason")]
        public string? LastRejectionReason { get; set; }

        // 過去にこの PC でアクティベートされた形跡があるか（レジストリの SavedLicenseKey 基準）。
        // Active=false でもこれが true なら「未アクティベート」ではなく「再確認待ち」の状態。
        [JsonPropertyName("has_saved_key")]
        public bool HasSavedKey { get; set; }

        // 直近のアクティベートがトライアル専用商品によるものか。Active=false でも
        // true のままなら「未アクティベート」ではなく「トライアル期限切れ」の状態。
        [JsonPropertyName("is_trial")]
        public bool IsTrial { get; set; }
    }
}
