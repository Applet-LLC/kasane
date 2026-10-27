namespace KasaneLicenseSvc;

/// <summary>
/// 24 時間ごとにサブスクリプション状態を Gumroad で再検証する。
/// サービス起動時にも一度実行して保存済みライセンスキーを回復する。
/// </summary>
public sealed class Worker : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    // PC 起動直後はネットワークスタック（特に DNS）がまだ立ち上がっていないことがあり、
    // サービスの起動タイミングでは api.gumroad.com の名前解決が一時的に失敗しうる
    // （実機で確認済み: SocketException 11001 "そのようなホストは不明です"）。
    // TransientFailure のときだけ、このバックオフ間隔でリトライする。
    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(60),
    };

    private readonly ILogger<Worker> _logger;
    private readonly LicenseService _license;

    public Worker(ILogger<Worker> logger, LicenseService license)
    {
        _logger = logger;
        _license = license;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 起動時: ドライバに、書き込み済みの LicenseToken を再検証させる。ドライバは起動直後の検証に
        // 失敗することがあり、そのときは自分でも再試行するが、ここでも成功を後押しする。
        // SOFT: 失敗しても状態を変えない（サービスもブート直後に起動するので、ここでの失敗を確定として
        // ドライバ自身の再試行を潰さない）。Gumroad への問い合わせを待たずに、すぐ行う。
        DriverRevalidator.Revalidate(DriverRevalidator.ModeSoft);

        // 起動時: 保存済みキーで再検証（一時的なネットワーク未確立に備えてリトライ付き）
        try
        {
            _logger.LogInformation("Worker startup: refreshing license");
            await RefreshWithRetryAsync("startup", stoppingToken);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup license refresh failed");
            LogStatusAfterRefresh("startup (after exception)");
        }

        // 24 時間ごとの定期チェック
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException) { break; }

            try
            {
                _logger.LogInformation("Periodic license refresh");
                await RefreshWithRetryAsync("periodic", stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Periodic license refresh failed");
                LogStatusAfterRefresh("periodic (after exception)");
            }
        }
    }

    // RefreshAsync を実行し、一時的失敗（ネットワーク未到達等）のときだけ
    // バックオフ間隔でリトライする。保存済みキーなし／Gumroad拒否は即座に確定させる。
    private async Task RefreshWithRetryAsync(string phase, CancellationToken ct)
    {
        var outcome = await _license.RefreshAsync(ct);
        LogStatusAfterRefresh(phase, outcome, attempt: 0);

        for (int i = 0; i < RetryDelays.Length && outcome == RefreshOutcome.TransientFailure; i++)
        {
            var delay = RetryDelays[i];
            _logger.LogWarning(
                "Worker [{Phase}]: refresh hit a transient failure (attempt {Attempt}); retrying in {Delay}",
                phase, i + 1, delay);
            await Task.Delay(delay, ct);

            outcome = await _license.RefreshAsync(ct);
            LogStatusAfterRefresh(phase, outcome, attempt: i + 1);
        }

        if (outcome == RefreshOutcome.TransientFailure)
        {
            _logger.LogWarning(
                "Worker [{Phase}]: gave up retrying after {Count} attempts; will try again on the next periodic check",
                phase, RetryDelays.Length);
        }
    }

    // RefreshAsync 完了直後の GetStatus() を明示的にログへ出す。
    // GUI が実際に受け取る値と同じものをここで確認できるようにする。
    private void LogStatusAfterRefresh(string phase, RefreshOutcome? outcome = null, int attempt = 0)
    {
        var status = _license.GetStatus();
        _logger.LogInformation(
            "Worker [{Phase}] attempt={Attempt} outcome={Outcome}: GetStatus() now returns Active={Active} Email={Email} MaskedKey={MaskedKey}",
            phase, attempt, outcome, status.Active, status.Email, status.MaskedLicenseKey);
    }
}
