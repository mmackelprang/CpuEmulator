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
    /// <summary>The SoftCard control-port page. $C500 = slot 5 (a documented SoftCard slot, research §1);
    /// it sits inside the $C000-$C5FF Mmio region SpecWithSystem's I/O-band carve leaves, so the slot is
    /// validator-clean (fully contained in Mmio). The Z80 sees it at $E500 (translation branch 5,
    /// $E000->$C000) — consistent with PR-J's "the Z80's matching write, which it sees as $EN00".</summary>
    public const uint ControlPortBase = 0xC500;
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
    public static BoardSpec Spec(byte[] systemRom, Apple2Iou iou, Apple2DiskII disk2, byte[] diskBootRom)
    {
        ArgumentNullException.ThrowIfNull(systemRom);
        ArgumentNullException.ThrowIfNull(iou);
        ArgumentNullException.ThrowIfNull(disk2);
        ArgumentNullException.ThrowIfNull(diskBootRom);

        BoardSpec baseSpec = Apple2Board.SpecWithSystem(systemRom, iou, disk2, diskBootRom);

        var controlPort = new SoftCardControlPort();
        var controlSlot = new PeripheralSlot(ControlPortName, controlPort, ControlPortBase, ControlPortLength);
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
