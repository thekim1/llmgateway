using Ume.LlmGateway.Infrastructure;
using Ume.LlmGateway.MigrationService;

var builder = Host.CreateApplicationBuilder(args);
builder.AddDeploymentSecrets();
builder.AddServiceDefaults();
builder.AddGatewayDatabase();
builder.AddGatewaySecurity();
builder.Services.LoadKeyRingOnFirstUse();
builder.Services.AddOptions<SeedOptions>().BindConfiguration(SeedOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddTransient<DevSeeder>();
builder.Services.AddHostedService<MigrationWorker>();

var host = builder.Build();
host.Run();
