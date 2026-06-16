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
}
