using KbdLayerLicenseSvc;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "kbdlayerLicenseSvc";
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
    settings.SourceName = "kbdlayerLicenseSvc";
});

var host = builder.Build();
host.Run();
