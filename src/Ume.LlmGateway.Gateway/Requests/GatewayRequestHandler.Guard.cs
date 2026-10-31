using System.Text.Json;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Gateway.Routing;
using Ume.LlmGateway.Infrastructure.Providers;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.Gateway.Requests;

/// <summary>
/// The content guard (docs/content-guard.md), for keys whose <see cref="VirtualKey.ContentGuardPolicy"/> is not Off:
/// in-process rules, then the guard model on an on-prem provider. Nothing here runs for keys with the guard off.
/// </summary>
public sealed partial class GatewayRequestHandler
{
    /// <summary>
    /// Checks the request. Record: flags it (<c>x-ume-guard</c>, usage record, security event) and lets it through. Block:
    /// refuses it with <c>content_blocked</c>. False when the request was refused (the answer has been written).
    /// </summary>
    private async Task<bool> GuardRequestAsync(HttpContext http, RequestState state, CatalogSnapshot snapshot, VirtualKey key, JsonObject body,
        CancellationToken ct)
    {
        var settings = options.CurrentValue.ContentGuard;
        var text = PiiJsonScanner.Text(body, settings.MaxCharacters);
        if (text.Length == 0)
        {
            return true;
        }

        var flags = new List<string>(guardRules.Check(text));
        if (flags.Count == 0 || key.ContentGuardPolicy == ContentGuardPolicy.Record)
        {
            // Block stops at the first finding; Record asks the model too, so the record says what it thought.
            switch (await GuardModelAsync(snapshot, text, state.RequestId, ct))
            {
                case { Flagged: true } verdict:
                    flags.Add(verdict.Categories ?? "unsafe");
                    break;
                case null when !string.IsNullOrWhiteSpace(settings.Model):
                    if (!settings.FailOpen && key.ContentGuardPolicy == ContentGuardPolicy.Block)
                    {
                        await RejectAsync(http, state, GatewayError.ContentGuardUnavailable, RequestOutcome.Rejected);
                        return false;
                    }

                    AddGuardFlags(state, "request", ["guard_unavailable"]);
                    break;
            }
        }

        if (flags.Count == 0)
        {
            return true;
        }

        var joined = AddGuardFlags(state, "request", flags);
        SecurityEvents.ContentFlagged(_security, key.ContentGuardPolicy == ContentGuardPolicy.Block ? "blocked" : "recorded", "request",
            state.RequestId, state.Endpoint.ToString(), joined, key.Id, key.Prefix, key.TeamId, key.Team?.DepartmentId ?? Guid.Empty);
        if (key.ContentGuardPolicy == ContentGuardPolicy.Block)
        {
            await RejectAsync(http, state, GatewayError.ContentBlocked(joined), RequestOutcome.Rejected);
            return false;
        }

        http.Response.Headers[GatewayHeaders.Guard] = "flagged";
        return true;
    }

    /// <summary>
    /// Checks a complete answer of a chat completions, Responses or Messages request (<see cref="ContentGuardOptions.CheckAnswers"/>):
    /// the blocked patterns and the guard model. Returns the error to answer with instead when the policy is Block.
    /// </summary>
    private async Task<GatewayError?> GuardAnswerAsync(HttpContext http, RequestState state, CatalogSnapshot snapshot, VirtualKey key,
        ReadOnlyMemory<byte> answer, CancellationToken ct)
    {
        var settings = options.CurrentValue.ContentGuard;
        if (!settings.CheckAnswers || key.ContentGuardPolicy == ContentGuardPolicy.Off
            || state.Endpoint is not (GatewayEndpoint.ChatCompletions or GatewayEndpoint.Responses or GatewayEndpoint.AnthropicMessages))
        {
            return null;
        }

        string text;
        try
        {
            text = AnswerText(JsonNode.Parse(answer.Span), settings.MaxCharacters);
        }
        catch (JsonException)
        {
            return null;
        }

        if (text.Length == 0)
        {
            return null;
        }

        var flags = new List<string>(guardRules.Check(text, includeInjection: false));
        if (await GuardModelAsync(snapshot, text, state.RequestId, ct) is { Flagged: true } verdict)
        {
            flags.Add(verdict.Categories ?? "unsafe");
        }

        if (flags.Count == 0)
        {
            return null;
        }

        var joined = AddGuardFlags(state, "answer", flags);
        SecurityEvents.ContentFlagged(_security, key.ContentGuardPolicy == ContentGuardPolicy.Block ? "blocked" : "recorded", "answer",
            state.RequestId, state.Endpoint.ToString(), joined, key.Id, key.Prefix, key.TeamId, key.Team?.DepartmentId ?? Guid.Empty);
        if (key.ContentGuardPolicy == ContentGuardPolicy.Block)
        {
            return GatewayError.AnswerBlocked(joined);
        }

        http.Response.Headers[GatewayHeaders.Guard] = "flagged";
        return null;
    }

    /// <summary>
    /// Asks the guard model about <paramref name="text"/>. Null when no model is configured or it is unavailable: not in
    /// the catalogue, not on an on-prem provider, failing, too slow, or answering something unreadable (logged).
    /// </summary>
    private async Task<GuardVerdict?> GuardModelAsync(CatalogSnapshot snapshot, string text, string requestId, CancellationToken ct)
    {
        var settings = options.CurrentValue.ContentGuard;
        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            return null;
        }

        // Only an on-prem deployment: the text must not leave the organisation to be checked.
        var onPrem = new RoutingConstraints(GatewayEndpoint.ChatCompletions, [DataResidency.OnPrem]);
        var target = snapshot.Resolve(settings.Model)?.Targets.FirstOrDefault(t => RouteSelector.IsEligible(t, onPrem));
        if (target?.ModelDeployment is not { } deployment || !TryGetCredential(snapshot, deployment.ProviderAccount!, out var credential))
        {
            LogGuardModelMissing(logger, settings.Model, requestId);
            return null;
        }

        var provider = deployment.ProviderAccount!;
        var body = new JsonObject
        {
            ["model"] = deployment.UpstreamModel,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = text }),
            ["max_tokens"] = 20,
            ["temperature"] = 0,
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(settings.TimeoutMs);
        try
        {
            var result = await _adapters.Resolve(provider.Type)
                .SendAsync(new ProviderCall(provider, deployment, GatewayEndpoint.ChatCompletions, body, false, credential, false), timeout.Token);
            if (result is not ProviderJsonResult json)
            {
                LogGuardModelFailed(logger, requestId, (result as ProviderFailure)?.Reason ?? "unexpected_result");
                return null;
            }

            using (json)
            {
                var answer = JsonNode.Parse(json.Body.Span)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
                var verdict = GuardVerdict.Parse(answer, settings.Format);
                if (verdict is null)
                {
                    LogGuardModelFailed(logger, requestId, "unreadable_answer");
                }

                return verdict;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LogGuardModelFailed(logger, requestId, "timeout");
            return null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            LogGuardModelFailed(logger, requestId, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>The answer's text: chat <c>choices[].message.content</c>, Responses <c>output[].content[].text</c>, Messages <c>content[].text</c>.</summary>
    private static string AnswerText(JsonNode? answer, int maxCharacters)
    {
        var parts = new List<string>();
        foreach (var choice in answer?["choices"] as JsonArray ?? [])
        {
            Add(choice?["message"]?["content"]);
        }

        foreach (var item in answer?["output"] as JsonArray ?? [])
        {
            foreach (var content in item?["content"] as JsonArray ?? [])
            {
                Add(content?["text"]);
            }
        }

        foreach (var block in answer?["content"] as JsonArray ?? [])
        {
            Add(block?["text"]);
        }

        var text = string.Join('\n', parts);
        return text.Length <= maxCharacters ? text : text[^maxCharacters..];

        void Add(JsonNode? node)
        {
            if (node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
            {
                parts.Add(s);
            }
        }
    }

    /// <summary>Adds <c>{part}:{flags}</c> to the request's guard flags (kept within the column's 200 characters); returns the flags joined.</summary>
    private static string AddGuardFlags(RequestState state, string part, IEnumerable<string> flags)
    {
        var joined = string.Join(',', flags.Distinct(StringComparer.Ordinal));
        var entry = $"{part}:{joined}";
        var all = state.GuardFlags is null ? entry : $"{state.GuardFlags};{entry}";
        state.GuardFlags = all.Length > 200 ? all[..200] : all;
        return joined;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Content guard model {Model} is not an enabled on-prem model in the catalogue; request {RequestId} checked by the rules only")]
    private static partial void LogGuardModelMissing(ILogger logger, string model, string requestId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Content guard model unavailable for request {RequestId} ({Reason})")]
    private static partial void LogGuardModelFailed(ILogger logger, string requestId, string reason);
}
