using CpuEmulator.Core;

namespace CpuEmulator.Machines;

/// <summary>Instantiates a validated BoardSpec into the existing runnable Machine (spec section 3).
/// Validates first (throws BoardValidationException on any diagnostic), applies the ResetConfig's
/// vector patches into the relevant ROM image, then compiles the spec into the fluent MachineBuilder:
/// the program AddressSpace, the RAM/ROM regions, the peripheral slots, and the CpuKind-resolved core.
/// IRQ wiring needs no explicit step here: devices claim their own wired-OR handle via
/// context.IrqLine.Source()/NmiLine.Source() in Realize (the IrqWiring is the validator's contract,
/// not a runtime mapping). The result keeps the device scheduler + interrupt plumbing unchanged.</summary>
public static class BoardMachineFactory
{
    public static Machine Build(BoardSpec spec, ExecutionTier tier = ExecutionTier.Interpreter)
    {
        IReadOnlyList<BoardDiagnostic> diagnostics = BoardSpecValidator.Validate(spec);
        if (diagnostics.Count > 0)
            throw new BoardValidationException(spec.Name, diagnostics);

        spec = ApplyVectorPatches(spec);

        MachineBuilder builder = Machine.Create(spec.Name)
            .WithAddressSpace(AddressSpaceKind.Program, spec.AddressBits,
                new AddressSpaceOptions { Endianness = spec.Endianness });

        foreach (MemoryRegion region in spec.Memory)
        {
            switch (region.Kind)
            {
                case RegionKind.Ram:
                    builder.WithRam(AddressSpaceKind.Program, region.Start, region.Length);
                    break;
                case RegionKind.Rom:
                    builder.WithRom(AddressSpaceKind.Program, region.Start, region.Image!);
                    break;
                case RegionKind.Mmio:
                    // An Mmio region is a hole that peripheral slots fill; no backing to map.
                    break;
            }
        }

        foreach (PeripheralSlot slot in spec.Peripherals)
            builder.WithPeripheral(AddressSpaceKind.Program, slot.Base, slot.Length, slot.Device);

        builder.WithCpu(CpuCoreFactory.ForKind(spec.Cpu, AddressSpaceKind.Program, tier));
        return builder.Build();
    }

    /// <summary>Write each ResetConfig vector byte into the ROM image whose region contains the
    /// patch address. Validation has already confirmed the address is mapped; a patch that lands in
    /// a non-Rom region is a board-author error surfaced here as an explicit exception. Patches are
    /// applied to a CLONE of each affected ROM image (and a rebuilt BoardSpec is returned), so the
    /// caller-owned image array is never mutated — Build(spec) is safe to call twice on the same
    /// spec instance. With no patches (the common case) the original spec is returned unchanged.</summary>
    private static BoardSpec ApplyVectorPatches(BoardSpec spec)
    {
        if (spec.Reset.VectorPatches.Count == 0)
            return spec;

        // Clone every ROM image up front so we patch our own copies, not the caller's arrays.
        var patched = spec.Memory
            .Select(r => r is { Kind: RegionKind.Rom, Image: not null }
                ? r with { Image = (byte[])r.Image.Clone() }
                : r)
            .ToList();

        foreach (VectorPatch patch in spec.Reset.VectorPatches)
        {
            MemoryRegion? target = patched.FirstOrDefault(r =>
                r.Kind == RegionKind.Rom && r.Image is not null &&
                patch.Address >= r.Start && patch.Address < (ulong)r.Start + r.Length);
            if (target is null)
                throw new BoardValidationException(spec.Name,
                    [new BoardDiagnostic("vector-not-rom",
                        $"Reset vector patch at ${patch.Address:X} does not land in a ROM image.")]);
            target.Image![(int)(patch.Address - target.Start)] = patch.Value;
        }

        return spec with { Memory = patched };
    }
}
