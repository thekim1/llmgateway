using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Infrastructure.Persistence;

public sealed class GatewayDbContext(DbContextOptions<GatewayDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<VirtualKey> VirtualKeys => Set<VirtualKey>();
    public DbSet<DataApiKey> DataApiKeys => Set<DataApiKey>();
    public DbSet<ProviderAccount> ProviderAccounts => Set<ProviderAccount>();
    public DbSet<ModelDeployment> ModelDeployments => Set<ModelDeployment>();
    public DbSet<ModelPrice> ModelPrices => Set<ModelPrice>();
    public DbSet<RouteAlias> RouteAliases => Set<RouteAlias>();
    public DbSet<RouteTarget> RouteTargets => Set<RouteTarget>();
    public DbSet<RoutingRule> RoutingRules => Set<RoutingRule>();
    public DbSet<RoutingRuleTarget> RoutingRuleTargets => Set<RoutingRuleTarget>();
    public DbSet<McpServer> McpServers => Set<McpServer>();
    public DbSet<McpTool> McpTools => Set<McpTool>();
    public DbSet<VirtualKeyMcpGrant> VirtualKeyMcpGrants => Set<VirtualKeyMcpGrant>();
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();
    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<AlertEvent> AlertEvents => Set<AlertEvent>();
    public DbSet<AuthFailure> AuthFailures => Set<AuthFailure>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Department>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.CostCenterCode).HasMaxLength(50);
            e.HasIndex(x => x.CostCenterCode).IsUnique();
            e.HasMany(x => x.Teams).WithOne(x => x.Department).HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Team>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.HasIndex(x => new { x.DepartmentId, x.Name }).IsUnique();
            e.HasMany(x => x.VirtualKeys).WithOne(x => x.Team).HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<VirtualKey>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.Prefix).HasMaxLength(20);
            e.Property(x => x.KeyHash).HasMaxLength(64);
            e.Property(x => x.EncryptedSecret).HasMaxLength(1000);
            e.Property(x => x.CreatedBy).HasMaxLength(200);
            e.HasIndex(x => x.KeyHash).IsUnique();
            e.Property(x => x.RotatedToKeyId).IsConcurrencyToken();
            e.Property(x => x.RevokedAt).IsConcurrencyToken();
            e.Property(x => x.PiiPolicy).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.AttachmentPolicy).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.Entity<DataApiKey>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.Prefix).HasMaxLength(20);
            e.Property(x => x.KeyHash).HasMaxLength(64);
            e.Property(x => x.CreatedBy).HasMaxLength(200);
            e.HasIndex(x => x.KeyHash).IsUnique();
            e.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<ProviderAccount>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.BaseUrl).HasMaxLength(500);
            e.Property(x => x.EncryptedCredential).HasMaxLength(4000);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.AuthMode).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Residency).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasMany(x => x.Deployments).WithOne(x => x.ProviderAccount).HasForeignKey(x => x.ProviderAccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ModelDeployment>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.UpstreamModel).HasMaxLength(200);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ParameterProfile).HasConversion<string>().HasMaxLength(30);
            // Unique per provider: two providers may offer a model of the same name (see PublicModelNames).
            e.HasIndex(x => new { x.ProviderAccountId, x.Name }).IsUnique();
            e.HasMany(x => x.Prices).WithOne().HasForeignKey(x => x.ModelDeploymentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ModelPrice>(e =>
        {
            e.Property(x => x.InputPerMillionUsd).HasPrecision(18, 6);
            e.Property(x => x.CachedInputPerMillionUsd).HasPrecision(18, 6);
            e.Property(x => x.OutputPerMillionUsd).HasPrecision(18, 6);
            e.Property(x => x.AudioPerMinuteUsd).HasPrecision(18, 6);
            e.Property(x => x.AudioInputPerMillionUsd).HasPrecision(18, 6);
            e.Property(x => x.AudioOutputPerMillionUsd).HasPrecision(18, 6);
            e.HasIndex(x => new { x.ModelDeploymentId, x.EffectiveFrom }).IsUnique();
        });

        modelBuilder.Entity<RouteAlias>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasMany(x => x.Targets).WithOne().HasForeignKey(x => x.RouteAliasId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RouteTarget>(e =>
            e.HasOne(x => x.ModelDeployment).WithMany().HasForeignKey(x => x.ModelDeploymentId).OnDelete(DeleteBehavior.Restrict));

        modelBuilder.Entity<RoutingRule>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.Condition).HasMaxLength(2000);
            e.Property(x => x.Scope).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.Scope, x.Priority });
            e.HasMany(x => x.Targets).WithOne().HasForeignKey(x => x.RoutingRuleId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RoutingRuleTarget>(e => e.Property(x => x.Model).HasMaxLength(200));

        modelBuilder.Entity<McpServer>(e =>
        {
            e.Property(x => x.Slug).HasMaxLength(McpToolNames.MaxSlugLength);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.Url).HasMaxLength(500);
            e.Property(x => x.Residency).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.AuthType).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.EncryptedCredential).HasMaxLength(4000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.Property(x => x.Instructions).HasMaxLength(McpServer.MaxInstructionsLength);
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasMany(x => x.Tools).WithOne().HasForeignKey(x => x.McpServerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<McpTool>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.Title).HasMaxLength(200);
            // The sync and approvals both change a tool; neither may overwrite the other's newer state.
            e.Property(x => x.DefinitionHash).HasMaxLength(64).IsConcurrencyToken();
            e.Property(x => x.Definition).HasColumnType("jsonb");
            e.Property(x => x.ApprovedDefinition).HasColumnType("jsonb");
            e.Property(x => x.ApprovalState).HasConversion<string>().HasMaxLength(20).IsConcurrencyToken();
            e.Property(x => x.ExposedName).HasMaxLength(64);
            e.Property(x => x.PriceSek).HasPrecision(18, 6);
            e.Property(x => x.ApprovedBy).HasMaxLength(200);
            e.HasIndex(x => new { x.McpServerId, x.Name }).IsUnique();
            e.HasIndex(x => x.ExposedName).IsUnique();
        });

        modelBuilder.Entity<VirtualKeyMcpGrant>(e =>
        {
            e.Property(x => x.ApprovalPolicy).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.VirtualKeyId, x.McpServerId }).IsUnique();
            e.HasIndex(x => x.McpServerId);
            e.HasOne<VirtualKey>().WithMany().HasForeignKey(x => x.VirtualKeyId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<McpServer>().WithMany().HasForeignKey(x => x.McpServerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Budget>(e =>
        {
            e.Property(x => x.LimitSek).HasPrecision(18, 2);
            e.Property(x => x.Scope).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Period).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.Scope, x.ScopeId });
        });

        modelBuilder.Entity<ExchangeRate>(e =>
        {
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.SekPerUnit).HasPrecision(18, 6);
            e.HasIndex(x => new { x.Currency, x.EffectiveFrom }).IsUnique();
        });

        modelBuilder.Entity<UsageRecord>(e =>
        {
            e.Property(x => x.RequestId).HasMaxLength(64);
            e.Property(x => x.RequestedModel).HasMaxLength(200);
            e.Property(x => x.ProviderName).HasMaxLength(100);
            e.Property(x => x.UpstreamModel).HasMaxLength(200);
            e.Property(x => x.CostUsd).HasPrecision(18, 8);
            e.Property(x => x.CostSek).HasPrecision(18, 6);
            e.Property(x => x.AudioSeconds).HasPrecision(12, 3);
            e.Property(x => x.Endpoint).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.PiiActionApplied).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.PiiCategories).HasMaxLength(200);
            e.Property(x => x.ErrorCode).HasMaxLength(50);
            e.Property(x => x.RoutingRuleName).HasMaxLength(200);
            e.Property(x => x.McpMethod).HasMaxLength(50);
            e.Property(x => x.McpTool).HasMaxLength(128);
            e.Property(x => x.ParentRequestId).HasMaxLength(64);
            e.Property(x => x.RecordedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => x.RequestId);
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => new { x.VirtualKeyId, x.Timestamp });
            e.HasIndex(x => new { x.TeamId, x.Timestamp });
            e.HasIndex(x => new { x.DepartmentId, x.Timestamp });
            // PII incidents report: only the few requests in which PII was found.
            e.HasIndex(x => x.Timestamp, "IX_UsageRecords_Timestamp_Pii").HasFilter("\"PiiCategories\" IS NOT NULL");
            e.HasIndex(x => new { x.McpServerId, x.Timestamp }).HasFilter("\"McpServerId\" IS NOT NULL");
        });

        modelBuilder.Entity<AuditLogEntry>(e =>
        {
            e.ToTable("AuditLog");
            e.Property(x => x.Actor).HasMaxLength(200);
            e.Property(x => x.Action).HasMaxLength(100);
            e.Property(x => x.EntityType).HasMaxLength(100);
            e.Property(x => x.EntityId).HasMaxLength(100);
            e.Property(x => x.RecordedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => x.Timestamp);
        });

        modelBuilder.Entity<AlertEvent>(e =>
        {
            e.Property(x => x.Scope).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.SpentSek).HasPrecision(18, 2);
            e.Property(x => x.LimitSek).HasPrecision(18, 2);
            e.HasIndex(x => new { x.BudgetId, x.PeriodStart, x.ThresholdPercent }).IsUnique();
        });

        modelBuilder.Entity<AuthFailure>(e =>
        {
            e.Property(x => x.Reason).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Endpoint).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.KeyPrefix).HasMaxLength(20);
            e.Property(x => x.SourceAddress).HasMaxLength(64);
            e.Property(x => x.RecordedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => x.LastSeen);
            e.HasIndex(x => new { x.VirtualKeyId, x.LastSeen });
        });
    }
}

public sealed class UtcDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
    value => value.ToUniversalTime(),
    value => value.ToUniversalTime());
