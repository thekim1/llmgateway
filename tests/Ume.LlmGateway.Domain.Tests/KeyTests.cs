using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Keys;

namespace Ume.LlmGateway.Domain.Tests;

public class VirtualKeyHasherTests
{
    private static readonly VirtualKeyHasher Hasher = new(new byte[32]);

    [Fact]
    public void Generate_produces_prefixed_key_with_matching_hash()
    {
        var key = Hasher.Generate();

        key.PlainText.ShouldStartWith("ume-sk-");
        key.PlainText.Length.ShouldBe(50);
        key.Prefix.ShouldBe(key.PlainText[..11]);
        key.Hash.ShouldBe(Hasher.Hash(key.PlainText));
        key.Hash.ShouldNotContain(key.PlainText);
        VirtualKeyHasher.LooksLikeKey(key.PlainText).ShouldBeTrue();
    }

    [Fact]
    public void Generated_keys_are_unique()
    {
        var keys = Enumerable.Range(0, 1000).Select(_ => Hasher.Generate().PlainText).ToHashSet();
        keys.Count.ShouldBe(1000);
    }

    [Fact]
    public void Verify_accepts_correct_key_and_rejects_others()
    {
        var key = Hasher.Generate();
        var other = Hasher.Generate();

        Hasher.Verify(key.PlainText, key.Hash).ShouldBeTrue();
        Hasher.Verify(other.PlainText, key.Hash).ShouldBeFalse();
        Hasher.Verify("not-a-key", key.Hash).ShouldBeFalse();
    }

    [Fact]
    public void Hash_depends_on_pepper()
    {
        var pepper2 = Enumerable.Repeat((byte)7, 32).ToArray();
        var key = Hasher.Generate();
        new VirtualKeyHasher(pepper2).Hash(key.PlainText).ShouldNotBe(key.Hash);
    }

    [Fact]
    public void Short_pepper_is_rejected() =>
        Should.Throw<ArgumentException>(() => new VirtualKeyHasher(new byte[16]));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sk-openai-123")]
    [InlineData("ume-sk-short")]
    [InlineData("ume-sk-!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")]
    public void LooksLikeKey_rejects_malformed(string? value) =>
        VirtualKeyHasher.LooksLikeKey(value).ShouldBeFalse();
}

public class KeyStatusAndRotationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly VirtualKeyHasher Hasher = new(new byte[32]);

    private static VirtualKey NewKey() => new()
    {
        Name = "Bygglov-app",
        Prefix = "ume-sk-abcd",
        KeyHash = "hash",
        TeamId = Guid.NewGuid(),
        AllowedModels = ["ume/chat-standard"],
        AllowedResidencies = [DataResidency.OnPrem],
        AllowedProviders = ["ollama-cloud"],
        PiiPolicy = PiiPolicy.RerouteToOnPrem,
        RequestsPerMinute = 60,
        TokensPerMinute = 10_000,
    };

    [Fact]
    public void Status_query_agrees_with_GetStatus_for_every_combination()
    {
        DateTimeOffset?[] times = [null, Now.AddDays(-1), Now, Now.AddDays(1)];
        var conditions = Enum.GetValues<KeyStatus>().ToDictionary(s => s, s => VirtualKey.HasStatus(s, Now).Compile());
        var checkedKeys = 0;
        foreach (var revoked in times)
        foreach (var enabled in new[] { true, false })
        foreach (var expires in times)
        foreach (var grace in times)
        {
            var key = NewKey();
            (key.RevokedAt, key.IsEnabled, key.ExpiresAt, key.GraceUntil) = (revoked, enabled, expires, grace);
            var status = key.GetStatus(Now);
            foreach (var (candidate, matches) in conditions)
            {
                matches(key).ShouldBe(candidate == status, $"{candidate} for revoked={revoked}, enabled={enabled}, expires={expires}, grace={grace} (is {status})");
            }

            checkedKeys++;
        }

        checkedKeys.ShouldBe(128);
    }

    [Fact]
    public void Status_reflects_flags_and_dates()
    {
        var key = NewKey();
        key.GetStatus(Now).ShouldBe(KeyStatus.Active);

        key.ExpiresAt = Now.AddMinutes(-1);
        key.GetStatus(Now).ShouldBe(KeyStatus.Expired);

        key.ExpiresAt = null;
        key.IsEnabled = false;
        key.GetStatus(Now).ShouldBe(KeyStatus.Disabled);
        key.IsUsable(Now).ShouldBeFalse();

        key.IsEnabled = true;
        key.RevokedAt = Now;
        key.GetStatus(Now).ShouldBe(KeyStatus.Revoked);
    }

    [Fact]
    public void Rotate_immediately_revokes_old_key_and_copies_policy()
    {
        var old = NewKey();
        old.RequireMaxResidency = true;
        var generated = Hasher.Generate();

        var replacement = KeyRotation.Rotate(old, generated, KeyRotationMode.RevokeImmediately, Now, "admin@umea.se");

        old.GetStatus(Now).ShouldBe(KeyStatus.Revoked);
        old.RotatedToKeyId.ShouldBe(replacement.Id);
        replacement.KeyHash.ShouldBe(generated.Hash);
        replacement.Prefix.ShouldBe(generated.Prefix);
        replacement.TeamId.ShouldBe(old.TeamId);
        replacement.AllowedModels.ShouldBe(old.AllowedModels);
        replacement.AllowedModels.ShouldNotBeSameAs(old.AllowedModels);
        replacement.AllowedResidencies.ShouldBe(old.AllowedResidencies);
        replacement.AllowedProviders.ShouldBe(old.AllowedProviders);
        replacement.AllowedProviders.ShouldNotBeSameAs(old.AllowedProviders);
        replacement.PiiPolicy.ShouldBe(PiiPolicy.RerouteToOnPrem);
        replacement.RequireMaxResidency.ShouldBeTrue();
        replacement.RequestsPerMinute.ShouldBe(60);
        replacement.TokensPerMinute.ShouldBe(10_000);
        replacement.CreatedBy.ShouldBe("admin@umea.se");
        replacement.GetStatus(Now).ShouldBe(KeyStatus.Active);
    }

    [Fact]
    public void Rotating_a_credential_key_keeps_its_integration_and_scopes()
    {
        var old = NewKey();
        var integration = Guid.NewGuid();
        old.IntegrationId = integration;
        old.IntegrationRole = IntegrationRole.Credential;
        old.IntegrationScopes = ["usage.read"];

        var replacement = KeyRotation.Rotate(old, Hasher.Generate(), KeyRotationMode.RevokeImmediately, Now, null);

        replacement.IntegrationId.ShouldBe(integration);
        replacement.IntegrationRole.ShouldBe(IntegrationRole.Credential);
        replacement.IntegrationScopes.ShouldBe(["usage.read"]);
        replacement.IntegrationScopes.ShouldNotBeSameAs(old.IntegrationScopes);
    }

    [Fact]
    public void Assigned_keys_are_found_by_id_on_their_credential_key()
    {
        var credential = NewKey();
        var chatbot = NewKey();
        credential.AssignedKey(chatbot.Id).ShouldBeNull();
        credential.IndexAssignedKeys([chatbot]);
        credential.AssignedKey(chatbot.Id).ShouldBeSameAs(chatbot);
        credential.AssignedKey(Guid.NewGuid()).ShouldBeNull();
        credential.AssignedKeys.ShouldBe([chatbot]);
    }

    [Fact]
    public void Rotate_with_grace_keeps_old_key_working_for_24_hours()
    {
        var old = NewKey();
        KeyRotation.Rotate(old, Hasher.Generate(), KeyRotationMode.Grace24Hours, Now, null);

        old.GetStatus(Now).ShouldBe(KeyStatus.InGracePeriod);
        old.IsUsable(Now.AddHours(23).AddMinutes(59)).ShouldBeTrue();
        old.GetStatus(Now.AddHours(24)).ShouldBe(KeyStatus.Revoked);
        old.IsUsable(Now.AddHours(24)).ShouldBeFalse();
    }

    [Fact]
    public void Revoked_key_cannot_be_rotated()
    {
        var old = NewKey();
        KeyRotation.Revoke(old, Now);
        Should.Throw<InvalidOperationException>(() => KeyRotation.Rotate(old, Hasher.Generate(), KeyRotationMode.RevokeImmediately, Now, null));
    }

    [Fact]
    public void Revoking_a_key_in_grace_period_stops_it_immediately()
    {
        var old = NewKey();
        KeyRotation.Rotate(old, Hasher.Generate(), KeyRotationMode.Grace24Hours, Now, null);
        KeyRotation.Revoke(old, Now.AddHours(1));
        old.IsUsable(Now.AddHours(1)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("", "OnPrem,Eu", "OnPrem,Eu", false)] // a credential key without a list bounds nothing
    [InlineData("OnPrem,Eu", "", "OnPrem,Eu", false)] // an assigned key without a list gets the credential key's
    [InlineData("OnPrem", "OnPrem,Eu", "OnPrem", false)]
    [InlineData("OnPrem,Eu", "Eu,External", "Eu", false)]
    [InlineData("OnPrem", "Eu", "Eu", true)] // nothing in common: kept (empty would mean all) and flagged
    public void An_assigned_keys_residencies_are_bounded_by_the_credential_keys(string credential, string assigned, string expected, bool none)
    {
        static List<DataResidency> Parse(string list) => [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Enum.Parse<DataResidency>)];
        var via = new VirtualKey { Name = "c", Prefix = "p", KeyHash = "h", AllowedResidencies = Parse(credential) };
        var key = new VirtualKey { Name = "a", Prefix = "p", KeyHash = "h", AllowedResidencies = Parse(assigned) };

        key.BoundBy(via);

        key.AllowedResidencies.ShouldBe(Parse(expected));
        key.OwnAllowedResidencies.ShouldBe(Parse(assigned));
        key.NoCommonResidency.ShouldBe(none);
    }
}
