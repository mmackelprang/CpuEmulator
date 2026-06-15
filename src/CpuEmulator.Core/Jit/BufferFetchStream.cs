namespace CpuEmulator.Core.Jit;

/// <summary>An IFetchStream over an in-memory byte buffer — the test/monitor stream (the
/// disassembler + InstructionLength are handed an instruction's bytes, not a live bus). UnitBytes
/// defaults to 1 (byte-granular: 6502/Z80/8086); a ctor arg sets it to 2 so the word-unit
/// micro-proof (Ground truth D / F.2) exercises a 68000-shaped fetch without a 68000. By default
/// NextUnit reads UnitBytes bytes LITTLE-endian; the M4.3a <paramref name="bigEndian"/> flag (RECON-
/// FINDING C2) composes a 2-byte unit HIGH-byte-first instead — the 68000's big-endian operword the
/// synthetic field-decode proof feeds. Length = UnitsConsumed × UnitBytes throughout.</summary>
public sealed class BufferFetchStream : IFetchStream
{
    private readonly System.ReadOnlyMemory<byte> _buffer;
    private readonly bool _bigEndian;
    private int _byteCursor;

    public BufferFetchStream(System.ReadOnlyMemory<byte> buffer, int unitBytes = 1, bool bigEndian = false)
    {
        if (unitBytes is not (1 or 2))
            throw new System.ArgumentOutOfRangeException(nameof(unitBytes), "fetch unit must be 1 or 2 bytes");
        _buffer = buffer;
        UnitBytes = unitBytes;
        _bigEndian = bigEndian;
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
        for (int i = 0; i < UnitBytes; i++)
        {
            // LE (default): byte 0 is the LOW byte. BE (M4.3a, C2): byte 0 is the HIGH byte.
            int shift = _bigEndian ? 8 * (UnitBytes - 1 - i) : 8 * i;
            v |= (uint)b[_byteCursor + i] << shift;
        }
        return v;
    }
}
