using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Machines;

/// <summary>The Microsoft Z-80 SoftCard board (ADR 0015): the fully-wired Apple ][+ base board
/// (<see cref="Apple2Board.SpecWithSystem"/>) PLUS a Z80 coprocessor that shares the 6502's program RAM
/// under run-one-then-the-other bus arbitration. Composes the SHIPPED dual-CPU seams (PR-I/J): the
/// <see cref="CoprocessorSpec"/> declares the Z80 + the <see cref="SoftCardTranslation"/> 6-branch table +
/// the ~2.0 clock ratio (research §3) + the control-port name; the <see cref="SoftCardControlPort"/> is a
/// $C500 (slot 5) peripheral whose $CnXX write toggles the active CPU. BoardMachineFactory builds the Z80
/// on the INTERPRETER tier regardless of board tier (ADR 0015 Decision 4 — JIT-under-translation is the
/// deferred PR-L). The 6502 is the bus master at reset (the Z80 is dormant until the boot loader issues
/// the $CnXX start write). The single-CPU base board is unchanged — this only ADDS the Coprocessor field
/// + the control-port slot.</summary>
public static class SoftCardBoard
{
    /// <summary>The shipped 2.2 SoftCard control-port page: $C500 = slot 5 (a documented SoftCard slot,
    /// research §1). It sits inside the $C000-$C5FF Mmio region SpecWithSystem's I/O-band carve leaves, so the
    /// slot is validator-clean (fully contained in Mmio). The Z80 sees it at $E500 (translation branch 5,
    /// $E000->$C000). This is the DEFAULT so the 2.2 board is byte-for-byte unchanged (ADR 0018 Decision 1).</summary>
    public const uint ControlPortBaseSlot5 = 0xC500;

    /// <summary>The apl2cpm3 SoftCard control-port page: $C400 = slot 4 (README: SoftCard in slot 4). ADR 0018
    /// Decision 1 — apl2cpm3 hard-codes STA $C400 to start the Z80 (live: 8D 00 C4 on track 0). $C400 (len $100)
    /// is also fully contained in the $C000-$C5FF Mmio region, so the slot-4 board is validator-clean too. The
    /// Z80 sees this at $E400 (translation branch 5, $E000->$C000) — symmetric with $C500.</summary>
    public const uint ControlPortBaseSlot4 = 0xC400;

    /// <summary>The shipped default control-port page (slot 5 / $C500). Retained as an alias so any existing
    /// reference to <c>ControlPortBase</c> compiles unchanged.</summary>
    public const uint ControlPortBase = ControlPortBaseSlot5;
    public const uint ControlPortLength = 0x0100;

    /// <summary>The Z80 SoftCard runs at ~2.04 MHz vs the 6502's ~1.02 MHz (research §3) — ~2x.</summary>
    public const double Z80ClockRatioToPrimary = 2.0;

    /// <summary>The control-port peripheral name; MUST match the CoprocessorSpec.ControlPortPeripheral so
    /// BoardSpecValidator's copro-control-port-unwired check passes (PR-I).</summary>
    public const string ControlPortName = "softcard";

    /// <summary>Compose the dual-CPU SoftCard BoardSpec from the base SpecWithSystem board.</summary>
    /// <param name="systemRom">The 12 KiB Apple ][+ system ROM ($D000-$FFFF).</param>
    /// <param name="iou">The IOU holding the LC + Disk II (same caller contract as SpecWithSystem).</param>
    /// <param name="disk2">The Disk II controller (drive 1 holds the CP/M .dsk when booting CP/M).</param>
    /// <param name="diskBootRom">The 256 B slot-6 $C600 Disk II boot ROM (the Autostart cold-boot entry).</param>
    /// <param name="controlPortBase">The page the SoftCard control port decodes (ADR 0018 Decision 1).
    /// Defaults to <see cref="ControlPortBaseSlot5"/> ($C500) so the shipped 2.2 board is byte-for-byte
    /// unchanged; apl2cpm3 passes <see cref="ControlPortBaseSlot4"/> ($C400). MUST be page-aligned and lie in
    /// the $C000-$CFFF I/O band so the BoardSpecValidator's slot-not-in-mmio check passes.</param>
    public static BoardSpec Spec(byte[] systemRom, Apple2Iou iou, Apple2DiskII disk2, byte[] diskBootRom,
                                 uint controlPortBase = ControlPortBaseSlot5)
    {
        ArgumentNullException.ThrowIfNull(systemRom);
        ArgumentNullException.ThrowIfNull(iou);
        ArgumentNullException.ThrowIfNull(disk2);
        ArgumentNullException.ThrowIfNull(diskBootRom);
        // The slot must be page-aligned and inside the $C000-$CFFF I/O band, AND must not overlap the
        // $C600-$C6FF disk-boot-ROM window (a Rom region, not Mmio — a peripheral there would fail
        // BoardSpecValidator's slot-not-in-mmio check). ADR 0018 Decision 1.
        if (controlPortBase % ControlPortLength != 0
            || controlPortBase < Apple2Board.IoBase
            || controlPortBase + ControlPortLength > Apple2Board.IoBase + Apple2Board.IoLength
            || (controlPortBase < Apple2Board.DiskBootRomBase + Apple2Board.DiskBootRomLength
                && controlPortBase + ControlPortLength > Apple2Board.DiskBootRomBase))
            throw new ArgumentOutOfRangeException(nameof(controlPortBase),
                $"the SoftCard control-port base must be a page-aligned address in the $C000-$CFFF "
              + $"I/O band and must not overlap the $C600-$C6FF disk-boot-ROM window; "
              + $"got ${controlPortBase:X}.");

        BoardSpec baseSpec = Apple2Board.SpecWithSystem(systemRom, iou, disk2, diskBootRom);

        var controlPort = new SoftCardControlPort();
        var controlSlot = new PeripheralSlot(ControlPortName, controlPort, controlPortBase, ControlPortLength);
        var coprocessor = new CoprocessorSpec(
            CpuKind.Z80, new SoftCardTranslation(), ControlPortName, Z80ClockRatioToPrimary);

        // Additive: add the control-port slot + the coprocessor declaration; everything else is the
        // shipped base board (BoardSpec is a record — `with` keeps the base spec immutable).
        return baseSpec with
        {
            Peripherals = [.. baseSpec.Peripherals, controlSlot],
            Coprocessor = coprocessor,
        };
    }
}
