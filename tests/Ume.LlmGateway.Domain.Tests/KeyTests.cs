using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;

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
        replacement.RequestsPerMinute.ShouldBe(60);
        replacement.TokensPerMinute.ShouldBe(10_000);
        replacement.CreatedBy.ShouldBe("admin@umea.se");
        replacement.GetStatus(Now).ShouldBe(KeyStatus.Active);
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
}
