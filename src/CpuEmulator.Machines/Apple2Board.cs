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

    /// <summary>The ][+ board with the Language Card wired (ADR 0014 Decision 4). The LC owns no bus
    /// page of its own (the IOU owns $C000-$C0FF, which includes $C08x) — so the board spec is byte-for-
    /// byte the base Spec; the IOU (already holding the LC) Realizes it (IOU-forwards-Realize), which is
    /// how the LC captures the program bus it remaps. No extra slot is added.
    /// <para>CALLER CONTRACT: <paramref name="iou"/> MUST have been constructed with this same
    /// <paramref name="lc"/> instance (<c>new Apple2Iou(state, lc)</c>). The <paramref name="lc"/>
    /// parameter is the spec's documentation of intent + the null-check; the IOU holds the live
    /// reference it both delegates $C08x to and Realizes. Passing a different LC here is a no-op footgun
    /// (the IOU's own LC wins) — kept as a constructor parameter rather than restructured to avoid a
    /// wider board-builder change in this PR.</para></summary>
    public static BoardSpec SpecWithLanguageCard(byte[] systemRom, Apple2Iou iou, Apple2LanguageCard lc)
    {
        ArgumentNullException.ThrowIfNull(lc);
        return Spec(systemRom, iou);   // the IOU (holding the LC) Realizes it; no extra slot needed
    }

    /// <summary>The ][+ board with the Disk II controller wired (ADR 0014 Decision 6). The controller is
    /// delegated $C0E0-$C0EF by the IOU (already attached) and is Realized by the IOU (IOU-forwards-Realize)
    /// so it captures the scheduler for the ~1 s motor-off delay. The $C600 boot ROM slot is added in PR-H
    /// (when the boot ROM is fetched); the synthetic gate needs no ROM.
    /// <para>CALLER CONTRACT: <paramref name="iou"/> MUST have been constructed with this same
    /// <paramref name="disk2"/> instance (<c>new Apple2Iou(state, disk2)</c>). The <paramref name="disk2"/>
    /// parameter is the spec's documentation of intent + the null-check; the IOU holds the live reference
    /// it both delegates $C0Ex to and Realizes (mirrors SpecWithLanguageCard).</para></summary>
    public static BoardSpec SpecWithDiskII(byte[] systemRom, Apple2Iou iou, Apple2DiskII disk2)
    {
        ArgumentNullException.ThrowIfNull(disk2);
        return Spec(systemRom, iou);   // the IOU (holding disk2) Realizes it; no extra slot needed
    }
}
