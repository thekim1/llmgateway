namespace Ume.LlmGateway.AdminApi.Auditing;

/// <summary>Audit log <c>EntityType</c> values (also the <c>entityType</c> filter of <c>GET /api/audit</c>).</summary>
public static class AuditEntities
{
    public const string Department = "Department";
    public const string Team = "Team";
    public const string VirtualKey = "VirtualKey";
    public const string DataApiKey = "DataApiKey";
    public const string Provider = "Provider";
    public const string Model = "Model";
    public const string Route = "Route";
    public const string RoutingRule = "RoutingRule";
    public const string Budget = "Budget";
    public const string Alert = "Alert";
    public const string Settings = "Settings";
    public const string Config = "Config";
    public const string Keys = "Keys";
    public const string Usage = "Usage";
}
