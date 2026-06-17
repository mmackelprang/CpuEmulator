namespace CpuEmulator.Core.Jit;

/// <summary>An IFetchStream over a live IAddressSpace at a PC — the byte-granular stream the
/// generated interpreter Step uses for a CPU that declares a DecodeStructure (a multi-byte CPU
/// must run the walk to resolve the operation-key before dispatching). UnitBytes == 1; NextUnit
/// reads the byte at the cursor and advances. This does NOT charge a cycle — the per-op bodies own
/// the bus cycle bookkeeping, exactly as the 6502 byte-fetch Step does. (The JIT's discovery uses
/// its own internal BusFetchStream; this is the interpreter-side companion that lives in Core so the
/// generated CPU assembly — which references only Core — can name it.)
///
/// <para>Two address modes (M5.3, ADR 0005 Decision 2):</para>
/// <list type="bullet">
///   <item><b>Flat (the <c>(bus, ushort pc)</c> ctor)</b> — the 6502/Z80 walk: the origin is a 16-bit PC
///     and the cursor wraps at 16 bits over a flat space (<c>(ushort)(_origin + _offset)</c>). UNCHANGED
///     by M5.3 (so the Z80's generated Step is byte-identical).</item>
///   <item><b>Segmented (the <c>(bus, ushort offset, ushort segment)</c> ctor)</b> — the 8086 instruction
///     fetch: the PHYSICAL address is <c>(segment &lt;&lt; 4) + offset</c>, where the 16-bit IP offset
///     (origin + cursor) wraps WITHIN the segment (the 8086 segment-relative wrap quirk — it does NOT carry
///     into the segment base) and the resulting 20-bit physical address is masked to 20 bits. This is the
///     <c>(CS&lt;&lt;4)+IP</c> physical instruction fetch (M5.3 wires the segment layer; M5.2's flat 16-bit
///     origin against the synthetic space was a placeholder).</item>
/// </list>
/// The bus itself stays a flat 20-bit little-endian <see cref="IAddressSpace"/> (no bus rework — the
/// segment shift + wrap is pure arithmetic on the fetch cursor, ADR 0005 Decision 2(A)).</summary>
public sealed class AddressSpaceFetchStream : IFetchStream
{
    private readonly IAddressSpace _bus;
    private readonly ushort _origin;
    private readonly uint _segmentBase;   // (segment << 4), the 20-bit segment base; unused (flat) for the 6502/Z80
    private readonly bool _segmented;     // true ⇒ the 8086 (segment<<4)+offset physical fetch
    private int _offset;

    /// <summary>Flat 16-bit-origin fetch (the 6502/Z80 walk). The cursor wraps at 16 bits over a flat
    /// space. UNCHANGED by M5.3 — the Z80's generated Step references this ctor byte-identically.</summary>
    public AddressSpaceFetchStream(IAddressSpace bus, ushort pc) { _bus = bus; _origin = pc; }

    /// <summary>Segmented 8086 instruction fetch (M5.3, ADR 0005 Decision 2): the physical address is
    /// <c>(segment &lt;&lt; 4) + offset</c>. The 16-bit IP offset wraps within the segment (it never carries
    /// into the segment base); the result is masked to 20 bits. This is the real <c>(CS&lt;&lt;4)+IP</c>
    /// fetch that resolves M5.2's deferred 16-bit-flat placeholder.</summary>
    public AddressSpaceFetchStream(IAddressSpace bus, ushort offset, ushort segment)
    {
        _bus = bus;
        _origin = offset;
        _segmentBase = (uint)segment << 4;
        _segmented = true;
    }

    public int UnitBytes => 1;
    public int UnitsConsumed => _offset;

    /// <summary>The current 20-bit physical (segmented) or 16-bit flat fetch address. The 8086 IP offset
    /// wraps at 16 bits BEFORE the segment shift (the segment-relative wrap quirk).</summary>
    private uint FetchAddress =>
        _segmented
            ? (_segmentBase + (ushort)(_origin + _offset)) & 0xFFFFFu
            : (ushort)(_origin + _offset);

    public uint NextUnit()
    {
        byte b = _bus.Read8(FetchAddress);
        _offset++;
        return b;
    }

    public uint PeekUnit() => _bus.Read8(FetchAddress);
}
