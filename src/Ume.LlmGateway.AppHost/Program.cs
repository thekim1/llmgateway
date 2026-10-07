using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

#pragma warning disable ASPIRECERTIFICATES001

var builder = DistributedApplication.CreateBuilder(args);
builder.AddDockerComposeEnvironment("compose").WithDashboard(false);

var pepper = Secret("key-pepper", 64);
var devKey = Secret("dev-key", 43);
var oidcSecret = Secret("oidc-client-secret", 48);
var devUserPassword = Secret("dev-user-password", 32);

var postgres = builder.AddPostgres("postgres").WithImageTag("17-alpine")
    .WithDataVolume(builder.ExecutionContext.IsRunMode ? builder.Configuration["DevelopmentVolumes:Postgres"] : null);
var database = postgres.AddDatabase("gatewaydb");
var redis = builder.AddRedis("redis", password: Secret("redis-password", 48));

var keycloak = builder.ExecutionContext.IsRunMode ? builder.AddKeycloak("keycloak")
    .WithRealmImport(Path.Combine("..", "..", "dev", "keycloak"))
    .WithEnvironment("UME_OIDC_CLIENT_SECRET", oidcSecret)
    .WithEnvironment("UME_DEV_USER_PASSWORD", devUserPassword)
    .WithHttpsDeveloperCertificate() : null;

var fakeLlm = builder.ExecutionContext.IsRunMode ? builder.AddProject<Projects.Ume_LlmGateway_FakeLlm>("fake-llm", o => o.ExcludeLaunchProfile = true)
    .WithHttpsEndpoint()
    .WithHttpHealthCheck("/health/ready", endpointName: "https") : null;

var migrations = builder.AddProject<Projects.Ume_LlmGateway_MigrationService>("migrations")
    .WithReference(database)
    .WithEnvironment("Security__KeyPepper", pepper)
    .WithEnvironment("Seed__Enabled", builder.ExecutionContext.IsRunMode ? "true" : "false")
    .WaitFor(database);

if (fakeLlm is not null)
{
    migrations.WithEnvironment("Seed__DevKey", devKey)
        .WithEnvironment("Seed__FakeLlmUrl", ReferenceExpression.Create($"{fakeLlm.GetEndpoint("https")}/v1"))
        .WaitFor(fakeLlm);
}

if (builder.Configuration.GetValue<bool>("Ollama:Enabled"))
{
    if (!builder.ExecutionContext.IsRunMode)
    {
        throw new InvalidOperationException("Ollama:Enabled is development-only. Production requires an independently managed HTTPS on-prem provider endpoint.");
    }
    var ollama = builder.AddOllama("ollama")
        .WithDataVolume(builder.Configuration["DevelopmentVolumes:Ollama"]);
    var model = ollama.AddModel("ollama-model", "qwen2.5:0.5b");
    migrations.WithEnvironment("Seed__OllamaUrl", ReferenceExpression.Create($"{ollama.GetEndpoint("http")}/v1"))
        .WaitFor(model);
}

var gateway = builder.AddProject<Projects.Ume_LlmGateway_Gateway>("gateway", o => o.LaunchProfileName = "https")
    .WithReference(database)
    .WithReference(redis)
    .WithEnvironment("Security__KeyPepper", pepper)
    .WaitFor(database)
    .WaitFor(redis)
    .WaitForCompletion(migrations)
    .WithHttpHealthCheck("/health/ready", endpointName: "https")
    .WithExternalHttpEndpoints();

var adminApi = builder.AddProject<Projects.Ume_LlmGateway_AdminApi>("adminapi", o => o.LaunchProfileName = "https")
    .WithReference(database)
    .WithReference(redis)
    .WithEnvironment("Security__KeyPepper", pepper)
    .WithEnvironment("Oidc__ClientId", "ume-admin")
    .WithEnvironment("Gateway__BaseUrl", gateway.GetEndpoint("https"))
    .WithEnvironment("Gateway__OperationsUrl", gateway.GetEndpoint("https"))
    .WaitFor(database)
    .WaitFor(redis)
    .WaitForCompletion(migrations)
    .WithHttpHealthCheck("/health/ready", endpointName: "https");

if (keycloak is not null)
{
    adminApi.WithEnvironment("Oidc__Authority", ReferenceExpression.Create($"{keycloak.GetEndpoint("http")}/realms/ume"))
        .WithEnvironment("Oidc__ClientSecret", oidcSecret)
        .WaitFor(keycloak);
}
else
{
    adminApi.WithExternalHttpEndpoints();
}

var adminUi = builder.AddViteApp("admin-ui", Path.Combine("..", "admin-ui"))
    .WithReference(adminApi)
    .WithEndpoint("http", endpoint => { endpoint.UriScheme = "https"; endpoint.Port = 5173; })
    .WithHttpsDeveloperCertificate()
    .WaitFor(adminApi)
    .WithExternalHttpEndpoints();

adminApi.PublishWithContainerFiles(adminUi, "wwwroot");

builder.Build().Run();

IResourceBuilder<ParameterResource> Secret(string name, int length) =>
    builder.AddParameter(name, new GenerateParameterDefault
    {
        MinLength = length, Lower = true, Upper = true, Numeric = true, Special = false,
    }, secret: true, persist: true);
