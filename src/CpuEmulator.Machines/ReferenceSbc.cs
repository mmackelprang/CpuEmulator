using CpuEmulator.Peripherals;

namespace CpuEmulator.Machines;

/// <summary>The uniform reference-board recipe (spec section 5): one convention, several CPUs.
/// RAM in the low range, ROM in the high range (carrying the reset vector / entry), a memory-mapped
/// UART + interval timer at fixed MMIO addresses, both IRQs wired to the CPU's maskable interrupt.
/// Piece #1 ships the recipe and the Z80 board; the 68000/8086 arms are deferred to piece #2 (their
/// cores have no real reset yet). Addresses follow the breadboard convention so the Z80 board reads
/// the same as the 6502 one: RAM $0000-$DFFF, UART $E000, timer $E100, ROM... see per-CPU notes.</summary>
public static class ReferenceSbc
{
    // The shared MMIO convention for a 16-bit reference board.
    private const uint RamBase = 0x0000;
    private const uint MmioBase = 0xC000;
    private const uint MmioLength = 0x1000;   // $C000-$CFFF: the UART + timer slots
    private const uint UartBase = 0xC000;
    private const uint TimerBase = 0xC100;
    private const uint RomBase = 0xE000;
    private const uint RomLength = 0x2000;     // $E000-$FFFF (8 KiB)
    private const uint RamLength = MmioBase;   // $0000-$BFFF (48 KiB), below the MMIO block

    public static BoardSpec Build(CpuKind cpu, SimpleUart uart, IntervalTimer timer, byte[] rom)
    {
        if (cpu is not (CpuKind.Mos6502 or CpuKind.Z80))
            throw new NotSupportedException(
                $"ReferenceSbc({cpu}) is deferred to piece #2: the {cpu} core has no real reset yet. "
              + "Piece #1 ships the 6502 + Z80 reference boards.");

        if (rom.Length != RomLength)
            throw new ArgumentException(
                $"ReferenceSbc ROM image must be exactly ${RomLength:X} bytes; got ${rom.Length:X}.",
                nameof(rom));

        return new BoardSpec($"ReferenceSbc-{cpu}", cpu, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(RamBase, RamLength, RegionKind.Ram),
                new MemoryRegion(MmioBase, MmioLength, RegionKind.Mmio),
                new MemoryRegion(RomBase, RomLength, RegionKind.Rom, rom),
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
            Reset: ResetConfig.None); // Z80 resets to PC=0 (RAM); the 6502 image carries $FFFC.
    }
}
