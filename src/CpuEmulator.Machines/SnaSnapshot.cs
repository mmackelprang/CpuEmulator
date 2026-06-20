using CpuEmulator.Core;
using CpuEmulator.Cpus.Z80;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Machines;

/// <summary>Loads a 48K ZX Spectrum <c>.SNA</c> snapshot into a built <see cref="Machine"/>. The 48K
/// .SNA is a 27-byte little-endian header (I, HL', DE', BC', AF', HL, DE, BC, IY, IX, IFF2, R, AF, SP,
/// IM, border) followed by 49152 bytes of RAM ($4000-$FFFF). On resume the PC is recovered RETN-style:
/// the snapshot pushed PC onto the stack, so we pop it from SP and advance SP by 2, and copy IFF2 to
/// IFF1. The machine's CPU must be a Z80.</summary>
public static class SnaSnapshot
{
    private const int HeaderLength = 27;
    private const int RamLength = 49152;        // $4000-$FFFF
    private const uint RamBase = 0x4000;

    public static void LoadInto(Machine machine, byte[] sna, SpectrumUla? ula = null)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(sna);
        if (sna.Length != HeaderLength + RamLength)
            throw new InvalidDataException(
                $".SNA must be exactly {HeaderLength + RamLength} bytes (48K format); got {sna.Length}.");
        if (machine.Cpu is not Z80Cpu z80)
            throw new InvalidOperationException(".SNA loading requires a Z80 machine.");

        // Restore RAM first (PC is read back from the restored stack).
        IAddressSpace prog = machine.Space(AddressSpaceKind.Program);
        for (int i = 0; i < RamLength; i++)
            prog.Write8(RamBase + (uint)i, sna[HeaderLength + i]);

        // Restore registers (little-endian).
        z80.I = sna[0x00];
        z80.HL_ = U16(sna, 0x01);
        z80.DE_ = U16(sna, 0x03);
        z80.BC_ = U16(sna, 0x05);
        z80.AF_ = U16(sna, 0x07);
        z80.HL = U16(sna, 0x09);
        z80.DE = U16(sna, 0x0B);
        z80.BC = U16(sna, 0x0D);
        z80.IY = U16(sna, 0x0F);
        z80.IX = U16(sna, 0x11);
        bool iff2 = (sna[0x13] & 0x04) != 0;
        z80.R = sna[0x14];
        z80.AF = U16(sna, 0x15);
        ushort sp = U16(sna, 0x17);
        z80.Im = sna[0x19];

        // Resume: pop PC off the restored stack (RETN idiom), advance SP, copy IFF2 -> IFF1.
        byte pcLo = prog.Read8(sp);
        byte pcHi = prog.Read8((ushort)(sp + 1));
        z80.PC = (ushort)(pcLo | (pcHi << 8));
        z80.SP = (ushort)(sp + 2);
        z80.Iff2 = iff2;
        z80.Iff1 = iff2;

        // The border byte drives the ULA (it is not a Z80 register).
        ula?.SetBorder(sna[0x1A] & 0x07);
    }

    private static ushort U16(byte[] b, int off) => (ushort)(b[off] | (b[off + 1] << 8));
}
