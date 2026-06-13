using CpuEmulator.Core;

namespace CpuEmulator.Jit;

/// <summary>The fastmem page classification, computed once at JittedCpu construction from the
/// AddressSpace page table (TryGetDirectAccess per page). For a fixed-map 8-bit board the map
/// is static (research §5). A null PageBacking[p] means "page p is MMIO/unmapped — emit a bus
/// callout." PageBacking is baked into emitted blocks as a constant reference; PageOffset and
/// PageWritable are baked per-store/per-load at emit time (the page is a runtime index, but the
/// per-page offset/writability are resolved through these arrays at run time too).</summary>
public sealed class Fastmem
{
    /// <summary>One slot per 256-byte page: the backing array for RAM/ROM, or null for
    /// MMIO/unmapped. Baked into emitted blocks (the runtime page-class branch tests this).</summary>
    public byte[]?[] PageBacking { get; }

    /// <summary>One slot per page: the index of the page's first byte within its backing array.</summary>
    public int[] PageOffset { get; }

    /// <summary>One slot per page: whether the page is writable (RAM true / ROM false).</summary>
    public bool[] PageWritable { get; }

    public Fastmem(AddressSpace bus, JitOptions options)
    {
        int pages = bus.PageCount;
        PageBacking = new byte[]?[pages];
        PageOffset = new int[pages];
        PageWritable = new bool[pages];

        for (int p = 0; p < pages; p++)
        {
            uint pageStart = (uint)p << 8;
            if (!options.DisableFastmem
                && bus.TryGetDirectAccess(pageStart, out byte[] backing, out int offset, out bool writable))
            {
                PageBacking[p] = backing;
                PageOffset[p] = offset;
                PageWritable[p] = writable;
            }
            // DisableFastmem (or an MMIO/unmapped page) leaves PageBacking[p] null, so every
            // access to that page takes the bus arm.
        }
    }
}
