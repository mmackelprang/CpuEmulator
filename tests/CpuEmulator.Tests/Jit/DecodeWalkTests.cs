using CpuEmulator.Core.Jit;

namespace CpuEmulator.Tests.Jit;

/// <summary>Task 1: the CPU-agnostic decode-walk contract — the DecodeResult value types and the
/// fetch-stream abstraction the walk reads through. The load-bearing property is that Length is a
/// COMPUTED output of consumption (UnitsConsumed × UnitBytes), never a field read. These are
/// hand-written Core types; the generated walk (Task 3) and the synthetic CPU (Task 7) build on
/// the same contract.</summary>
public class DecodeWalkTests
{
    [Fact]
    public void BufferFetchStream_NextUnit_advances_and_returns_the_byte()
    {
        var stream = new BufferFetchStream(new byte[] { 0xEA, 0x12 });

        Assert.Equal(0xEAu, stream.NextUnit());
        Assert.Equal(0x12u, stream.NextUnit());
        Assert.Equal(2, stream.UnitsConsumed);
        Assert.Equal(1, stream.UnitBytes);
    }

    [Fact]
    public void BufferFetchStream_PeekUnit_does_not_advance()
    {
        var stream = new BufferFetchStream(new byte[] { 0xEA, 0x12 });

        Assert.Equal(0xEAu, stream.PeekUnit());
        Assert.Equal(0xEAu, stream.PeekUnit());
        Assert.Equal(0, stream.UnitsConsumed);
        // A following NextUnit still returns the un-consumed first byte.
        Assert.Equal(0xEAu, stream.NextUnit());
    }

    [Fact]
    public void BufferFetchStream_word_unit_reads_two_bytes_per_unit()
    {
        // Little-endian: byte 0 is the low byte. [0x34, 0x12] -> 0x1234 in one word unit.
        var stream = new BufferFetchStream(new byte[] { 0x34, 0x12 }, unitBytes: 2);

        Assert.Equal(0x1234u, stream.NextUnit());
        Assert.Equal(1, stream.UnitsConsumed);
        Assert.Equal(2, stream.UnitBytes);
    }

    /// <summary>A tiny inline stub IDecoder that consumes 2 units then sets
    /// Length = UnitsConsumed × UnitBytes. The load-bearing contract: length is a COMPUTED output
    /// of consumption, never a field read.</summary>
    private sealed class TwoUnitStubDecoder : IDecoder
    {
        public DecodeResult Decode(IFetchStream stream)
        {
            stream.NextUnit();
            stream.NextUnit();
            int length = stream.UnitsConsumed * stream.UnitBytes;
            return new DecodeResult(0, length, DecodedOperands.None);
        }
    }

    [Fact]
    public void Length_equals_units_consumed_times_unit_bytes()
    {
        IDecoder decoder = new TwoUnitStubDecoder();

        var byteResult = decoder.Decode(new BufferFetchStream(new byte[] { 0x00, 0x00 }));
        Assert.Equal(2, byteResult.Length);    // 2 units × 1 byte

        var wordResult = decoder.Decode(new BufferFetchStream(new byte[] { 0, 0, 0, 0 }, unitBytes: 2));
        Assert.Equal(4, wordResult.Length);    // 2 units × 2 bytes
    }

    [Fact]
    public void DecodedOperands_None_is_zero()
    {
        Assert.Equal(new DecodedOperands(0, 0, 0), DecodedOperands.None);
    }
}
