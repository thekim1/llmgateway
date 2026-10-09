using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi.Tests;

public sealed class SecurityEventTests(AdminFixture fixture)
{
    [Fact]
    public async Task Audit_entries_become_admin_change_events_once_saved_and_without_details()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        using var loggers = new CapturingLoggerFactory();
        var ctx = new AdminContext(db, scope.ServiceProvider.GetRequiredService<IInvalidationBus>(), TimeProvider.System, loggers);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "admin-123")], "Test"));
        var id = Guid.NewGuid();

        ctx.StageAudit(user, "revoke", "VirtualKey", id, new { secret = "before-value" }, null);
        loggers.Messages.ShouldBeEmpty();

        await ctx.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var message = loggers.Messages.ShouldHaveSingleItem();
        message.ShouldBe($"{SecurityEvents.Category}|admin.change|Admin admin-123: revoke VirtualKey {id}");
        message.ShouldNotContain("before-value");

        await ctx.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        loggers.Messages.Count.ShouldBe(1);
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Messages);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class Logger(string category, ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue($"{category}|{eventId.Name}|{formatter(state, exception)}");
        }
    }
}
