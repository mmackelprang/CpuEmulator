namespace CpuEmulator.Core;

/// <summary>Per-space bus behavior policy (spec §7).</summary>
public sealed class AddressSpaceOptions
{
    /// <summary>Value returned by reads from unmapped addresses.</summary>
    public byte OpenBusValue { get; init; } = 0xFF;

    /// <summary>When true, unmapped reads/writes and ROM writes throw
    /// <see cref="StrictBusViolationException"/> instead of using open-bus semantics.</summary>
    public bool Strict { get; init; }

    /// <summary>The byte order this space composes wide (16/32-bit) accesses in. Little-endian by default
    /// (the 6502/Z80/8086 convention); a big-endian CPU (the 68000) sets this so its wide bus reads/writes
    /// — including the reset vectors and the fetched instruction stream — are byte-ordered correctly. This
    /// is the per-space seam <see cref="MachineBuilder.WithAddressSpace"/> threads through to the
    /// <see cref="AddressSpace"/> constructor's endianness, so a board recipe can declare its CPU's order.</summary>
    public Endianness Endianness { get; init; } = Endianness.LittleEndian;
}
