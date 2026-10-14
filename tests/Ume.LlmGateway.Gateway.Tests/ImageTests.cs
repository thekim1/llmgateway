using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>Images: <c>/v1/images/generations</c> (JSON) and <c>/v1/images/edits</c> (multipart uploads).</summary>
public sealed class ImageTests(GatewayFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string[] UpstreamBodies(string provider) =>
        [.. fixture.Upstream.LogEntries.Where(e => e.RequestMessage!.Path.StartsWith($"/{provider}/", StringComparison.Ordinal))
            .Select(e => e.RequestMessage!.BodyAsBytes is { } bytes ? Encoding.Latin1.GetString(bytes) : e.RequestMessage.Body ?? string.Empty)];

    private Task<HttpResponseMessage> GenerateAsync(TestKey? key, string model, string prompt, string extra = "") =>
        fixture.SendRawAsync(key, "/v1/images/generations", $$"""{"model":"{{model}}","prompt":"{{prompt}}"{{extra}}}""");

    [Fact]
    public async Task Generation_is_forwarded_and_billed_per_returned_picture()
    {
        var key = await fixture.CreateKeyAsync();
        var marker = Guid.NewGuid().ToString("N");
        using var response = await GenerateAsync(key, "imgpic/image", marker, ""","n":2,"size":"1024x1024" """.TrimEnd());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["data"]!.AsArray().Count.ShouldBe(2);
        response.Headers.GetValues("x-ume-model").Single().ShouldBe("imgpic/image");

        // The model is rewritten to the upstream name; the other fields go through unchanged.
        var sent = UpstreamBodies("imgpic").Single(b => b.Contains(marker, StringComparison.Ordinal));
        sent.ShouldContain("imgpic-image");
        sent.ShouldContain("1024x1024");
        fixture.Upstream.LogEntries.ShouldContain(e => e.RequestMessage!.Path == "/imgpic/v1/images/generations");

        var usage = await fixture.UsageAsync(response);
        usage.Endpoint.ShouldBe(GatewayEndpoint.ImageGenerations);
        usage.Outcome.ShouldBe(RequestOutcome.Success);
        usage.CostUsd.ShouldBe(0.08m); // 2 pictures × 0.04, however many were asked for
        usage.CostSek.ShouldBe(0.8m);
    }

    [Fact]
    public async Task Token_billed_model_records_reported_tokens()
    {
        var key = await fixture.CreateKeyAsync();
        using var response = await GenerateAsync(key, "imgtokens/image", "en katt");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var usage = await fixture.UsageAsync(response);
        usage.InputTokens.ShouldBe(500);
        usage.OutputTokens.ShouldBe(1000);
        usage.CostUsd.ShouldBe(0.0425m); // 500 × 5 + 1000 × 40 per million; no per-picture price
    }

    [Fact]
    public async Task Edit_forwards_every_picture_and_the_mask_as_multipart()
    {
        var key = await fixture.CreateKeyAsync();
        var marker = Guid.NewGuid().ToString("N");
        using var response = await fixture.SendImageEditAsync(key, "imgpic/image", [GatewayFixture.Png(), GatewayFixture.Png(80)], mask: GatewayFixture.Png(16),
            fields: new Dictionary<string, string> { ["size"] = "1024x1024", ["user"] = marker }, imageField: "image[]");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var sent = UpstreamBodies("imgpic").Single(b => b.Contains(marker, StringComparison.Ordinal));
        sent.ShouldContain("imgpic-image");
        sent.ShouldContain("name=size");
        sent.Split("name=\"image[]\"").Length.ShouldBe(3); // two pictures
        sent.ShouldContain("name=mask");
        sent.ShouldContain("filename=bild1.png");
        sent.ShouldContain("PNG");
        fixture.Upstream.LogEntries.ShouldContain(e => e.RequestMessage!.Path == "/imgpic/v1/images/edits");

        var usage = await fixture.UsageAsync(response);
        usage.Endpoint.ShouldBe(GatewayEndpoint.ImageEdits);
        usage.CostUsd.ShouldBe(0.08m);
    }

    [Fact]
    public async Task Fallback_sends_the_pictures_again()
    {
        var key = await fixture.CreateKeyAsync();
        var route = await fixture.CreateRouteAsync("imgfail/image", "imgpic/image");
        var marker = Guid.NewGuid().ToString("N");
        using var response = await fixture.SendImageEditAsync(key, route, [GatewayFixture.Png()], fields: new Dictionary<string, string> { ["user"] = marker });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("x-ume-fallbacks").Single().ShouldBe("1");
        UpstreamBodies("imgfail").Single(b => b.Contains(marker, StringComparison.Ordinal)).ShouldContain("PNG");
        UpstreamBodies("imgpic").Single(b => b.Contains(marker, StringComparison.Ordinal)).ShouldContain("PNG");
    }

    [Fact]
    public async Task Streaming_is_refused()
    {
        using var response = await GenerateAsync(await fixture.CreateKeyAsync(), "imgpic/image", "en katt", ""","stream":true""");
        var error = await ErrorAsync(response, 400, "invalid_request");
        error["message"]!.GetValue<string>().ShouldContain("stream");
    }

    [Fact]
    public async Task Pii_policy_redacts_the_prompt()
    {
        var key = await fixture.CreateKeyAsync(k => k.PiiPolicy = PiiPolicy.Redact);
        // Letters only: a run of digits in a hex id can look like a phone number and be redacted along with the personnummer.
        var marker = new string([.. Guid.NewGuid().ToString("N").Select(c => (char)('g' + Convert.ToInt32(c.ToString(), 16)))]);
        using var response = await GenerateAsync(key, "imgpic/image", $"{marker} porträtt av 19121212-1212");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var sent = UpstreamBodies("imgpic").Single(b => b.Contains(marker, StringComparison.Ordinal));
        sent.ShouldNotContain("19121212-1212");
        sent.ShouldContain("[PERSONNUMMER]");
    }

    [Fact]
    public async Task Keys_without_attachments_cannot_edit_but_may_generate()
    {
        var key = await fixture.CreateKeyAsync(k => k.AttachmentPolicy = AttachmentPolicy.None);
        using var edit = await fixture.SendImageEditAsync(key, "imgpic/image", [GatewayFixture.Png()]);
        var error = await ErrorAsync(edit, 400, "attachment_not_allowed");
        error["message"]!.GetValue<string>().ShouldContain("bilder");

        using var generate = await GenerateAsync(key, "imgpic/image", "en katt");
        generate.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Keys_that_allow_only_images_may_edit()
    {
        var key = await fixture.CreateKeyAsync(k => k.AttachmentPolicy = AttachmentPolicy.ImagesOnly);
        using var response = await fixture.SendImageEditAsync(key, "imgpic/image", [GatewayFixture.Png()]);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Edit_without_a_picture_is_rejected()
    {
        using var response = await fixture.SendImageEditAsync(await fixture.CreateKeyAsync(), "imgpic/image", [], mask: GatewayFixture.Png(16));
        await ErrorAsync(response, 400, "invalid_request");
    }

    [Fact]
    public async Task Edit_with_a_file_in_an_unknown_field_is_rejected()
    {
        using var response = await fixture.SendImageEditAsync(await fixture.CreateKeyAsync(), "imgpic/image", [GatewayFixture.Png()], imageField: "photo");
        await ErrorAsync(response, 400, "invalid_request");
    }

    [Fact]
    public async Task Edit_with_a_json_body_is_rejected_with_415()
    {
        using var response = await fixture.SendRawAsync(await fixture.CreateKeyAsync(), "/v1/images/edits", """{"model":"imgpic/image","prompt":"x"}""");
        await ErrorAsync(response, 415, "unsupported_media_type");
    }

    [Fact]
    public async Task Upload_over_the_image_limit_is_rejected_with_413()
    {
        // The fixture allows 256 KB of pictures together.
        using var response = await fixture.SendImageEditAsync(await fixture.CreateKeyAsync(), "imgpic/image", [GatewayFixture.Png(200 * 1024), GatewayFixture.Png(100 * 1024)]);
        await ErrorAsync(response, 413, "request_too_large");
    }

    [Fact]
    public async Task Models_and_endpoints_must_match()
    {
        var key = await fixture.CreateKeyAsync();
        using var chatModel = await GenerateAsync(key, "eu/ok", "en katt");
        await ErrorAsync(chatModel, 400, "invalid_request");

        using var imageModel = await fixture.SendAsync(key, model: "imgpic/image");
        await ErrorAsync(imageModel, 400, "invalid_request");
    }

    private static async Task<JsonNode> ErrorAsync(HttpResponseMessage response, int status, string code)
    {
        ((int)response.StatusCode).ShouldBe(status);
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["error"]!;
        error["code"]!.GetValue<string>().ShouldBe(code);
        return error;
    }
}
