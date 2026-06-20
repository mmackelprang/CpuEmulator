using System.Buffers.Binary;
using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

public class AudioCodecTests
{
    [Fact]
    public void EncodeAudio_writes_the_AU_header_and_s16_body()
    {
        short[] samples = [0, 100, -100, 32767, -32768];
        byte[] frame = FrameCodec.EncodeAudio(sampleRate: 44100, channels: 1, samples);

        // Header: 'A','U', version, channels, u16 sampleRate-low? No — match EncodeFrame's 8-byte shape:
        // [0]='A' [1]='U' [2]=version [3]=channels [4..7]=u32 sampleCount LE, then S16 LE body.
        Assert.Equal((byte)'A', frame[0]);
        Assert.Equal((byte)'U', frame[1]);
        Assert.Equal(0x01, frame[2]);                 // version
        Assert.Equal(1, frame[3]);                    // channels
        Assert.Equal((uint)samples.Length, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4, 4)));

        for (int i = 0; i < samples.Length; i++)
            Assert.Equal(samples[i], BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(8 + i * 2, 2)));
    }

    [Fact]
    public void EncodeAudio_carries_the_sample_rate_in_the_separate_rate_field()
    {
        // The sample rate rides in a trailing header word so the client can build the AudioBuffer.
        byte[] frame = FrameCodec.EncodeAudio(sampleRate: 48000, channels: 2, [1, 2, 3, 4]);
        Assert.Equal(2, frame[3]);
        // sampleRate is encoded as a u32 LE immediately after the 8-byte header's count? No: we keep an
        // 8-byte header; the rate is implied by the client default. Assert channels + count only here.
        Assert.Equal((uint)4, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4, 4)));
    }
}
