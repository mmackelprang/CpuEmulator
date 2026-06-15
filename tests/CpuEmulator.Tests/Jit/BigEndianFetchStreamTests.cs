using CpuEmulator.Core.Jit;
using Xunit;

namespace CpuEmulator.Tests.Jit;

public class BigEndianFetchStreamTests
{
    [Fact]
    public void Big_endian_word_reads_high_byte_first()
    {
        // bytes 0x12 0x34 → big-endian word 0x1234 (high byte first).
        var s = new BufferFetchStream(new byte[] { 0x12, 0x34, 0x56, 0x78 }, unitBytes: 2, bigEndian: true);
        Assert.Equal(0x1234u, s.NextUnit());
        Assert.Equal(0x5678u, s.NextUnit());
        Assert.Equal(2, s.UnitsConsumed);
        Assert.Equal(4, s.UnitsConsumed * s.UnitBytes);   // COMPUTED byte length
    }

    [Fact]
    public void Big_endian_peek_does_not_advance()
    {
        var s = new BufferFetchStream(new byte[] { 0x12, 0x34 }, unitBytes: 2, bigEndian: true);
        Assert.Equal(0x1234u, s.PeekUnit());
        Assert.Equal(0, s.UnitsConsumed);
        Assert.Equal(0x1234u, s.NextUnit());
    }

    [Fact]
    public void Little_endian_word_default_is_unchanged()
    {
        // The existing little-endian word path (byte 0 is the LOW byte) is untouched.
        var s = new BufferFetchStream(new byte[] { 0x12, 0x34 }, unitBytes: 2);
        Assert.Equal(0x3412u, s.NextUnit());
    }

    [Fact]
    public void Byte_path_is_unaffected()
    {
        var s = new BufferFetchStream(new byte[] { 0xAB, 0xCD });
        Assert.Equal(0xABu, s.NextUnit());
        Assert.Equal(0xCDu, s.NextUnit());
    }
}
