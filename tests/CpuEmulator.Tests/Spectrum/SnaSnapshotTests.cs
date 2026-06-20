using CpuEmulator.Core;
using CpuEmulator.Cpus.Z80;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Spectrum;

public class SnaSnapshotTests
{
    /// <summary>Build a synthetic 48K .SNA: 27-byte header + 49152 bytes RAM. We seed registers + a
    /// known PC pushed on the stack, and a recognizable screen byte so the first frame is assertable.</summary>
    private static byte[] BuildSyntheticSna()
    {
        var sna = new byte[27 + 49152];
        // Header (little-endian).
        sna[0x00] = 0x3F;                 // I = 0x3F (the Spectrum ROM's interrupt vector page)
        WriteU16(sna, 0x01, 0x1111);      // HL'
        WriteU16(sna, 0x03, 0x2222);      // DE'
        WriteU16(sna, 0x05, 0x3333);      // BC'
        WriteU16(sna, 0x07, 0x4444);      // AF'
        WriteU16(sna, 0x09, 0xABCD);      // HL
        WriteU16(sna, 0x0B, 0x1234);      // DE
        WriteU16(sna, 0x0D, 0x5678);      // BC
        WriteU16(sna, 0x0F, 0x9ABC);      // IY
        WriteU16(sna, 0x11, 0xDEF0);      // IX
        sna[0x13] = 0x04;                 // IFF2 (bit 2 set = EI)
        sna[0x14] = 0x7E;                 // R
        WriteU16(sna, 0x15, 0x55AA);      // AF
        WriteU16(sna, 0x17, 0xFF00);      // SP -> points into RAM ($FF00)
        sna[0x19] = 0x01;                 // IM 1
        sna[0x1A] = 0x05;                 // border = cyan(5)

        // RAM block: $4000..$FFFF. The byte at SP / SP+1 is the PC to resume at (RETN-style pop).
        // SP = 0xFF00 → RAM offset (0xFF00 - 0x4000) = 0xBF00. Push PC = 0x8000 (low, high).
        int spOffset = 0xFF00 - 0x4000;
        sna[27 + spOffset + 0] = 0x00;    // PC low
        sna[27 + spOffset + 1] = 0x80;    // PC high → PC = 0x8000

        // A recognizable screen byte at $4000 (offset 0 of the RAM block) + attribute at $5800.
        sna[27 + (0x4000 - 0x4000)] = 0x80;                 // pixel (0,0) ink
        sna[27 + (0x5800 - 0x4000)] = (byte)(2 | (7 << 3)); // red ink, white paper
        return sna;
    }

    private static void WriteU16(byte[] b, int off, ushort v)
    {
        b[off] = (byte)v;
        b[off + 1] = (byte)(v >> 8);
    }

    [Fact]
    public void Sna_restores_registers_ram_and_pops_pc_from_the_restored_stack()
    {
        var blankRom = new byte[SpectrumRom.RomLength];
        Machine machine = SpectrumMachine.Build(blankRom, out _);
        machine.Reset();

        SnaSnapshot.LoadInto(machine, BuildSyntheticSna());

        var z80 = Assert.IsType<Z80Cpu>(machine.Cpu);
        Assert.Equal(0x8000u, z80.PC);     // PC popped from the restored stack
        Assert.Equal(0xFF02u, z80.SP);     // SP incremented by 2 after the pop
        Assert.Equal(0xABCDu, z80.HL);
        Assert.Equal(0x1234u, z80.DE);
        Assert.Equal(0x5678u, z80.BC);
        Assert.Equal(0x55AAu, z80.AF);
        Assert.Equal(0x1111u, z80.HL_);
        Assert.Equal(0x9ABCu, z80.IY);
        Assert.Equal(0xDEF0u, z80.IX);
        Assert.Equal(0x3F, z80.I);
        Assert.Equal(1, z80.Im);
        Assert.True(z80.Iff1);             // IFF2 (bit 2 set) → RETN copies IFF2 to IFF1
        Assert.Equal(0x80, machine.Space(AddressSpaceKind.Program).Read8(0x4000)); // RAM restored
    }

    [Fact]
    public void Sna_first_frame_matches_the_restored_screen()
    {
        var blankRom = new byte[SpectrumRom.RomLength];
        Machine machine = SpectrumMachine.Build(blankRom, out SpectrumUla ula);
        machine.Reset();
        SnaSnapshot.LoadInto(machine, BuildSyntheticSna(), ula);

        var rgba = new uint[SpectrumUla.FullWidth * SpectrumUla.FullHeight];
        ula.RenderInto(rgba);

        // The restored screen byte → red ink at (0,0); the restored border (cyan=5) → border colour.
        Assert.Equal(SpectrumPalette.Colors[2], rgba[SpectrumUla.BorderPx * SpectrumUla.FullWidth + SpectrumUla.BorderPx]);
        Assert.Equal(SpectrumPalette.Colors[5], rgba[0]); // border cyan
    }

    [Fact]
    public void A_wrong_length_sna_is_rejected()
    {
        var blankRom = new byte[SpectrumRom.RomLength];
        Machine machine = SpectrumMachine.Build(blankRom, out _);
        machine.Reset();
        Assert.Throws<InvalidDataException>(() => SnaSnapshot.LoadInto(machine, new byte[100]));
    }
}
