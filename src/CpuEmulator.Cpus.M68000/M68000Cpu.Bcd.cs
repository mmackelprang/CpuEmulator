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
    /// <summary>BCD CCR: C=X=decimal carry-out + V=overflow (both explicit, vector-derived inputs from the byte
    /// helpers); N from the .b result MSB; Z STICKY (cleared on non-zero, preserved on zero — never freshly set).
    /// V is the 68000's "sign changed by the decimal correction" bit (ABCD: ~sum & full & 0x80; SBCD/NBCD:
    /// sum & ~full & 0x80), surfaced by the byte helpers (vector-pinned in Task 22).</summary>
    public static class BcdCcr
    {
        public static byte Bcd(uint result, bool carryOut, bool overflow, byte oldCcr)
        {
            byte ccr = (byte)(oldCcr & ~0x0F);                 // clear N Z V C; X handled below
            if ((result & 0x80u) != 0) ccr |= 0x08;            // N from .b MSB
            if (overflow) ccr |= 0x02;                          // V (the decimal-correction sign flip)
            if (carryOut) ccr |= 0x01;                          // C
            ccr = (byte)((ccr & ~0x10) | (carryOut ? 0x10 : 0x00));   // X = C
            // Sticky Z: clear it, then preserve oldCcr's Z only when the result byte is zero.
            ccr = (byte)(ccr & ~0x04);
            if ((result & 0xFFu) == 0u) ccr |= (byte)(oldCcr & 0x04);
            return ccr;
        }
        public static byte BcdProbe(uint result, bool carryOut, bool overflow, byte oldCcr)
            => Bcd(result, carryOut, overflow, oldCcr);
    }

    /// <summary>Decimal add of two BCD bytes with X-in (the vector-exact 68000 algorithm). The high-nibble carry
    /// is tested on the RAW binary sum (a+b+x), the low nibble adds +6 when it exceeds 9; outputs the decimal
    /// carry (C=X) and V (the sign-flip the correction introduces: ~sum & full & 0x80).</summary>
    private static uint AbcdByte(uint a, uint b, bool xIn, out bool carry, out bool overflow)
    {
        uint x = xIn ? 1u : 0u;
        uint corf = ((a & 0x0F) + (b & 0x0F) + x) > 9u ? 6u : 0u;
        uint sum = a + b + x;                                   // raw binary
        carry = sum > 0x99u;
        if (carry) corf += 0x60u;
        uint full = sum + corf;                                 // unmasked (may exceed 0xFF)
        overflow = ((~sum) & full & 0x80u) != 0;                // sign 0 -> 1 across the correction
        return full & 0xFFu;
    }

    /// <summary>Decimal sub (a - b - X) (the vector-exact 68000 algorithm). The low nibble subtracts 6 on borrow;
    /// the high borrow (C=X) is tested on (sum - lowCorf); outputs V (sum &amp; ~full &amp; 0x80).</summary>
    private static uint SbcdByte(uint a, uint b, bool xIn, out bool carry, out bool overflow)
    {
        int x = xIn ? 1 : 0;
        int corfLo = ((int)(a & 0x0F) - (int)(b & 0x0F) - x) < 0 ? 6 : 0;
        int sum = (int)a - (int)b - x;                          // raw binary
        // The RESULT high correction (-0x60) follows the FULL binary borrow (sum < 0); the CARRY/borrow FLAG
        // follows the post-low-correction borrow (sum - corfLo < 0) — the two decouple on invalid-BCD inputs
        // (vector-confirmed: e.g. F1-EC has sum>=0 so no high correction, yet C=1).
        bool resultBorrow = sum < 0;
        carry = (sum - corfLo) < 0;
        int corf = corfLo + (resultBorrow ? 0x60 : 0);
        int full = sum - corf;
        overflow = (((uint)sum) & (~(uint)full) & 0x80u) != 0;  // sign 1 -> 0 across the correction
        return (uint)full & 0xFFu;
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

        uint a, b, result; bool carry, overflow;
        if (!mem)   // Dx,Dy -> Dy (.b)
        {
            a = DataReg(yReg) & 0xFFu;
            b = DataReg(xReg) & 0xFFu;
            result = add ? AbcdByte(a, b, xIn, out carry, out overflow) : SbcdByte(a, b, xIn, out carry, out overflow);
            SetDataRegPartial(yReg, result, 0u);
        }
        else        // -(Ax),-(Ay) -> (Ay) : predecrement BOTH (source Ax first, then dest Ay — the pairing)
        {
            uint aAddr = ComputeEa(4u, xReg, 0u, CpuEmulator.Core.Jit.ExtensionWords.None, pureEa: false); // -(Ax)
            b = ReadByteAt(aAddr) & 0xFFu;
            uint dAddr = ComputeEa(4u, yReg, 0u, CpuEmulator.Core.Jit.ExtensionWords.None, pureEa: false); // -(Ay)
            a = ReadByteAt(dAddr) & 0xFFu;
            result = add ? AbcdByte(a, b, xIn, out carry, out overflow) : SbcdByte(a, b, xIn, out carry, out overflow);
            WriteByteAt(dAddr, (byte)result);
        }
        SR = (ushort)((SR & 0xFF00) | BcdCcr.Bcd(result, carry, overflow, oldCcr));
    }

    // NBCD: 0 - dst - X (decimal), .b, UnaryEa (the EA is both source and dest).
    partial void NbcdExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        bool xIn = (Ccr & 0x10) != 0;
        byte oldCcr = (byte)(SR & 0xFF);
        AluDest dest = ResolveEaDest(srcMode, srcReg, 0u, r.ExtensionWords, out uint operand);
        uint result = SbcdByte(0u, operand & 0xFFu, xIn, out bool carry, out bool overflow);    // 0 - operand - X
        WriteResolvedDest(dest, 0u, result);
        SR = (ushort)((SR & 0xFF00) | BcdCcr.Bcd(result, carry, overflow, oldCcr));
    }
}
