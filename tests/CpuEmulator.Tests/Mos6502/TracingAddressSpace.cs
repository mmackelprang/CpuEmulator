using CpuEmulator.Core;

namespace CpuEmulator.Tests.Mos6502;

/// <summary>One recorded bus transaction. <see cref="Value"/> is <c>uint</c> so a Word/Long wide access
/// records its full value (the M4.5 TomHarte gate diffs word-granular transactions with their value);
/// a byte access stores a byte (which widens implicitly). <see cref="ToString"/> renders two hex digits —
/// the 8-bit CPUs (6502/Z80) only ever produce <see cref="AccessWidth.Byte"/> entries, so their trace
/// strings are unchanged.</summary>
internal sealed record BusAccess(uint Address, uint Value, bool IsRead, AccessWidth Width = AccessWidth.Byte)
{
    public override string ToString() => $"{(IsRead ? "R" : "W")} {Address:X4}={Value:X2}";
}

/// <summary>
/// Records every bus access in order. Cycle-number convention (recorded in the 2a plan):
/// the CPU charges _cycles BEFORE the access, so trace entry N corresponds to cycle N+1
/// of the instruction stream; tests assert the ordered access list and the cycle total
/// separately rather than per-entry cycle stamps.
/// </summary>
internal sealed class TracingAddressSpace(IAddressSpace inner) : IAddressSpace
{
    public List<BusAccess> Trace { get; } = [];

    /// <summary>Clear the recorded access trace — the per-worker REUSE reset (lever 4). A reused
    /// TracingAddressSpace (e.g. the Z80 JIT path's persistent Io trace, bound ONCE into the reused inner
    /// Z80) accumulates across cases; clearing it per case makes the trace identical to a freshly constructed
    /// TracingAddressSpace's. Test-only helper.</summary>
    public void ResetTrace() => Trace.Clear();

    public AddressSpaceKind Kind => inner.Kind;
    public int AddressBits => inner.AddressBits;

    public byte Read8(uint address)
    {
        byte value = inner.Read8(address);
        Trace.Add(new BusAccess(address, value, true));
        return value;
    }

    public void Write8(uint address, byte value)
    {
        Trace.Add(new BusAccess(address, value, false));
        inner.Write8(address, value);
    }

    public ushort Read16(uint address)
    {
        ushort value = inner.Read16(address);
        Trace.Add(new BusAccess(address, value, IsRead: true, AccessWidth.Word));
        return value;
    }

    public uint Read32(uint address)
    {
        uint value = inner.Read32(address);
        Trace.Add(new BusAccess(address, value, IsRead: true, AccessWidth.Long));
        return value;
    }

    public void Write16(uint address, ushort value)
    {
        Trace.Add(new BusAccess(address, value, IsRead: false, AccessWidth.Word));
        inner.Write16(address, value);
    }

    public void Write32(uint address, uint value)
    {
        Trace.Add(new BusAccess(address, value, IsRead: false, AccessWidth.Long));
        inner.Write32(address, value);
    }

    /// <summary>Mirror the inner space's byte order so the wide overrides write through correctly.</summary>
    public Endianness Endianness => inner.Endianness;

    public bool TryPeek8(uint address, out byte value) => inner.TryPeek8(address, out value);

    public void MapMemory(uint start, byte[] backing, bool writable) =>
        inner.MapMemory(start, backing, writable);

    public void MapPeripheral(uint start, uint length, IPeripheral peripheral) =>
        inner.MapPeripheral(start, length, peripheral);
}
