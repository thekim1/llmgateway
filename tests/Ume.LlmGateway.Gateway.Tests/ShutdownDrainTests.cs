using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Ume.LlmGateway.ServiceDefaults;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>Readiness fails as soon as the host stops, and the listener stays open for the configured drain time.</summary>
public sealed class ShutdownDrainTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
        public void Dispose() => _stopping.Dispose();
    }

    private static ShutdownDrain Drain(int seconds, IHostApplicationLifetime lifetime, TimeProvider time) =>
        new(Options.Create(new ShutdownOptions { DrainSeconds = seconds }), lifetime, time, NullLogger<ShutdownDrain>.Instance);

    private static async Task<HealthStatus> StatusAsync(ShutdownDrain drain) =>
        (await drain.CheckHealthAsync(new HealthCheckContext(), Ct)).Status;

    [Fact]
    public async Task Ready_until_the_stop_signal()
    {
        using var lifetime = new Lifetime();
        var drain = Drain(0, lifetime, TimeProvider.System);

        (await StatusAsync(drain)).ShouldBe(HealthStatus.Healthy);
        lifetime.StopApplication();
        (await StatusAsync(drain)).ShouldBe(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task Without_a_drain_time_stopping_does_not_wait()
    {
        using var lifetime = new Lifetime();
        var drain = Drain(0, lifetime, new FakeTimeProvider());

        var stopping = drain.StoppingAsync(Ct);

        stopping.IsCompletedSuccessfully.ShouldBeTrue();
        (await StatusAsync(drain)).ShouldBe(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task Stopping_waits_the_drain_time_while_not_ready()
    {
        using var lifetime = new Lifetime();
        var time = new FakeTimeProvider();
        var drain = Drain(10, lifetime, time);

        var stopping = drain.StoppingAsync(Ct);
        (await StatusAsync(drain)).ShouldBe(HealthStatus.Unhealthy);
        time.Advance(TimeSpan.FromSeconds(9));
        stopping.IsCompleted.ShouldBeFalse();

        time.Advance(TimeSpan.FromSeconds(1));
        await stopping.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task The_shutdown_timeout_cuts_the_drain_short()
    {
        using var lifetime = new Lifetime();
        using var timeout = new CancellationTokenSource();
        var drain = Drain(60, lifetime, new FakeTimeProvider());

        var stopping = drain.StoppingAsync(timeout.Token);
        await timeout.CancelAsync();

        await stopping.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        stopping.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task A_stopping_host_is_alive_not_ready_and_closes_connections_after_each_response()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddDefaultHealthChecks();
        await using var app = builder.Build();
        app.MapDefaultEndpoints();
        app.MapGet("/", () => "ok");
        await app.StartAsync(Ct);
        using var client = app.GetTestClient();

        using (var before = await client.GetAsync(new Uri("/", UriKind.Relative), Ct))
        {
            before.Headers.ConnectionClose.ShouldNotBe(true);
        }
        (await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        app.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        (await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await client.GetAsync(new Uri("/health/live", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var after = await client.GetAsync(new Uri("/", UriKind.Relative), Ct);
        after.StatusCode.ShouldBe(HttpStatusCode.OK);
        after.Headers.ConnectionClose.ShouldBe(true);
    }
}
