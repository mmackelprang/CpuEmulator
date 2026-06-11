namespace CpuEmulator.Core;

/// <summary>
/// One bus: a page-table-backed address space. Pages resolve to backing memory
/// (RAM/ROM fast path) or an <see cref="IPeripheral"/> handler (MMIO slow path).
/// </summary>
public interface IAddressSpace
{
    AddressSpaceKind Kind { get; }
    int AddressBits { get; }

    byte Read8(uint address);
    void Write8(uint address, byte value);

    /// <summary>Map RAM (<paramref name="writable"/>=true) or ROM (false). The backing
    /// length must be a positive multiple of the page size; start must be page-aligned.</summary>
    void MapMemory(uint start, byte[] backing, bool writable);

    /// <summary>Map a device over [start, start+length). Same alignment rules.</summary>
    void MapPeripheral(uint start, uint length, IPeripheral peripheral);
}
