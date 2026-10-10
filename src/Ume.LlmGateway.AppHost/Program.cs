using Microsoft.Extensions.Configuration;
using Ume.LlmGateway.ServiceDefaults;

#pragma warning disable ASPIRECERTIFICATES001

// Connection-string names the services look up (Ume.LlmGateway.Infrastructure.InfrastructureExtensions.DatabaseResourceName and RedisResourceName).
const string DatabaseResourceName = "gatewaydb";
const string RedisResourceName = "redis";

var builder = DistributedApplication.CreateBuilder(args);
builder.AddDockerComposeEnvironment("compose").WithDashboard(false);

var pepper = Secret("key-pepper", 64);
var devKey = Secret("dev-key", 43);
var oidcSecret = Secret("oidc-client-secret", 48);
var devUserPassword = Secret("dev-user-password", 32);
var dataClientSecret = Secret("data-client-secret", 48);

var postgres = builder.AddPostgres("postgres").WithImageTag("17-alpine")
    .WithDataVolume(builder.ExecutionContext.IsRunMode ? builder.Configuration["DevelopmentVolumes:Postgres"] : null);
var database = postgres.AddDatabase(DatabaseResourceName);
var redis = builder.AddRedis(RedisResourceName, password: Secret("redis-password", 48));

// Oidc:Authority (AppHost user secrets or appsettings) points local runs at an existing identity provider
// (company Keycloak, AD FS, Entra ID) instead of the bundled test Keycloak, which is then not started.
var externalOidc = !string.IsNullOrWhiteSpace(builder.Configuration["Oidc:Authority"]);
var keycloak = builder.ExecutionContext.IsRunMode && !externalOidc ? builder.AddKeycloak("keycloak")
    .WithRealmImport(Path.Combine("..", "..", "dev", "keycloak"))
    .WithEnvironment("UME_OIDC_CLIENT_SECRET", oidcSecret)
    .WithEnvironment("UME_DEV_USER_PASSWORD", devUserPassword)
    .WithEnvironment("UME_DATA_CLIENT_SECRET", dataClientSecret)
    .WithHttpsDeveloperCertificate() : null;

// FakeLlm:Enabled=false (AppHost user secrets or appsettings) keeps the fake provider from starting in local runs.
var fakeLlm = builder.ExecutionContext.IsRunMode && builder.Configuration.GetValue("FakeLlm:Enabled", true) ? builder.AddProject<Projects.Ume_LlmGateway_FakeLlm>("fake-llm", o => o.ExcludeLaunchProfile = true)
    .WithHttpsEndpoint()
    .WithHttpHealthCheck(HealthEndpoints.ReadyPath, endpointName: "https") : null;

var migrations = builder.AddProject<Projects.Ume_LlmGateway_MigrationService>("migrations")
    .WithReference(database)
    .WithEnvironment("Security__KeyPepper", pepper)
    .WithEnvironment("Seed__Enabled", builder.ExecutionContext.IsRunMode && builder.Configuration.GetValue("Seed:Enabled", true) ? "true" : "false")
    .WaitFor(database);

if (builder.ExecutionContext.IsRunMode)
{
    migrations.WithEnvironment("Seed__DevKey", devKey);
}

if (fakeLlm is not null)
{
    migrations.WithEnvironment("Seed__FakeLlmUrl", ReferenceExpression.Create($"{fakeLlm.GetEndpoint("https")}/v1"))
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
    .WithHttpHealthCheck(HealthEndpoints.ReadyPath, endpointName: "https")
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
    .WithHttpHealthCheck(HealthEndpoints.ReadyPath, endpointName: "https");

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

if (externalOidc)
{
    // Every Oidc:* setting (Authority, ClientId, ClientSecret, RoleClaim, RoleGroups:..., ...) is passed through.
    foreach (var (key, value) in builder.Configuration.GetSection("Oidc").AsEnumerable(makePathsRelative: true))
    {
        if (value is not null)
        {
            adminApi.WithEnvironment("Oidc__" + key.Replace(":", "__", StringComparison.Ordinal), value);
        }
    }
}

// Read-only Data API for BI, management and security integrations (docs/data-access.md). Local runs include it
// (DataApi:Enabled=false leaves it out); its tokens come from the bundled Keycloak's ume-data-dev client (secret:
// data-client-secret). `aspire publish` leaves it out unless DataApi:Enabled=true, because compose.hardening.yaml does
// not cover it; deploy it with compose.prod.yaml or compose.portainer.yaml (profile "data") instead.
if (builder.Configuration.GetValue("DataApi:Enabled", builder.ExecutionContext.IsRunMode))
{
    var dataApi = builder.AddProject<Projects.Ume_LlmGateway_DataApi>("dataapi", o => o.LaunchProfileName = "https")
        .WithReference(database)
        .WaitFor(database)
        .WaitForCompletion(migrations)
        .WithHttpHealthCheck(HealthEndpoints.ReadyPath, endpointName: "https");

    if (keycloak is not null)
    {
        dataApi.WithEnvironment("DataApi__Authority", ReferenceExpression.Create($"{keycloak.GetEndpoint("http")}/realms/ume"))
            .WaitFor(keycloak);
    }

    // Every DataApi:* setting (Authority, Audience, PermissionClaims:0, MinimumGroupSize, ...) is passed through.
    foreach (var (key, value) in builder.Configuration.GetSection("DataApi").AsEnumerable(makePathsRelative: true))
    {
        if (value is not null && key != "Enabled")
        {
            dataApi.WithEnvironment("DataApi__" + key.Replace(":", "__", StringComparison.Ordinal), value);
        }
    }
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
