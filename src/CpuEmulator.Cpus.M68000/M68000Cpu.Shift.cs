namespace CpuEmulator.Cpus.M68000;

/// <summary>
/// M4.5c (ADR 0007 §7.1 — option C, the modest extension): the shift/rotate helper layer. Shifts set X/C from
/// the LAST BIT SHIFTED OUT (not recoverable from a/result for count>1) and ASL sets V from "the MSB changed
/// during the shift" (intermediate state) — so the shift CCR rules take carry-out + msb-changed as EXPLICIT
/// inputs, and shifts run through a SIBLING ShiftRotateExecute driver (NOT BinaryAluExecute). Count = a register
/// (mod 64), an immediate (1-8), or implicitly 1 (the memory form). Reuses the M4.5b/M4.5a substrate
/// (ResolveEaDest/WriteResolvedDest, SetDataRegPartial, DataReg, SizeMask, the wide bus) — a new caller; the
/// seam is untouched (ADR 0007 §5.4). ShiftCcr is a SIBLING static class (AluCcr is non-partial — not reopened).
/// </summary>
public sealed partial class M68000Cpu
{
    /// <summary>The shift/rotate CCR rules. carryOut/msbChanged are EXPLICIT inputs; countZero handles the
    /// count-0 quirk. CCR bits: X=0x10 N=0x08 Z=0x04 V=0x02 C=0x01.</summary>
    public static class ShiftCcr
    {
        private static uint SignBit(uint size) => size switch { 0u => 0x80u, 1u => 0x8000u, _ => 0x80000000u };
        private static uint Mask(uint size)    => size switch { 0u => 0xFFu, 1u => 0xFFFFu, _ => 0xFFFFFFFFu };

        private static byte NZ(uint result, uint size, byte ccr)
        {
            if ((result & SignBit(size)) != 0) ccr |= 0x08;
            if ((result & Mask(size)) == 0)    ccr |= 0x04;
            return ccr;
        }

        /// <summary>ASL/ASR/LSL/LSR: C=X=last bit out (count>0); count 0 -> C=0, X UNCHANGED. V: ASL=msbChanged,
        /// else 0. N/Z from result.</summary>
        internal static byte Shift(uint result, uint size, bool lastBitOut, bool msbChanged, byte oldCcr, bool countZero)
        {
            byte ccr = (byte)(oldCcr & ~0x0F);            // clear N Z V C; X handled below
            ccr = NZ(result, size, ccr);
            if (msbChanged) ccr |= 0x02;                  // V (ASL only; the driver passes false otherwise)
            if (countZero)
            {
                ccr = (byte)(ccr & ~0x01);                // C = 0
                ccr = (byte)((ccr & ~0x10) | (oldCcr & 0x10));   // X UNCHANGED
            }
            else
            {
                if (lastBitOut) ccr |= 0x01;              // C
                ccr = (byte)((ccr & ~0x10) | (lastBitOut ? 0x10 : 0x00));   // X = C
            }
            return ccr;
        }

        /// <summary>ROL/ROR: C=last bit rotated; X UNTOUCHED; V=0; N/Z from result; count 0 -> C=0.</summary>
        internal static byte Rotate(uint result, uint size, bool lastBitOut, byte oldCcr, bool countZero)
        {
            byte ccr = (byte)(oldCcr & ~0x0F);            // clear N Z V C; keep X
            ccr = NZ(result, size, ccr);
            if (!countZero && lastBitOut) ccr |= 0x01;    // C
            return ccr;                                    // X preserved
        }

        /// <summary>ROXL/ROXR (rotate through X): C=X=last bit out; count 0 -> C=X (current), X unchanged; V=0.</summary>
        internal static byte RotateX(uint result, uint size, bool lastBitOut, byte oldCcr, bool countZero)
        {
            byte ccr = (byte)(oldCcr & ~0x0F);            // clear N Z V C
            ccr = NZ(result, size, ccr);
            bool x = countZero ? (oldCcr & 0x10) != 0 : lastBitOut;
            if (x) ccr |= 0x01;                            // C = X
            ccr = (byte)((ccr & ~0x10) | (x ? 0x10 : 0x00));
            return ccr;
        }

        // Test seams.
        public static byte ShiftProbe(uint result, uint size, bool lastBitOut, bool msbChanged, byte oldCcr, bool countZero = false)
            => Shift(result, size, lastBitOut, msbChanged, oldCcr, countZero);
        public static byte RotateProbe(uint result, uint size, bool lastBitOut, byte oldCcr, bool countZero = false)
            => Rotate(result, size, lastBitOut, oldCcr, countZero);
        public static byte RotateXProbe(uint result, uint size, bool lastBitOut, byte oldCcr, bool countZero = false)
            => RotateX(result, size, lastBitOut, oldCcr, countZero);
    }

    /// <summary>The 8 shift/rotate kinds. The registration decodes operword bit 8 (direction) within each pair.</summary>
    private enum ShiftKind { Asl, Asr, Lsl, Lsr, Rol, Ror, Roxl, Roxr }

    /// <summary>The shift/rotate driver (ADR 0007 §7.1 sibling to BinaryAluExecute). REGISTER form (0xF018):
    /// count = reg(Dn mod 64) or imm(bits 11-9, 0->8) per bit 5, target Dn(bits 2-0), size bits 7-6. MEMORY form
    /// (SHIFT_MEM): count 1, .w, target EA. Captures last-bit-out + (ASL) msbChanged; sets CCR via ShiftCcr.</summary>
    private void ShiftRotateExecute(ShiftKind kind, uint operword, CpuEmulator.Core.Jit.DecodeResult r,
        uint size, uint srcMode, uint srcReg, bool memoryForm)
    {
        uint mask = SizeMask(size);
        byte oldCcr = (byte)(SR & 0xFF);
        bool xIn = (oldCcr & 0x10) != 0;

        int count;
        uint value;
        AluDest dest;
        uint targetDn = operword & 7u;
        if (memoryForm)
        {
            count = 1;
            dest = ResolveEaDest(srcMode, srcReg, size, r.ExtensionWords, out value);   // .w memory RMW (address-once)
            value &= mask;
        }
        else
        {
            bool regCount = (operword & 0x20u) != 0;                  // bit 5: 1 = register count
            if (regCount) count = (int)(DataReg((operword >> 9) & 7u) % 64u);   // Dn mod 64
            else { uint q = (operword >> 9) & 7u; count = q == 0u ? 8 : (int)q; } // imm 1-8 (0->8)
            value = DataReg(targetDn) & mask;
            dest = AluDest.DataRegister(targetDn);
        }

        uint sb = size switch { 0u => 0x80u, 1u => 0x8000u, _ => 0x80000000u };
        uint v = value & mask;
        bool lastBitOut = false, msbChanged = false;
        for (int i = 0; i < count; i++)
        {
            bool msbBefore = (v & sb) != 0;
            switch (kind)
            {
                case ShiftKind.Asl: case ShiftKind.Lsl:
                    lastBitOut = (v & sb) != 0; v = (v << 1) & mask; break;
                case ShiftKind.Asr:
                    lastBitOut = (v & 1u) != 0; v = ((v >> 1) | (msbBefore ? sb : 0u)) & mask; break;   // sign-fill
                case ShiftKind.Lsr:
                    lastBitOut = (v & 1u) != 0; v = (v >> 1) & mask; break;
                case ShiftKind.Rol:
                    lastBitOut = (v & sb) != 0; v = ((v << 1) | (lastBitOut ? 1u : 0u)) & mask; break;
                case ShiftKind.Ror:
                    lastBitOut = (v & 1u) != 0; v = ((v >> 1) | (lastBitOut ? sb : 0u)) & mask; break;
                case ShiftKind.Roxl:
                    lastBitOut = (v & sb) != 0; v = ((v << 1) | (xIn ? 1u : 0u)) & mask; xIn = lastBitOut; break;
                default: /* Roxr */
                    { bool lobit = (v & 1u) != 0; v = ((v >> 1) | (xIn ? sb : 0u)) & mask; lastBitOut = lobit; xIn = lobit; } break;
            }
            if (((v & sb) != 0) != msbBefore) msbChanged = true;
        }
        uint result = v & mask;

        // The non-rotating shifts (ASL/ASR/LSL/LSR) clear C/X when the count exceeds the operand width: the
        // operand bits are fully shifted out, and the 68000 reports the carry-out as 0 (vector-confirmed — for
        // ASR-of-negative the result is sign-filled but C=0). The rotates (ROL/ROR/ROXL/ROXR) wrap, so count
        // beyond the width still has a meaningful last-bit-out and is NOT capped here.
        int widthBits = size == 0u ? 8 : size == 1u ? 16 : 32;
        bool isRotate = kind is ShiftKind.Rol or ShiftKind.Ror or ShiftKind.Roxl or ShiftKind.Roxr;
        if (!isRotate && count > widthBits) lastBitOut = false;

        if (memoryForm) WriteResolvedDest(dest, size, result);
        else SetDataRegPartial(targetDn, result, size);

        bool countZero = count == 0;
        byte ccr = kind switch
        {
            ShiftKind.Asl => ShiftCcr.Shift(result, size, lastBitOut, msbChanged, oldCcr, countZero),
            ShiftKind.Asr or ShiftKind.Lsl or ShiftKind.Lsr
                          => ShiftCcr.Shift(result, size, lastBitOut, msbChanged: false, oldCcr, countZero),
            ShiftKind.Rol or ShiftKind.Ror   => ShiftCcr.Rotate(result, size, lastBitOut, oldCcr, countZero),
            _ /* Roxl/Roxr */                => ShiftCcr.RotateX(result, size, lastBitOut, oldCcr, countZero),
        };
        SR = (ushort)((SR & 0xFF00) | ccr);
    }

    // ── Task 4: ASL/ASR/LSL/LSR register form. bit 8 (dr): 0 = right, 1 = left. The dataset ROW picks the shift
    //    FAMILY; bit 8 picks the direction. ───────────────────────────────────────────────────────────────────
    partial void AslrRegExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => ShiftRotateExecute((operword & 0x0100u) != 0 ? ShiftKind.Asl : ShiftKind.Asr, operword, r, size, srcMode, srcReg, memoryForm: false);
    partial void LslrRegExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => ShiftRotateExecute((operword & 0x0100u) != 0 ? ShiftKind.Lsl : ShiftKind.Lsr, operword, r, size, srcMode, srcReg, memoryForm: false);

    // ── Task 5: ROL/ROR/ROXL/ROXR register form. ───────────────────────────────────────────────────────────
    partial void RolrRegExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => ShiftRotateExecute((operword & 0x0100u) != 0 ? ShiftKind.Rol : ShiftKind.Ror, operword, r, size, srcMode, srcReg, memoryForm: false);
    partial void RoxlrRegExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => ShiftRotateExecute((operword & 0x0100u) != 0 ? ShiftKind.Roxl : ShiftKind.Roxr, operword, r, size, srcMode, srcReg, memoryForm: false);

    // ── Task 6: the memory-by-1 shift form (SHIFT_MEM). .w only, count 1. ───────────────────────────────────
    partial void ShiftMemExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        // .w memory shift-by-1. bits 10-9: 00=AS, 01=LS, 10=ROX, 11=RO. bit 8: 1=left, 0=right.
        bool left = (operword & 0x0100u) != 0;
        uint cls = (operword >> 9) & 3u;
        ShiftKind kind = cls switch
        {
            0u => left ? ShiftKind.Asl  : ShiftKind.Asr,
            1u => left ? ShiftKind.Lsl  : ShiftKind.Lsr,
            2u => left ? ShiftKind.Roxl : ShiftKind.Roxr,
            _  => left ? ShiftKind.Rol  : ShiftKind.Ror,
        };
        ShiftRotateExecute(kind, operword, r, size: 1u /* .w */, srcMode, srcReg, memoryForm: true);
    }
}
