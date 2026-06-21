namespace CpuEmulator.Core;

/// <summary>An IAddressSpace the coprocessor is constructed over (ADR 0015 Decision 2): every access
/// is translated (IAddressTranslation.ToPhysical) then forwarded to the inner primary program
/// AddressSpace. Read8/Write8/TryPeek8 route through ToPhysical; the default-interface wide accessors
/// (Read16/Read32/Write16/Write32 on IAddressSpace) compose over these, so a wide access translates
/// each composed byte independently (the correct 4 KiB-page-wrap behavior at a window boundary). The
/// coprocessor core sees an ordinary 16-bit IAddressSpace and is UNCHANGED. The wrapper does not own a
/// page table — the primary does — so MapMemory/MapPeripheral/Remap/RemapPeripheral are unsupported
/// (a mis-wire throws loudly rather than silently corrupting the primary's map).</summary>
public sealed class TranslatingAddressSpace : IAddressSpace
{
    private readonly IAddressSpace _inner;
    private readonly IAddressTranslation _translation;

    public TranslatingAddressSpace(IAddressSpace inner, IAddressTranslation translation)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(translation);
        _inner = inner;
        _translation = translation;
    }

    public AddressSpaceKind Kind => _inner.Kind;
    public int AddressBits => _inner.AddressBits;
    public Endianness Endianness => _inner.Endianness;

    public byte Read8(uint address) => _inner.Read8(_translation.ToPhysical(address));

    public void Write8(uint address, byte value) =>
        _inner.Write8(_translation.ToPhysical(address), value);

    public bool TryPeek8(uint address, out byte value) =>
        _inner.TryPeek8(_translation.ToPhysical(address), out value);

    public void MapMemory(uint start, byte[] backing, bool writable) =>
        throw new NotSupportedException("TranslatingAddressSpace does not own a page table; map on the primary space.");

    public void MapPeripheral(uint start, uint length, IPeripheral peripheral) =>
        throw new NotSupportedException("TranslatingAddressSpace does not own a page table; map on the primary space.");
}
