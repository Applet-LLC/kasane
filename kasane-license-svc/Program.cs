using KasaneLicenseSvc;

// 未処理例外でプロセスが落ちたとき、WER の Report.wer には .NET の例外内容が残らないため、
// debug.log に全文を書き残す。
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    DebugTrace.Log($"UnhandledException (terminating={e.IsTerminating}): {e.ExceptionObject}");

// ライセンスキーと署名鍵のフォルダを、SYSTEM と Administrators だけにする。すでにあるファイルにも届く。
try { ProtectedFolder.Ensure(); }
catch (Exception ex) { DebugTrace.Log($"ProtectedFolder.Ensure failed: {ex}"); }

// 1.00 までは、このサービスが署名鍵を持ち、DPAPI で保護して signingkey.dat に置いていた。1.01 から署名は
// ライセンスサーバーだけが行うので、上書きインストールで残った古い鍵のファイルは消す。
try { File.Delete(Path.Combine(ProtectedFolder.Dir, "signingkey.dat")); }
catch (Exception ex) { DebugTrace.Log($"signingkey.dat cleanup failed: {ex.Message}"); }

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "kasaneLicenseSvc";
});

builder.Services.AddSingleton<RegistryAccess>();
builder.Services.AddSingleton<LicenseStateStore>();
builder.Services.AddSingleton<LicenseServerClient>();
builder.Services.AddSingleton<LicenseService>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<PipeServer>();

// AddWindowsService が標準の EventLog プロバイダーを自動で登録するので、それを外して、
// 書き込み失敗を握りつぶす版だけを使う(理由は SafeEventLogLoggerProvider 参照)。
var stockEventLog = builder.Services
    .Where(d => d.ServiceType == typeof(ILoggerProvider)
        && d.ImplementationType == typeof(Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider))
    .ToList();
foreach (var d in stockEventLog)
{
    builder.Services.Remove(d);
}
DebugTrace.Log($"Removed stock EventLog logger providers: {stockEventLog.Count}");
builder.Logging.AddProvider(new SafeEventLogLoggerProvider(
    new Microsoft.Extensions.Logging.EventLog.EventLogSettings { SourceName = "kasaneLicenseSvc" }));

var host = builder.Build();

// シャットダウン時のクラッシュ調査用: 停止要求を受けた後に発生した例外を、握りつぶされたものも含めて
// 全部 debug.log に残す(UnhandledException だけでは、原因の例外が見えなかったため)。
// 通常稼働中は何も出さない。
var stopping = false;
host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
{
    stopping = true;
    DebugTrace.Log("ApplicationStopping");
});
var inFirstChance = new ThreadLocal<bool>();
AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
{
    if (!stopping || inFirstChance.Value) return;
    inFirstChance.Value = true;
    try { DebugTrace.Log($"FirstChanceException: {e.Exception}"); }
    finally { inFirstChance.Value = false; }
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => DebugTrace.Log("ProcessExit");

host.Run();
DebugTrace.Log("host.Run returned");
