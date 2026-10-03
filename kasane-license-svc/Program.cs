using KasaneLicenseSvc;

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
