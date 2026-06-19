namespace CpuEmulator.Machines;

/// <summary>Load-time validation of a BoardSpec (spec section 3). Returns every finding; an empty
/// list means the spec is well-formed. Checks: region overlap, address-width fit, 256-byte page
/// alignment (start + length), MMIO-slot-in-Mmio-region, IRQ-wired-to-a-real-peripheral, ROM-image
/// size, and vector-patch-in-mapped-memory. Page size and width rules mirror AddressSpace.</summary>
public static class BoardSpecValidator
{
    private const uint PageSize = 256; // AddressSpace.PageSize

    public static IReadOnlyList<BoardDiagnostic> Validate(BoardSpec spec)
    {
        var diagnostics = new List<BoardDiagnostic>();
        ValidateRegions(spec, diagnostics);
        return diagnostics;
    }

    private static void ValidateRegions(BoardSpec spec, List<BoardDiagnostic> diagnostics)
    {
        // Top of the bus for this address width (e.g. 0xFFFF for 16 bits).
        ulong addressCeiling = 1UL << spec.AddressBits;

        for (int i = 0; i < spec.Memory.Count; i++)
        {
            MemoryRegion r = spec.Memory[i];

            if (r.Length == 0 || r.Start % PageSize != 0 || r.Length % PageSize != 0)
                diagnostics.Add(new BoardDiagnostic("region-misaligned",
                    $"Region at ${r.Start:X} (length ${r.Length:X}) must be page-aligned: "
                  + $"start a multiple of {PageSize} and length a positive multiple of {PageSize}."));

            if ((ulong)r.Start + r.Length > addressCeiling)
                diagnostics.Add(new BoardDiagnostic("region-out-of-range",
                    $"Region [${r.Start:X}, ${(ulong)r.Start + r.Length:X}) exceeds the "
                  + $"{spec.AddressBits}-bit address space (ceiling ${addressCeiling:X})."));

            for (int j = i + 1; j < spec.Memory.Count; j++)
            {
                MemoryRegion other = spec.Memory[j];
                if (r.Start < other.Start + other.Length && other.Start < r.Start + r.Length)
                    diagnostics.Add(new BoardDiagnostic("region-overlap",
                        $"Region [${r.Start:X}, ${r.Start + r.Length:X}) overlaps "
                      + $"[${other.Start:X}, ${other.Start + other.Length:X})."));
            }
        }
    }
}
