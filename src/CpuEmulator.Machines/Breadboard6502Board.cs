using CpuEmulator.Peripherals;

namespace CpuEmulator.Machines;

/// <summary>The canonical 6502 breadboard, re-expressed as a declarative BoardSpec (spec section 7,
/// Board #1 — the zero-behavior-change gate). The map is byte-for-byte the hand-wired
/// Breadboard6502's v2 layout: RAM $0000-$CFFF (52 KiB), UART at $D000 (1 page), IntervalTimer at
/// $D100 (1 page), $D200-$DFFF open-bus, ROM $E000-$FFFF (8 KiB). The MMIO region spans the whole
/// $D000 page-block so the two device slots land inside it and the validator passes; the open-bus
/// $D200-$DFFF span is simply left unmapped (no region), reproducing the hand-wired board's
/// open-bus reads. The demo ROM image already carries its $FFFC reset vector, so ResetConfig.None.</summary>
public static class Breadboard6502Board
{
    public const uint UartBase = 0xD000;
    public const uint TimerBase = 0xD100;

    /// <summary>Build the board-spec over a caller-supplied ROM image and the two devices (so the
    /// caller keeps handles to FeedInput / OnTransmit, matching how Breadboard6502 exposes them).</summary>
    public static BoardSpec Spec(byte[] rom, SimpleUart uart, IntervalTimer timer) =>
        new("breadboard6502", CpuKind.Mos6502, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(0x0000, 0xD000, RegionKind.Ram),
                new MemoryRegion(0xD000, 0x1000, RegionKind.Mmio), // $D000-$DFFF: slots + open-bus hole
                new MemoryRegion(0xE000, 0x2000, RegionKind.Rom, rom),
            ],
            Peripherals:
            [
                new PeripheralSlot("uart", uart, UartBase, 0x0100),
                new PeripheralSlot("timer", timer, TimerBase, 0x0100),
            ],
            Irq: new IrqWiring(
            [
                new PeripheralIrq("uart", CpuInterrupt.Irq),
                new PeripheralIrq("timer", CpuInterrupt.Irq),
            ]),
            Reset: ResetConfig.None);
}
