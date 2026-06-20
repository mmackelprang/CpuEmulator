namespace CpuEmulator.Machines;

/// <summary>One contiguous span of the address space. Start must be 256-byte page-aligned and
/// Length a positive multiple of 256 (the AddressSpace page granularity). For Rom, Image carries
/// the bytes (its length must equal Length). For Ram and Mmio, Image is null. Space selects the bus:
/// Program (default — Ram/Rom/Mmio) or Io (IoMmio holes for the I/O port space).</summary>
public sealed record MemoryRegion(
    uint Start,
    uint Length,
    RegionKind Kind,
    byte[]? Image = null,
    PeripheralSpace Space = PeripheralSpace.Program);
