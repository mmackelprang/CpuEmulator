namespace CpuEmulator.Core;

/// <summary>
/// The byte order of a bus's multi-byte (word/long) transactions. A BUS property (ADR 0003 Decision 2),
/// not a CPU-side convention — the wide accessors (<see cref="IAddressSpace.Read16"/> etc.) assemble bytes
/// per this. <see cref="LittleEndian"/> is the default (the 6502/Z80 order: the low byte at the lower
/// address); <see cref="BigEndian"/> is the 68000 order (the high byte at the lower address). LittleEndian
/// is the zero value, so a default-constructed bus is little-endian (byte-identical to the pre-M4.2
/// behaviour, where multi-byte order was a CPU-side little-endian convention).
/// </summary>
public enum Endianness
{
    /// <summary>Low byte at the lower address (6502 / Z80). The default.</summary>
    LittleEndian,

    /// <summary>High byte at the lower address (Motorola 68000).</summary>
    BigEndian,
}
