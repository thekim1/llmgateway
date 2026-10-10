using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>Speech to text: <c>/v1/audio/transcriptions</c> and <c>/v1/audio/translations</c> (multipart uploads).</summary>
public sealed class AudioTests(GatewayFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string[] UpstreamBodies(string provider) =>
        [.. fixture.Upstream.LogEntries.Where(e => e.RequestMessage!.Path.StartsWith($"/{provider}/", StringComparison.Ordinal))
            .Select(e => e.RequestMessage!.BodyAsBytes is { } bytes ? Encoding.UTF8.GetString(bytes) : e.RequestMessage.Body ?? string.Empty)];

    [Fact]
    public async Task Transcription_is_forwarded_as_multipart_and_billed_per_reported_minute()
    {
        var key = await fixture.CreateKeyAsync();
        var marker = Guid.NewGuid().ToString("N");
        using var response = await fixture.SendAudioAsync(key, "speech/whisper", GatewayFixture.Wav(2),
            fields: new Dictionary<string, string> { ["language"] = "sv", ["prompt"] = marker, ["timestamp_granularities[]"] = "word" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["text"]!.GetValue<string>().ShouldBe("Hej från Umeå");
        response.Headers.GetValues("x-ume-model").Single().ShouldBe("speech/whisper");

        // The model is rewritten to the upstream name; the other fields and the file go through unchanged.
        var sent = UpstreamBodies("speech").Single(b => b.Contains(marker, StringComparison.Ordinal));
        sent.ShouldContain("speech-whisper");
        sent.ShouldContain("name=language");
        sent.ShouldContain("name=\"timestamp_granularities[]\"");
        sent.ShouldContain("filename=samtal.wav");
        sent.ShouldContain("RIFF");
        fixture.Upstream.LogEntries.ShouldContain(e => e.RequestMessage!.Path == "/speech/v1/audio/transcriptions");

        var usage = await fixture.UsageAsync(response);
        usage.Endpoint.ShouldBe(GatewayEndpoint.AudioTranscriptions);
        usage.Outcome.ShouldBe(RequestOutcome.Success);
        usage.AudioSeconds.ShouldBe(90m); // as reported, not the file's 2 s
        usage.CostUsd.ShouldBe(0.009m); // 1.5 min × 0.006
        usage.CostSek.ShouldBe(0.09m);
    }

    [Fact]
    public async Task Plain_text_answer_passes_through_and_is_billed_from_the_file_length()
    {
        var key = await fixture.CreateKeyAsync();
        using var response = await fixture.SendAudioAsync(key, "speechtext/whisper", GatewayFixture.Wav(3),
            fields: new Dictionary<string, string> { ["response_format"] = "text" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/plain");
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBe("Hej från Umeå\n");

        var usage = await fixture.UsageAsync(response);
        usage.AudioSeconds.ShouldBe(3m); // from the WAV header
        usage.CostUsd.ShouldBe(0.0003m);
    }

    [Fact]
    public async Task Token_billed_model_records_reported_tokens()
    {
        var key = await fixture.CreateKeyAsync();
        using var response = await fixture.SendAudioAsync(key, "speechtokens/transcribe", GatewayFixture.Wav(1));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var usage = await fixture.UsageAsync(response);
        usage.InputTokens.ShouldBe(1000);
        usage.OutputTokens.ShouldBe(50);
        usage.AudioSeconds.ShouldBe(1m); // not reported: from the file, for reports
        usage.CostUsd.ShouldBe(0.0065m); // 1000 × 6 + 50 × 10 per million; no per-minute price
    }

    [Fact]
    public async Task Streamed_transcription_is_passed_through_and_billed_from_the_final_event()
    {
        var key = await fixture.CreateKeyAsync();
        using var response = await fixture.SendAudioAsync(key, "speechtokens/transcribe", GatewayFixture.Wav(1),
            fields: new Dictionary<string, string> { ["stream"] = "true" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldContain("transcript.text.delta");
        body.ShouldContain("transcript.text.done");

        var usage = await fixture.UsageAsync(response);
        usage.Streamed.ShouldBeTrue();
        usage.InputTokens.ShouldBe(1000);
        usage.OutputTokens.ShouldBe(50);
    }

    [Fact]
    public async Task Translation_goes_to_the_translations_endpoint()
    {
        var key = await fixture.CreateKeyAsync();
        using var response = await fixture.SendAudioAsync(key, "speech/whisper", GatewayFixture.Wav(1), endpoint: "/v1/audio/translations");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LogEntries.ShouldContain(e => e.RequestMessage!.Path == "/speech/v1/audio/translations");
        (await fixture.UsageAsync(response)).Endpoint.ShouldBe(GatewayEndpoint.AudioTranslations);
    }

    [Fact]
    public async Task Fallback_sends_the_file_again()
    {
        var key = await fixture.CreateKeyAsync();
        var route = await fixture.CreateRouteAsync("speechfail/whisper", "speech/whisper");
        var marker = Guid.NewGuid().ToString("N");
        using var response = await fixture.SendAudioAsync(key, route, GatewayFixture.Wav(1), fields: new Dictionary<string, string> { ["prompt"] = marker });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("x-ume-fallbacks").Single().ShouldBe("1");
        UpstreamBodies("speechfail").Single(b => b.Contains(marker, StringComparison.Ordinal)).ShouldContain("RIFF");
        UpstreamBodies("speech").Single(b => b.Contains(marker, StringComparison.Ordinal)).ShouldContain("RIFF");
    }

    [Fact]
    public async Task Pii_policy_redacts_the_prompt_field()
    {
        var key = await fixture.CreateKeyAsync(k => k.PiiPolicy = PiiPolicy.Redact);
        var marker = Guid.NewGuid().ToString("N");
        using var response = await fixture.SendAudioAsync(key, "speech/whisper", GatewayFixture.Wav(1),
            fields: new Dictionary<string, string> { ["prompt"] = $"{marker} Samtal med 19121212-1212" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("x-ume-pii").Single().ShouldBe("redacted");
        var sent = UpstreamBodies("speech").Single(b => b.Contains(marker, StringComparison.Ordinal));
        sent.ShouldNotContain("19121212-1212");
        sent.ShouldContain("[PERSONNUMMER]");
    }

    [Theory]
    [InlineData(AttachmentPolicy.ImagesOnly)]
    [InlineData(AttachmentPolicy.None)]
    public async Task Keys_that_refuse_audio_cannot_transcribe(AttachmentPolicy policy)
    {
        var key = await fixture.CreateKeyAsync(k => k.AttachmentPolicy = policy);
        using var response = await fixture.SendAudioAsync(key, "speech/whisper", GatewayFixture.Wav(1));

        var error = await ErrorAsync(response, 400, "attachment_not_allowed");
        error["message"]!.GetValue<string>().ShouldContain("ljudfiler");
    }

    [Fact]
    public async Task Missing_file_is_rejected()
    {
        using var response = await fixture.SendAudioAsync(await fixture.CreateKeyAsync(), "speech/whisper", file: null);
        await ErrorAsync(response, 400, "invalid_request");
    }

    [Fact]
    public async Task Json_body_is_rejected_with_415()
    {
        using var response = await fixture.SendRawAsync(await fixture.CreateKeyAsync(), "/v1/audio/transcriptions", """{"model":"speech/whisper"}""");
        await ErrorAsync(response, 415, "unsupported_media_type");
    }

    [Fact]
    public async Task Upload_over_the_audio_limit_is_rejected_with_413()
    {
        // The fixture allows 256 KB of audio (and only 4 KB for JSON bodies): 10 s of 16 kHz PCM is 320 KB.
        using var response = await fixture.SendAudioAsync(await fixture.CreateKeyAsync(), "speech/whisper", GatewayFixture.Wav(10));
        await ErrorAsync(response, 413, "request_too_large");
    }

    [Fact]
    public async Task Upload_larger_than_the_json_limit_is_accepted()
    {
        using var response = await fixture.SendAudioAsync(await fixture.CreateKeyAsync(), "speech/whisper", GatewayFixture.Wav(5)); // 160 KB
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Models_and_endpoints_must_match()
    {
        var key = await fixture.CreateKeyAsync();
        using var chatModel = await fixture.SendAudioAsync(key, "eu/ok", GatewayFixture.Wav(1));
        await ErrorAsync(chatModel, 400, "invalid_request");

        using var speechModel = await fixture.SendAsync(key, model: "speech/whisper");
        await ErrorAsync(speechModel, 400, "invalid_request");
    }

    private static async Task<JsonNode> ErrorAsync(HttpResponseMessage response, int status, string code)
    {
        ((int)response.StatusCode).ShouldBe(status);
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["error"]!;
        error["code"]!.GetValue<string>().ShouldBe(code);
        return error;
    }
}
