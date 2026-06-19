using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Machines;

/// <summary>The uniform reference-board recipe (machine-model spec §5): one convention, several CPUs.
/// A memory-mapped UART + interval timer at fixed MMIO addresses, both IRQs wired to the CPU's maskable
/// interrupt, scaled to each CPU's address width — with the ROM placed where each CPU BOOTS:
/// <list type="bullet">
///   <item>6502/Z80 (16-bit): RAM low, MMIO at $C000, ROM high at $E000 (the 6502 reads $FFFC there; the
///         Z80 runs from RAM at $0).</item>
///   <item>68000 (24-bit): ROM LOW at $0 (carrying the reset vectors at $0/$4 + the boot program), MMIO,
///         then RAM high — the 68000 fetches its SSP/PC vectors from low memory.</item>
///   <item>8086 (20-bit): RAM low, MMIO, ROM HIGH ending at $100000 (covering the 0xFFFF0 reset entry) —
///         like the 6502's high reset vector.</item>
/// </list>
/// Reset is not cycle-gated (no TomHarte reset vectors exist); functionally-correct landed state is the bar.</summary>
public static class ReferenceSbc
{
    // ── The 16-bit convention (6502/Z80), unchanged from piece #1. ──────────────────────────────────
    private const uint RamBase16 = 0x0000;
    private const uint MmioBase16 = 0xC000;
    private const uint MmioLength16 = 0x1000;   // $C000-$CFFF: the UART + timer slots
    private const uint UartBase16 = 0xC000;
    private const uint TimerBase16 = 0xC100;
    private const uint RomBase16 = 0xE000;
    private const uint RomLength16 = 0x2000;     // $E000-$FFFF (8 KiB)
    private const uint RamLength16 = MmioBase16; // $0000-$BFFF (48 KiB), below the MMIO block

    // ── The 68000 convention (24-bit): ROM LOW (vectors + program), MMIO, RAM HIGH. ─────────────────
    private const uint RomBase68k = 0x00_0000;        // ROM at $0 so the $0/$4 reset vectors are in ROM
    private const uint MmioBase68k = 0x01_0000;       // $010000 MMIO block (page-aligned, above ROM)
    private const uint MmioLength68k = 0x1000;        // $010000-$010FFF
    private const uint UartBase68k = 0x01_0000;       // UART DATA at $010000
    private const uint TimerBase68k = 0x01_0100;      // timer at $010100
    private const uint RamBase68k = 0x02_0000;        // RAM from $020000
    private const uint RamLength68k = 0x02_0000;      // 128 KiB RAM (ample for a smoke)

    // ── The 8086 convention (20-bit): RAM LOW, MMIO, ROM HIGH (covers 0xFFFF0). ──────────────────────
    private const uint RamBase86 = 0x0_0000;          // RAM from 0
    private const uint RamLength86 = 0x8_0000;        // 512 KiB RAM
    private const uint MmioBase86 = 0xA_0000;         // $A0000 MMIO block (page-aligned)
    private const uint MmioLength86 = 0x1000;         // $A0000-$A0FFF
    private const uint UartBase86 = 0xA_0000;         // UART DATA at $A0000
    private const uint TimerBase86 = 0xA_0100;        // timer at $A0100
    private const uint RomBase86 = 0xF_0000;          // ROM $F0000-$FFFFF (64 KiB), covers 0xFFFF0

    private static IrqWiring SharedIrq() => new(
    [
        new PeripheralIrq("uart", CpuInterrupt.Irq),
        new PeripheralIrq("timer", CpuInterrupt.Irq),
    ]);

    public static BoardSpec Build(CpuKind cpu, SimpleUart uart, IntervalTimer timer, byte[] rom) => cpu switch
    {
        CpuKind.Mos6502 or CpuKind.Z80 => Build16(cpu, uart, timer, rom),
        CpuKind.M68000 => Build68000(uart, timer, rom),
        CpuKind.I8086 => Build8086(uart, timer, rom),
        _ => throw new NotSupportedException($"ReferenceSbc({cpu}) has no reference-board recipe."),
    };

    private static BoardSpec Build16(CpuKind cpu, SimpleUart uart, IntervalTimer timer, byte[] rom)
    {
        if (rom.Length != RomLength16)
            throw new ArgumentException(
                $"ReferenceSbc({cpu}) ROM image must be exactly ${RomLength16:X} bytes; got ${rom.Length:X}.",
                nameof(rom));

        return new BoardSpec($"ReferenceSbc-{cpu}", cpu, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(RamBase16, RamLength16, RegionKind.Ram),
                new MemoryRegion(MmioBase16, MmioLength16, RegionKind.Mmio),
                new MemoryRegion(RomBase16, RomLength16, RegionKind.Rom, rom),
            ],
            Peripherals:
            [
                new PeripheralSlot("uart", uart, UartBase16, 0x0100),
                new PeripheralSlot("timer", timer, TimerBase16, 0x0100),
            ],
            Irq: SharedIrq(),
            Reset: ResetConfig.None); // Z80 resets to PC=0 (RAM); the 6502 image carries $FFFC.
    }

    private static BoardSpec Build68000(SimpleUart uart, IntervalTimer timer, byte[] rom)
    {
        if (rom.Length != MmioBase68k)   // ROM spans $0 up to the MMIO block ($010000 = 64 KiB)
            throw new ArgumentException(
                $"ReferenceSbc(M68000) ROM image must be exactly ${MmioBase68k:X} bytes; got ${rom.Length:X}.",
                nameof(rom));

        return new BoardSpec("ReferenceSbc-M68000", CpuKind.M68000, AddressBits: 24,
            Memory:
            [
                new MemoryRegion(RomBase68k, MmioBase68k, RegionKind.Rom, rom), // ROM low: vectors + program
                new MemoryRegion(MmioBase68k, MmioLength68k, RegionKind.Mmio),
                new MemoryRegion(RamBase68k, RamLength68k, RegionKind.Ram),
            ],
            Peripherals:
            [
                new PeripheralSlot("uart", uart, UartBase68k, 0x0100),
                new PeripheralSlot("timer", timer, TimerBase68k, 0x0100),
            ],
            Irq: SharedIrq(),
            Reset: ResetConfig.None, // the 68000 reads its SSP/PC vectors from the low ROM image directly.
            Endianness: Endianness.BigEndian); // the 68000 is big-endian: vectors + opcode words are MSB-first.
    }

    private static BoardSpec Build8086(SimpleUart uart, IntervalTimer timer, byte[] rom)
    {
        const uint romLength86 = 0x1_0000; // $F0000-$FFFFF (64 KiB), covers the 0xFFFF0 reset entry
        if (rom.Length != romLength86)
            throw new ArgumentException(
                $"ReferenceSbc(I8086) ROM image must be exactly ${romLength86:X} bytes; got ${rom.Length:X}.",
                nameof(rom));

        return new BoardSpec("ReferenceSbc-I8086", CpuKind.I8086, AddressBits: 20,
            Memory:
            [
                new MemoryRegion(RamBase86, RamLength86, RegionKind.Ram),
                new MemoryRegion(MmioBase86, MmioLength86, RegionKind.Mmio),
                new MemoryRegion(RomBase86, romLength86, RegionKind.Rom, rom), // ROM high: covers 0xFFFF0
            ],
            Peripherals:
            [
                new PeripheralSlot("uart", uart, UartBase86, 0x0100),
                new PeripheralSlot("timer", timer, TimerBase86, 0x0100),
            ],
            Irq: SharedIrq(),
            Reset: ResetConfig.None); // the 8086 jams CS:IP to FFFF:0000; the high ROM carries the entry.
    }
}
