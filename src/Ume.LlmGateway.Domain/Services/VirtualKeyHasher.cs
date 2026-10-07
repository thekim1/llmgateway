using System.Security.Cryptography;
using System.Text;

namespace Ume.LlmGateway.Domain.Services;

public sealed record GeneratedKey(string PlainText, string Prefix, string Hash);

/// <summary>
/// Generates and verifies virtual keys. Format: <c>ume-sk-</c> + 43 base62 chars (~256 bits entropy).
/// Keys are stored as HMAC-SHA256(pepper, key) so a database leak alone cannot be used to authenticate,
/// and lookups are O(1) by hash.
/// </summary>
public sealed class VirtualKeyHasher
{
    public const string KeyPrefix = "ume-sk-";
    private const string Base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const int RandomChars = 43;
    private static readonly System.Buffers.SearchValues<char> Base62Values = System.Buffers.SearchValues.Create(Base62);
    private const int DisplayPrefixLength = 11; // "ume-sk-" + 4 chars

    private readonly byte[] _pepper;

    public VirtualKeyHasher(byte[] pepper)
    {
        ArgumentNullException.ThrowIfNull(pepper);
        if (pepper.Length < 32)
        {
            throw new ArgumentException("The key pepper must be at least 32 bytes.", nameof(pepper));
        }

        _pepper = pepper;
    }

    public GeneratedKey Generate()
    {
        var chars = new char[RandomChars];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Base62[RandomNumberGenerator.GetInt32(Base62.Length)];
        }

        var key = KeyPrefix + new string(chars);
        return new GeneratedKey(key, key[..DisplayPrefixLength], Hash(key));
    }

    public string Hash(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var mac = HMACSHA256.HashData(_pepper, Encoding.UTF8.GetBytes(key));
        return Convert.ToHexStringLower(mac);
    }

    public bool Verify(string presentedKey, string storedHash)
    {
        if (!LooksLikeKey(presentedKey))
        {
            return false;
        }

        var computed = Encoding.ASCII.GetBytes(Hash(presentedKey));
        var stored = Encoding.ASCII.GetBytes(storedHash);
        return CryptographicOperations.FixedTimeEquals(computed, stored);
    }

    public static bool LooksLikeKey(string? value) =>
        value is { Length: > 0 }
        && value.Length == KeyPrefix.Length + RandomChars
        && value.StartsWith(KeyPrefix, StringComparison.Ordinal)
        && value.AsSpan(KeyPrefix.Length).IndexOfAnyExcept(Base62Values) < 0;
}
