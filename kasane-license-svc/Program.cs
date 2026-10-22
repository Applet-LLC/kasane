using KasaneLicenseSvc;

// 未処理例外でプロセスが落ちたとき、WER の Report.wer には .NET の例外内容が残らないため、
// debug.log に全文を書き残す。
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    DebugTrace.Log($"UnhandledException (terminating={e.IsTerminating}): {e.ExceptionObject}");

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "kasaneLicenseSvc";
});

builder.Services.AddSingleton<RegistryAccess>();
builder.Services.AddSingleton<LicenseStateStore>();
builder.Services.AddSingleton<TokenSigner>();
builder.Services.AddSingleton<GumroadClient>();
builder.Services.AddSingleton<LicenseService>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<PipeServer>();

builder.Logging.AddEventLog(settings =>
{
    settings.SourceName = "kasaneLicenseSvc";
});

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
