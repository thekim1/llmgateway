namespace Ume.LlmGateway.AdminApi.Auditing;

/// <summary>Audit log <c>Action</c> values.</summary>
public static class AuditActions
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Delete = "delete";
    public const string Drain = "drain";
    public const string Price = "price";
    public const string Rotate = "rotate";
    public const string Revoke = "revoke";
    public const string Reveal = "reveal";
    public const string Copy = "copy";
    public const string Reassign = "reassign";
    public const string Reorder = "reorder";
    public const string Deactivate = "deactivate";
    public const string Acknowledge = "acknowledge";
    public const string Circuit = "circuit";
    public const string Run = "run";
    public const string Import = "import";
    public const string Invalidate = "invalidate";
    public const string Export = "export";
}
