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
}
