namespace CpuEmulator.Machines;

/// <summary>How a MemoryRegion is mapped onto the bus.</summary>
public enum RegionKind
{
    /// <summary>Writable backing memory.</summary>
    Ram,

    /// <summary>Read-only backing memory (carries the Image bytes).</summary>
    Rom,

    /// <summary>A device window (peripheral slots must land in/over an Mmio region).</summary>
    Mmio,

    /// <summary>The I/O-port-space analogue of Mmio: a hole an Io slot fills (a CPU with a separate
    /// I/O port space — the Z80 IN/OUT range).</summary>
    IoMmio,
}
