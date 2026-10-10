using System.Text.Json;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Providers;

namespace Ume.LlmGateway.Gateway.Pipeline.Realtime;

/// <summary>Client events the gateway looks into; everything else is passed on untouched.</summary>
public enum RealtimeClientEvent
{
    Other = 0,

    /// <summary><c>input_audio_buffer.append</c> (sessions) or <c>session.input_audio_buffer.append</c> (translations).</summary>
    AudioAppend = 1,

    /// <summary><c>session.update</c> or <c>transcription_session.update</c>: model names are rewritten here.</summary>
    SessionUpdate = 2,

    /// <summary><c>conversation.item.create</c>: may carry text (PII guard) and audio (metering).</summary>
    ConversationItemCreate = 3,

    /// <summary><c>response.create</c>: may carry instructions and input.</summary>
    ResponseCreate = 4,
}

/// <summary>Server events the gateway looks into.</summary>
public enum RealtimeServerEvent
{
    Other = 0,

    /// <summary>Spoken output; by far the largest and most frequent event, passed on without further reading.</summary>
    AudioDelta = 1,

    /// <summary><c>session.created</c> / <c>session.updated</c>: tells the input audio format, used for metering.</summary>
    SessionState = 2,

    /// <summary><c>conversation.item.input_audio_transcription.completed</c>: usage of the input transcription model.</summary>
    InputTranscriptionCompleted = 3,
}

/// <summary>Forward-only reading of the top-level <c>type</c> (and audio length) of realtime events, without building a tree.</summary>
public static class RealtimeEvents
{
    /// <summary>The event kind and, for audio appends, the number of audio bytes in its base64 <c>audio</c> field.</summary>
    public static (RealtimeClientEvent Kind, long AudioBytes) InspectClient(ReadOnlySpan<byte> json)
    {
        var kind = RealtimeClientEvent.Other;
        long audio = 0;
        try
        {
            var reader = new Utf8JsonReader(json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return default;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isType = reader.ValueTextEquals("type"u8);
                var isAudio = !isType && reader.ValueTextEquals("audio"u8);
                reader.Read();
                if (isType && reader.TokenType == JsonTokenType.String)
                {
                    kind = reader.ValueTextEquals("input_audio_buffer.append"u8) || reader.ValueTextEquals("session.input_audio_buffer.append"u8) ? RealtimeClientEvent.AudioAppend
                        : reader.ValueTextEquals("session.update"u8) || reader.ValueTextEquals("transcription_session.update"u8) ? RealtimeClientEvent.SessionUpdate
                        : reader.ValueTextEquals("conversation.item.create"u8) ? RealtimeClientEvent.ConversationItemCreate
                        : reader.ValueTextEquals("response.create"u8) ? RealtimeClientEvent.ResponseCreate
                        : RealtimeClientEvent.Other;
                }
                else if (isAudio && reader.TokenType == JsonTokenType.String)
                {
                    audio = reader.ValueIsEscaped ? Base64Bytes(reader.GetString()!) : Base64Bytes(reader.ValueSpan);
                }
                else
                {
                    reader.Skip();
                }
            }
        }
        catch (JsonException)
        {
            // Not ours to judge: the provider rejects malformed events (and does not bill them).
            return default;
        }

        return (kind, kind == RealtimeClientEvent.AudioAppend ? audio : 0);
    }

    public static RealtimeServerEvent InspectServer(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return RealtimeServerEvent.Other;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isType = reader.ValueTextEquals("type"u8);
                reader.Read();
                if (isType && reader.TokenType == JsonTokenType.String)
                {
                    return reader.ValueTextEquals("response.output_audio.delta"u8) || reader.ValueTextEquals("response.audio.delta"u8) || reader.ValueTextEquals("session.output_audio.delta"u8)
                        ? RealtimeServerEvent.AudioDelta
                        : reader.ValueTextEquals("session.created"u8) || reader.ValueTextEquals("session.updated"u8)
                          || reader.ValueTextEquals("transcription_session.created"u8) || reader.ValueTextEquals("transcription_session.updated"u8)
                            ? RealtimeServerEvent.SessionState
                            : reader.ValueTextEquals("conversation.item.input_audio_transcription.completed"u8)
                                ? RealtimeServerEvent.InputTranscriptionCompleted
                                : RealtimeServerEvent.Other;
                }

                reader.Skip();
            }
        }
        catch (JsonException)
        {
        }

        return RealtimeServerEvent.Other;
    }

    /// <summary>
    /// Bytes of input audio per second for the session's input format: <c>audio/pcm</c> (16-bit) at its rate, G.711
    /// (<c>audio/pcmu</c>, <c>audio/pcma</c>, <c>g711_ulaw</c>, <c>g711_alaw</c>) at 8 kHz. Null when the session does not say.
    /// </summary>
    public static int? InputBytesPerSecond(JsonObject? session)
    {
        if (session?["audio"]?["input"]?["format"] is JsonObject format)
        {
            var type = format["type"] is JsonValue t && t.TryGetValue<string>(out var s) ? s : null;
            var rate = format["rate"] is JsonValue r && r.TryGetValue<int>(out var n) && n is > 0 and <= 384_000 ? n : 24_000;
            return type is "audio/pcmu" or "audio/pcma" ? 8_000 : rate * 2;
        }

        return session?["input_audio_format"] is JsonValue legacy && legacy.TryGetValue<string>(out var name)
            ? name.StartsWith("g711", StringComparison.Ordinal) ? 8_000 : 48_000
            : null;
    }

    /// <summary>Usage carried by a server event: top-level <c>usage</c> (transcriptions) or <c>response.usage</c> (<c>response.done</c>).</summary>
    public static bool TryReadUsage(JsonObject evt, out TokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return UsageParser.TryRead(evt["usage"], out usage) || UsageParser.TryRead(evt["response"]?["usage"], out usage);
    }

    /// <summary>An OpenAI-style <c>error</c> server event, so clients handle gateway refusals like provider errors.</summary>
    public static byte[] Error(string code, string message, string? clientEventId, string type = "invalid_request_error") =>
        JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["type"] = "error",
            ["event_id"] = "event_ume_" + Guid.CreateVersion7().ToString("N"),
            ["error"] = new JsonObject
            {
                ["type"] = type,
                ["code"] = code,
                ["message"] = message,
                ["param"] = null,
                ["event_id"] = clientEventId,
            },
        });

    internal static long Base64Bytes(ReadOnlySpan<byte> base64)
    {
        var padding = base64.Length > 0 && base64[^1] == '=' ? base64.Length > 1 && base64[^2] == '=' ? 2 : 1 : 0;
        return Math.Max(0, base64.Length * 3L / 4 - padding);
    }

    internal static long Base64Bytes(string base64)
    {
        var padding = base64.EndsWith("==", StringComparison.Ordinal) ? 2 : base64.EndsWith('=') ? 1 : 0;
        return Math.Max(0, base64.Length * 3L / 4 - padding);
    }
}

/// <summary>
/// What a live session has used so far. Audio is counted from the bytes the client sends (the session's input format
/// gives the rate); usage the provider reports is summed per event. Safe to use from both relay directions.
/// </summary>
public sealed class RealtimeMeter
{
    /// <summary>PCM16 at 24 kHz, the Realtime API's default input format.</summary>
    public const int DefaultBytesPerSecond = 48_000;

    private readonly Lock _lock = new();
    private int _bytesPerSecond = DefaultBytesPerSecond;
    private decimal _countedSeconds;
    private TokenUsage _session;
    private TokenUsage _transcription;

    public void AddAudio(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        lock (_lock)
        {
            _countedSeconds += bytes / (decimal)_bytesPerSecond;
        }
    }

    public void SetInputBytesPerSecond(int bytesPerSecond)
    {
        lock (_lock)
        {
            _bytesPerSecond = Math.Max(1, bytesPerSecond);
        }
    }

    /// <summary>Usage reported by the provider; <paramref name="transcriptionModel"/> when it belongs to a separately priced input transcription model.</summary>
    public void Add(TokenUsage usage, bool transcriptionModel)
    {
        lock (_lock)
        {
            if (transcriptionModel)
            {
                _transcription += usage;
            }
            else
            {
                _session += usage;
            }
        }
    }

    public decimal CountedSeconds
    {
        get
        {
            lock (_lock)
            {
                return _countedSeconds;
            }
        }
    }

    /// <summary>
    /// The session model's usage (with the audio duration the provider reported, or else the counted duration) and the
    /// separately priced input transcription model's usage.
    /// </summary>
    public (TokenUsage Session, TokenUsage Transcription) Snapshot()
    {
        lock (_lock)
        {
            return (_session with { AudioSeconds = _session.AudioSeconds > 0 ? _session.AudioSeconds : decimal.Round(_countedSeconds, 3) }, _transcription);
        }
    }
}

/// <summary>Why the gateway refused a client event; the session stays open.</summary>
public sealed record RealtimeRejection(string Code, string Message);

/// <summary>
/// The gateway's rules for client events in a live session: model names are the gateway's (the client never names an
/// upstream model), and text goes through the key's PII policy. The session's routing was decided at connect, so PII
/// that may only go on-prem is refused rather than rerouted.
/// </summary>
public sealed class RealtimeEventPolicy(
    ModelDeployment session,
    Func<string, ModelDeployment?> resolveTranscriptionModel,
    PiiPolicy piiPolicy,
    bool onPrem)
{
    private readonly Dictionary<PiiCategory, int> _pii = [];

    /// <summary>The separately priced model transcribing the input of a conversation session, once the client chose one.</summary>
    public ModelDeployment? TranscriptionDeployment { get; private set; }

    /// <summary>The key's policy, once PII was found in any event.</summary>
    public PiiPolicy? PiiApplied { get; private set; }

    public string? PiiCategories => _pii.Count == 0 ? null : new PiiScanResult(_pii).Summary;

    /// <summary>Applies the rules to <paramref name="evt"/> (rewriting it in place). Returns audio bytes found, or a rejection.</summary>
    public (RealtimeRejection? Rejection, long AudioBytes) Apply(JsonObject evt, RealtimeClientEvent kind)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (kind == RealtimeClientEvent.SessionUpdate && evt["session"] is JsonObject config && RewriteModels(config) is { } refused)
        {
            return (refused, 0);
        }

        var audio = kind is RealtimeClientEvent.ConversationItemCreate or RealtimeClientEvent.ResponseCreate ? AudioBytes(evt) : 0;
        if (piiPolicy == PiiPolicy.Off)
        {
            return (null, audio);
        }

        var scan = PiiJsonScanner.Scan(evt, redact: piiPolicy == PiiPolicy.Redact);
        if (!scan.HasPii)
        {
            return (null, audio);
        }

        foreach (var (category, count) in scan.Counts)
        {
            _pii[category] = _pii.GetValueOrDefault(category) + count;
        }

        PiiApplied = piiPolicy;
        return PiiJsonScanner.Decide(piiPolicy, scan) switch
        {
            PiiDecision.Block => (new RealtimeRejection(GatewayErrorCodes.PiiBlocked,
                $"Händelsen innehåller personuppgifter ({scan.Summary}) och nyckelns policy tillåter inte det. Den skickades inte vidare."), 0),
            PiiDecision.ForwardOnPremOnly when !onPrem => (new RealtimeRejection(GatewayErrorCodes.PiiBlocked,
                $"Händelsen innehåller personuppgifter ({scan.Summary}) som bara får skickas till en lokal (on-prem) modell, men sessionen går till en extern leverantör. Den skickades inte vidare."), 0),
            _ => (null, audio),
        };
    }

    private RealtimeRejection? RewriteModels(JsonObject config)
    {
        if (config["model"] is JsonValue)
        {
            config["model"] = session.UpstreamModel;
        }

        // GA: session.audio.input.transcription; beta and transcription_session.update: session.input_audio_transcription.
        foreach (var transcription in new[] { config["audio"]?["input"]?["transcription"], config["input_audio_transcription"] })
        {
            if (transcription is not JsonObject t || t["model"] is not JsonValue value || !value.TryGetValue<string>(out var requested))
            {
                continue;
            }

            if (session.Kind is ModelKind.Transcription or ModelKind.SpeechTranslation)
            {
                t["model"] = session.UpstreamModel;
                continue;
            }

            if (resolveTranscriptionModel(requested) is not { } deployment)
            {
                return new RealtimeRejection(GatewayErrorCodes.ModelNotAllowed,
                    $"Transkriberingsmodellen '{ModelNames.Truncate(requested)}' finns inte hos sessionens leverantör eller får inte användas med nyckeln. Ange ett tal till text-alias från GET /v1/models.");
            }

            t["model"] = deployment.UpstreamModel;
            TranscriptionDeployment = deployment;
        }

        return null;
    }

    /// <summary>Audio sent inside conversation items (<c>input_audio</c> parts) rather than appended to the buffer.</summary>
    private static long AudioBytes(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Sum(p => p.Key == "audio" && p.Value is JsonValue v && v.TryGetValue<string>(out var s) ? RealtimeEvents.Base64Bytes(s) : AudioBytes(p.Value)),
        JsonArray array => array.Sum(AudioBytes),
        _ => 0,
    };
}
