using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Domain.Tests;

public class PiiDetectorTests
{
    [Theory]
    [InlineData("Mitt personnummer är 19121212-1212.")]
    [InlineData("pnr: 121212-1212")]
    [InlineData("pnr 1212121212 tack")]
    [InlineData("191212121212")]
    [InlineData("121212+1212")] // over 100 years old
    public void Detects_valid_personnummer(string text) =>
        PiiDetector.Detect(text).ShouldContain(m => m.Category == PiiCategory.Personnummer);

    // Made-up numbers (wrong check digit) are what people paste when testing, so every unambiguous shape is caught
    // without Luhn: a separator ('-', '+' or one space) or a four-digit year.
    [Theory]
    [InlineData("pnr 900101-1234")] // YYMMDD-NNNN
    [InlineData("pnr 900101+1234")]
    [InlineData("pnr 900101 1234")]
    [InlineData("pnr 19900101-1234")] // YYYYMMDD-NNNN
    [InlineData("pnr 19900101 1234")]
    [InlineData("pnr 199001011234")] // YYYYMMDDNNNN
    [InlineData("pnr 200512241234 tack")]
    [InlineData("pnr 18991231-0000")]
    public void Detects_personnummer_with_separator_or_century_without_luhn(string text) =>
        PiiDetector.Detect(text).ShouldContain(m => m.Category == PiiCategory.Personnummer);

    [Fact]
    public void Redacts_whole_personnummer_in_every_format()
    {
        foreach (var value in new[] { "900101-1234", "9001011239", "19900101-1234", "199001011234", "900101 1234" })
        {
            var text = $"Patienten har {value}.";
            var matches = PiiDetector.Detect(text);
            PiiDetector.Redact(text, matches).ShouldBe("Patienten har [PERSONNUMMER].", value);
        }
    }

    [Theory]
    [InlineData("9001011234")] // YYMMDDNNNN with bad check digit: too ambiguous without Luhn
    [InlineData("121312-1212")] // month 13
    [InlineData("900230-1234")] // 30 February
    [InlineData("19900132-1234")] // day 32
    [InlineData("19901301 1234")] // month 13
    [InlineData("210001011234")] // century 21
    [InlineData("1990010112345")] // 13 digits
    [InlineData("Ärende 2026-1234 gäller")]
    [InlineData("Summa 1000000 kr")]
    public void Rejects_invalid_personnummer(string text) =>
        PiiDetector.Detect(text).ShouldNotContain(m => m.Category == PiiCategory.Personnummer || m.Category == PiiCategory.Samordningsnummer);

    [Theory]
    [InlineData("Ring 0701234567")]
    [InlineData("Ring 070-1234567")]
    [InlineData("Ring 070-123 45 67")]
    [InlineData("Ring 0907123456")]
    public void Phone_numbers_are_not_personnummer(string text)
    {
        var matches = PiiDetector.Detect(text);
        matches.ShouldNotContain(m => m.Category == PiiCategory.Personnummer || m.Category == PiiCategory.Samordningsnummer);
        matches.ShouldContain(m => m.Category == PiiCategory.Phone);
    }

    [Theory]
    [InlineData("samordningsnr 900161-1234")]
    [InlineData("samordningsnr 199001611234")]
    public void Detects_samordningsnummer_without_luhn(string text) =>
        PiiDetector.Detect(text).ShouldContain(m => m.Category == PiiCategory.Samordningsnummer);

    [Fact]
    public void Bare_ten_digit_samordningsnummer_needs_luhn()
    {
        var body = "900161123";
        var check = Enumerable.Range(0, 10).First(d => PiiDetector.LuhnValid(body + d));
        PiiDetector.Detect(body + check).ShouldContain(m => m.Category == PiiCategory.Samordningsnummer);
        PiiDetector.Detect(body + ((check + 1) % 10)).ShouldBeEmpty();
    }

    [Fact]
    public void Detects_samordningsnummer()
    {
        // Day 12 + 60 = 72; compute a valid check digit to keep the test self-contained.
        var body = "121272123";
        var check = Enumerable.Range(0, 10).First(d => PiiDetector.LuhnValid(body + d));
        PiiDetector.Detect($"samordningsnr 121272-123{check}").ShouldContain(m => m.Category == PiiCategory.Samordningsnummer);
    }

    [Fact]
    public void Leap_day_is_validated()
    {
        var body = "000229123";
        var check = Enumerable.Range(0, 10).First(d => PiiDetector.LuhnValid(body + d));
        PiiDetector.Detect($"20000229-123{check}").ShouldContain(m => m.Category == PiiCategory.Personnummer);

        body = "010229123";
        check = Enumerable.Range(0, 10).First(d => PiiDetector.LuhnValid(body + d));
        PiiDetector.Detect($"20010229-123{check}").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Maila anna.andersson@umea.se idag", PiiCategory.Email)]
    [InlineData("Ring 070-123 45 67", PiiCategory.Phone)]
    [InlineData("Ring +46 70 123 45 67", PiiCategory.Phone)]
    [InlineData("Växel 090-16 10 00", PiiCategory.Phone)]
    [InlineData("IBAN SE45 5000 0000 0583 9825 7466", PiiCategory.Iban)]
    public void Detects_other_categories(string text, PiiCategory expected) =>
        PiiDetector.Detect(text).ShouldContain(m => m.Category == expected);

    [Fact]
    public void Invalid_iban_is_ignored() =>
        PiiDetector.Detect("SE45 5000 0000 0583 9825 7467").ShouldNotContain(m => m.Category == PiiCategory.Iban);

    [Fact]
    public void Text_without_pii_returns_nothing() =>
        PiiDetector.Detect("Sammanfatta detaljplanen för Västerslätt i tre punkter.").ShouldBeEmpty();

    [Fact]
    public void Redact_replaces_values_with_placeholders()
    {
        const string text = "Hej, jag heter Anna (19121212-1212), mejl anna@umea.se.";
        var redacted = PiiDetector.Redact(text, PiiDetector.Detect(text));

        redacted.ShouldBe("Hej, jag heter Anna ([PERSONNUMMER]), mejl [E-POST].");
    }
}

public class PiiJsonScannerTests
{
    private static JsonNode ChatBody() => JsonNode.Parse("""
        {
          "model": "ume/chat-standard",
          "messages": [
            { "role": "system", "content": "Du är en hjälpsam assistent." },
            { "role": "user", "content": [
                { "type": "text", "text": "Mitt pnr är 121212-1212 och mejl anna@umea.se" },
                { "type": "image_url", "image_url": { "url": "data:image/png;base64,AAAA" } }
            ] }
          ],
          "user": "anna@umea.se"
        }
        """)!;

    [Fact]
    public void Scan_counts_categories_without_modifying_body()
    {
        var body = ChatBody();
        var original = body.ToJsonString();

        var result = PiiJsonScanner.Scan(body, redact: false);

        result.HasPii.ShouldBeTrue();
        result.Counts[PiiCategory.Personnummer].ShouldBe(1);
        result.Counts[PiiCategory.Email].ShouldBe(1); // "user" field is structural and skipped
        result.Summary.ShouldBe("Personnummer:1,Email:1");
        body.ToJsonString().ShouldBe(original);
    }

    [Fact]
    public void Scan_with_redact_rewrites_only_content()
    {
        var body = ChatBody();
        PiiJsonScanner.Scan(body, redact: true);

        var text = body["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>();
        text.ShouldBe("Mitt pnr är [PERSONNUMMER] och mejl [E-POST]");
        body["model"]!.GetValue<string>().ShouldBe("ume/chat-standard");
        body.ToJsonString().ShouldNotContain("121212-1212");
    }

    [Fact]
    public void Scan_leaves_file_payloads_alone()
    {
        // The base64 happens to contain a valid personnummer; redacting it would corrupt the file.
        const string Payload = "data:application/pdf;base64,QUJD1212121212QUJD";
        var body = JsonNode.Parse($$$"""
            {"messages":[{"role":"user","content":[
              {"type":"file","file":{"filename":"a.pdf","file_data":"{{{Payload}}}"}},
              {"type":"video_url","video_url":"data:video/mp4;base64,QUJD1212121212QUJD"}]}],
             "input":[{"role":"user","content":[{"type":"input_file","file_data":"{{{Payload}}}"}]}]}
            """)!;

        PiiJsonScanner.Scan(body, redact: true).HasPii.ShouldBeFalse();
        body["messages"]![0]!["content"]![0]!["file"]!["file_data"]!.GetValue<string>().ShouldBe(Payload);
    }

    [Theory]
    [InlineData(PiiPolicy.Allow, PiiDecision.Forward)]
    [InlineData(PiiPolicy.Redact, PiiDecision.ForwardRedacted)]
    [InlineData(PiiPolicy.Block, PiiDecision.Block)]
    [InlineData(PiiPolicy.RerouteToOnPrem, PiiDecision.ForwardOnPremOnly)]
    public void Decide_maps_policy_when_pii_found(PiiPolicy policy, PiiDecision expected) =>
        PiiJsonScanner.Decide(policy, PiiJsonScanner.Scan(ChatBody(), false)).ShouldBe(expected);

    [Fact]
    public void Scan_redacts_personnummer_in_string_and_content_part_chat_messages()
    {
        var body = JsonNode.Parse("""
            {"model":"ume/chat","messages":[
              {"role":"user","content":"Mitt pnr är 900101-1234"},
              {"role":"user","content":[{"type":"text","text":"och hans är 199001011234"}]}]}
            """)!;

        var result = PiiJsonScanner.Scan(body, redact: true);

        result.Counts[PiiCategory.Personnummer].ShouldBe(2);
        body["messages"]![0]!["content"]!.GetValue<string>().ShouldBe("Mitt pnr är [PERSONNUMMER]");
        body["messages"]![1]!["content"]![0]!["text"]!.GetValue<string>().ShouldBe("och hans är [PERSONNUMMER]");
    }

    [Fact]
    public void Decide_forwards_when_no_pii() =>
        PiiJsonScanner.Decide(PiiPolicy.Block, PiiJsonScanner.Scan(JsonNode.Parse("""{"input":"hej"}"""), false)).ShouldBe(PiiDecision.Forward);
}

public class RequestRewriterTests
{
    [Fact]
    public void Standard_profile_only_sets_model_and_preserves_unknown_fields()
    {
        var body = JsonNode.Parse("""{"model":"ume/chat","max_tokens":100,"temperature":0.2,"future_param":{"x":1},"verbosity":"low"}""")!.AsObject();

        RequestRewriter.Apply(body, GatewayEndpoint.ChatCompletions, "gpt-4o-mini", ParameterProfile.Standard, streaming: false);

        body["model"]!.GetValue<string>().ShouldBe("gpt-4o-mini");
        body["max_tokens"]!.GetValue<int>().ShouldBe(100);
        body["temperature"]!.GetValue<double>().ShouldBe(0.2);
        body["future_param"]!["x"]!.GetValue<int>().ShouldBe(1);
        body["verbosity"]!.GetValue<string>().ShouldBe("low");
        body["stream_options"].ShouldBeNull();
    }

    [Fact]
    public void Reasoning_profile_rewrites_for_gpt5_and_gpt6_families()
    {
        var body = JsonNode.Parse("""{"model":"ume/smart","max_tokens":500,"temperature":0.7,"top_p":0.9,"reasoning_effort":"high"}""")!.AsObject();

        RequestRewriter.Apply(body, GatewayEndpoint.ChatCompletions, "gpt-6", ParameterProfile.OpenAIReasoning, streaming: false);

        body["max_tokens"].ShouldBeNull();
        body["max_completion_tokens"]!.GetValue<int>().ShouldBe(500);
        body["temperature"].ShouldBeNull();
        body["top_p"].ShouldBeNull();
        body["reasoning_effort"]!.GetValue<string>().ShouldBe("high");
    }

    [Fact]
    public void Streaming_requests_ask_for_usage()
    {
        var body = JsonNode.Parse("""{"model":"x","stream":true,"stream_options":{"foo":true}}""")!.AsObject();
        RequestRewriter.Apply(body, GatewayEndpoint.ChatCompletions, "m", ParameterProfile.Standard, streaming: true);

        body["stream_options"]!["include_usage"]!.GetValue<bool>().ShouldBeTrue();
        body["stream_options"]!["foo"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void Reads_requested_max_output_tokens_and_stream_flag()
    {
        var body = JsonNode.Parse("""{"max_output_tokens":2048,"stream":true}""")!.AsObject();
        RequestRewriter.RequestedMaxOutputTokens(body).ShouldBe(2048);
        RequestRewriter.IsStreaming(body).ShouldBeTrue();
        RequestRewriter.RequestedMaxOutputTokens(new JsonObject()).ShouldBeNull();
    }

    [Theory]
    [InlineData("""{}""", 1)]
    [InlineData("""{"n":3}""", 3)]
    [InlineData("""{"n":"4"}""", 4)] // multipart forms carry every value as a string
    [InlineData("""{"n":0}""", 1)]
    [InlineData("""{"n":-2}""", 1)]
    [InlineData("""{"n":"-2"}""", 1)]
    [InlineData("""{"n":"x"}""", 1)]
    [InlineData("""{"n":null}""", 1)]
    [InlineData("""{"n":1000000}""", RequestRewriter.MaxEstimatedImages)]
    public void Reads_the_requested_image_count(string json, int expected) =>
        RequestRewriter.RequestedImageCount(JsonNode.Parse(json)!.AsObject()).ShouldBe(expected);
}
