namespace CpuEmulator.Cpus.M68000;

/// <summary>
/// M4.5c bit ops (ADR 0007 §7.1 — the descriptor fits with a new CCR rule + the existing RMW path). BTST/BCHG/
/// BCLR/BSET, dynamic (bit# = Dn bits 11-9) and static (bit# = the extension word low byte). Operand size is
/// EA-mode-selected: a Dn target is .l (bit# mod 32); a memory target is .b (bit# mod 8). CCR: Z from the tested
/// bit; N/V/C/X UNCHANGED. BTST does not write; the others toggle/clear/set then write back (RMW, address-once
/// via ResolveEaDest — the M4.5b double-compute fix). Reuses the merged ALU layer as a caller; seam untouched.
/// </summary>
public sealed partial class M68000Cpu
{
    private enum BitKind { Tst, Chg, Clr, Set }

    /// <summary>The bit-op CCR rule: Z from the tested bit (BEFORE any modification); N/V/C/X kept.</summary>
    public static class BitCcr
    {
        public static byte BitTest(uint operand, int bit, byte oldCcr)
        {
            byte ccr = (byte)(oldCcr & ~0x04);                 // clear Z; keep N V C X
            if (((operand >> bit) & 1u) == 0u) ccr |= 0x04;    // Z = tested bit was 0
            return ccr;
        }
        public static byte BitTestProbe(uint operand, int bit, byte oldCcr) => BitTest(operand, bit, oldCcr);
    }

    /// <summary>The bit-op driver. bitNumber pre-resolved (dynamic or static); writes is false only for BTST.</summary>
    private void BitOpExecute(BitKind kind, int bitNumber, uint srcMode, uint srcReg,
        CpuEmulator.Core.Jit.ExtensionWords ext)
    {
        bool isReg = srcMode == 0u;                    // Dn target -> .l, bit mod 32; memory -> .b, bit mod 8
        uint size = isReg ? 2u : 0u;
        int bit = isReg ? (bitNumber & 31) : (bitNumber & 7);

        AluDest dest = ResolveEaDest(srcMode, srcReg, size, ext, out uint operand);   // address-once read
        SR = (ushort)((SR & 0xFF00) | BitCcr.BitTest(operand, bit, (byte)(SR & 0xFF))); // Z from the tested bit

        if (kind == BitKind.Tst) return;               // BTST: no write
        uint mbit = 1u << bit;
        uint result = kind switch
        {
            BitKind.Chg => operand ^ mbit,
            BitKind.Clr => operand & ~mbit,
            _           => operand | mbit,             // Set
        };
        WriteResolvedDest(dest, size, result);
    }

    // Dynamic: bit# = Dn(bits 11-9). The EA is the target (bits 5-0 = srcMode/srcReg).
    partial void BtstExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BitOpExecute(BitKind.Tst, (int)DataReg((operword >> 9) & 7u), srcMode, srcReg, r.ExtensionWords);
    partial void BchgExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BitOpExecute(BitKind.Chg, (int)DataReg((operword >> 9) & 7u), srcMode, srcReg, r.ExtensionWords);
    partial void BclrExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BitOpExecute(BitKind.Clr, (int)DataReg((operword >> 9) & 7u), srcMode, srcReg, r.ExtensionWords);
    partial void BsetExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BitOpExecute(BitKind.Set, (int)DataReg((operword >> 9) & 7u), srcMode, srcReg, r.ExtensionWords);

    // Static: bit# = the LEADING extension word low byte; the EA's words follow (ShiftExt by 1).
    partial void BtstStaticExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BitOpExecute(BitKind.Tst, (int)(r.ExtensionWords[0] & 0xFFu), srcMode, srcReg, ShiftExt(r.ExtensionWords, 1));
    partial void BchgStaticExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BitOpExecute(BitKind.Chg, (int)(r.ExtensionWords[0] & 0xFFu), srcMode, srcReg, ShiftExt(r.ExtensionWords, 1));
    partial void BclrStaticExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BitOpExecute(BitKind.Clr, (int)(r.ExtensionWords[0] & 0xFFu), srcMode, srcReg, ShiftExt(r.ExtensionWords, 1));
    partial void BsetStaticExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BitOpExecute(BitKind.Set, (int)(r.ExtensionWords[0] & 0xFFu), srcMode, srcReg, ShiftExt(r.ExtensionWords, 1));
}
