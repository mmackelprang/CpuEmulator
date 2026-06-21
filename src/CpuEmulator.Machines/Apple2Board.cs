using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Machines;

/// <summary>
/// The base Apple ][+ as a declarative <see cref="BoardSpec"/> (ADR 0014 Decision 1): a 6502 with
/// 48 KiB RAM $0000-$BFFF, the $C000-$CFFF I/O + slot band as an Mmio hole, and the 12 KiB system ROM
/// (Applesoft + Monitor) at $D000-$FFFF. Memory-mapped I/O only — IoAddressBits stays 0 (no Z80-style
/// port space; the Apple's I/O is at $C0xx on the Program bus). The Apple2Iou soft-switch decoder owns
/// the $C000 page (any-access toggle, peek-free, ADR 0014 Decision 2). The system ROM carries its own
/// $FFFC/$FFFD reset vector (-> $FA62), so ResetConfig.None. The bare ][+ has no interrupt source
/// (Disk II is polled), so IrqWiring.None. Later PRs add the video/keyboard/speaker chips (C/D), the
/// Language Card ports (E), and Disk II (F); they delegate through this same IOU / fill the same hole.
/// </summary>
public static class Apple2Board
{
    public const uint RamBase = 0x0000;
    public const uint RamLength = 0xC000;   // 48 KiB $0000-$BFFF
    public const uint IoBase = 0xC000;
    public const uint IoLength = 0x1000;    // $C000-$CFFF (the soft-switch + slot band)
    public const uint RomBase = 0xD000;
    public const uint RomLength = 0x3000;   // 12 KiB $D000-$FFFF
    public const uint IouBase = 0xC000;
    public const uint IouLength = 0x0100;   // the $C000 page

    public static BoardSpec Spec(byte[] systemRom, Apple2Iou iou)
    {
        ArgumentNullException.ThrowIfNull(systemRom);
        ArgumentNullException.ThrowIfNull(iou);
        if (systemRom.Length != RomLength)
            throw new ArgumentException(
                $"Apple ][+ system ROM must be exactly ${RomLength:X} bytes; got ${systemRom.Length:X}.",
                nameof(systemRom));

        return new BoardSpec("apple2plus", CpuKind.Mos6502, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(RamBase, RamLength, RegionKind.Ram),
                new MemoryRegion(IoBase, IoLength, RegionKind.Mmio),       // the $C000-$CFFF hole
                new MemoryRegion(RomBase, RomLength, RegionKind.Rom, systemRom),
            ],
            Peripherals:
            [
                new PeripheralSlot("iou", iou, IouBase, IouLength),        // the $C000 page decoder
            ],
            Irq: IrqWiring.None,        // the bare ][+ has no interrupt source (Disk II is polled)
            Reset: ResetConfig.None);   // the system ROM carries its own $FFFC/$FFFD vector
        // IoAddressBits defaults to 0: memory-mapped I/O only (no port space).
    }
}
