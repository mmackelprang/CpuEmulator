namespace CpuEmulator.Core.Jit;

/// <summary>An IFetchStream over an in-memory byte buffer — the test/monitor stream (the
/// disassembler + InstructionLength are handed an instruction's bytes, not a live bus). UnitBytes
/// defaults to 1 (byte-granular: 6502/Z80/8086); a ctor arg sets it to 2 so the word-unit
/// micro-proof (Ground truth D / F.2) exercises a 68000-shaped fetch without a 68000. NextUnit
/// reads UnitBytes bytes little-endian and advances one unit; Length = UnitsConsumed × UnitBytes.</summary>
public sealed class BufferFetchStream : IFetchStream
{
    private readonly System.ReadOnlyMemory<byte> _buffer;
    private int _byteCursor;

    public BufferFetchStream(System.ReadOnlyMemory<byte> buffer, int unitBytes = 1)
    {
        if (unitBytes is not (1 or 2))
            throw new System.ArgumentOutOfRangeException(nameof(unitBytes), "fetch unit must be 1 or 2 bytes");
        _buffer = buffer;
        UnitBytes = unitBytes;
    }

    public int UnitBytes { get; }
    public int UnitsConsumed => _byteCursor / UnitBytes;

    public uint NextUnit()
    {
        uint v = PeekUnit();
        _byteCursor += UnitBytes;
        return v;
    }

    public uint PeekUnit()
    {
        System.ReadOnlySpan<byte> b = _buffer.Span;
        uint v = 0;
        for (int i = 0; i < UnitBytes; i++)            // little-endian: byte 0 is the low byte
            v |= (uint)b[_byteCursor + i] << (8 * i);
        return v;
    }
}
