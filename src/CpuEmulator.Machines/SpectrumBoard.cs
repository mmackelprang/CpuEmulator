using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Machines;

/// <summary>
/// The ZX Spectrum 48K as a declarative <see cref="BoardSpec"/>: a Z80 with the 16 KB ROM at
/// $0000-$3FFF, 48 KB RAM at $4000-$FFFF (the screen lives at $4000-$5AFF), and the ULA on the I/O
/// PORT space — a single Io peripheral slot covering the whole 16-bit port range with bit-0-clear
/// decode (the real ULA answers every even port). The Z80 resets to PC=0 (ROM); the ULA raises the
/// 50 Hz IM1 interrupt from its scheduler tick (claimed in Realize). The ULA also implements the SP0
/// display/keyboard/audio host contracts, so a surface drives it directly.
/// </summary>
public static class SpectrumBoard
{
    public const uint RomBase = 0x0000;
    public const uint RomLength = 0x4000;   // 16 KiB
    public const uint RamBase = 0x4000;
    public const uint RamLength = 0xC000;   // 48 KiB ($4000-$FFFF)

    public static BoardSpec Spec(byte[] rom, SpectrumUla ula)
    {
        ArgumentNullException.ThrowIfNull(rom);
        ArgumentNullException.ThrowIfNull(ula);
        if (rom.Length != RomLength)
            throw new ArgumentException(
                $"Spectrum ROM image must be exactly ${RomLength:X} bytes; got ${rom.Length:X}.", nameof(rom));

        return new BoardSpec("zx-spectrum-48k", CpuKind.Z80, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(RomBase, RomLength, RegionKind.Rom, rom),
                new MemoryRegion(RamBase, RamLength, RegionKind.Ram),
                // The whole 16-bit I/O port space is an IoMmio hole the ULA slot fills.
                new MemoryRegion(0x0000, 0x10000, RegionKind.IoMmio, Space: PeripheralSpace.Io),
            ],
            Peripherals:
            [
                new PeripheralSlot("ula", ula, 0x0000, 0x10000, PeripheralSpace.Io),
            ],
            Irq: new IrqWiring([new PeripheralIrq("ula", CpuInterrupt.Irq)]),
            Reset: ResetConfig.None, // the Z80 resets to PC=0 (ROM)
            IoAddressBits: 16);
    }
}
