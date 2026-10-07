using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ume.LlmGateway.Infrastructure.Security;

public static class OperationsSignature
{
    public const string Path = "/health/operations";
    public const string TimestampHeader = "x-ume-ops-timestamp";
    public const string SignatureHeader = "x-ume-ops-signature";

    public static string Sign(string pepper, long timestamp) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(pepper),
            Encoding.UTF8.GetBytes("Ume.LlmGateway.Operations.v1\nGET\n" + Path + "\n" + timestamp.ToString(CultureInfo.InvariantCulture))));

    public static bool Verify(string pepper, string timestamp, string signature, DateTimeOffset now)
    {
        var seconds = now.ToUnixTimeSeconds();
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value < seconds - 30 || value > seconds + 30 || signature.Length != 64)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(signature), Encoding.ASCII.GetBytes(Sign(pepper, value)));
    }
}

public sealed record UsageWriterStatus(int QueueDepth, int Capacity, int InFlightRecords, DateTimeOffset? LastWriteAt, int ConsecutiveFailures);
public sealed record GatewayOperationsStatus(string Status, string Version, UsageWriterStatus UsageWriter);
