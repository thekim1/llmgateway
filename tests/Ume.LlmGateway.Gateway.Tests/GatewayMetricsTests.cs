using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Gateway.Pipeline;

namespace Ume.LlmGateway.Gateway.Tests;

public sealed class GatewayMetricsTests
{
    [Fact]
    public void GenAI_metrics_are_recorded_only_when_switched_on()
    {
        var off = Measure(genAiMetrics: false);
        off.ShouldNotContain(m => m.Name.StartsWith("gen_ai.", StringComparison.Ordinal)); // the default: no extra work per request
        off.ShouldContain(m => m.Name == "ume.gateway.cost.usd" && m.Value == 0.25);
        off.ShouldContain(m => m.Name == "ume.gateway.provider.time_to_first_token" && m.Value == 250 && Equals(m.Tags["provider"], "claude"));

        var on = Measure(genAiMetrics: true);
        on.ShouldContain(m => m.Name == "gen_ai.client.token.usage" && m.Value == 100 && Equals(m.Tags["gen_ai.token.type"], "input")
            && Equals(m.Tags["gen_ai.provider.name"], "anthropic") && Equals(m.Tags["gen_ai.operation.name"], "chat")
            && Equals(m.Tags["gen_ai.request.model"], "claude-upstream"));
        on.ShouldContain(m => m.Name == "gen_ai.client.token.usage" && m.Value == 20 && Equals(m.Tags["gen_ai.token.type"], "output"));
        on.ShouldContain(m => m.Name == "gen_ai.server.request.duration" && m.Value == 1.5);
        on.ShouldContain(m => m.Name == "gen_ai.client.operation.duration" && m.Value == 1.2);
        on.ShouldContain(m => m.Name == "gen_ai.server.time_to_first_token" && m.Value == 0.3);
    }

    private static List<(string Name, double Value, Dictionary<string, object?> Tags)> Measure(bool genAiMetrics)
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        var seen = new ConcurrentQueue<(string Name, double Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Scope == factory)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        void Capture<T>(Instrument i, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) =>
            seen.Enqueue((i.Name, Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture), tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
        listener.SetMeasurementEventCallback<long>(Capture);
        listener.SetMeasurementEventCallback<double>(Capture);
        listener.Start();

        var metrics = new GatewayMetrics(factory, Options.Create(new GatewayOptions { GenAiMetrics = genAiMetrics }));
        metrics.Record(new UsageRecord
        {
            RequestId = "r", Endpoint = GatewayEndpoint.ChatCompletions, RequestedModel = "ume/chat", ProviderName = "claude",
            UpstreamModel = "claude-upstream", InputTokens = 100, OutputTokens = 20, CostUsd = 0.25m, LatencyMs = 1500,
            UpstreamMs = 1200, TimeToFirstTokenMs = 300, ProviderFirstTokenMs = 250, StatusCode = 200, Outcome = RequestOutcome.Success,
        }, ProviderType.Anthropic);
        return [.. seen];
    }
}
