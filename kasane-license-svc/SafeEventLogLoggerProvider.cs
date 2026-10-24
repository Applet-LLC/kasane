using Microsoft.Extensions.Logging.Configuration;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Options;

namespace KasaneLicenseSvc;

/// <summary>
/// EventLogLoggerProvider を包み、書き込みの失敗を握りつぶす。
///
/// システムのシャットダウン中は EventLog サービスが先に止まり、書き込みが
/// Win32Exception(1717 "そのインターフェイスは認識されません") で失敗する。標準の
/// EventLogLogger はそれを例外として投げ返し、ホスト側の StopApplication()(ServiceBase.
/// DeferredShutdown スレッド上)がさらにその失敗をログに書こうとして再び投げるため、
/// 未処理例外になってプロセスが APPCRASH で落ちていた。
/// ログは補助情報なので、書けなくてもサービスの動作に影響させない。
/// </summary>
[ProviderAlias("EventLog")]
internal sealed class SafeEventLogLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly EventLogLoggerProvider _inner;

    public SafeEventLogLoggerProvider(EventLogSettings settings) =>
        _inner = new EventLogLoggerProvider(Options.Create(settings));

    public ILogger CreateLogger(string categoryName) => new SafeLogger(_inner.CreateLogger(categoryName));

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _inner.SetScopeProvider(scopeProvider);

    public void Dispose()
    {
        try { _inner.Dispose(); }
        catch { }
    }

    private sealed class SafeLogger : ILogger
    {
        private readonly ILogger _inner;

        public SafeLogger(ILogger inner) => _inner = inner;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            try { _inner.Log(logLevel, eventId, state, exception, formatter); }
            catch { }
        }
    }
}
