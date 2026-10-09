using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Domain.Tests;

public class AttachmentScannerTests
{
    private static AttachmentKinds Scan(string json) => AttachmentScanner.Scan(JsonNode.Parse(json)!.AsObject());

    [Theory]
    // OpenAI Chat Completions
    [InlineData("""{"messages":[{"role":"user","content":[{"type":"image_url","image_url":{"url":"https://x/y.png"}}]}]}""", AttachmentKinds.Image)]
    [InlineData("""{"messages":[{"role":"user","content":[{"type":"file","file":{"file_id":"file-1"}}]}]}""", AttachmentKinds.Document)]
    [InlineData("""{"messages":[{"role":"user","content":[{"type":"input_audio","input_audio":{"data":"YWJj","format":"wav"}}]}]}""", AttachmentKinds.Audio)]
    // OpenAI Responses
    [InlineData("""{"input":[{"role":"user","content":[{"type":"input_text","text":"a"},{"type":"input_file","file_url":"https://x/a.pdf"},{"type":"input_image","image_url":"data:image/png;base64,YQ=="}]}]}""", AttachmentKinds.Document | AttachmentKinds.Image)]
    // Anthropic Messages, including content nested in tool results and the system prompt
    [InlineData("""{"messages":[{"role":"user","content":[{"type":"tool_result","content":[{"type":"image","source":{"type":"base64","data":"YQ=="}}]}]}]}""", AttachmentKinds.Image)]
    [InlineData("""{"system":[{"type":"text","text":"s"}],"messages":[{"role":"user","content":[{"type":"document","source":{"type":"text","data":"hej"}}]}]}""", AttachmentKinds.Document)]
    // Text only
    [InlineData("""{"messages":[{"role":"user","content":"Hej"},{"role":"user","content":[{"type":"text","text":"image"}]}]}""", AttachmentKinds.None)]
    [InlineData("""{"input":["a","b"]}""", AttachmentKinds.None)]
    // Tool schemas and parameters are not message content
    [InlineData("""{"messages":[{"role":"user","content":"Hej"}],"tools":[{"type":"function","function":{"parameters":{"type":"object","properties":{"f":{"type":"file"}}}}}]}""", AttachmentKinds.None)]
    public void Scan_finds_file_parts_in_all_request_formats(string json, AttachmentKinds expected) =>
        Scan(json).ShouldBe(expected);

    [Theory]
    [InlineData(AttachmentPolicy.Allowed, AttachmentKinds.Image | AttachmentKinds.Document | AttachmentKinds.Audio, AttachmentKinds.None)]
    [InlineData(AttachmentPolicy.ImagesOnly, AttachmentKinds.Image, AttachmentKinds.None)]
    [InlineData(AttachmentPolicy.ImagesOnly, AttachmentKinds.Image | AttachmentKinds.Document, AttachmentKinds.Document)]
    [InlineData(AttachmentPolicy.ImagesOnly, AttachmentKinds.Audio, AttachmentKinds.Audio)]
    [InlineData(AttachmentPolicy.None, AttachmentKinds.Image, AttachmentKinds.Image)]
    [InlineData(AttachmentPolicy.None, AttachmentKinds.None, AttachmentKinds.None)]
    public void Disallowed_applies_policy(AttachmentPolicy policy, AttachmentKinds found, AttachmentKinds expected) =>
        AttachmentScanner.Disallowed(policy, found).ShouldBe(expected);
}
