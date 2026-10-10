using System.Buffers.Binary;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Domain.Tests;

public class AudioDurationTests
{
    private static byte[] Wav(int byteRate, int dataLength, int declaredLength)
    {
        var wav = new byte[44 + dataLength];
        "RIFF"u8.CopyTo(wav);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), byteRate);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), (uint)declaredLength);
        return wav;
    }

    [Fact]
    public void Wav_length_comes_from_the_header()
    {
        AudioDuration.Estimate(Wav(32_000, 80_000, 80_000)).ShouldBe(2.5m);
        // Streamed writers leave the data size at 0 or 0xFFFFFFFF: the data runs to the end of the file.
        AudioDuration.Estimate(Wav(32_000, 64_000, 0)).ShouldBe(2m);
        AudioDuration.Estimate(Wav(32_000, 64_000, -1)).ShouldBe(2m);
    }

    [Fact]
    public void Flac_length_comes_from_streaminfo()
    {
        var flac = new byte[64];
        "fLaC"u8.CopyTo(flac);
        flac[4] = 0x80; // last metadata block, type 0 (STREAMINFO)
        var info = flac.AsSpan(8);
        // 44 100 Hz (20 bits), then channels and bits per sample, then 36 bits of total samples: 441 000 = 10 s.
        info[10] = 0x0A;
        info[11] = 0xC4;
        info[12] = 0x42;
        info[13] = 0xF0;
        BinaryPrimitives.WriteUInt32BigEndian(info[14..], 441_000);
        AudioDuration.Estimate(flac).ShouldBe(10m);
    }

    [Fact]
    public void Mp3_length_comes_from_the_first_frame_bitrate_after_id3()
    {
        var mp3 = new byte[10 + 20 + 160_000];
        "ID3"u8.CopyTo(mp3);
        mp3[9] = 20; // tag body size
        mp3[30] = 0xFF;
        mp3[31] = 0xFB; // MPEG-1 Layer III
        mp3[32] = 0x90; // 128 kbps
        AudioDuration.Estimate(mp3).ShouldBe(10m); // 160 000 bytes at 16 000 bytes/s
    }

    [Fact]
    public void Other_formats_assume_32_kbps()
    {
        AudioDuration.Estimate(new byte[40_000]).ShouldBe(10m);
        AudioDuration.Estimate([]).ShouldBe(0m);
    }
}
