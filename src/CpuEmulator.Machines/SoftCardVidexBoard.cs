using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Machines;

/// <summary>The Microsoft Z-80 SoftCard + Videx Videoterm board — the CP/M-display capstone (ADR 0016,
/// PR-O). Composes the dual-CPU SoftCard machinery (PR-K: the Z80 CoprocessorSpec + the $C500
/// SoftCardControlPort) AND the Videx 80-column card (PR-N: the $C800 firmware window + the $CC00 banked
/// VRAM + the "videx" slot + the IOU $C0Bx delegate) over one base Apple board. CP/M runs on the Z80
/// (translated against shared 6502 RAM) and drives the Videx terminal; the Videx's ActiveChanged signal
/// (consumed by the surface's DisplayMultiplexer) switches the host display Apple-40 -> Videx-80. The
/// $C000-$CFFF band is carved so every window is validator-clean: the IOU at $C000, the $C500 control
/// port, the $C600 disk-boot ROM, the $C800 Videx firmware slot, and the $CC00 VRAM RAM window all
/// coexist. BoardMachineFactory builds the Z80 on the INTERPRETER tier (ADR 0015 Decision 4 — no
/// JIT-under-translation). The 6502 is bus master at reset; the Z80 is dormant until the boot loader's
/// $CnXX write.</summary>
public static class SoftCardVidexBoard
{
    /// <summary>Compose the dual-CPU + Videx CP/M board.</summary>
    /// <param name="systemRom">The 12 KiB Apple ][+ system ROM ($D000-$FFFF).</param>
    /// <param name="iou">The IOU holding the LC + Disk II + Videx (new Apple2Iou(state, lc, disk2, videx)).</param>
    /// <param name="disk2">The Disk II controller (drive 1 holds the CP/M .dsk).</param>
    /// <param name="diskBootRom">The 256 B slot-6 $C600 Disk II boot ROM (the Autostart cold-boot entry).</param>
    /// <param name="videx">The Videx Videoterm (the same instance the IOU delegates $C0Bx to).</param>
    /// <param name="controlPortBase">The page the SoftCard control port decodes (ADR 0018 Decision 1).
    /// Defaults to <see cref="SoftCardBoard.ControlPortBaseSlot5"/> ($C500) so the shipped Videx board is
    /// byte-for-byte unchanged; apl2cpm3 passes <see cref="SoftCardBoard.ControlPortBaseSlot4"/> ($C400).</param>
    public static BoardSpec Spec(byte[] systemRom, Apple2Iou iou, Apple2DiskII disk2, byte[] diskBootRom,
                                 VidexVideoterm videx,
                                 uint controlPortBase = SoftCardBoard.ControlPortBaseSlot5)
    {
        ArgumentNullException.ThrowIfNull(systemRom);
        ArgumentNullException.ThrowIfNull(iou);
        ArgumentNullException.ThrowIfNull(disk2);
        ArgumentNullException.ThrowIfNull(diskBootRom);
        ArgumentNullException.ThrowIfNull(videx);
        if (systemRom.Length != Apple2Board.RomLength)
            throw new ArgumentException(
                $"Apple ][+ system ROM must be exactly ${Apple2Board.RomLength:X} bytes; "
              + $"got ${systemRom.Length:X}.", nameof(systemRom));
        if (diskBootRom.Length != Apple2Board.DiskBootRomLength)
            throw new ArgumentException(
                $"Disk II boot ROM must be exactly ${Apple2Board.DiskBootRomLength:X} bytes; "
              + $"got ${diskBootRom.Length:X}.", nameof(diskBootRom));
        // The slot must be page-aligned and inside the $C000-$CFFF I/O band, AND must not overlap the
        // $C600-$C6FF disk-boot-ROM window (a Rom region, not Mmio — a peripheral there would fail
        // BoardSpecValidator's slot-not-in-mmio check). ADR 0018 Decision 1.
        if (controlPortBase % SoftCardBoard.ControlPortLength != 0
            || controlPortBase < Apple2Board.IoBase
            || controlPortBase + SoftCardBoard.ControlPortLength > Apple2Board.IoBase + Apple2Board.IoLength
            || (controlPortBase < Apple2Board.DiskBootRomBase + Apple2Board.DiskBootRomLength
                && controlPortBase + SoftCardBoard.ControlPortLength > Apple2Board.DiskBootRomBase))
            throw new ArgumentOutOfRangeException(nameof(controlPortBase),
                $"the SoftCard control-port base must be a page-aligned address in the $C000-$CFFF "
              + $"I/O band and must not overlap the $C600-$C6FF disk-boot-ROM window; "
              + $"got ${controlPortBase:X}.");

        var controlPort = new SoftCardControlPort();
        var coprocessor = new CoprocessorSpec(
            CpuKind.Z80, new SoftCardTranslation(),
            SoftCardBoard.ControlPortName, SoftCardBoard.Z80ClockRatioToPrimary);

        return new BoardSpec("softcard-videx-cpm", CpuKind.Mos6502, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(Apple2Board.RamBase, Apple2Board.RamLength, RegionKind.Ram),     // $0000-$BFFF
                new MemoryRegion(Apple2Board.IoBase,                                              // $C000-$C5FF I/O
                    Apple2Board.DiskBootRomBase - Apple2Board.IoBase, RegionKind.Mmio),
                new MemoryRegion(Apple2Board.DiskBootRomBase, Apple2Board.DiskBootRomLength,      // $C600-$C6FF Rom
                    RegionKind.Rom, diskBootRom),
                new MemoryRegion(Apple2Board.DiskBootRomBase + Apple2Board.DiskBootRomLength,     // $C700-$C7FF I/O
                    Apple2Board.VidexFirmwareBase
                        - (Apple2Board.DiskBootRomBase + Apple2Board.DiskBootRomLength), RegionKind.Mmio),
                new MemoryRegion(Apple2Board.VidexFirmwareBase, Apple2Board.VidexFirmwareLength,  // $C800-$CBFF Videx slot
                    RegionKind.Mmio),
                new MemoryRegion(Apple2Board.VidexVramBase, Apple2Board.VidexVramLength,          // $CC00-$CDFF VRAM
                    RegionKind.Ram),
                new MemoryRegion(Apple2Board.VidexVramBase + Apple2Board.VidexVramLength,         // $CE00-$CFFF I/O
                    Apple2Board.IoBase + Apple2Board.IoLength
                        - (Apple2Board.VidexVramBase + Apple2Board.VidexVramLength), RegionKind.Mmio),
                new MemoryRegion(Apple2Board.RomBase, Apple2Board.RomLength,                      // $D000-$FFFF Rom
                    RegionKind.Rom, systemRom),
            ],
            Peripherals:
            [
                new PeripheralSlot("iou", iou, Apple2Board.IouBase, Apple2Board.IouLength),       // $C000 page
                new PeripheralSlot(SoftCardBoard.ControlPortName, controlPort,                    // slot control port
                    controlPortBase, SoftCardBoard.ControlPortLength),
                new PeripheralSlot("videx", videx,                                               // $C800 Videx slot
                    Apple2Board.VidexFirmwareBase, Apple2Board.VidexFirmwareLength),
            ],
            Irq: IrqWiring.None,
            Reset: ResetConfig.None,
            Coprocessor: coprocessor,
            // The 6502 is bus master; the scheduler clock is the primary (virtual 6502) domain, so the
            // real-time ratio is against the Apple clock (the Z80's faster rate folds in via the ratio).
            NominalClockHz: Apple2Board.NominalClockHz);
    }
}
