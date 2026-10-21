using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
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
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "admin-123")], "Test"));
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } };
        var ctx = new AdminContext(db, scope.ServiceProvider.GetRequiredService<IInvalidationBus>(), TimeProvider.System, loggers, http);
        var id = Guid.NewGuid();

        ctx.StageAudit("revoke", "VirtualKey", id, new { secret = "before-value" }, null);
        loggers.Messages.ShouldBeEmpty();

        await ctx.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var message = loggers.Messages.ShouldHaveSingleItem();
        message.ShouldBe($"{SecurityEvents.Category}|admin.change|Admin admin-123: revoke VirtualKey {id}");
        message.ShouldNotContain("before-value");

        await ctx.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        loggers.Messages.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("gpt-4o", "gpt-4o")]
    [InlineData("Umeå kommun – sammanfattning", "Umeå kommun – sammanfattning")]
    [InlineData("/v1/usage\nwarn: forged", "/v1/usage?warn: forged")]
    [InlineData("a\r\n\u001b[2Jb\u0085\u007f", "a???[2Jb??")]
    public void Client_values_are_logged_without_control_characters(string value, string logged)
    {
        var printable = SecurityEvents.Printable(value);
        printable.ShouldBe(logged);
        if (value == logged)
        {
            printable.ShouldBeSameAs(value);
        }
    }

    [Fact]
    public async Task A_saved_change_is_not_reported_as_failed_when_the_gateways_cannot_be_told()
    {
        using var scope = fixture.Services.CreateScope();
        using var loggers = new CapturingLoggerFactory();
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "admin-123")], "Test")) } };
        var bus = new FailingBus(failures: 1);
        var ctx = new AdminContext(scope.ServiceProvider.GetRequiredService<GatewayDbContext>(), bus, TimeProvider.System, loggers, http);

        // One failure: the retry succeeds and nothing is reported.
        await ctx.PublishAsync([InvalidationKind.Keys], TestContext.Current.CancellationToken);
        bus.Attempts.ShouldBe(2);
        http.HttpContext.Response.Headers.ContainsKey(AdminContext.PropagationHeader).ShouldBeFalse();

        // Down for good: the request still succeeds, says so in a header and logs a warning.
        bus.Failures = int.MaxValue;
        await ctx.PublishAsync([InvalidationKind.Keys, InvalidationKind.Config], TestContext.Current.CancellationToken);
        http.HttpContext.Response.Headers[AdminContext.PropagationHeader].ToString().ShouldBe("delayed");
        loggers.Messages.Count(m => m.StartsWith($"{typeof(AdminContext).FullName}|LogPublishFailed|", StringComparison.Ordinal)).ShouldBe(2);
    }

    private sealed class FailingBus(int failures) : IInvalidationBus
    {
        public int Failures { get; set; } = failures;
        public int Attempts { get; private set; }

        public Task PublishAsync(InvalidationKind kind, CancellationToken cancellationToken) =>
            ++Attempts <= Failures ? Task.FromException(new InvalidOperationException("Redis unavailable")) : Task.CompletedTask;

        public IDisposable Subscribe(Action<InvalidationKind> handler) => throw new NotSupportedException();
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
