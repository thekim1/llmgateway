using System.Buffers.Binary;

namespace Ume.LlmGateway.Domain.Services;

/// <summary>
/// Estimates how long an uploaded audio file plays, without decoding it. Used to reserve budget before a transcription
/// and to bill one when the provider reports neither duration nor tokens. WAV and FLAC headers give the exact length,
/// MP3 the bitrate of its first frame; anything else (M4A, OGG, Opus, WebM …) is assumed to be 32 kbps, which
/// over-estimates nearly every real recording.
/// </summary>
public static class AudioDuration
{
    /// <summary>Bytes per second assumed for formats whose length is not read from the header (32 kbps).</summary>
    public const int FallbackBytesPerSecond = 4000;

    public static decimal Estimate(ReadOnlySpan<byte> file) =>
        file.IsEmpty ? 0 : Math.Round(Wav(file) ?? Flac(file) ?? Mp3(file) ?? (decimal)file.Length / FallbackBytesPerSecond, 3);

    private static decimal? Wav(ReadOnlySpan<byte> file)
    {
        if (file.Length < 12 || !file[..4].SequenceEqual("RIFF"u8) || !file[8..12].SequenceEqual("WAVE"u8))
        {
            return null;
        }

        uint byteRate = 0;
        var position = 12;
        while (position + 8 <= file.Length)
        {
            var id = file.Slice(position, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(position + 4, 4));
            var body = position + 8;
            if (id.SequenceEqual("fmt "u8) && body + 12 <= file.Length)
            {
                byteRate = BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(body + 8, 4));
            }
            else if (id.SequenceEqual("data"u8))
            {
                // Streamed WAV writers leave the size at 0 or 0xFFFFFFFF: the data then runs to the end of the file.
                long data = size == 0 || size > file.Length - body ? file.Length - body : size;
                return byteRate > 0 ? (decimal)data / byteRate : null;
            }

            position = body + (int)Math.Min(size + (size & 1), int.MaxValue - body);
        }

        return null;
    }

    private static decimal? Flac(ReadOnlySpan<byte> file)
    {
        // "fLaC", a 4-byte metadata block header, then STREAMINFO: sample rate (20 bits) and total samples (36 bits) at bytes 10-17.
        if (file.Length < 26 || !file[..4].SequenceEqual("fLaC"u8) || (file[4] & 0x7F) != 0)
        {
            return null;
        }

        var info = file.Slice(8, 18);
        var sampleRate = (info[10] << 12) | (info[11] << 4) | (info[12] >> 4);
        var samples = ((long)(info[13] & 0x0F) << 32) | BinaryPrimitives.ReadUInt32BigEndian(info.Slice(14, 4));
        return sampleRate > 0 && samples > 0 ? (decimal)samples / sampleRate : null;
    }

    private static readonly int[] Mpeg1Layer3Kbps = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
    private static readonly int[] Mpeg2Layer3Kbps = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];

    private static decimal? Mp3(ReadOnlySpan<byte> file)
    {
        var start = 0;
        if (file.Length >= 10 && file[..3].SequenceEqual("ID3"u8))
        {
            // ID3v2 size: four 7-bit bytes, plus the 10-byte header.
            start = 10 + ((file[6] & 0x7F) << 21 | (file[7] & 0x7F) << 14 | (file[8] & 0x7F) << 7 | (file[9] & 0x7F));
        }

        // The first frame sync within a few KB; variable-bitrate files are estimated from that frame.
        var end = Math.Min(file.Length - 3, start + 8192);
        for (var i = start; i < end; i++)
        {
            if (file[i] != 0xFF || (file[i + 1] & 0xE6) != 0xE2)
            {
                continue; // not sync + Layer III
            }

            var mpeg1 = (file[i + 1] & 0x18) == 0x18;
            var kbps = (mpeg1 ? Mpeg1Layer3Kbps : Mpeg2Layer3Kbps)[file[i + 2] >> 4];
            if (kbps > 0)
            {
                return (decimal)(file.Length - i) * 8 / (kbps * 1000);
            }
        }

        return null;
    }
}
