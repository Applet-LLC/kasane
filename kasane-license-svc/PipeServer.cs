using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KasaneLicenseSvc;

/// <summary>
/// 名前付きパイプ \\.\pipe\kbdlayer-license で GUI からの要求を受け付ける。
///
/// プロトコル（1 行 JSON / UTF-8 / LF 区切り）:
///   要求: {"action":"activate","license_key":"XXXX-XXXX-XXXX-XXXX"}
///   要求: {"action":"status"}
///   応答: {"success":true,"expiry":"2026-06-04","user":"xxx@example.com"}
///   応答: {"success":false,"error":"validation_failed"}
///   応答: {"active":true,"expiry":"2026-06-04","days_remaining":6}
/// </summary>
public sealed class PipeServer : BackgroundService
{
    public const string PipeName = "kbdlayer-license";

    private readonly ILogger<PipeServer> _logger;
    private readonly LicenseService _license;

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public PipeServer(ILogger<PipeServer> logger, LicenseService license)
    {
        _logger = logger;
        _license = license;
    }

    private long _connectionSeq;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DebugTrace.Log("PipeServer.ExecuteAsync: loop starting");
        while (!stoppingToken.IsCancellationRequested)
        {
            long seq = Interlocked.Increment(ref _connectionSeq);
            try
            {
                DebugTrace.Log($"PipeServer.ExecuteAsync: [{seq}] entering AcceptOneConnectionAsync");
                await AcceptOneConnectionAsync(seq, stoppingToken);
                DebugTrace.Log($"PipeServer.ExecuteAsync: [{seq}] AcceptOneConnectionAsync returned normally, looping");
            }
            catch (OperationCanceledException)
            {
                DebugTrace.Log($"PipeServer.ExecuteAsync: [{seq}] OperationCanceledException, exiting loop");
                break;
            }
            catch (Exception ex)
            {
                DebugTrace.Log($"PipeServer.ExecuteAsync: [{seq}] EXCEPTION: {ex}");
                _logger.LogError(ex, "PipeServer error (seq={Seq})", seq);
                await Task.Delay(1000, stoppingToken);
            }
        }
        DebugTrace.Log("PipeServer.ExecuteAsync: loop exited");
    }

    private async Task AcceptOneConnectionAsync(long seq, CancellationToken ct)
    {
        // GUI は非昇格（管理者権限なし）で起動されるため、Administrators 限定 ACL だと
        // UAC のフィルタード トークンにより Access Denied で接続できない。
        // レジストリ書き込み等の特権操作はすべてサービス側（LocalSystem）が行うため、
        // パイプ自体はログオン済みユーザーに広く許可してよい。
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        DebugTrace.Log($"AcceptOneConnectionAsync[{seq}]: creating pipe instance");
        using var pipe = NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096,
            pipeSecurity: security);
        DebugTrace.Log($"AcceptOneConnectionAsync[{seq}]: pipe instance created, IsConnected={pipe.IsConnected}, waiting for connection");

        await pipe.WaitForConnectionAsync(ct);
        DebugTrace.Log($"AcceptOneConnectionAsync[{seq}]: client connected, IsConnected={pipe.IsConnected}");
        _logger.LogDebug("Pipe client connected");

        try
        {
            await HandleRequestAsync(seq, pipe, ct);
            DebugTrace.Log($"AcceptOneConnectionAsync[{seq}]: HandleRequestAsync completed normally");

            // 重要: 書き込み直後に Disconnect() すると、クライアントがまだ読み切っていない
            // データが破棄され、クライアント側で ReadLineAsync が null（EOF）を返して
            // "Pipe is broken" になる競合状態が実機で確認された（アクティベート成功直後の
            // status 確認などタイミングが詰まっている場面で高頻度に再現）。
            // WaitForPipeDrain() でクライアントが全バイトを読み終えるまで待ってから切断する。
            try
            {
                DebugTrace.Log($"AcceptOneConnectionAsync[{seq}]: waiting for pipe drain (client to finish reading)");
                pipe.WaitForPipeDrain();
                DebugTrace.Log($"AcceptOneConnectionAsync[{seq}]: pipe drain completed");
            }
            catch (Exception ex)
            {
                // クライアントが既に切断している等で drain が失敗しても致命的ではない。
                DebugTrace.Log($"AcceptOneConnectionAsync[{seq}]: WaitForPipeDrain() THREW (non-fatal): {ex}");
            }
        }
        catch (Exception ex)
        {
            DebugTrace.Log($"AcceptOneConnectionAsync[{seq}]: HandleRequestAsync threw: {ex}");
            throw;
        }
        finally
        {
            try
            {
                DebugTrace.Log($"AcceptOneConnectionAsync[{seq}]: calling Disconnect(), IsConnected={pipe.IsConnected}");
                pipe.Disconnect();
                DebugTrace.Log($"AcceptOneConnectionAsync[{seq}]: Disconnect() returned OK");
            }
            catch (Exception ex)
            {
                DebugTrace.Log($"AcceptOneConnectionAsync[{seq}]: Disconnect() THREW: {ex}");
                // 元の例外（あれば）を優先して伝播させたいのでここでは再throwしない。
            }
        }
    }

    private async Task HandleRequestAsync(long seq, PipeStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        DebugTrace.Log($"HandleRequestAsync[{seq}]: reading request line");
        string? line = await reader.ReadLineAsync(ct);
        DebugTrace.Log($"HandleRequestAsync[{seq}]: read line = '{line}'");
        if (string.IsNullOrWhiteSpace(line))
        {
            DebugTrace.Log($"HandleRequestAsync[{seq}]: empty/null line, returning without response");
            return;
        }

        _logger.LogDebug("Pipe request: {Request}", line[..Math.Min(200, line.Length)]);

        PipeRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<PipeRequest>(line, s_jsonOpts);
        }
        catch (JsonException)
        {
            await WriteErrorAsync(writer, "invalid_json", ct);
            return;
        }

        if (req is null)
        {
            await WriteErrorAsync(writer, "invalid_json", ct);
            return;
        }

        switch (req.Action?.ToLowerInvariant())
        {
            case "activate":
                await HandleActivateAsync(req, writer, ct);
                break;
            case "status":
                await HandleStatusAsync(writer);
                break;
            case "refresh":
                await HandleRefreshAsync(writer, ct);
                break;
            default:
                await WriteErrorAsync(writer, "unknown_action", ct);
                break;
        }
    }

    private async Task HandleActivateAsync(PipeRequest req, StreamWriter writer, CancellationToken ct)
    {
        DebugTrace.Log($"HandleActivateAsync: request received, keyLen={req.LicenseKey?.Length ?? 0}");

        if (string.IsNullOrWhiteSpace(req.LicenseKey))
        {
            await WriteResponseAsync(writer,
                new { success = false, error = "missing_license_key" });
            return;
        }

        var result = await _license.ActivateAsync(req.LicenseKey, ct);
        DebugTrace.Log($"HandleActivateAsync: result success={result.Success} error={result.Error} gumroadMessage={result.GumroadMessage}");
        if (result.Success)
        {
            await WriteResponseAsync(writer, new
            {
                success  = true,
                expiry   = result.Expiry.ToString("yyyy-MM-dd"),
                user     = result.UserEmail ?? string.Empty,
                is_trial = result.IsTrial
            });
        }
        else
        {
            await WriteResponseAsync(writer, new
            {
                success         = false,
                error           = result.Error ?? "unknown_error",
                gumroad_message = result.GumroadMessage
            });
        }
    }

    private async Task HandleStatusAsync(StreamWriter writer)
    {
        var status = _license.GetStatus();
        await WriteResponseAsync(writer, new
        {
            active          = status.Active,
            expiry          = status.Active ? status.Expiry.ToString("yyyy-MM-dd") : null,
            days_remaining  = status.Active ? (int?)status.DaysRemaining : null,
            user            = status.Active ? status.Email : null,
            masked_license_key = status.MaskedLicenseKey,
            // Active のときは意味がないので出さない。未アクティベート時のみ、直近の無効化理由を表示する。
            last_rejection_reason = status.Active ? null : status.LastRejectionReason,
            has_saved_key = status.HasSavedKey,
            is_trial = status.IsTrial
        });
    }

    /// <summary>
    /// 保存済みキーで即座に（リトライなしの一回のみ）Gumroad 再検証を行い、
    /// 結果を反映した最新状態を返す。GUI の「ライセンスを再確認」ボタンから呼ばれる。
    /// ネットワーク未接続などで一時的に自動リフレッシュが失敗した状態からの
    /// 手動復帰を想定している（Worker のバックオフリトライは待たない）。
    /// </summary>
    private async Task HandleRefreshAsync(StreamWriter writer, CancellationToken ct)
    {
        DebugTrace.Log("HandleRefreshAsync: manual refresh requested");
        var outcome = await _license.RefreshAsync(ct);
        DebugTrace.Log($"HandleRefreshAsync: outcome={outcome}");

        var status = _license.GetStatus();
        await WriteResponseAsync(writer, new
        {
            active          = status.Active,
            expiry          = status.Active ? status.Expiry.ToString("yyyy-MM-dd") : null,
            days_remaining  = status.Active ? (int?)status.DaysRemaining : null,
            user            = status.Active ? status.Email : null,
            masked_license_key = status.MaskedLicenseKey,
            refresh_outcome = outcome.ToString(),
            last_rejection_reason = status.Active ? null : status.LastRejectionReason,
            has_saved_key = status.HasSavedKey,
            is_trial = status.IsTrial
        });
    }

    private static Task WriteErrorAsync(StreamWriter writer, string error, CancellationToken ct) =>
        WriteResponseAsync(writer, new { success = false, error });

    private static async Task WriteResponseAsync(StreamWriter writer, object response)
    {
        string json = JsonSerializer.Serialize(response, s_jsonOpts);
        DebugTrace.Log($"WriteResponseAsync: writing '{json}'");
        try
        {
            await writer.WriteLineAsync(json);
            DebugTrace.Log("WriteResponseAsync: write completed OK");
        }
        catch (Exception ex)
        {
            DebugTrace.Log($"WriteResponseAsync: WRITE THREW: {ex}");
            throw;
        }
    }

    // ---------------------------------------------------------------
    // 要求 JSON モデル
    // ---------------------------------------------------------------

    private sealed class PipeRequest
    {
        [JsonPropertyName("action")]
        public string? Action { get; set; }

        [JsonPropertyName("license_key")]
        public string? LicenseKey { get; set; }
    }
}
