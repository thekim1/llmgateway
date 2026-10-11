namespace Ume.LlmGateway.AdminApi.Tests;

/// <summary>Meter names, units and prices as the Azure retail prices API lists them for Sweden Central.</summary>
public sealed class AzurePriceMatcherTests
{
    private static readonly AzurePriceMeter[] Meters =
    [
        .. new (string Product, string Meter, string Unit, decimal Price, string Type)[]
        {
            ("Azure OpenAI", "gpt 4.1 Inp glbl Tokens", "1K", 0.002m, "Consumption"),
            ("Azure OpenAI", "gpt 4.1 cached Inp glbl Tokens", "1K", 0.0005m, "Consumption"),
            ("Azure OpenAI", "gpt 4.1 Outp glbl Tokens", "1K", 0.008m, "Consumption"),
            ("Azure OpenAI", "gpt 4.1 Inp Data Zone Tokens", "1K", 0.0022m, "Consumption"),
            ("Azure OpenAI", "gpt 4.1 Outp Data Zone Tokens", "1K", 0.0088m, "Consumption"),
            ("Azure OpenAI", "gpt 4.1 mini Inp glbl Tokens", "1K", 0.0004m, "Consumption"),
            ("Azure OpenAI", "gpt 4.1 mini Outp glbl Tokens", "1K", 0.0016m, "Consumption"),
            ("Azure OpenAI GPT5", "GPT 5 Inpt Glbl 1M Tokens", "1M", 1.25m, "Consumption"),
            ("Azure OpenAI GPT5", "GPT 5 cchd Inpt Glbl 1M Tokens", "1M", 0.125m, "Consumption"),
            ("Azure OpenAI GPT5", "GPT 5 outpt Glbl 1M Tokens", "1M", 10m, "Consumption"),
            ("Azure OpenAI GPT5", "GPT 5 Batch Inpt Glbl 1M Tokens", "1M", 0.625m, "Consumption"),
            ("Azure OpenAI GPT5", "5 pp inp Gl 1M Tokens", "1M", 2.5m, "Consumption"),
            ("Azure OpenAI GPT5", "GPT 5 Mini Inpt DZone 1M Tokens", "1M", 0.275m, "Consumption"),
            ("Azure OpenAI GPT5", "GPT 5 Mini outpt DZone 1M Tokens", "1M", 2.2m, "Consumption"),
            ("Azure OpenAI", "o3 0416 Inp glbl Tokens", "1K", 0.002m, "Consumption"),
            ("Azure OpenAI", "o3 0416 Outp glbl Tokens", "1K", 0.008m, "Consumption"),
            ("Azure OpenAI", "gpt-4o-mini-transcribe-aud-inp-glbl Tokens", "1K", 0.003m, "Consumption"),
            ("Azure OpenAI", "gpt-4o-mini-transcribe-txt-inp-glbl Tokens", "1K", 0.00125m, "Consumption"),
            ("Azure OpenAI", "gpt-4o-mini-transcribe-txt-out-glbl Tokens", "1K", 0.005m, "Consumption"),
            ("Azure OpenAI Media", "gpt-realtime-2.1 Audio inp Gl 1M Tokens", "1M", 32m, "Consumption"),
            ("Azure OpenAI Media", "gpt-realtime-2.1 Audio cd inp Gl 1M Tokens", "1M", 0.4m, "Consumption"),
            ("Azure OpenAI Media", "gpt-realtime-2.1 Audio opt Gl 1M Tokens", "1M", 64m, "Consumption"),
            ("Azure OpenAI Media", "gpt-realtime-2.1 Text inp Gl 1M Tokens", "1M", 4m, "Consumption"),
            ("Azure OpenAI Media", "gpt-realtime-2.1 Text cd inp Gl 1M Tokens", "1M", 0.4m, "Consumption"),
            ("Azure OpenAI Media", "gpt-realtime-2.1 Text opt Gl 1M Tokens", "1M", 24m, "Consumption"),
            ("Azure OpenAI Media", "gpt-realtime-2.1 Image inp Gl 1M Tokens", "1M", 5m, "Consumption"),
            ("Azure OpenAI", "text-embedding-3-large-glbl Tokens", "1K", 0.00013m, "Consumption"),
            ("Azure OpenAI", "text-embedding-3-large-grader Tokens", "1K", 0.00013m, "Consumption"),
            ("Azure Deepseek Models", "V3.1 Inp glbl Tokens", "1K", 0.00123m, "Consumption"),
            ("Azure Deepseek Models", "V3.1 Outp glbl Tokens", "1K", 0.00494m, "Consumption"),
            ("Azure Llama Models", "Llama 3.3 70B Inp glbl Tokens", "1K", 0.00071m, "Consumption"),
            ("Azure Llama Models", "Llama 3.3 70B Outp glbl Tokens", "1K", 0.00071m, "Consumption"),
            ("Azure OpenAI", "Provisioned Managed Global Unit", "1 Hour", 1m, "Consumption"),
            ("Azure OpenAI", "gpt 4.1 Inp glbl Tokens", "1K", 0.0015m, "Reservation"),
        }.Select(m => AzurePriceMatcher.Parse(m.Product, m.Meter, m.Unit, m.Price, m.Type)).OfType<AzurePriceMeter>(),
    ];

    [Theory]
    [InlineData("gpt-4.1", "2025-04-14", "GlobalStandard", 2.0, 0.5, 8.0)]
    [InlineData("gpt-4.1", "2025-04-14", "DataZoneStandard", 2.2, 2.2, 8.8)]
    [InlineData("gpt-4.1-mini", "2025-04-14", "GlobalStandard", 0.4, 0.4, 1.6)]
    [InlineData("gpt-5", "2025-08-07", "GlobalStandard", 1.25, 0.125, 10.0)]
    [InlineData("gpt-5-mini", "2025-08-07", "DataZoneStandard", 0.275, 0.275, 2.2)]
    [InlineData("o3", "2025-04-16", "GlobalStandard", 2.0, 2.0, 8.0)]
    [InlineData("text-embedding-3-large", "1", "GlobalStandard", 0.13, 0.13, 0.0)]
    [InlineData("DeepSeek-V3.1", "1", "GlobalStandard", 1.23, 1.23, 4.94)]
    [InlineData("Llama-3.3-70B-Instruct", "1", "GlobalStandard", 0.71, 0.71, 0.71)]
    public void Token_prices_per_million_come_from_the_deployment_type_meters(string model, string version, string sku, double input, double cached, double output)
    {
        var (price, _) = AzurePriceMatcher.Suggest(Meters, model, version, sku).ShouldNotBeNull();

        price.ShouldBe(new DiscoveredPrice((decimal)input, (decimal)cached, (decimal)output));
    }

    [Fact]
    public void Audio_tokens_get_their_own_prices_and_image_meters_are_ignored()
    {
        AzurePriceMatcher.Suggest(Meters, "gpt-4o-mini-transcribe", "2025-03-20", "GlobalStandard")!.Value.Price
            .ShouldBe(new DiscoveredPrice(1.25m, 1.25m, 5m, AudioInputPerMillionUsd: 3m));
        AzurePriceMatcher.Suggest(Meters, "gpt-realtime-2.1", "2026-05-01", "GlobalStandard")!.Value.Price
            .ShouldBe(new DiscoveredPrice(4m, 0.4m, 24m, AudioInputPerMillionUsd: 32m, AudioOutputPerMillionUsd: 64m));
    }

    [Fact]
    public void The_meters_used_are_reported()
    {
        AzurePriceMatcher.Suggest(Meters, "gpt-4.1", "2025-04-14", "GlobalStandard")!.Value.Meters
            .ShouldBe(["gpt 4.1 Inp glbl Tokens", "gpt 4.1 Outp glbl Tokens", "gpt 4.1 cached Inp glbl Tokens"]);
    }

    [Theory]
    [InlineData("gpt-4.1", "2025-04-14", "ProvisionedManaged")] // billed per PTU hour
    [InlineData("gpt-4.1", "2025-04-14", "GlobalBatch")]
    [InlineData("gpt-4.1", "2025-04-14", null)]
    [InlineData("o3", "2025-01-31", "GlobalStandard")] // only the 0416 version is listed
    [InlineData("gpt-4.1-nano", "2025-04-14", "GlobalStandard")] // not listed
    [InlineData("4.1", "1", "GlobalStandard")] // "gpt 4.1" does not end a model called 4.1
    [InlineData("claude-sonnet-4-5", "1", "GlobalStandard")] // Claude is billed through Azure Marketplace
    public void No_price_is_suggested_without_an_unambiguous_pay_as_you_go_meter(string model, string version, string? sku) =>
        AzurePriceMatcher.Suggest(Meters, model, version, sku).ShouldBeNull();

    [Fact]
    public void Conflicting_meters_suggest_nothing()
    {
        AzurePriceMeter[] meters =
        [
            AzurePriceMatcher.Parse("Azure OpenAI", "gpt 4.1 Inp glbl Tokens", "1K", 0.002m, "Consumption")!,
            AzurePriceMatcher.Parse("Azure OpenAI", "gpt-4.1-Inp-glbl Tokens", "1K", 0.003m, "Consumption")!,
            AzurePriceMatcher.Parse("Azure OpenAI", "gpt 4.1 Outp glbl Tokens", "1K", 0.008m, "Consumption")!,
        ];

        AzurePriceMatcher.Suggest(meters, "gpt-4.1", "2025-04-14", "GlobalStandard").ShouldBeNull();
    }
}
