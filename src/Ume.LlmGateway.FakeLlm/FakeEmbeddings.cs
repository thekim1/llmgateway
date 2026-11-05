using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Ume.LlmGateway.FakeLlm;

/// <summary>
/// Deterministic embeddings for development and tests. Without a size the vector is the 8 values of the original fake
/// (unchanged, so existing tests and benchmarks see the same output). With a size, from the OpenAI <c>dimensions</c>
/// parameter or a model name ending in <c>-&lt;n&gt;</c> (<c>fake-embed-1024</c>), similar texts get similar vectors:
/// feature hashing of lower-cased words and character trigrams, L2-normalised, the same on every machine.
/// </summary>
public static class FakeEmbeddings
{
    /// <summary>The largest size accepted, like OpenAI's largest embedding model (3 072) with room to spare.</summary>
    public const int MaxDimensions = 4096;

    /// <summary>Size of the original fake vector.</summary>
    public const int DefaultDimensions = 8;

    /// <summary>
    /// The size asked for: <paramref name="dimensions"/> (the request parameter) first, then a <c>-&lt;n&gt;</c> suffix of the
    /// model name, else null (the original 8-value vector). False when the parameter is not an integer from 1 to
    /// <see cref="MaxDimensions"/> (OpenAI answers 400).
    /// </summary>
    public static bool TryGetDimensions(JsonNode? dimensions, string model, out int? size)
    {
        ArgumentNullException.ThrowIfNull(model);
        size = null;
        if (dimensions is not null)
        {
            if (dimensions is JsonValue value && value.TryGetValue<int>(out var n) && n is >= 1 and <= MaxDimensions)
            {
                size = n;
                return true;
            }

            return false;
        }

        var dash = model.LastIndexOf('-');
        if (dash >= 0 && int.TryParse(model.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var suffix)
            && suffix is >= 1 and <= MaxDimensions)
        {
            size = suffix;
        }

        return true;
    }

    /// <summary>The vector for <paramref name="text"/>: the original 8 values when <paramref name="size"/> is null.</summary>
    public static float[] Vector(string text, int? size)
    {
        ArgumentNullException.ThrowIfNull(text);
        return size is { } n ? Hashed(text, n) : Original(text);
    }

    /// <summary>The vector as the client asked for it: a JSON array of floats, or little-endian float32 bytes in base64.</summary>
    public static object Encode(float[] vector, bool base64)
    {
        ArgumentNullException.ThrowIfNull(vector);
        if (!base64)
        {
            return vector;
        }

        var bytes = new byte[vector.Length * sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        }

        return Convert.ToBase64String(bytes);
    }

    private static float[] Original(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return [.. hash.Take(DefaultDimensions).Select(b => (b - 128) / 128f)];
    }

    private static float[] Hashed(string text, int size)
    {
        var vector = new double[size];
        var lower = text.ToLowerInvariant();
        foreach (var word in lower.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            Add(vector, "w:" + word);
        }

        var padded = " " + lower + " ";
        for (var i = 0; i + 3 <= padded.Length; i++)
        {
            Add(vector, string.Concat("c:", padded.AsSpan(i, 3)));
        }

        var norm = Math.Sqrt(vector.Sum(v => v * v));
        if (norm == 0)
        {
            vector[0] = 1; // empty text: a unit vector all the same
            norm = 1;
        }

        return [.. vector.Select(v => (float)(v / norm))];
    }

    /// <summary>Feature hashing: the feature's bucket and sign come from one FNV-1a 64 hash of its UTF-8 bytes.</summary>
    private static void Add(double[] vector, string feature)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(feature))
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        vector[(int)(hash % (ulong)vector.Length)] += (hash >> 63) == 0 ? 1 : -1;
    }
}
