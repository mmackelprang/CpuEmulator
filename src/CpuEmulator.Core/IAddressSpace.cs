namespace CpuEmulator.Core;

/// <summary>
/// One bus: a page-table-backed address space. Pages resolve to backing memory
/// (RAM/ROM fast path) or an <see cref="IPeripheral"/> handler (MMIO slow path).
/// </summary>
public interface IAddressSpace
{
    AddressSpaceKind Kind { get; }
    int AddressBits { get; }

    /// <summary>Read one byte. Addresses are masked to <see cref="AddressBits"/> (the bus wraps).
    /// Reads from unmapped addresses return the space's open-bus value (or throw in strict mode).</summary>
    byte Read8(uint address);

    /// <summary>Write one byte. Addresses are masked to <see cref="AddressBits"/> (the bus wraps).
    /// Writes to unmapped addresses or read-only memory are ignored (or throw in strict mode).</summary>
    void Write8(uint address, byte value);

    /// <summary>Map RAM (<paramref name="writable"/>=true) or ROM (false). The backing
    /// length must be a positive multiple of the page size; start must be page-aligned.</summary>
    void MapMemory(uint start, byte[] backing, bool writable);

    /// <summary>Map a device over [start, start+length). Same alignment rules.</summary>
    void MapPeripheral(uint start, uint length, IPeripheral peripheral);
}
