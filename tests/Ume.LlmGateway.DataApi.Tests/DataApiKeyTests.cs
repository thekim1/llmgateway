using System.Net;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain;

namespace Ume.LlmGateway.DataApi.Tests;

/// <summary>Data API keys created in the admin UI: a second way in next to OAuth client credentials.</summary>
public sealed class DataApiKeyTests(DataApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_key_reads_what_its_permissions_allow_and_is_identified_in_the_log()
    {
        var (key, secret) = await fixture.CreateKeyAsync([DataPermissions.UsageDetail, DataPermissions.CatalogRead]);

        using (var bearer = fixture.KeyClient(secret))
        {
            (await bearer.GetAsync("/v1/usage/records?limit=1", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await bearer.GetAsync("/v1/catalog/departments", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await bearer.GetAsync("/v1/security/audit?limit=1", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await bearer.GetAsync("/v1/usage/aggregate?from=2026-10-01&to=2026-10-02", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // Many ETL tools can only set a plain header.
        using (var header = fixture.KeyClient(secret, apiKeyHeader: true))
        {
            (await header.GetAsync("/v1/usage/records?limit=1", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        fixture.Logs.Text.ShouldContain($"Data API client key:{key.Name} read /v1/usage/records: status 200");
        fixture.Logs.Text.ShouldNotContain(secret);
        var lastUsed = await fixture.DbAsync(db => db.DataApiKeys.Where(k => k.Id == key.Id).Select(k => k.LastUsedAt).SingleAsync(Ct));
        lastUsed.ShouldNotBeNull();
    }

    [Fact]
    public async Task Revoked_expired_unknown_and_malformed_keys_are_refused()
    {
        var (revoked, revokedSecret) = await fixture.CreateKeyAsync([DataPermissions.UsageDetail], k => k.RevokedAt = DateTimeOffset.UtcNow.AddMinutes(-1));
        var (_, expiredSecret) = await fixture.CreateKeyAsync([DataPermissions.UsageDetail], k => k.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1));
        var unknown = Domain.Keys.DataApiKeyHasher.Generate().PlainText;

        foreach (var secret in new[] { revokedSecret, expiredSecret, unknown, "ume-dk-tooshort" })
        {
            using var client = fixture.KeyClient(secret);
            (await client.GetAsync("/v1/usage/records?limit=1", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            using var header = fixture.KeyClient(secret, apiKeyHeader: true);
            (await header.GetAsync("/v1/usage/records?limit=1", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // A known key that is still used is a security signal; the event names the key by prefix, never by its secret.
        fixture.Logs.Text.ShouldContain($"Ume.LlmGateway.Security|data.auth.failed|Data API key refused: key_revoked (key {revoked.Id} {revoked.Prefix})");
        fixture.Logs.Text.ShouldNotContain(revokedSecret);
        fixture.Logs.Text.ShouldNotContain(unknown);
    }

    [Fact]
    public async Task Revoking_a_key_takes_effect_and_access_tokens_cannot_claim_key_permissions()
    {
        var (key, secret) = await fixture.CreateKeyAsync([DataPermissions.SecurityRead]);
        using var client = fixture.KeyClient(secret);
        (await client.GetAsync("/v1/security/audit?limit=1", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await fixture.DbAsync(db => db.DataApiKeys.Where(k => k.Id == key.Id).ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, DateTimeOffset.UtcNow), Ct));
        (await client.GetAsync("/v1/security/audit?limit=1", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The key permission claim only counts for keys: a token carrying it gets nothing from it.
        using var token = fixture.Client("security.read", claim: "ume_data_permission");
        (await token.GetAsync("/v1/security/audit?limit=1", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        // Access tokens keep working next to keys.
        using var oidc = fixture.Client("security.read");
        (await oidc.GetAsync("/v1/security/audit?limit=1", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
