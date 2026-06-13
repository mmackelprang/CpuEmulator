namespace CpuEmulator.Core.Jit;

/// <summary>An IFetchStream over a live IAddressSpace at a PC — the byte-granular stream the
/// generated interpreter Step uses for a CPU that declares a DecodeStructure (a multi-byte CPU
/// must run the walk to resolve the operation-key before dispatching). UnitBytes == 1; NextUnit
/// reads the byte at the cursor and advances. This does NOT charge a cycle — the per-op bodies own
/// the bus cycle bookkeeping, exactly as the 6502 byte-fetch Step does. (The JIT's discovery uses
/// its own internal BusFetchStream; this is the interpreter-side companion that lives in Core so the
/// generated CPU assembly — which references only Core — can name it.)</summary>
public sealed class AddressSpaceFetchStream : IFetchStream
{
    private readonly IAddressSpace _bus;
    private readonly ushort _origin;
    private int _offset;

    public AddressSpaceFetchStream(IAddressSpace bus, ushort pc) { _bus = bus; _origin = pc; }

    public int UnitBytes => 1;
    public int UnitsConsumed => _offset;

    public uint NextUnit()
    {
        byte b = _bus.Read8((ushort)(_origin + _offset));
        _offset++;
        return b;
    }

    public uint PeekUnit() => _bus.Read8((ushort)(_origin + _offset));
}
