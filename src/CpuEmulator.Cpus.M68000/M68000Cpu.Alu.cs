namespace CpuEmulator.Cpus.M68000;

/// <summary>
/// M4.5b (ADR 0007 option C): the table-driven integer-ALU helper layer. ONE BinaryAluExecute driver does
/// read-A / read-B (per the AluShape) / write-result / set-CCR (per the CcrRule) for the whole regular core;
/// the regular families are one-line *Execute registrations. The CCR rules (Ccr.Arith/Logic/Cmp/ArithX) are
/// written + tested ONCE — the dominant ALU TomHarte-failure class. The irregular tail (EXT/CLR/ADDX/SUBX/
/// NEGX/MULU/MULS/DIVU/DIVS) keeps bespoke bodies. Reuses the M4.5a substrate verbatim (ReadEaOperand,
/// WriteEaOperand, SetDataRegPartial, DataReg, SizeMask, ComputeEa, the wide bus) — this layer is a new CALLER,
/// nothing in those primitives changes (ADR 0007 §5.2/§5.4 — the seam is untouched).
/// </summary>
public sealed partial class M68000Cpu
{
    // ── The per-family descriptor types (ADR 0007 §5.1) ──────────────────────────────────────────────────────
    /// <summary>The per-family ALU function: (a, b, xIn, size) -> result. Pure; no state, no CCR.</summary>
    public delegate uint AluFn(uint a, uint b, bool xIn, uint size);

    /// <summary>The per-family CCR rule: (a, b, result, size, xIn, oldCcr) -> the new CCR byte. One instance
    /// per CCR family (Arith / Logic / Cmp / ArithX).</summary>
    public delegate byte CcrRule(uint a, uint b, uint result, uint size, bool xIn, byte oldCcr);

    /// <summary>Where operand A and B come from + where the result goes (ADR 0007 §5.1).</summary>
    private enum AluShape { RegEa, ImmEa, QuickEa, UnaryEa }

    /// <summary>Pure ALU functions — the per-family content that differs by ONE line. (Carry/overflow live in
    /// the CcrRule; these compute only the result value, full-width then masked by the body.) Add/Sub here are
    /// the NO-X regular forms; AddX/SubX (Task 10) honor the incoming X.</summary>
    public static class Alu
    {
        public static uint Add(uint a, uint b, bool x, uint size) => a + b;
        public static uint Sub(uint a, uint b, bool x, uint size) => a - b;
        public static uint And(uint a, uint b, bool x, uint size) => a & b;
        public static uint Or (uint a, uint b, bool x, uint size) => a | b;
        public static uint Eor(uint a, uint b, bool x, uint size) => a ^ b;

        // The with-X arithmetic (ADDX/SUBX) honor the incoming X flag.
        public static uint AddX(uint a, uint b, bool x, uint size) => a + b + (x ? 1u : 0u);
        public static uint SubX(uint a, uint b, bool x, uint size) => a - b - (x ? 1u : 0u);

        // Unary functions (NEG/NOT/TST). The driver passes a = <ea>, b = 0 for UnaryEa.
        public static uint NegFn(uint a, uint b, bool x, uint size) => 0u - a;       // 0 - operand
        public static uint NotFn(uint a, uint b, bool x, uint size) => ~a;            // bitwise complement
        public static uint TstFn(uint a, uint b, bool x, uint size) => a;             // identity (compare to 0)
    }

    // Test seam (mirrors M4.5a's *Probe wrappers).
    public static uint SizeMaskProbe(uint size) => SizeMask(size);

    /// <summary>The ALU driver (ADR 0007 §5.1). Read operand A and B per <paramref name="shape"/>, apply
    /// <paramref name="aluFn"/>, write the result to the destination (unless <paramref name="writesResult"/> is
    /// false — CMP/CMPI/TST compare-only), set CCR via <paramref name="ccrRule"/>. ONE implementation of
    /// read-A / read-B / write / CCR for the whole regular core. operword/r/size/srcMode/srcReg are the SAME
    /// inputs the generated dispatch passes the MOVE bodies (the seam is unchanged).</summary>
    private void BinaryAluExecute(
        AluFn aluFn, CcrRule ccrRule, AluShape shape, bool writesResult,
        uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint mask = SizeMask(size);
        bool xIn = (Ccr & 0x10) != 0;   // the incoming X flag (only ArithX reads it; harmless for others)
        byte oldCcr = (byte)(SR & 0xFF);

        // Resolve A (operand read first / dest), B (the second operand), and the destination (mode,reg).
        uint a, b, dstMode, dstReg;
        switch (shape)
        {
            case AluShape.RegEa:
            {
                uint dnReg = (operword >> 9) & 7u;       // bits 11-9 = the Dn operand
                bool toEa  = (operword & 0x0100u) != 0;  // bit 8 direction: 1 = Dn op <ea> -> <ea>
                uint dn = DataReg(dnReg) & mask;
                uint ea = ReadEaOperand(srcMode, srcReg, size, r.ExtensionWords);   // the <ea> operand
                if (toEa) { a = ea; b = dn;  dstMode = srcMode; dstReg = srcReg; }   // dest = EA
                else      { a = dn; b = ea;  dstMode = 0u;      dstReg = dnReg;  }   // dest = Dn
                break;
            }
            case AluShape.ImmEa:
            {
                // Immediate forms: the #imm is the LEADING extension word(s); the EA's words follow. The decode
                // walk captured them in stream order, so ext[0..immCount-1] = imm, ext[immCount..] = EA.
                int immCount = size == 2u ? 2 : 1;
                uint imm = size == 2u ? (((uint)r.ExtensionWords[0] << 16) | r.ExtensionWords[1])
                                      : (r.ExtensionWords[0] & mask);
                var eaExt = ShiftExt(r.ExtensionWords, immCount);
                a = ReadEaOperand(srcMode, srcReg, size, eaExt) & mask;   // dest EA value (operand A)
                b = imm & mask;
                dstMode = srcMode; dstReg = srcReg;
                break;
            }
            case AluShape.QuickEa:
            {
                uint imm3 = (operword >> 9) & 7u; if (imm3 == 0u) imm3 = 8u;   // 0 -> 8
                a = ReadEaOperand(srcMode, srcReg, size, r.ExtensionWords) & mask;
                b = imm3;
                dstMode = srcMode; dstReg = srcReg;
                break;
            }
            default: // UnaryEa — one EA operand (NEG/NOT/TST). b=0; the aluFn ignores the unused arg.
            {
                a = ReadEaOperand(srcMode, srcReg, size, r.ExtensionWords) & mask;
                b = 0u;
                dstMode = srcMode; dstReg = srcReg;
                break;
            }
        }

        uint result = aluFn(a, b, xIn, size) & mask;
        if (writesResult) WriteEaOperand(dstMode, dstReg, size, result, r.ExtensionWords);
        SR = (ushort)((SR & 0xFF00) | ccrRule(a, b, result, size, xIn, oldCcr));
    }

    /// <summary>Shift the extension-word buffer down by <paramref name="drop"/> words (used to skip the leading
    /// immediate words so ComputeEa(EA) reads ext[0]/ext[1]). Mirrors M4.5a's DestExtensionWords slice.</summary>
    private static CpuEmulator.Core.Jit.ExtensionWords ShiftExt(CpuEmulator.Core.Jit.ExtensionWords all, int drop)
        => new CpuEmulator.Core.Jit.ExtensionWords(
            all[drop], all[drop + 1], all[drop + 2], all[drop + 3],
            System.Math.Max(0, all.Count - drop));

    /// <summary>The ALU CCR rules — the dominant TomHarte-failure class, written + tested ONCE (ADR 0007 §3).
    /// CCR bits: X=0x10 N=0x08 Z=0x04 V=0x02 C=0x01. The arithmetic carry/overflow rule is parameterized by
    /// isSub (subtraction borrows where addition carries; CMP/SUB share the borrow form). The instances exposed
    /// as CcrRule delegates (Arith/Logic/Cmp/ArithX) close over isSub.
    /// NOTE: named AluCcr (not Ccr) — the M68000Cpu instance already has a live `Ccr` property (the CCR
    /// register accessor); this is the static ALU CCR-rule table.</summary>
    public static class AluCcr
    {
        private static uint SignBit(uint size) => size switch { 0u => 0x80u, 1u => 0x8000u, _ => 0x80000000u };
        private static uint Mask(uint size)    => size switch { 0u => 0xFFu, 1u => 0xFFFFu, _ => 0xFFFFFFFFu };

        /// <summary>Arithmetic NZVCX from a + b (or a - b). X mirrors C. Carry = unsigned carry/borrow out of the
        /// size; V = signed overflow.</summary>
        internal static byte Arith(uint a, uint b, uint result, uint size, bool xIn, byte oldCcr, bool isSub)
        {
            uint m = Mask(size), sb = SignBit(size);
            uint r = result & m;
            bool n = (r & sb) != 0;
            bool z = r == 0;
            bool c, v;
            if (!isSub)
            {
                // add: carry-out if the masked sum overflows the size width (with the X-in for ADDX).
                ulong full = (ulong)(a & m) + (b & m) + (xIn ? 1u : 0u);
                c = (full & ~(ulong)m) != 0;
                v = (((a ^ r) & (b ^ r)) & sb) != 0;          // both inputs' sign differs from result's sign
            }
            else
            {
                // sub: borrow if a < b (+ X-in for SUBX).
                ulong sub = (ulong)(b & m) + (xIn ? 1u : 0u);
                c = (ulong)(a & m) < sub;                     // borrow out
                v = (((a ^ (b & m)) & (a ^ r)) & sb) != 0;    // a and b differ in sign AND a differs from result
            }
            byte ccr = (byte)(oldCcr & ~0x1F);
            if (n) ccr |= 0x08;
            if (z) ccr |= 0x04;
            if (v) ccr |= 0x02;
            if (c) ccr |= 0x01;
            if (c) ccr |= 0x10; else ccr &= unchecked((byte)~0x10);   // X = C
            return ccr;
        }

        /// <summary>The CcrRule delegate instances the registrations pass.</summary>
        public static byte ArithAdd(uint a, uint b, uint r, uint size, bool xIn, byte old) => Arith(a, b, r, size, xIn, old, isSub: false);
        public static byte ArithSub(uint a, uint b, uint r, uint size, bool xIn, byte old) => Arith(a, b, r, size, xIn, old, isSub: true);

        /// <summary>Logic NZ; V=C=0; X untouched.</summary>
        public static byte Logic(uint a, uint b, uint r, uint size, bool xIn, byte old)
        {
            uint m = Mask(size), sb = SignBit(size);
            byte ccr = (byte)(old & ~0x0F);   // clear N Z V C; keep X (bit 4)
            if ((r & sb) != 0) ccr |= 0x08;
            if ((r & m) == 0) ccr |= 0x04;
            return ccr;                        // V=C=0 by the clear
        }

        /// <summary>NEG CCR = Arith borrow of (0 - a). a here is the ORIGINAL operand (the driver's operand A).</summary>
        public static byte NegRule(uint a, uint b, uint r, uint size, bool xIn, byte old)
            => Arith(0u, a, r, size, false, old, isSub: true);

        /// <summary>Compare: Arith-borrow but X is NEVER touched (CMP/CMPA/CMPI do not affect X).</summary>
        public static byte Cmp(uint a, uint b, uint r, uint size, bool xIn, byte old)
        {
            byte arith = Arith(a, b, r, size, xIn, old, isSub: true);
            byte keptX = (byte)(old & 0x10);
            return (byte)((arith & ~0x10) | keptX);   // restore the original X
        }

        /// <summary>ArithX (ADDX/SUBX/NEGX): Arith, but Z is STICKY — cleared on a non-zero result, and on a zero
        /// result it is PRESERVED from oldCcr (never freshly SET). isSub picks add vs sub borrow.</summary>
        internal static byte ArithX(uint a, uint b, uint result, uint size, bool xIn, byte oldCcr, bool isSub)
        {
            byte ccr = Arith(a, b, result, size, xIn, oldCcr, isSub);
            uint m = Mask(size);
            bool zResult = (result & m) == 0;
            // Arith() set Z = (r==0). Override with the sticky rule: clear it, then re-OR oldCcr's Z only if zero.
            ccr = (byte)(ccr & ~0x04);
            if (zResult) ccr |= (byte)(oldCcr & 0x04);   // preserve incoming Z when result is zero
            return ccr;
        }
        public static byte ArithXAdd(uint a, uint b, uint r, uint size, bool xIn, byte old) => ArithX(a, b, r, size, xIn, old, isSub: false);
        public static byte ArithXSub(uint a, uint b, uint r, uint size, bool xIn, byte old) => ArithX(a, b, r, size, xIn, old, isSub: true);

        // Test seams.
        public static byte ArithProbe(uint a, uint b, uint result, uint size, bool xIn, byte oldCcr, bool isSub) => Arith(a, b, result, size, xIn, oldCcr, isSub);
        public static byte LogicProbe(uint result, uint size, byte oldCcr) => Logic(0, 0, result, size, false, oldCcr);
        public static byte CmpProbe(uint a, uint b, uint result, uint size, byte oldCcr) => Cmp(a, b, result, size, false, oldCcr);
        public static byte ArithXProbe(uint a, uint b, uint result, uint size, bool xIn, byte oldCcr, bool isSub) => ArithX(a, b, result, size, xIn, oldCcr, isSub);
    }
}
