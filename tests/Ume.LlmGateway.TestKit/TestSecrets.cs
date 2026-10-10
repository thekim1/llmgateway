using System.Security.Cryptography;

namespace Ume.LlmGateway.TestKit;

public static class TestSecrets
{
    /// <summary>A random secret as hex: a key pepper, provider credential or password (32 bytes by default).</summary>
    public static string RandomHex(int bytes = 32) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes));
}
