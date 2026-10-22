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
host.Run();
