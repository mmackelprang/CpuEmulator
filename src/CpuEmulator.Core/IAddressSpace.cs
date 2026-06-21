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

    /// <summary>Re-point an ALREADY-mapped, page-aligned range to a new RAM (<paramref
    /// name="writable"/>=true) or ROM (false) backing — the run-time bank-switch primitive (ADR 0009
    /// Decision 2; the Apple Language Card is the first consumer, ADR 0014 Decision 4). Unlike
    /// MapMemory, the range may already be mapped (that is the point); the old mapping is overwritten.
    /// Fires the JIT invalidation listener so emitted fast-path code re-classifies + evicts the range.
    /// Default: not supported (only the concrete AddressSpace remaps).</summary>
    void Remap(uint start, byte[] backing, bool writable) =>
        throw new NotSupportedException("This address space does not support Remap.");

    /// <summary>Re-point an ALREADY-mapped, page-aligned range to an MMIO device (the remap analogue
    /// of MapPeripheral). Used by the Videx $C800 expansion-bank window (ADR 0016 Decision 3). Default:
    /// not supported.</summary>
    void RemapPeripheral(uint start, uint length, IPeripheral peripheral) =>
        throw new NotSupportedException("This address space does not support RemapPeripheral.");

    /// <summary>Side-effect-free read for monitors and debuggers. Memory pages: always true
    /// (RAM and ROM bytes). Peripheral pages: the device's TryPeek (false if the device
    /// has no honest peek). Unmapped: open-bus value, true, never throws — even in strict
    /// mode (a peek is a debugger view, not a bus transaction).</summary>
    bool TryPeek8(uint address, out byte value);

    /// <summary>The byte order of this bus's multi-byte (word/long) transactions (ADR 0003 Decision 2).
    /// Default <see cref="Endianness.LittleEndian"/> (the 6502/Z80 order); the 68000's bus is
    /// <see cref="Endianness.BigEndian"/>. The wide accessors below assemble bytes per this.</summary>
    Endianness Endianness => Endianness.LittleEndian;

    /// <summary>Read one 16-bit word — ONE transaction (two byte accesses composed per
    /// <see cref="Endianness"/>). Big-endian: the high byte is at <paramref name="address"/>. The default
    /// composes over <see cref="Read8"/>; <see cref="AddressSpace"/> overrides with its page path. Each
    /// composed byte access masks its address independently (the bus wraps at the top of the space).
    /// NOTE (M4.2): a word/long access at an ODD address is misaligned on the 68000 (see
    /// <see cref="BusAlignment.IsMisaligned"/>); M4.2 does NOT fault here — the address-error EXCEPTION is
    /// the M4.5 exception model's job. The caller (the M4.5 interpreter) checks alignment BEFORE the
    /// access.</summary>
    ushort Read16(uint address) =>
        Endianness == Endianness.BigEndian
            ? (ushort)((Read8(address) << 8) | Read8(address + 1))
            : (ushort)(Read8(address) | (Read8(address + 1) << 8));

    /// <summary>Read one 32-bit long — TWO word transactions, HIGH WORD FIRST under big-endian
    /// (ADR 0003 §1.2). Composes over <see cref="Read16"/>.</summary>
    uint Read32(uint address) =>
        Endianness == Endianness.BigEndian
            ? ((uint)Read16(address) << 16) | Read16(address + 2)
            : (uint)Read16(address) | ((uint)Read16(address + 2) << 16);

    /// <summary>Write one 16-bit word — ONE transaction (two byte accesses composed per
    /// <see cref="Endianness"/>). Big-endian: the HIGH byte is written at <paramref name="address"/>
    /// (high-byte-first). See the M4.2 alignment note on <see cref="Read16"/>.</summary>
    void Write16(uint address, ushort value)
    {
        if (Endianness == Endianness.BigEndian)
        {
            Write8(address, (byte)(value >> 8));       // high byte at the lower address
            Write8(address + 1, (byte)value);
        }
        else
        {
            Write8(address, (byte)value);              // low byte at the lower address
            Write8(address + 1, (byte)(value >> 8));
        }
    }

    /// <summary>Write one 32-bit long — TWO word transactions, HIGH WORD FIRST under big-endian
    /// (ADR 0003 §1.2). Composes over <see cref="Write16"/>.</summary>
    void Write32(uint address, uint value)
    {
        if (Endianness == Endianness.BigEndian)
        {
            Write16(address, (ushort)(value >> 16));   // high word first
            Write16(address + 2, (ushort)value);
        }
        else
        {
            Write16(address, (ushort)value);           // low word first
            Write16(address + 2, (ushort)(value >> 16));
        }
    }
}
