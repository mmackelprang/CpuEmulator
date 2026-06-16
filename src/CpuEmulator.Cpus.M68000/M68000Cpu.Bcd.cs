namespace CpuEmulator.Cpus.M68000;

/// <summary>
/// M4.5c BCD (ADR 0007 §7.1 — the descriptor fits via the XAlu SHAPE + a new CCR rule, ZERO new shape). ABCD/
/// SBCD = decimal add/sub with X-in, .b only, sticky Z; the operand shape (bit 3: Dn-Dn vs -(An)-(An)) is
/// IDENTICAL to ADDX/SUBX, so BcdXAlu mirrors the merged XAlu but with decimal funcs + the BCD carry. NBCD =
/// 0 - dst - X decimal, .b, UnaryEa. The decimal carry drives C and X; Z is sticky. The "undefined N but
/// vector-pinned" 68000 quirk is reconciled in BcdCcr against the ABCD/SBCD/NBCD vectors. Seam untouched.
/// </summary>
public sealed partial class M68000Cpu
{
    /// <summary>BCD CCR: C=X=decimal carry-out (explicit input); Z STICKY (cleared on non-zero, preserved on
    /// zero — never freshly set); N from .b MSB and V=0 are the "undefined but vector-pinned" pair (reconcile
    /// in Task 22). carryOut is the decimal carry from the body.</summary>
    public static class BcdCcr
    {
        public static byte Bcd(uint result, bool carryOut, byte oldCcr)
        {
            byte ccr = (byte)(oldCcr & ~0x0F);                 // clear N Z V C; X handled below
            if ((result & 0x80u) != 0) ccr |= 0x08;            // N from .b MSB (vector-pinned)
            // V left 0 (vector-pinned for the common path; confirm in Task 22).
            if (carryOut) ccr |= 0x01;                          // C
            ccr = (byte)((ccr & ~0x10) | (carryOut ? 0x10 : 0x00));   // X = C
            // Sticky Z: clear it, then preserve oldCcr's Z only when the result byte is zero.
            ccr = (byte)(ccr & ~0x04);
            if ((result & 0xFFu) == 0u) ccr |= (byte)(oldCcr & 0x04);
            return ccr;
        }
        public static byte BcdProbe(uint result, bool carryOut, byte oldCcr) => Bcd(result, carryOut, oldCcr);
    }

    /// <summary>Decimal add of two BCD bytes with X-in. Returns the .b result; outputs the decimal carry.</summary>
    private static uint AbcdByte(uint a, uint b, bool xIn, out bool carry)
    {
        uint lo = (a & 0x0F) + (b & 0x0F) + (xIn ? 1u : 0u);
        uint hi = (a >> 4) + (b >> 4);
        if (lo > 9) { lo += 6; hi += 1; }
        bool c = hi > 9;
        if (c) hi += 6;
        carry = c;
        return ((hi << 4) | (lo & 0x0F)) & 0xFFu;
    }

    /// <summary>Decimal sub (a - b - X). Returns the .b result; outputs the borrow as 'carry' (C=X on borrow).</summary>
    private static uint SbcdByte(uint a, uint b, bool xIn, out bool carry)
    {
        int lo = (int)(a & 0x0F) - (int)(b & 0x0F) - (xIn ? 1 : 0);
        int hi = (int)(a >> 4) - (int)(b >> 4);
        if (lo < 0) { lo += 10; hi -= 1; }
        bool borrow = hi < 0;
        if (borrow) hi += 10;
        carry = borrow;
        return (((uint)hi << 4) | ((uint)lo & 0x0F)) & 0xFFu;
    }

    // ABCD/SBCD: bit 3 (R/M): 0 = Dy,Dx (Dn-Dn); 1 = -(Ay),-(Ax) (predecrement). Same shape as ADDX/SUBX.
    partial void AbcdExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BcdXAlu(operword, add: true);
    partial void SbcdExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BcdXAlu(operword, add: false);

    private void BcdXAlu(uint ow, bool add)
    {
        bool xIn = (Ccr & 0x10) != 0;
        byte oldCcr = (byte)(SR & 0xFF);
        uint yReg = (ow >> 9) & 7u;   // Dy / Ay (the dest, operand A)
        uint xReg = ow & 7u;          // Dx / Ax (the source, operand B)
        bool mem  = (ow & 0x0008u) != 0;

        uint a, b, result; bool carry;
        if (!mem)   // Dx,Dy -> Dy (.b)
        {
            a = DataReg(yReg) & 0xFFu;
            b = DataReg(xReg) & 0xFFu;
            result = add ? AbcdByte(a, b, xIn, out carry) : SbcdByte(a, b, xIn, out carry);
            SetDataRegPartial(yReg, result, 0u);
        }
        else        // -(Ax),-(Ay) -> (Ay) : predecrement BOTH (source Ax first, then dest Ay — the pairing)
        {
            uint aAddr = ComputeEa(4u, xReg, 0u, CpuEmulator.Core.Jit.ExtensionWords.None, pureEa: false); // -(Ax)
            b = ReadByteAt(aAddr) & 0xFFu;
            uint dAddr = ComputeEa(4u, yReg, 0u, CpuEmulator.Core.Jit.ExtensionWords.None, pureEa: false); // -(Ay)
            a = ReadByteAt(dAddr) & 0xFFu;
            result = add ? AbcdByte(a, b, xIn, out carry) : SbcdByte(a, b, xIn, out carry);
            WriteByteAt(dAddr, (byte)result);
        }
        SR = (ushort)((SR & 0xFF00) | BcdCcr.Bcd(result, carry, oldCcr));
    }

    // NBCD: 0 - dst - X (decimal), .b, UnaryEa (the EA is both source and dest).
    partial void NbcdExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        bool xIn = (Ccr & 0x10) != 0;
        byte oldCcr = (byte)(SR & 0xFF);
        AluDest dest = ResolveEaDest(srcMode, srcReg, 0u, r.ExtensionWords, out uint operand);
        uint result = SbcdByte(0u, operand & 0xFFu, xIn, out bool carry);    // 0 - operand - X
        WriteResolvedDest(dest, 0u, result);
        SR = (ushort)((SR & 0xFF00) | BcdCcr.Bcd(result, carry, oldCcr));
    }
}
