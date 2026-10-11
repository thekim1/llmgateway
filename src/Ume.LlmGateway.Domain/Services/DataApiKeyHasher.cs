using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace Ume.LlmGateway.Domain.Services;

/// <summary>
/// Generates and hashes Data API keys. Format: <c>ume-dk-</c> + 43 base62 characters (about 256 bits of entropy), so a
/// Data API key can never be mistaken for a gateway key (<c>ume-sk-</c>) and secret scanners can tell them apart.
/// Stored as SHA-256 of the key. Unlike gateway keys no pepper is mixed in: the Data API may run in another network
/// zone and should not hold the gateway's pepper, and a pepper adds nothing against offline guessing of a 256-bit
/// random secret. A database copy alone therefore cannot be turned back into a working key.
/// </summary>
public static class DataApiKeyHasher
{
    public const string KeyPrefix = "ume-dk-";
    private const string Base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const int RandomChars = 43;
    private const int DisplayPrefixLength = 11; // "ume-dk-" + 4 chars
    private static readonly SearchValues<char> Base62Values = SearchValues.Create(Base62);

    public static GeneratedKey Generate()
    {
        var chars = new char[RandomChars];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Base62[RandomNumberGenerator.GetInt32(Base62.Length)];
        }

        var key = KeyPrefix + new string(chars);
        return new GeneratedKey(key, key[..DisplayPrefixLength], Hash(key));
    }

    /// <summary>Lower-case hex SHA-256 of the key (64 characters).</summary>
    public static string Hash(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    /// <summary>True when the value has the shape of a Data API key; says nothing about whether the key exists.</summary>
    public static bool LooksLikeKey(string? value) =>
        value is not null && value.Length == KeyPrefix.Length + RandomChars
        && value.StartsWith(KeyPrefix, StringComparison.Ordinal)
        && value.AsSpan(KeyPrefix.Length).IndexOfAnyExcept(Base62Values) < 0;
}
