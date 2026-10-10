namespace Ume.LlmGateway.ServiceDefaults;

/// <summary>
/// Health endpoint paths. Internal and dependency-free on purpose: the AppHost compiles this file in as a link
/// (its health checks probe these paths) without referencing the ServiceDefaults assembly.
/// </summary>
internal static class HealthEndpoints
{
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";
}
