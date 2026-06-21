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
        ValidateRomImages(spec, diagnostics);
        ValidatePeripheralSlots(spec, diagnostics);
        ValidateIoSpace(spec, diagnostics);
        ValidateIrqWiring(spec, diagnostics);
        ValidateVectorPatches(spec, diagnostics);
        ValidateCoprocessor(spec, diagnostics);
        return diagnostics;
    }

    private static void ValidateRomImages(BoardSpec spec, List<BoardDiagnostic> diagnostics)
    {
        foreach (MemoryRegion r in spec.Memory)
        {
            if (r.Kind != RegionKind.Rom)
                continue;
            if (r.Image is null || r.Image.Length != r.Length)
                diagnostics.Add(new BoardDiagnostic("rom-image-mismatch",
                    $"Rom region at ${r.Start:X} (length ${r.Length:X}) needs an image of exactly "
                  + $"${r.Length:X} bytes; got {(r.Image is null ? "none" : $"${r.Image.Length:X}")}."));
        }
    }

    private static void ValidatePeripheralSlots(BoardSpec spec, List<BoardDiagnostic> diagnostics)
    {
        foreach (PeripheralSlot slot in spec.Peripherals)
        {
            if (slot.Space == PeripheralSpace.Io)
                continue; // Io slots are validated by ValidateIoSpace

            if (slot.Base % PageSize != 0 || slot.Length == 0 || slot.Length % PageSize != 0)
                diagnostics.Add(new BoardDiagnostic("slot-misaligned",
                    $"Peripheral '{slot.Name}' slot at ${slot.Base:X} (length ${slot.Length:X}) "
                  + $"must be page-aligned: start a multiple of {PageSize}, length a positive multiple."));

            bool inMmio = spec.Memory.Any(r =>
                r.Kind == RegionKind.Mmio &&
                slot.Base >= r.Start &&
                (ulong)slot.Base + slot.Length <= (ulong)r.Start + r.Length);
            if (!inMmio)
                diagnostics.Add(new BoardDiagnostic("slot-not-in-mmio",
                    $"Peripheral '{slot.Name}' slot [${slot.Base:X}, ${(ulong)slot.Base + slot.Length:X}) "
                  + "is not fully contained in any Mmio region."));
        }
    }

    private static void ValidateIrqWiring(BoardSpec spec, List<BoardDiagnostic> diagnostics)
    {
        foreach (PeripheralIrq line in spec.Irq.Lines)
        {
            if (!spec.Peripherals.Any(p => p.Name == line.PeripheralName))
                diagnostics.Add(new BoardDiagnostic("irq-unwired",
                    $"IRQ wiring names peripheral '{line.PeripheralName}', which is not a declared slot."));
        }
    }

    private static void ValidateCoprocessor(BoardSpec spec, List<BoardDiagnostic> diagnostics)
    {
        if (spec.Coprocessor is not { } copro)
            return; // single-CPU board: no coprocessor checks (the common case, unchanged)

        if (copro.Translation is null)
            diagnostics.Add(new BoardDiagnostic("copro-no-translation",
                "The coprocessor spec has no address translation; a coprocessor needs an IAddressTranslation."));

        if (copro.ClockRatioToPrimary <= 0)
            diagnostics.Add(new BoardDiagnostic("copro-bad-clock-ratio",
                $"The coprocessor clock ratio must be positive; got {copro.ClockRatioToPrimary}."));

        if (!spec.Peripherals.Any(p => p.Name == copro.ControlPortPeripheral))
            diagnostics.Add(new BoardDiagnostic("copro-control-port-unwired",
                $"The coprocessor control port names peripheral '{copro.ControlPortPeripheral}', "
              + "which is not a declared slot."));
    }

    private static void ValidateVectorPatches(BoardSpec spec, List<BoardDiagnostic> diagnostics)
    {
        foreach (VectorPatch patch in spec.Reset.VectorPatches)
        {
            bool mapped = spec.Memory.Any(r =>
                patch.Address >= r.Start && patch.Address < (ulong)r.Start + r.Length);
            if (!mapped)
            {
                diagnostics.Add(new BoardDiagnostic("vector-unmapped",
                    $"Reset vector patch at ${patch.Address:X} lands in no declared region."));
                continue;
            }

            // A patch pokes a byte into a ROM image before mapping, so it must land in a Rom region
            // that carries an image. Surface this as a clean pre-flight diagnostic rather than letting
            // BoardMachineFactory.ApplyVectorPatches throw "vector-not-rom" late at build time.
            bool inRom = spec.Memory.Any(r =>
                r.Kind == RegionKind.Rom && r.Image is not null &&
                patch.Address >= r.Start && patch.Address < (ulong)r.Start + r.Length);
            if (!inRom)
                diagnostics.Add(new BoardDiagnostic("vector-not-in-rom",
                    $"Reset vector patch at ${patch.Address:X} lands in a non-ROM region; "
                  + "vector patches may only poke ROM images."));
        }
    }

    private static void ValidateIoSpace(BoardSpec spec, List<BoardDiagnostic> diagnostics)
    {
        bool hasIoSlots = spec.Peripherals.Any(p => p.Space == PeripheralSpace.Io);
        bool hasIoRegions = spec.Memory.Any(r => r.Space == PeripheralSpace.Io);

        if ((hasIoSlots || hasIoRegions) && spec.IoAddressBits <= 0)
        {
            diagnostics.Add(new BoardDiagnostic("io-space-undeclared",
                "The board has Io regions/slots but IoAddressBits is 0; set IoAddressBits (16 for the Z80)."));
            return; // further Io checks need a declared space width
        }

        if (spec.IoAddressBits == 0)
            return;

        ulong ioCeiling = 1UL << spec.IoAddressBits;

        foreach (PeripheralSlot slot in spec.Peripherals)
        {
            if (slot.Space != PeripheralSpace.Io)
                continue;

            if (slot.Base % PageSize != 0 || slot.Length == 0 || slot.Length % PageSize != 0)
                diagnostics.Add(new BoardDiagnostic("io-slot-misaligned",
                    $"Io peripheral '{slot.Name}' slot at ${slot.Base:X} (length ${slot.Length:X}) "
                  + $"must be page-aligned: start a multiple of {PageSize}, length a positive multiple."));

            if ((ulong)slot.Base + slot.Length > ioCeiling)
                diagnostics.Add(new BoardDiagnostic("io-slot-out-of-range",
                    $"Io peripheral '{slot.Name}' slot [${slot.Base:X}, ${(ulong)slot.Base + slot.Length:X}) "
                  + $"exceeds the {spec.IoAddressBits}-bit I/O space (ceiling ${ioCeiling:X})."));

            bool inIoMmio = spec.Memory.Any(r =>
                r.Space == PeripheralSpace.Io && r.Kind == RegionKind.IoMmio &&
                slot.Base >= r.Start &&
                (ulong)slot.Base + slot.Length <= (ulong)r.Start + r.Length);
            if (!inIoMmio)
                diagnostics.Add(new BoardDiagnostic("io-slot-not-in-iommio",
                    $"Io peripheral '{slot.Name}' slot [${slot.Base:X}, ${(ulong)slot.Base + slot.Length:X}) "
                  + "is not fully contained in any IoMmio region."));
        }
    }

    private static void ValidateRegions(BoardSpec spec, List<BoardDiagnostic> diagnostics)
    {
        // Top of the bus for this address width (e.g. 0xFFFF for 16 bits).
        ulong addressCeiling = 1UL << spec.AddressBits;

        for (int i = 0; i < spec.Memory.Count; i++)
        {
            MemoryRegion r = spec.Memory[i];

            if (r.Space == PeripheralSpace.Io)
                continue; // Io regions are validated against the I/O ceiling in ValidateIoSpace

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
                if (other.Space == PeripheralSpace.Io)
                    continue; // Io regions live in a different space; checked by ValidateIoSpace
                if (r.Start < other.Start + other.Length && other.Start < r.Start + r.Length)
                    diagnostics.Add(new BoardDiagnostic("region-overlap",
                        $"Region [${r.Start:X}, ${r.Start + r.Length:X}) overlaps "
                      + $"[${other.Start:X}, ${other.Start + other.Length:X})."));
            }
        }
    }
}
