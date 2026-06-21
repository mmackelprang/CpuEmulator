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
            if (bus.TryGetDirectAccess(pageStart, out byte[] backing, out int offset, out bool writable))
            {
                // PageOffset + PageWritable are ALWAYS recorded (even under DisableFastmem) so the
                // emitted bus arm can still dirty.Mark a writable RAM page — SMC must keep working
                // in trace mode (Ground truth G, last row). Only PageBacking is suppressed under
                // DisableFastmem, which is what forces every access onto the bus arm.
                PageOffset[p] = offset;
                PageWritable[p] = writable;
                if (!options.DisableFastmem)
                    PageBacking[p] = backing;
            }
            // An MMIO/unmapped page leaves PageBacking[p] null AND PageWritable[p] false, so its
            // accesses take the bus arm and never mark dirty (MMIO cannot hold code).
        }
    }

    /// <summary>Re-classify ONE page after a bus remap (ADR 0014 Decision 4). Re-runs the same
    /// TryGetDirectAccess + DisableFastmem rule the constructor applies, for the single page
    /// <paramref name="page"/>, so emitted fast-path loads/stores see the NEW backing/offset/writability.
    /// An MMIO/unmapped page (TryGetDirectAccess false) is reset to the bus-arm classification
    /// (null backing, offset 0, not writable) — symmetric with the constructor's else branch.</summary>
    public void Reclassify(AddressSpace bus, int page, JitOptions options)
    {
        uint pageStart = (uint)page << 8;
        if (bus.TryGetDirectAccess(pageStart, out byte[] backing, out int offset, out bool writable))
        {
            PageOffset[page] = offset;
            PageWritable[page] = writable;
            PageBacking[page] = options.DisableFastmem ? null : backing;
        }
        else
        {
            PageBacking[page] = null;
            PageOffset[page] = 0;
            PageWritable[page] = false;
        }
    }
}
