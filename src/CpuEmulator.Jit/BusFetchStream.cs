using CpuEmulator.Core;
using CpuEmulator.Core.Jit;

namespace CpuEmulator.Jit;

/// <summary>An IFetchStream over a live IAddressSpace at a PC — the byte-granular default the JIT
/// Discover (and, wrapped, the interpreter Step) read through. UnitBytes == 1; NextUnit reads the
/// byte at the cursor and advances (a debugger-view decode — it does NOT charge a cycle; Discover
/// never executes, matching today's bus.Read8(pc) at BlockCompiler.cs:100). SeekTo repositions the
/// cursor so Discover can walk instruction to instruction by the COMPUTED length.</summary>
internal sealed class BusFetchStream : IFetchStream
{
    private readonly IAddressSpace _bus;
    private ushort _origin;
    private int _offset;

    public BusFetchStream(IAddressSpace bus, ushort pc) { _bus = bus; _origin = pc; }

    public int UnitBytes => 1;
    public int UnitsConsumed => _offset;

    public uint NextUnit()
    {
        byte b = _bus.Read8((ushort)(_origin + _offset));
        _offset++;
        return b;
    }

    public uint PeekUnit() => _bus.Read8((ushort)(_origin + _offset));

    /// <summary>Reposition at a new PC and reset the consumed count (between instructions).</summary>
    public void SeekTo(ushort pc) { _origin = pc; _offset = 0; }
}
