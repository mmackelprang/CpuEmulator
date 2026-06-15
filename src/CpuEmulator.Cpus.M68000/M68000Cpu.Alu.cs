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
        public static uint NegXFn(uint a, uint b, bool x, uint size) => 0u - a - (x ? 1u : 0u);   // 0 - operand - X
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

        // Resolve A (the EA/dest operand), B (the second operand), and WHERE the result goes. The destination
        // is captured as a resolved descriptor so the write does NOT recompute a memory EA (the read-modify-
        // write DOUBLE-COMPUTE fix: ComputeEa(pureEa:false) does the (An)+/-(An) write-back, so calling it again
        // for the write would advance An a SECOND time and write to the wrong address — ADR 0007 finding #2).
        uint a, b;
        AluDest dest;
        switch (shape)
        {
            case AluShape.RegEa:
            {
                uint dnReg = (operword >> 9) & 7u;       // bits 11-9 = the Dn operand
                bool toEa  = (operword & 0x0100u) != 0;  // bit 8 direction: 1 = Dn op <ea> -> <ea>
                uint dn = DataReg(dnReg) & mask;
                if (toEa)
                {
                    // dest = EA. Resolve the EA ONCE (single write-back), read A at it, write the result at it.
                    dest = ResolveEaDest(srcMode, srcReg, size, r.ExtensionWords, out a);
                    a &= mask; b = dn;
                }
                else
                {
                    // dest = Dn. The EA is a pure source read (its own (An)+/-(An) advance happens once, here).
                    a = DataReg(dnReg) & mask;
                    b = ReadEaOperand(srcMode, srcReg, size, r.ExtensionWords) & mask;
                    dest = AluDest.Register(0u, dnReg);
                }
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
                dest = ResolveEaDest(srcMode, srcReg, size, eaExt, out a);   // dest EA value (operand A)
                a &= mask; b = imm & mask;
                break;
            }
            case AluShape.QuickEa:
            {
                uint imm3 = (operword >> 9) & 7u; if (imm3 == 0u) imm3 = 8u;   // 0 -> 8
                dest = ResolveEaDest(srcMode, srcReg, size, r.ExtensionWords, out a);
                a &= mask; b = imm3;
                break;
            }
            default: // UnaryEa — one EA operand (NEG/NOT/TST). b=0; the aluFn ignores the unused arg.
            {
                dest = ResolveEaDest(srcMode, srcReg, size, r.ExtensionWords, out a);
                a &= mask; b = 0u;
                break;
            }
        }

        uint result = aluFn(a, b, xIn, size) & mask;
        if (writesResult) WriteResolvedDest(dest, size, result);
        SR = (ushort)((SR & 0xFF00) | ccrRule(a, b, result, size, xIn, oldCcr));
    }

    /// <summary>A resolved ALU destination: either a data register (Mode 0) or an already-computed memory
    /// address. Capturing the memory address ONCE (at read time) is the read-modify-write double-compute fix
    /// (ADR 0007 finding #2): the (An)+/-(An) write-back is performed exactly once by the read's ComputeEa, and
    /// the write reuses the resolved address rather than recomputing the EA (which would advance An again).</summary>
    private readonly struct AluDest
    {
        public readonly bool IsRegister;
        public readonly uint Mode;       // EA mode (only meaningful when IsRegister) — 0 = Dn
        public readonly uint Reg;        // register index (when IsRegister)
        public readonly uint Address;    // resolved memory address (when !IsRegister)

        private AluDest(bool isReg, uint mode, uint reg, uint addr)
        { IsRegister = isReg; Mode = mode; Reg = reg; Address = addr; }

        public static AluDest Register(uint mode, uint reg) => new(true, mode, reg, 0u);
        public static AluDest Memory(uint addr) => new(false, 0u, 0u, addr);
    }

    /// <summary>Read the EA operand AND resolve its destination in ONE pass. For a register EA (Dn mode 0 / An
    /// mode 1) it reads the register and returns a register dest. For a memory EA it computes the address ONCE
    /// (single (An)+/-(An) write-back), reads the operand at it, and returns a Memory dest carrying that exact
    /// address — so the later write does NOT recompute the EA (the double-compute fix).</summary>
    private AluDest ResolveEaDest(uint mode, uint reg, uint size,
        CpuEmulator.Core.Jit.ExtensionWords ext, out uint operand)
    {
        if (mode == 0u) { operand = DataReg(reg); return AluDest.Register(0u, reg); }   // Dn
        if (mode == 1u) { operand = Areg(reg);    return AluDest.Register(1u, reg); }   // An (full 32)
        uint ea = ComputeEa(mode, reg, size, ext, pureEa: false);   // ONE write-back for (An)+/-(An)
        operand = size switch { 0u => ReadByteAt(ea), 1u => ReadWordBus(ea), _ => ReadLongBus(ea) };
        return AluDest.Memory(ea);
    }

    /// <summary>Write the ALU result to a resolved destination — a register (partial write for .b/.w) or the
    /// already-computed memory address (no second ComputeEa, so no second (An)+/-(An) advance).</summary>
    private void WriteResolvedDest(AluDest dest, uint size, uint result)
    {
        if (dest.IsRegister)
        {
            SetDataRegPartial(dest.Reg, result, size);   // Dn partial write (An is never an ALU-driver dest)
            return;
        }
        switch (size)
        {
            case 0u: WriteByteAt(dest.Address, (byte)result); break;
            case 1u: WriteWordBus(dest.Address, (ushort)result); break;
            default: WriteLongBus(dest.Address, result); break;
        }
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

        /// <summary>The CcrRule delegate instances the registrations pass. The PLAIN add/sub (ADD/SUB/ADDI/
        /// SUBI/ADDQ/SUBQ) do NOT consume the incoming X in their carry/borrow — only the X-variants (ADDX/SUBX,
        /// via ArithXAdd/ArithXSub) do. So Arith is called with xIn:false here; passing the live X would corrupt
        /// the carry/borrow for a non-X op when X happened to be set (TomHarte-confirmed: SUB.w D5,D5 = 0 must
        /// give C=X=0, not C=X=1).</summary>
        public static byte ArithAdd(uint a, uint b, uint r, uint size, bool xIn, byte old) => Arith(a, b, r, size, false, old, isSub: false);
        public static byte ArithSub(uint a, uint b, uint r, uint size, bool xIn, byte old) => Arith(a, b, r, size, false, old, isSub: true);

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

        /// <summary>NEGX CCR = ArithX borrow of (0 - a - X), with sticky Z. a is the ORIGINAL operand.</summary>
        public static byte NegXRule(uint a, uint b, uint r, uint size, bool xIn, byte old)
            => ArithX(0u, a, r, size, xIn, old, isSub: true);

        /// <summary>Compare: Arith-borrow (with xIn:false — CMP never consumes X), but X is NEVER touched
        /// (CMP/CMPA/CMPI do not affect X — the original X is restored).</summary>
        public static byte Cmp(uint a, uint b, uint r, uint size, bool xIn, byte old)
        {
            byte arith = Arith(a, b, r, size, false, old, isSub: true);
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

    // ════════════════════════════════════════════════════════════════════════════════════════════════════════
    // The op-body registrations (ADR 0007 option C). Classic `partial void` (matching the generator-emitted
    // declarations + the MOVE bodies): the regular families are one-line BinaryAluExecute registrations; the
    // irregular tail (ADDA/SUBA/CMPA, EXT, CLR, ADDX/SUBX/NEGX, MULU/MULS, DIVU/DIVS) keeps bespoke bodies.
    // ════════════════════════════════════════════════════════════════════════════════════════════════════════

    // ── Task 3: the regular two-operand reg<->EA families — one line each ──────────────────────────────────────
    // (Partial-method parameter NAMES must match the generator-emitted declaration — operword/r/size/srcMode/
    //  srcReg — per CS8826; the body forwards them positionally to BinaryAluExecute / the bespoke helpers.)
    partial void AddExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BinaryAluExecute(Alu.Add, AluCcr.ArithAdd, AluShape.RegEa, writesResult: true,  operword, r, size, srcMode, srcReg);
    partial void SubExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BinaryAluExecute(Alu.Sub, AluCcr.ArithSub, AluShape.RegEa, writesResult: true,  operword, r, size, srcMode, srcReg);
    partial void AndExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BinaryAluExecute(Alu.And, AluCcr.Logic,    AluShape.RegEa, writesResult: true,  operword, r, size, srcMode, srcReg);
    partial void OrExecute (uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BinaryAluExecute(Alu.Or,  AluCcr.Logic,    AluShape.RegEa, writesResult: true,  operword, r, size, srcMode, srcReg);
    partial void EorExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BinaryAluExecute(Alu.Eor, AluCcr.Logic,    AluShape.RegEa, writesResult: true,  operword, r, size, srcMode, srcReg);
    partial void CmpExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BinaryAluExecute(Alu.Sub, AluCcr.Cmp,      AluShape.RegEa, writesResult: false, operword, r, size, srcMode, srcReg);

    // ── Task 4: address-reg variants. An dest, .w source sign-extends to 32, the op is full-32-bit. ───────────
    partial void AddAExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => AddrAlu(operword, r, size, srcMode, srcReg, Alu.Add, setsCcr: false, writes: true);
    partial void SubAExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => AddrAlu(operword, r, size, srcMode, srcReg, Alu.Sub, setsCcr: false, writes: true);
    partial void CmpAExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => AddrAlu(operword, r, size, srcMode, srcReg, Alu.Sub, setsCcr: true,  writes: false);

    /// <summary>ADDA/SUBA/CMPA shared body: An dest (bits 11-9). The decode walk already REMAPPED the operword's
    /// 1-bit size field (opmode bit 8: 0=.w, 1=.l) to the real operand size index (1=.w, 2=.l) — see
    /// IsAddressRegVariant in the generator — so here size is the genuine .w/.l index. A .w source SIGN-EXTENDS
    /// to 32 and the arithmetic is ALWAYS on the full 32 bits. ADDA/SUBA set no CCR and write An; CMPA sets CCR
    /// (a full-32-bit Cmp) and writes nothing.</summary>
    private void AddrAlu(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg,
        AluFn aluFn, bool setsCcr, bool writes)
    {
        bool isWord = size == 1u;                                // remapped: 1 = .w, 2 = .l
        uint anReg = (operword >> 9) & 7u;
        uint srcRaw = ReadEaOperand(srcMode, srcReg, size, r.ExtensionWords);
        uint src = isWord ? unchecked((uint)(int)(short)(ushort)srcRaw) : srcRaw;   // .w sign-extend to 32
        uint a = Areg(anReg);
        uint result = aluFn(a, src, false, 2u);                  // full 32-bit op (size index 2)
        if (writes) SetAreg(anReg, result);
        if (setsCcr)
            SR = (ushort)((SR & 0xFF00) | AluCcr.Cmp(a, src, result, 2u, false, (byte)(SR & 0xFF)));
    }

    // ── Task 5: the unary core. NEG = 0 - ea (Arith); NOT = ~ea (Logic); TST = compare ea to 0 (Logic, no write).
    partial void NegExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BinaryAluExecute(Alu.NegFn, AluCcr.NegRule, AluShape.UnaryEa, writesResult: true,  operword, r, size, srcMode, srcReg);
    partial void NotExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BinaryAluExecute(Alu.NotFn, AluCcr.Logic,   AluShape.UnaryEa, writesResult: true,  operword, r, size, srcMode, srcReg);
    partial void TstExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BinaryAluExecute(Alu.TstFn, AluCcr.Logic,   AluShape.UnaryEa, writesResult: false, operword, r, size, srcMode, srcReg);

    // ── Task 6: immediate forms (ImmEa). #imm is operand B; the EA is operand A AND the dest (CMPI no write). ──
    partial void AddIExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BinaryAluExecute(Alu.Add, AluCcr.ArithAdd, AluShape.ImmEa, writesResult: true,  operword, r, size, srcMode, srcReg);
    partial void SubIExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BinaryAluExecute(Alu.Sub, AluCcr.ArithSub, AluShape.ImmEa, writesResult: true,  operword, r, size, srcMode, srcReg);
    partial void AndIExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BinaryAluExecute(Alu.And, AluCcr.Logic,    AluShape.ImmEa, writesResult: true,  operword, r, size, srcMode, srcReg);
    partial void OrIExecute (uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BinaryAluExecute(Alu.Or,  AluCcr.Logic,    AluShape.ImmEa, writesResult: true,  operword, r, size, srcMode, srcReg);
    partial void EorIExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BinaryAluExecute(Alu.Eor, AluCcr.Logic,    AluShape.ImmEa, writesResult: true,  operword, r, size, srcMode, srcReg);
    partial void CmpIExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BinaryAluExecute(Alu.Sub, AluCcr.Cmp,      AluShape.ImmEa, writesResult: false, operword, r, size, srcMode, srcReg);

    // ── Task 7: quick forms. imm3 = bits 11-9 (0->8). An dest = full-32-bit, NO CCR; else the QuickEa path. ───
    partial void AddQExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => QuickAlu(operword, r, size, srcMode, srcReg, Alu.Add, AluCcr.ArithAdd);
    partial void SubQExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => QuickAlu(operword, r, size, srcMode, srcReg, Alu.Sub, AluCcr.ArithSub);

    private void QuickAlu(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg,
        AluFn aluFn, CcrRule ccrRule)
    {
        uint imm3 = (operword >> 9) & 7u; if (imm3 == 0u) imm3 = 8u;
        if (srcMode == 1u)   // An dest: full-32-bit, NO CCR (the quick-to-An quirk)
        {
            uint an = Areg(srcReg);
            SetAreg(srcReg, aluFn(an, imm3, false, 2u));
            return;
        }
        // Else: ride the QuickEa driver (it re-reads imm3 the same way + sets CCR).
        BinaryAluExecute(aluFn, ccrRule, AluShape.QuickEa, writesResult: true, operword, r, size, srcMode, srcReg);
    }

    // ── Task 8: EXT — Dn sign-extend (bespoke; no EA). opmode bits 8-6: 010 = .b->.w, 011 = .w->.l. ────────────
    partial void ExtExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint dn = operword & 7u;
        uint opmode = (operword >> 6) & 7u;        // 010 = byte->word, 011 = word->long
        uint cur = DataReg(dn);
        uint result;
        uint resultSize;
        if (opmode == 2u)                    // .b -> .w
        {
            result = unchecked((uint)(int)(sbyte)(byte)cur) & 0xFFFFu;
            SetDataRegPartial(dn, result, 1u);   // write the low word; upper word preserved
            resultSize = 1u;
        }
        else                                 // .w -> .l (opmode 3)
        {
            result = unchecked((uint)(int)(short)(ushort)cur);
            SetDataRegPartial(dn, result, 2u);   // write the whole long
            resultSize = 2u;
        }
        // CCR: N/Z from the (size-relative) result, V=C=0, X untouched.
        SR = (ushort)((SR & 0xFF00) | AluCcr.Logic(0, 0, result, resultSize, false, (byte)(SR & 0xFF)));
    }

    // ── Task 9: CLR — write 0; CCR always Z=1, N=V=C=0, X untouched. The 68000 READS the EA before writing
    //    (the vector-confirmed dummy read; data-axis-invisible but issued so the M4.5d trace matches). The
    //    address-once form: for (An)+/-(An) (modes 3/4) compute the EA ONCE so An advances exactly once. ───────
    partial void ClrExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        if (srcMode is 3u or 4u)
        {
            uint ea = ComputeEa(srcMode, srcReg, size, r.ExtensionWords, pureEa: false);   // single write-back
            switch (size)
            {
                case 0u: _ = ReadByteAt(ea); WriteByteAt(ea, 0); break;
                case 1u: _ = ReadWordBus(ea); WriteWordBus(ea, 0); break;
                default: _ = ReadLongBus(ea); WriteLongBus(ea, 0); break;
            }
        }
        else
        {
            _ = ReadEaOperand(srcMode, srcReg, size, r.ExtensionWords);   // dummy read (Dn/simple-memory)
            WriteEaOperand(srcMode, srcReg, size, 0u, r.ExtensionWords);  // write 0
        }
        byte ccr = (byte)(SR & 0xFF);
        ccr = (byte)((ccr & 0x10) | 0x04);                     // keep X; set Z; clear N/V/C
        SR = (ushort)((SR & 0xFF00) | ccr);
    }

    // ── Task 10: ADDX/SUBX — X-flag in, sticky Z (AluCcr.ArithX). bit 3 (R/M): 0 = Dx op Dy -> Dy;
    //    1 = -(Ax) op -(Ay) -> (Ay). NEGX = 0 - ea - X (UnaryEa, ArithX). ───────────────────────────────────────
    partial void AddXExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => XAlu(operword, size, Alu.AddX, AluCcr.ArithXAdd);
    partial void SubXExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => XAlu(operword, size, Alu.SubX, AluCcr.ArithXSub);

    private void XAlu(uint ow, uint size, AluFn aluFn, CcrRule ccrRule)
    {
        uint mask = SizeMask(size);
        bool xIn = (Ccr & 0x10) != 0;
        byte oldCcr = (byte)(SR & 0xFF);
        uint yReg = (ow >> 9) & 7u;   // Dy / Ay (the dest, operand A)
        uint xReg = ow & 7u;          // Dx / Ax (the source, operand B)
        bool mem  = (ow & 0x0008u) != 0;

        uint a, b, result;
        if (!mem)   // Dx op Dy -> Dy
        {
            a = DataReg(yReg) & mask;
            b = DataReg(xReg) & mask;
            result = aluFn(a, b, xIn, size) & mask;
            SetDataRegPartial(yReg, result, size);
        }
        else        // -(Ax) op -(Ay) -> (Ay) : predecrement BOTH (source Ax first, then dest Ay — the pairing)
        {
            uint aAddr = ComputeEa(4u, xReg, size, CpuEmulator.Core.Jit.ExtensionWords.None, pureEa: false); // -(Ax)
            b = ReadSized(aAddr, size) & mask;
            uint dAddr = ComputeEa(4u, yReg, size, CpuEmulator.Core.Jit.ExtensionWords.None, pureEa: false); // -(Ay)
            a = ReadSized(dAddr, size) & mask;
            result = aluFn(a, b, xIn, size) & mask;
            WriteSized(dAddr, size, result);
        }
        SR = (ushort)((SR & 0xFF00) | ccrRule(a, b, result, size, xIn, oldCcr));
    }

    private uint ReadSized(uint ea, uint size) => size switch { 0u => ReadByteAt(ea), 1u => ReadWordBus(ea), _ => ReadLongBus(ea) };
    private void WriteSized(uint ea, uint size, uint v)
    { switch (size) { case 0u: WriteByteAt(ea, (byte)v); break; case 1u: WriteWordBus(ea, (ushort)v); break; default: WriteLongBus(ea, v); break; } }

    partial void NegXExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => BinaryAluExecute(Alu.NegXFn, AluCcr.NegXRule, AluShape.UnaryEa, writesResult: true, operword, r, size, srcMode, srcReg);

    // ── Task 11: MULU/MULS — Dn.w * ea.w -> Dn.l. CCR: N/Z from the 32-bit result, V=C=0, X untouched. ────────
    partial void MulUExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => Mul(operword, r, srcMode, srcReg, signed: false);
    partial void MulSExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => Mul(operword, r, srcMode, srcReg, signed: true);

    private void Mul(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint srcMode, uint srcReg, bool signed)
    {
        uint dn = (ow >> 9) & 7u;
        uint srcW = ReadEaOperand(srcMode, srcReg, 1u, r.ExtensionWords) & 0xFFFFu;   // .w source
        uint dnW  = DataReg(dn) & 0xFFFFu;
        uint result = signed
            ? unchecked((uint)((int)(short)(ushort)dnW * (int)(short)(ushort)srcW))
            : (dnW * srcW);
        SetDataRegPartial(dn, result, 2u);   // whole-long write
        SR = (ushort)((SR & 0xFF00) | AluCcr.Logic(0, 0, result, 2u, false, (byte)(SR & 0xFF)));  // N/Z, V=C=0, X kept
    }

    // ── Task 12: DIVU/DIVS — Dn.l / ea.w -> quotient(low16) + remainder(high16) in Dn. ÷0 detected here; the
    //    vector-5 EXCEPTION is M4.5d (detect-and-defer: on ÷0 the body returns WITHOUT writing — the real
    //    vector takes the trap, classified deferred by the runner's IsExceptionCase). V on quotient overflow. ──
    partial void DivUExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => Div(operword, r, srcMode, srcReg, signed: false);
    partial void DivSExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
        => Div(operword, r, srcMode, srcReg, signed: true);

    private void Div(uint ow, CpuEmulator.Core.Jit.DecodeResult r, uint srcMode, uint srcReg, bool signed)
    {
        uint dn = (ow >> 9) & 7u;
        uint divisorW = ReadEaOperand(srcMode, srcReg, 1u, r.ExtensionWords) & 0xFFFFu;   // .w divisor
        if (divisorW == 0u)
            return;   // DETECT ÷0; DEFER the vector-5 exception to M4.5d (no write, no CCR change)

        uint dividend = DataReg(dn);
        uint quotient, remainder;
        bool overflow;
        if (!signed)
        {
            ulong q = (ulong)dividend / divisorW;
            remainder = dividend % divisorW;
            overflow = q > 0xFFFFu;
            quotient = (uint)(q & 0xFFFFu);
        }
        else
        {
            int dvd = unchecked((int)dividend);
            int dvs = (int)(short)(ushort)divisorW;
            long q = (long)dvd / dvs;
            int rem = dvd % dvs;
            overflow = q > short.MaxValue || q < short.MinValue;
            quotient = (uint)((int)q & 0xFFFF);
            remainder = (uint)(rem & 0xFFFF);
        }

        byte ccr = (byte)(SR & 0xFF);
        if (overflow)
        {
            // On a DIV overflow (quotient doesn't fit 16 bits) the 68000 sets V=1, clears C, and leaves
            // N/Z/X UNCHANGED (TomHarte-confirmed across DIVU/DIVS); Dn is NOT written.
            ccr = (byte)((ccr & ~0x01) | 0x02);   // C=0, V=1; N/Z/X preserved
            SR = (ushort)((SR & 0xFF00) | ccr);
            return;                                // do NOT write Dn on overflow (vector-confirmed)
        }
        ccr = (byte)(ccr & ~0x0F);   // clear N Z V C; keep X
        if ((quotient & 0x8000u) != 0) ccr |= 0x08;   // N from the 16-bit quotient sign
        if (quotient == 0u) ccr |= 0x04;              // Z
        SR = (ushort)((SR & 0xFF00) | ccr);
        SetDataRegPartial(dn, (remainder << 16) | (quotient & 0xFFFFu), 2u);
    }
}
