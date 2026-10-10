using System.ComponentModel.DataAnnotations;
using Ume.LlmGateway.Domain;

namespace Ume.LlmGateway.AdminApi.Tests;

public sealed class ProviderRequestTests
{
    [Theory]
    [InlineData("https://res.services.ai.azure.com/voice-live?api-version=2026-04-10", true)]
    [InlineData("https://res.openai.azure.com/openai/v1", true)]
    [InlineData("https://res.example/v1?api-key=secret", false)]
    [InlineData("https://res.example/v1?access_token=secret", false)]
    [InlineData("https://res.example/v1?sig=abc", false)]
    [InlineData("https://user:pass@res.example/v1", false)]
    [InlineData("https://res.example/v1#fragment", false)]
    [InlineData("http://res.example/v1", false)] // plain HTTP only on-prem
    public void A_base_url_may_carry_parameters_such_as_api_version_but_never_credentials(string baseUrl, bool valid)
    {
        var request = new ProviderRequest("p", baseUrl, ProviderType.AzureAIFoundry, ProviderAuthMode.ApiKeyHeader, DataResidency.Eu, [ProviderCapabilities.Realtime]);
        Validator.TryValidateObject(request, new ValidationContext(request), [], validateAllProperties: true).ShouldBe(valid);
    }
}
