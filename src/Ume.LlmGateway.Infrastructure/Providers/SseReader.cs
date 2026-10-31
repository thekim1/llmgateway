using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace Ume.LlmGateway.Infrastructure.Providers;

public readonly record struct SseEvent(string? EventName, string Data)
{
    public const string Done = "[DONE]";

    public string Format()
    {
        var buffer = new ArrayBufferWriter<byte>();
        WriteTo(buffer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Writes the event in SSE wire format as UTF-8 straight into <paramref name="writer"/> (no intermediate string).</summary>
    public void WriteTo(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (EventName is not null)
        {
            writer.Write("event: "u8);
            Encoding.UTF8.GetBytes(EventName, writer);
            writer.Write("\n"u8);
        }

        var data = Data.AsSpan();
        while (true)
        {
            var newline = data.IndexOf('\n');
            writer.Write("data: "u8);
            Encoding.UTF8.GetBytes(newline < 0 ? data : data[..newline], writer);
            writer.Write("\n"u8);
            if (newline < 0)
            {
                break;
            }

            data = data[(newline + 1)..];
        }

        writer.Write("\n"u8);
    }
}

public static class SseReader
{
    public static async IAsyncEnumerable<SseEvent> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? eventName = null;
        // Almost every event has a single data line: keep it as one substring and only use the builder for more.
        string? data = null;
        StringBuilder? multiline = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (data is not null)
                {
                    yield return new SseEvent(eventName, multiline is { Length: > 0 } ? multiline.ToString() : data);
                }

                eventName = null;
                data = null;
                multiline?.Clear();
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var field = colon < 0 ? line.AsSpan() : line.AsSpan(0, colon);
            var valueStart = colon < 0 ? line.Length : colon + 1;
            if (valueStart < line.Length && line[valueStart] == ' ')
            {
                valueStart++;
            }

            if (field.SequenceEqual("data"))
            {
                if (data is null)
                {
                    data = line[valueStart..];
                }
                else
                {
                    multiline ??= new StringBuilder();
                    if (multiline.Length == 0)
                    {
                        multiline.Append(data);
                    }

                    multiline.Append('\n').Append(line, valueStart, line.Length - valueStart);
                }
            }
            else if (field.SequenceEqual("event"))
            {
                eventName = line[valueStart..];
            }
        }

        if (data is not null)
        {
            yield return new SseEvent(eventName, multiline is { Length: > 0 } ? multiline.ToString() : data);
        }
    }
}
