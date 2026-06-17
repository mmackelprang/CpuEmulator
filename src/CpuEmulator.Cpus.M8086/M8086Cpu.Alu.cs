using System.Numerics;

namespace CpuEmulator.Cpus.M8086;

/// <summary>
/// M5.5b — the 8086 integer ALU + the flag-computation core (hand-written). Reuses the M5.5a MOV EA pipeline
/// (decode → ModR/M → EA → segment → bus, via the helpers in M8086Cpu.Mov.cs / M8086Cpu.Ea.cs) and adds the
/// arithmetic/logical bodies + the cycle-correct flag set. The generated <c>ExecuteX86</c> dispatches the keys
/// for ADD/ADC/SUB/SBB/CMP/AND/OR/XOR/INC/DEC/TEST/NOT/NEG/MUL/IMUL/DIV/IDIV here.
///
/// <para><b>The flag core (the densest correctness pocket — TomHarte is unforgiving).</b> The 8086 FLAGS bits
/// (M8086Spec layout): CF=0, PF=2, AF=4 (the spec's <c>H</c> member — the BCD half-carry), ZF=6, SF=7,
/// OF=11. Parity is over the LOW 8 bits of the result (byte AND word). Aux/AF is the carry/borrow out of bit 3.
/// Overflow is the signed-overflow predicate (sign-bit width-aware). Undefined flags (e.g. AF on logical ops,
/// SF/ZF/PF on MUL/DIV) are left as the natural fallout — the TomHarte flags-mask excludes them from the
/// comparison, so only the DEFINED flags must match.</para>
///
/// <para><b>Divide error — honest deferral (M5.5b).</b> DIV/IDIV (F6 /6 /7, F7 /6 /7) and AAM with base 0
/// raise INT0 (the divide-error vector). The 8086 interrupt seam is M5.5d, so M5.5b does NOT fake the push:
/// when a divide-error condition is detected, the body routes to <see cref="HandleUndefinedOpcode"/> with a
/// clear disclosure comment and leaves register state UNCHANGED (the case then fails HONESTLY rather than
/// silently producing wrong state). The NON-erroring (valid-quotient) DIV/IDIV cases compute correctly and go
/// green. The data-axis gate (Task 5) classifies the INT0-vector cases as a counted, disclosed deferral.</para>
/// </summary>
public sealed partial class M8086Cpu
{
    // ── FLAGS bit masks (the 8086 layout pinned by M8086Spec.Flags). ────────────────────────────────────
    private const ushort FlagCF = 1 << 0;    // carry
    private const ushort FlagPF = 1 << 2;    // parity (of the low 8 bits)
    private const ushort FlagAF = 1 << 4;    // auxiliary / BCD half-carry (the spec's `H` member)
    private const ushort FlagZF = 1 << 6;    // zero
    private const ushort FlagSF = 1 << 7;    // sign (top bit of the width)
    private const ushort FlagOF = 1 << 11;   // signed overflow (the spec's `V` member)

    /// <summary>Set or clear a FLAGS bit mask in the whole-FLAGS word.</summary>
    private void SetFlag(ushort mask, bool on)
    {
        if (on) FLAGS = (ushort)(FLAGS | mask);
        else FLAGS = (ushort)(FLAGS & ~mask);
    }

    /// <summary>PF = 1 when the LOW 8 bits of the result have an EVEN number of set bits (true for both byte
    /// AND word results — always the low byte). PopCount &amp; 1 == 0 ⇒ even ⇒ parity set.</summary>
    private static bool ParityEven(uint result) => (BitOperations.PopCount(result & 0xFFu) & 1) == 0;

    /// <summary>Set SF/ZF/PF from a width-masked result. <paramref name="width16"/> picks the sign bit
    /// (bit15 vs bit7). Shared by every arithmetic/logical op (the logical ops also clear CF/OF separately).</summary>
    private void SetSzp(uint result, bool width16)
    {
        ushort signBit = width16 ? (ushort)0x8000 : (ushort)0x80;
        uint widthMask = width16 ? 0xFFFFu : 0xFFu;
        SetFlag(FlagZF, (result & widthMask) == 0);
        SetFlag(FlagSF, (result & signBit) != 0);
        SetFlag(FlagPF, ParityEven(result));
    }

    // ── The add/sub flag computations. Each takes the two operands + the carry-in (0 for non-carry forms),
    //    sets ALL six relevant flags, and returns the width-masked result. ────────────────────────────────

    /// <summary>8/16-bit ADD-class flag set (ADD/ADC). Carry-in <paramref name="carryIn"/> is 0 for ADD, the
    /// incoming CF for ADC. Sets CF/PF/AF/ZF/SF/OF; returns the width-masked sum.</summary>
    private uint AddFlags(uint a, uint b, uint carryIn, bool width16)
    {
        uint widthMask = width16 ? 0xFFFFu : 0xFFu;
        ushort signBit = width16 ? (ushort)0x8000 : (ushort)0x80;
        uint full = a + b + carryIn;                 // wider intermediate (carry-out lives above the width)
        uint result = full & widthMask;
        SetFlag(FlagCF, (full & (widthMask + 1)) != 0);                 // carry out of bit7/bit15
        SetFlag(FlagAF, ((a ^ b ^ result) & 0x10) != 0);               // carry out of bit 3 → bit 4
        SetFlag(FlagOF, (~(a ^ b) & (a ^ result) & signBit) != 0);     // signed overflow (ADD form)
        SetSzp(result, width16);
        return result;
    }

    /// <summary>8/16-bit SUB-class flag set (SUB/SBB/CMP/NEG). Borrow-in <paramref name="borrowIn"/> is 0 for
    /// SUB/CMP, the incoming CF for SBB. Sets CF(=borrow)/PF/AF/ZF/SF/OF; returns the width-masked difference.</summary>
    private uint SubFlags(uint a, uint b, uint borrowIn, bool width16)
    {
        uint widthMask = width16 ? 0xFFFFu : 0xFFu;
        ushort signBit = width16 ? (ushort)0x8000 : (ushort)0x80;
        uint full = a - b - borrowIn;                // wraps; the borrow lives in the bit above the width
        uint result = full & widthMask;
        SetFlag(FlagCF, (full & (widthMask + 1)) != 0);                // borrow out of bit7/bit15
        SetFlag(FlagAF, ((a ^ b ^ result) & 0x10) != 0);              // borrow out of bit 3
        SetFlag(FlagOF, ((a ^ b) & (a ^ result) & signBit) != 0);     // signed overflow (SUB form)
        SetSzp(result, width16);
        return result;
    }

    /// <summary>Logical-class flag set (AND/OR/XOR/TEST). CF=0, OF=0; SF/ZF/PF from the result. AF is UNDEFINED
    /// on the 8086 for logical ops (the TomHarte mask excludes it) — we clear it here (common silicon behavior),
    /// but its exact value is not asserted. Returns the width-masked result.</summary>
    private uint LogicFlags(uint result, bool width16)
    {
        result &= width16 ? 0xFFFFu : 0xFFu;
        SetFlag(FlagCF, false);
        SetFlag(FlagOF, false);
        SetFlag(FlagAF, false);   // undefined on real 8086 → masked; cleared for determinism.
        SetSzp(result, width16);
        return result;
    }

    /// <summary>INC/DEC flag set: like ADD/SUB of 1 but CF is PRESERVED (INC/DEC do NOT touch carry). Sets
    /// OF/SF/ZF/AF/PF only. Returns the width-masked result.</summary>
    private uint IncDecFlags(uint a, bool decrement, bool width16)
    {
        ushort savedCf = (ushort)(FLAGS & FlagCF);                     // preserve CF across the op
        uint result = decrement ? SubFlags(a, 1, 0, width16) : AddFlags(a, 1, 0, width16);
        FLAGS = (ushort)((FLAGS & ~FlagCF) | savedCf);                 // restore CF
        return result;
    }

    /// <summary>M5.5b: execute one integer-ALU / unary-group instruction. Dispatched by the generated
    /// <c>ExecuteX86</c> with the resolved OperationKey + the full DecodeResult.</summary>
    partial void AluExecute(uint key, CpuEmulator.Core.Jit.DecodeResult r)
    {
        byte modrm = r.X86.ModRm;
        uint mod = (uint)(modrm >> 6) & 3u;
        uint reg = (uint)(modrm >> 3) & 7u;
        uint rm = (uint)modrm & 7u;
        ushort disp = r.X86.Disp;
        X86SegmentOverride over = OverrideFromByte(r.X86.SegOverride);

        switch (key)
        {
            // ── 00-3D: the eight ALU families' r/m,reg + reg,r/m + acc,imm forms. base B + {0..5}. ────────
            // ADD=0x00 OR=0x08 ADC=0x10 SBB=0x18 AND=0x20 SUB=0x28 XOR=0x30 CMP=0x38
            case 0x00: case 0x01: case 0x02: case 0x03: case 0x04: case 0x05:   // ADD
                AluStd(0x00, key, AluOp.Add, mod, reg, rm, disp, over, r); break;
            case 0x08: case 0x09: case 0x0A: case 0x0B: case 0x0C: case 0x0D:   // OR
                AluStd(0x08, key, AluOp.Or, mod, reg, rm, disp, over, r); break;
            case 0x10: case 0x11: case 0x12: case 0x13: case 0x14: case 0x15:   // ADC
                AluStd(0x10, key, AluOp.Adc, mod, reg, rm, disp, over, r); break;
            case 0x18: case 0x19: case 0x1A: case 0x1B: case 0x1C: case 0x1D:   // SBB
                AluStd(0x18, key, AluOp.Sbb, mod, reg, rm, disp, over, r); break;
            case 0x20: case 0x21: case 0x22: case 0x23: case 0x24: case 0x25:   // AND
                AluStd(0x20, key, AluOp.And, mod, reg, rm, disp, over, r); break;
            case 0x28: case 0x29: case 0x2A: case 0x2B: case 0x2C: case 0x2D:   // SUB
                AluStd(0x28, key, AluOp.Sub, mod, reg, rm, disp, over, r); break;
            case 0x30: case 0x31: case 0x32: case 0x33: case 0x34: case 0x35:   // XOR
                AluStd(0x30, key, AluOp.Xor, mod, reg, rm, disp, over, r); break;
            case 0x38: case 0x39: case 0x3A: case 0x3B: case 0x3C: case 0x3D:   // CMP
                AluStd(0x38, key, AluOp.Cmp, mod, reg, rm, disp, over, r); break;

            // ── 84/85: TEST r/m,reg.  A8/A9: TEST acc,imm. ────────────────────────────────────────────────
            case 0x84: AluTestRm(false, mod, reg, rm, disp, over); break;
            case 0x85: AluTestRm(true, mod, reg, rm, disp, over); break;
            case 0xA8: { uint res = (uint)(AL & (byte)r.X86.Imm); LogicFlags(res, false); break; }
            case 0xA9: { uint res = (uint)(AX & r.X86.Imm); LogicFlags(res, true); break; }

            // ── 40-47 INC r16 ; 48-4F DEC r16 (the register-shortcut forms; CF preserved). ─────────────────
            case 0x40: case 0x41: case 0x42: case 0x43: case 0x44: case 0x45: case 0x46: case 0x47:
                SetReg16(key & 7u, (ushort)IncDecFlags(Reg16(key & 7u), decrement: false, width16: true)); break;
            case 0x48: case 0x49: case 0x4A: case 0x4B: case 0x4C: case 0x4D: case 0x4E: case 0x4F:
                SetReg16(key & 7u, (ushort)IncDecFlags(Reg16(key & 7u), decrement: true, width16: true)); break;

            // ── 80/81/83 group: ALU r/m,imm (reg field selects the operation). Keys (op<<3)|reg. ───────────
            //    0x80 → 0x400..0x407 (r/m8,imm8) ; 0x81 → 0x408..0x40F (r/m16,imm16) ;
            //    0x83 → 0x418..0x41F (r/m16, imm8 SIGN-EXTENDED).
            case >= 0x400 and <= 0x407: AluGroupImm(0x80, key, false, false, mod, reg, rm, disp, over, r); break;
            case >= 0x408 and <= 0x40F: AluGroupImm(0x81, key, true, false, mod, reg, rm, disp, over, r); break;
            case >= 0x418 and <= 0x41F: AluGroupImm(0x83, key, true, true, mod, reg, rm, disp, over, r); break;

            // ── FE group: /0 INC r/m8, /1 DEC r/m8.  Keys 0x7F0, 0x7F1. (CF preserved.) ─────────────────────
            case 0x7F0: AluIncDecRm(false, false, mod, rm, disp, over); break;   // INC r/m8
            case 0x7F1: AluIncDecRm(false, true, mod, rm, disp, over); break;    // DEC r/m8
            // ── FF group: /0 INC r/m16, /1 DEC r/m16.  Keys 0x7F8, 0x7F9. (/2..7 are M5.5c/d — not here.) ──
            case 0x7F8: AluIncDecRm(true, false, mod, rm, disp, over); break;    // INC r/m16
            case 0x7F9: AluIncDecRm(true, true, mod, rm, disp, over); break;     // DEC r/m16

            // ── F6 group (r/m8): /0 /1 TEST imm8 ; /2 NOT ; /3 NEG ; /4 MUL ; /5 IMUL ; /6 DIV ; /7 IDIV. ──
            //    Keys 0x7B0..0x7B7.
            case 0x7B0: case 0x7B1: AluUnaryTestImm(false, mod, rm, disp, over, (byte)r.X86.Imm); break;
            case 0x7B2: AluUnaryNot(false, mod, rm, disp, over); break;
            case 0x7B3: AluUnaryNeg(false, mod, rm, disp, over); break;
            case 0x7B4: AluMul(false, signed: false, mod, rm, disp, over); break;
            case 0x7B5: AluMul(false, signed: true, mod, rm, disp, over); break;
            case 0x7B6: AluDiv(false, signed: false, mod, rm, disp, over); break;
            case 0x7B7: AluDiv(false, signed: true, mod, rm, disp, over); break;

            // ── F7 group (r/m16): same eight.  Keys 0x7B8..0x7BF. ───────────────────────────────────────────
            case 0x7B8: case 0x7B9: AluUnaryTestImm(true, mod, rm, disp, over, r.X86.Imm); break;
            case 0x7BA: AluUnaryNot(true, mod, rm, disp, over); break;
            case 0x7BB: AluUnaryNeg(true, mod, rm, disp, over); break;
            case 0x7BC: AluMul(true, signed: false, mod, rm, disp, over); break;
            case 0x7BD: AluMul(true, signed: true, mod, rm, disp, over); break;
            case 0x7BE: AluDiv(true, signed: false, mod, rm, disp, over); break;
            case 0x7BF: AluDiv(true, signed: true, mod, rm, disp, over); break;
        }
    }

    private enum AluOp { Add, Adc, Sub, Sbb, Cmp, And, Or, Xor }

    // ── Operand read/write helpers (byte + word) over a ModR/M r/m operand. mod=11 ⇒ register; else memory. ──

    private byte ReadRmByte(uint mod, uint rm, ushort disp, X86SegmentOverride over) =>
        mod == 3u ? Reg8(rm) : ReadEaByte(ResolveEaPhysical(mod, rm, disp, over));

    private void WriteRmByte(uint mod, uint rm, ushort disp, X86SegmentOverride over, byte value)
    {
        if (mod == 3u) SetReg8(rm, value);
        else WriteEaByte(ResolveEaPhysical(mod, rm, disp, over), value);
    }

    private ushort ReadRmWord(uint mod, uint rm, ushort disp, X86SegmentOverride over)
    {
        if (mod == 3u) return Reg16(rm);
        var (seg, off) = ResolveEaSegOffset(mod, rm, disp, over);
        return ReadEaWordWrapped(seg, off);
    }

    private void WriteRmWord(uint mod, uint rm, ushort disp, X86SegmentOverride over, ushort value)
    {
        if (mod == 3u) SetReg16(rm, value);
        else { var (seg, off) = ResolveEaSegOffset(mod, rm, disp, over); WriteEaWordWrapped(seg, off, value); }
    }

    /// <summary>Compute one ALU op (a OP b [+carry]), set the flags, and return the width-masked result. CMP
    /// uses the SUB flag set; TEST uses AND (but TEST has its own opcodes — this handles AND only).</summary>
    private uint AluCompute(AluOp op, uint a, uint b, bool width16) => op switch
    {
        AluOp.Add => AddFlags(a, b, 0, width16),
        AluOp.Adc => AddFlags(a, b, (uint)(FLAGS & FlagCF), width16),
        AluOp.Sub => SubFlags(a, b, 0, width16),
        AluOp.Sbb => SubFlags(a, b, (uint)(FLAGS & FlagCF), width16),
        AluOp.Cmp => SubFlags(a, b, 0, width16),                    // CMP = SUB, result discarded by caller
        AluOp.And => LogicFlags(a & b, width16),
        AluOp.Or  => LogicFlags(a | b, width16),
        AluOp.Xor => LogicFlags(a ^ b, width16),
        _ => 0,
    };

    private static bool WritesResult(AluOp op) => op != AluOp.Cmp;   // CMP is flags-only

    /// <summary>The 00-3D standard ALU forms. <paramref name="baseOp"/> is the family base (e.g. 0x00 ADD);
    /// <c>key-baseOp</c> selects the form (0=r/m8&lt;-r8 store, 1=r/m16&lt;-r16, 2=r8&lt;-r/m8 load,
    /// 3=r16&lt;-r/m16, 4=AL,imm8, 5=AX,imm16).</summary>
    private void AluStd(uint baseOp, uint key, AluOp op, uint mod, uint reg, uint rm, ushort disp,
        X86SegmentOverride over, CpuEmulator.Core.Jit.DecodeResult r)
    {
        switch (key - baseOp)
        {
            case 0:   // r/m8, r8 (destination = r/m, source = reg)
            {
                uint a = ReadRmByte(mod, rm, disp, over);
                uint res = AluCompute(op, a, Reg8(reg), false);
                if (WritesResult(op)) WriteRmByte(mod, rm, disp, over, (byte)res);
                break;
            }
            case 1:   // r/m16, r16
            {
                uint a = ReadRmWord(mod, rm, disp, over);
                uint res = AluCompute(op, a, Reg16(reg), true);
                if (WritesResult(op)) WriteRmWord(mod, rm, disp, over, (ushort)res);
                break;
            }
            case 2:   // r8, r/m8 (destination = reg, source = r/m)
            {
                uint a = Reg8(reg);
                uint res = AluCompute(op, a, ReadRmByte(mod, rm, disp, over), false);
                if (WritesResult(op)) SetReg8(reg, (byte)res);
                break;
            }
            case 3:   // r16, r/m16
            {
                uint a = Reg16(reg);
                uint res = AluCompute(op, a, ReadRmWord(mod, rm, disp, over), true);
                if (WritesResult(op)) SetReg16(reg, (ushort)res);
                break;
            }
            case 4:   // AL, imm8
            {
                uint res = AluCompute(op, AL, (byte)r.X86.Imm, false);
                if (WritesResult(op)) AL = (byte)res;
                break;
            }
            case 5:   // AX, imm16
            {
                uint res = AluCompute(op, AX, r.X86.Imm, true);
                if (WritesResult(op)) AX = (ushort)res;
                break;
            }
        }
    }

    /// <summary>The 80/81/83 group: ALU r/m, imm. The ModR/M reg field selects the op (0=ADD 1=OR 2=ADC 3=SBB
    /// 4=AND 5=SUB 6=XOR 7=CMP). For 0x83 the imm8 is SIGN-EXTENDED to 16 bits before the 16-bit op.</summary>
    private void AluGroupImm(uint opcode, uint key, bool width16, bool signExtend, uint mod, uint reg, uint rm,
        ushort disp, X86SegmentOverride over, CpuEmulator.Core.Jit.DecodeResult r)
    {
        AluOp op = (key & 7u) switch
        {
            0u => AluOp.Add, 1u => AluOp.Or, 2u => AluOp.Adc, 3u => AluOp.Sbb,
            4u => AluOp.And, 5u => AluOp.Sub, 6u => AluOp.Xor, _ => AluOp.Cmp,
        };
        if (!width16)
        {
            uint a = ReadRmByte(mod, rm, disp, over);
            uint res = AluCompute(op, a, (byte)r.X86.Imm, false);
            if (WritesResult(op)) WriteRmByte(mod, rm, disp, over, (byte)res);
        }
        else
        {
            uint a = ReadRmWord(mod, rm, disp, over);
            // 0x83: sign-extend the imm8 (low byte) to 16 bits; 0x81: use the captured imm16 directly.
            ushort imm = signExtend ? unchecked((ushort)(sbyte)(byte)r.X86.Imm) : r.X86.Imm;
            uint res = AluCompute(op, a, imm, true);
            if (WritesResult(op)) WriteRmWord(mod, rm, disp, over, (ushort)res);
        }
    }

    /// <summary>84/85: TEST r/m, reg — AND with the result discarded (flags only).</summary>
    private void AluTestRm(bool width16, uint mod, uint reg, uint rm, ushort disp, X86SegmentOverride over)
    {
        if (!width16) LogicFlags((uint)(ReadRmByte(mod, rm, disp, over) & Reg8(reg)), false);
        else LogicFlags((uint)(ReadRmWord(mod, rm, disp, over) & Reg16(reg)), true);
    }

    /// <summary>FE/FF /0 /1: INC/DEC r/m (CF preserved).</summary>
    private void AluIncDecRm(bool width16, bool decrement, uint mod, uint rm, ushort disp, X86SegmentOverride over)
    {
        if (!width16)
        {
            uint res = IncDecFlags(ReadRmByte(mod, rm, disp, over), decrement, false);
            WriteRmByte(mod, rm, disp, over, (byte)res);
        }
        else
        {
            uint res = IncDecFlags(ReadRmWord(mod, rm, disp, over), decrement, true);
            WriteRmWord(mod, rm, disp, over, (ushort)res);
        }
    }

    /// <summary>F6/F7 /0 /1: TEST r/m, imm — AND with imm, flags only (the split-immediate carrier delivers
    /// the imm only for reg 0/1; the decode walk withheld it for /2..7).</summary>
    private void AluUnaryTestImm(bool width16, uint mod, uint rm, ushort disp, X86SegmentOverride over, ushort imm)
    {
        if (!width16) LogicFlags((uint)(ReadRmByte(mod, rm, disp, over) & (byte)imm), false);
        else LogicFlags((uint)(ReadRmWord(mod, rm, disp, over) & imm), true);
    }

    /// <summary>F6/F7 /2: NOT r/m — bitwise complement. NOT sets NO flags.</summary>
    private void AluUnaryNot(bool width16, uint mod, uint rm, ushort disp, X86SegmentOverride over)
    {
        if (!width16) WriteRmByte(mod, rm, disp, over, (byte)~ReadRmByte(mod, rm, disp, over));
        else WriteRmWord(mod, rm, disp, over, (ushort)~ReadRmWord(mod, rm, disp, over));
    }

    /// <summary>F6/F7 /3: NEG r/m — 0 - operand (SUB semantics). CF = (operand != 0); SF/ZF/PF/AF/OF accordingly.</summary>
    private void AluUnaryNeg(bool width16, uint mod, uint rm, ushort disp, X86SegmentOverride over)
    {
        if (!width16)
        {
            uint a = ReadRmByte(mod, rm, disp, over);
            uint res = SubFlags(0, a, 0, false);
            WriteRmByte(mod, rm, disp, over, (byte)res);
        }
        else
        {
            uint a = ReadRmWord(mod, rm, disp, over);
            uint res = SubFlags(0, a, 0, true);
            WriteRmWord(mod, rm, disp, over, (ushort)res);
        }
    }

    /// <summary>F6 /4 /5 (r/m8) + F7 /4 /5 (r/m16): MUL/IMUL. Byte: AX = AL * r/m8. Word: DX:AX = AX * r/m16.
    /// CF=OF set when the high half is significant (MUL: high half != 0; IMUL: high half not the sign-extension
    /// of the low half). SF/ZF/PF/AF are undefined on the 8086 (masked) — left as natural fallout.</summary>
    private void AluMul(bool width16, bool signed, uint mod, uint rm, ushort disp, X86SegmentOverride over)
    {
        if (!width16)
        {
            byte src = ReadRmByte(mod, rm, disp, over);
            ushort product = signed
                ? unchecked((ushort)((sbyte)AL * (sbyte)src))
                : (ushort)(AL * src);
            AX = product;
            // MUL: CF/OF set iff AH != 0. IMUL: CF/OF set iff AH is NOT the sign-extension of AL.
            bool upperSignificant = signed
                ? AH != (byte)((AL & 0x80) != 0 ? 0xFF : 0x00)
                : AH != 0;
            SetFlag(FlagCF, upperSignificant);
            SetFlag(FlagOF, upperSignificant);
        }
        else
        {
            ushort src = ReadRmWord(mod, rm, disp, over);
            uint product = signed
                ? unchecked((uint)((short)AX * (short)src))
                : (uint)AX * src;
            AX = (ushort)(product & 0xFFFF);
            DX = (ushort)(product >> 16);
            bool upperSignificant = signed
                ? DX != (ushort)((AX & 0x8000) != 0 ? 0xFFFF : 0x0000)
                : DX != 0;
            SetFlag(FlagCF, upperSignificant);
            SetFlag(FlagOF, upperSignificant);
        }
    }

    /// <summary>F6 /6 /7 (r/m8) + F7 /6 /7 (r/m16): DIV/IDIV. Byte: AL=AX/d, AH=AX%d. Word: AX=DX:AX/d,
    /// DX=remainder. A divisor of 0 OR a quotient that overflows the destination raises INT0 (the divide-error
    /// vector). The interrupt seam is M5.5d — M5.5b DISCLOSES + DEFERS: on a divide-error it routes to
    /// <see cref="HandleUndefinedOpcode"/> and leaves registers UNCHANGED (the case fails honestly). The valid
    /// (non-erroring) quotient is computed correctly and goes green.</summary>
    private void AluDiv(bool width16, bool signed, uint mod, uint rm, ushort disp, X86SegmentOverride over)
    {
        if (!width16)
        {
            byte d = ReadRmByte(mod, rm, disp, over);
            if (d == 0)
            {
                // M5.5b honest deferral: divide-by-zero → INT0 (divide-error vector). The interrupt push is
                // M5.5d; disclose + defer (no fake state). The data-axis gate counts these as deferred.
                HandleUndefinedOpcode(0xF6);
                return;
            }
            if (!signed)
            {
                uint quot = (uint)AX / d;
                uint rem = (uint)AX % d;
                if (quot > 0xFF) { HandleUndefinedOpcode(0xF6); return; }   // quotient overflow → INT0 (defer)
                AL = (byte)quot;
                AH = (byte)rem;
            }
            else
            {
                short dividend = unchecked((short)AX);
                int quot = dividend / (sbyte)d;
                int rem = dividend % (sbyte)d;
                if (quot < -128 || quot > 127) { HandleUndefinedOpcode(0xF6); return; }   // overflow → INT0 (defer)
                AL = (byte)(sbyte)quot;
                AH = (byte)(sbyte)rem;
            }
        }
        else
        {
            ushort d = ReadRmWord(mod, rm, disp, over);
            if (d == 0) { HandleUndefinedOpcode(0xF7); return; }   // divide-by-zero → INT0 (defer)
            uint dividend = (uint)((DX << 16) | AX);
            if (!signed)
            {
                uint quot = dividend / d;
                uint rem = dividend % d;
                if (quot > 0xFFFF) { HandleUndefinedOpcode(0xF7); return; }   // overflow → INT0 (defer)
                AX = (ushort)quot;
                DX = (ushort)rem;
            }
            else
            {
                int signedDividend = unchecked((int)dividend);
                int quot = signedDividend / (short)d;
                int rem = signedDividend % (short)d;
                if (quot < -32768 || quot > 32767) { HandleUndefinedOpcode(0xF7); return; }   // overflow → INT0 (defer)
                AX = (ushort)(short)quot;
                DX = (ushort)(short)rem;
            }
        }
    }
}
