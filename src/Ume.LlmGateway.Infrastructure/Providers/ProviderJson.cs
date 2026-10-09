using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Infrastructure.Providers;

/// <summary>
/// Forward-only readers for the few fields the gateway needs from provider JSON. They replace building a full
/// <see cref="JsonNode"/> tree for every response and every streamed chunk.
/// </summary>
internal static class ProviderJson
{
    private const int MaxTrackedDepth = 8;
    private const byte Other = 0, Choices = 1, Delta = 2, Content = 3, Text = 4;

    /// <summary>
    /// Validates a complete JSON document and reads token usage from its top-level <c>usage</c> object, skipping
    /// everything else without materialising it. Throws <see cref="JsonException"/> for invalid JSON.
    /// </summary>
    public static TokenUsage ReadUsage(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        TokenUsage usage = default;
        if (!reader.Read())
        {
            throw new JsonException("Empty response body.");
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
        }
        else
        {
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isUsage = reader.ValueTextEquals("usage"u8);
                reader.Read();
                if (isUsage && reader.TokenType == JsonTokenType.StartObject)
                {
                    UsageParser.TryRead(JsonNode.Parse(ref reader), out usage);
                }
                else
                {
                    reader.Skip();
                }
            }
        }

        // Anything after the root value is invalid JSON (the reader throws).
        while (reader.Read())
        {
        }

        return usage;
    }

    /// <summary>Characters of generated text in an OpenAI chunk: <c>choices[].delta.content</c> (chat) or a top-level <c>delta</c> string (Responses API).</summary>
    public static long OpenAIContentLength(string json) => TextLength(json, anthropic: false);

    /// <summary>Characters of generated text in an Anthropic <c>content_block_delta</c> event: <c>delta.text</c>.</summary>
    public static long AnthropicTextLength(string json) => TextLength(json, anthropic: true);

    /// <summary>Length in UTF-16 chars (as <see cref="string.Length"/> of the decoded value); 0 for invalid JSON.</summary>
    private static long TextLength(string json, bool anthropic)
    {
        byte[]? rented = null;
        var maxBytes = Encoding.UTF8.GetMaxByteCount(json.Length);
        Span<byte> utf8 = maxBytes <= 1024 ? stackalloc byte[1024] : (rented = ArrayPool<byte>.Shared.Rent(maxBytes));
        Span<byte> names = stackalloc byte[MaxTrackedDepth + 1];
        Span<char> unescaped = stackalloc char[256];
        try
        {
            var reader = new Utf8JsonReader(utf8[..Encoding.UTF8.GetBytes(json, utf8)]);
            long length = 0;
            while (reader.Read())
            {
                var depth = reader.CurrentDepth;
                if (depth >= MaxTrackedDepth)
                {
                    continue;
                }

                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        names[depth] = reader.ValueTextEquals("choices"u8) ? Choices
                            : reader.ValueTextEquals("delta"u8) ? Delta
                            : reader.ValueTextEquals("content"u8) ? Content
                            : reader.ValueTextEquals("text"u8) ? Text
                            : Other;
                        break;
                    case JsonTokenType.StartObject or JsonTokenType.StartArray:
                        names[depth + 1] = Other; // array elements have no property name
                        break;
                    case JsonTokenType.String when anthropic
                        ? depth == 2 && names[1] == Delta && names[2] == Text
                        : (depth == 4 && names[1] == Choices && names[3] == Delta && names[4] == Content) || (depth == 1 && names[1] == Delta):
                        length += !reader.ValueIsEscaped ? Encoding.UTF8.GetCharCount(reader.ValueSpan)
                            : reader.ValueSpan.Length <= unescaped.Length ? reader.CopyString(unescaped)
                            : reader.GetString()!.Length;
                        break;
                }
            }

            return length;
        }
        catch (JsonException)
        {
            return 0;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>Reads the whole response body into a rented array. The caller returns it to <see cref="ArrayPool{T}.Shared"/>.</summary>
    public static async Task<(byte[] Buffer, int Length)> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(content.Headers.ContentLength is > 0 and < int.MaxValue - 1 and var n ? (int)n + 1 : 16 * 1024);
        var length = 0;
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken)) > 0)
            {
                length += read;
                if (length == buffer.Length)
                {
                    var larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    buffer.AsSpan(0, length).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }
            }

            return (buffer, length);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }
}
