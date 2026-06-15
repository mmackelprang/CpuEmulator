namespace CpuEmulator.Core.Jit;

/// <summary>
/// The live 68000 instruction fetch stream: word-granular (UnitBytes == 2), big-endian, over a uint-wide
/// PC origin. The generated word-granular Decode(IFetchStream) walk consumes the operword + extension words
/// through this. Distinct from AddressSpaceFetchStream (byte-only, ushort PC — Recon §D): the 68000 fetches
/// 16-bit big-endian words from a 24-bit address. The bus is constructed BigEndian (M4.2), so Read16 already
/// composes high-byte-first — this stream just walks it word by word.
/// </summary>
public sealed class M68000FetchStream : IFetchStream
{
    private readonly IAddressSpace _bus;
    private readonly uint _origin;
    private int _offset;   // in WORDS

    public M68000FetchStream(IAddressSpace bus, uint origin)
    {
        _bus = bus;
        _origin = origin;
    }

    public int UnitBytes => 2;
    public int UnitsConsumed => _offset;

    public uint NextUnit()
    {
        ushort word = _bus.Read16(unchecked(_origin + (uint)(_offset * 2)));
        _offset++;
        return word;
    }

    public uint PeekUnit() => _bus.Read16(unchecked(_origin + (uint)(_offset * 2)));
}
