using System.Diagnostics;
using System.Diagnostics.Metrics;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.Gateway.Usage;

/// <summary>OpenTelemetry metrics (no content, no key secrets; key prefix is not used as a tag to bound cardinality).</summary>
public sealed class GatewayMetrics
{
    private readonly Counter<long> _requests;
    private readonly Counter<long> _tokens;
    private readonly Counter<long> _fallbacks;
    private readonly Counter<long> _ruleRouted;
    private readonly Histogram<double> _latency;
    private readonly Histogram<double> _overhead;
    private readonly Histogram<double> _timeToFirstToken;
    private readonly Histogram<double> _providerFirstToken;
    private readonly Histogram<double> _sessionDuration;
    private readonly UpDownCounter<long> _openSessions;
    private readonly Counter<double> _audioSeconds;
    private readonly Histogram<double> _streamMaxGap;
    private readonly Histogram<double> _streamMaxWrite;
    private readonly Counter<long> _streamStalls;
    private readonly Counter<double> _costUsd;
    private readonly Histogram<long>? _genAiTokens;
    private readonly Histogram<double>? _genAiClientDuration;
    private readonly Histogram<double>? _genAiServerDuration;
    private readonly Histogram<double>? _genAiTimeToFirstToken;
    private readonly Meter _meter;

    // Bucket boundaries recommended by the OpenTelemetry GenAI semantic conventions.
    private static readonly IReadOnlyList<long> TokenBuckets = [1, 4, 16, 64, 256, 1024, 4096, 16384, 65536, 262144, 1048576, 4194304, 16777216, 67108864];
    private static readonly IReadOnlyList<double> DurationBuckets = [0.01, 0.02, 0.04, 0.08, 0.16, 0.32, 0.64, 1.28, 2.56, 5.12, 10.24, 20.48, 40.96, 81.92];
    private static readonly IReadOnlyList<double> FirstTokenBuckets = [0.001, 0.005, 0.01, 0.02, 0.04, 0.06, 0.08, 0.1, 0.25, 0.5, 0.75, 1.0, 2.5, 5.0, 7.5, 10.0];

    public GatewayMetrics(IMeterFactory meterFactory, Microsoft.Extensions.Options.IOptions<GatewayOptions> options)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(options);
        _meter = meterFactory.Create(Extensions.TelemetryName);
        _requests = _meter.CreateCounter<long>("ume.gateway.requests", description: "Gateway requests by outcome");
        _tokens = _meter.CreateCounter<long>("ume.gateway.tokens", description: "Tokens processed");
        _costUsd = _meter.CreateCounter<double>("ume.gateway.cost.usd", unit: "USD", description: "Cost in USD, the currency prices and budgets are in");

        _fallbacks = _meter.CreateCounter<long>("ume.gateway.fallbacks", description: "Fallback attempts");
        _ruleRouted = _meter.CreateCounter<long>("ume.gateway.routing.rule_routed", description: "Requests routed by a routing rule (no per-rule label: rule names are unbounded)");
        _latency = _meter.CreateHistogram<double>("ume.gateway.request.duration", unit: "ms", description: "End-to-end latency");
        _overhead = _meter.CreateHistogram<double>("ume.gateway.overhead", unit: "ms", description: "The gateway's own time: end-to-end latency minus time spent waiting on providers");
        _timeToFirstToken = _meter.CreateHistogram<double>("ume.gateway.time_to_first_token", unit: "ms", description: "Streamed answers: time until the first event reached the client");
        _providerFirstToken = _meter.CreateHistogram<double>("ume.gateway.provider.time_to_first_token", unit: "ms", description: "Streamed answers: time from sending the request to the provider that answered until its first event, without fallbacks or gateway time");
        _sessionDuration = _meter.CreateHistogram<double>("ume.gateway.realtime.session.duration", unit: "s", description: "Length of live audio sessions");
        _openSessions = _meter.CreateUpDownCounter<long>("ume.gateway.realtime.sessions", description: "Live audio sessions open on this instance");
        _audioSeconds = _meter.CreateCounter<double>("ume.gateway.audio", unit: "s", description: "Seconds of audio processed");
        _streamMaxGap = _meter.CreateHistogram<double>("ume.gateway.stream.max_upstream_gap", unit: "ms", description: "Streamed answers: longest wait for the provider's next event after the first");
        _streamMaxWrite = _meter.CreateHistogram<double>("ume.gateway.stream.max_write", unit: "ms", description: "Streamed answers: longest time the gateway spent writing one event to the client");
        _streamStalls = _meter.CreateCounter<long>("ume.gateway.stream.stalls", description: "Waits of a second or more for a provider's next event, mid-stream");

        // OpenTelemetry GenAI semantic conventions, so standard GenAI dashboards work (Gateway:GenAiMetrics, off by default:
        // four more histogram recordings per request). Model attributes are the catalogue's upstream model names
        // (bounded), never the name a client sent.
        if (!options.Value.GenAiMetrics)
        {
            return;
        }

        _genAiTokens = _meter.CreateHistogram<long>("gen_ai.client.token.usage", unit: "{token}", description: "Tokens used per request to a provider",
            advice: new InstrumentAdvice<long> { HistogramBucketBoundaries = TokenBuckets });
        _genAiClientDuration = _meter.CreateHistogram<double>("gen_ai.client.operation.duration", unit: "s", description: "Time spent waiting on providers per request",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = DurationBuckets });
        _genAiServerDuration = _meter.CreateHistogram<double>("gen_ai.server.request.duration", unit: "s", description: "Gateway request duration, end to end",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = DurationBuckets });
        _genAiTimeToFirstToken = _meter.CreateHistogram<double>("gen_ai.server.time_to_first_token", unit: "s", description: "Streamed answers: time until the first event reached the client",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = FirstTokenBuckets });
    }

    /// <summary>Once per stream: its longest provider gap and client write, and its stalls.</summary>
    public void StreamGaps(string provider, GatewayEndpoint endpoint, double maxUpstreamGapMs, double maxWriteMs, int stalls)
    {
        var tags = new TagList { { "provider", provider }, { "endpoint", endpoint.ToString() } };
        _streamMaxGap.Record(maxUpstreamGapMs, tags);
        _streamMaxWrite.Record(maxWriteMs, tags);
        if (stalls > 0)
        {
            _streamStalls.Add(stalls, tags);
        }
    }

    public void SessionOpened(GatewayEndpoint endpoint) => _openSessions.Add(1, new TagList { { "endpoint", endpoint.ToString() } });

    public void SessionClosed(GatewayEndpoint endpoint) => _openSessions.Add(-1, new TagList { { "endpoint", endpoint.ToString() } });

    public void ObserveQueueDepth(Func<int> depth) =>
        _meter.CreateObservableGauge("ume.gateway.usage_queue.depth", depth, description: "Usage records waiting to be written");

    /// <summary>The <c>gen_ai.provider.name</c> of a provider type (the semantic conventions' names where there is one).</summary>
    public static string GenAiProviderName(ProviderType type) => type switch
    {
        ProviderType.OpenAI => "openai",
        ProviderType.AzureOpenAI => "azure.ai.openai",
        ProviderType.AzureAIFoundry => "azure.ai.inference",
        ProviderType.Anthropic => "anthropic",
        ProviderType.Ollama or ProviderType.OllamaCloud => "ollama",
        _ => "openai_compatible",
    };

    /// <summary>The <c>gen_ai.operation.name</c> of an endpoint.</summary>
    public static string GenAiOperationName(GatewayEndpoint endpoint) => endpoint switch
    {
        GatewayEndpoint.ChatCompletions or GatewayEndpoint.Responses or GatewayEndpoint.AnthropicMessages => "chat",
        GatewayEndpoint.Embeddings => "embeddings",
        _ => GatewayEndpoints.Info(endpoint).RoutingName,
    };

    /// <param name="record">The request's usage record.</param>
    /// <param name="providerType">The type of the provider the request went to; with it the GenAI metrics are recorded too.</param>
    public void Record(UsageRecord record, ProviderType? providerType = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        var tags = new TagList
        {
            { "outcome", record.Outcome.ToString() },
            { "provider", record.ProviderName ?? "none" },
            { "endpoint", record.Endpoint.ToString() },
        };
        _requests.Add(1, tags);
        if (GatewayEndpoints.Info(record.Endpoint).IsRealtime)
        {
            _sessionDuration.Record(record.LatencyMs / 1000d, tags); // a session's length is not request latency
        }
        else
        {
            _latency.Record(record.LatencyMs, tags);
            if (record.UpstreamMs is { } upstream)
            {
                _overhead.Record(Math.Max(0, record.LatencyMs - upstream), tags);
            }

            if (record.TimeToFirstTokenMs is { } firstToken)
            {
                _timeToFirstToken.Record(firstToken, tags);
            }

            if (record.ProviderFirstTokenMs is { } providerFirstToken)
            {
                _providerFirstToken.Record(providerFirstToken, tags);
            }
        }

        if (record.AudioSeconds > 0)
        {
            _audioSeconds.Add((double)record.AudioSeconds, new TagList { { "provider", record.ProviderName ?? "none" }, { "endpoint", record.Endpoint.ToString() } });
        }

        _tokens.Add(record.InputTokens, new TagList { { "provider", record.ProviderName ?? "none" }, { "direction", "input" } });
        _tokens.Add(record.OutputTokens, new TagList { { "provider", record.ProviderName ?? "none" }, { "direction", "output" } });
        _costUsd.Add((double)record.CostUsd, new TagList { { "provider", record.ProviderName ?? "none" } });
        if (_genAiTokens is not null && providerType is { } type && record.UpstreamModel is { } model && !GatewayEndpoints.Info(record.Endpoint).IsRealtime)
        {
            RecordGenAi(record, type, model);
        }
        if (record.RoutingRuleId is not null)
        {
            _ruleRouted.Add(1, new TagList { { "endpoint", record.Endpoint.ToString() } });
        }

        if (record.FallbackCount > 0)
        {
            _fallbacks.Add(record.FallbackCount, new TagList { { "endpoint", record.Endpoint.ToString() } });
        }
    }

    private void RecordGenAi(UsageRecord record, ProviderType type, string model)
    {
        var tags = new TagList
        {
            { "gen_ai.operation.name", GenAiOperationName(record.Endpoint) },
            { "gen_ai.provider.name", GenAiProviderName(type) },
            { "gen_ai.request.model", model },
        };
        if (record.Outcome != RequestOutcome.Success)
        {
            tags.Add("error.type", record.ErrorCode ?? record.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        _genAiServerDuration!.Record(record.LatencyMs / 1000d, tags);
        if (record.UpstreamMs is { } upstream)
        {
            _genAiClientDuration!.Record(upstream / 1000d, tags);
        }

        if (record.TimeToFirstTokenMs is { } firstToken)
        {
            _genAiTimeToFirstToken!.Record(firstToken / 1000d, tags);
        }

        if (record.Outcome == RequestOutcome.Success)
        {
            var input = tags;
            input.Add("gen_ai.token.type", "input");
            _genAiTokens!.Record(record.InputTokens, input);
            if (GatewayEndpoints.Info(record.Endpoint).HasOutputTokens)
            {
                var output = tags;
                output.Add("gen_ai.token.type", "output");
                _genAiTokens!.Record(record.OutputTokens, output);
            }
        }
    }
}
