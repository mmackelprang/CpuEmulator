using CpuEmulator.Core;

namespace CpuEmulator.Tests.Mos6502;

internal sealed record BusAccess(uint Address, byte Value, bool IsRead)
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

    public void MapMemory(uint start, byte[] backing, bool writable) =>
        inner.MapMemory(start, backing, writable);

    public void MapPeripheral(uint start, uint length, IPeripheral peripheral) =>
        inner.MapPeripheral(start, length, peripheral);
}
