namespace CpuEmulator.Cpus.M8086;

/// <summary>
/// M5.5c — the 8086 shift/rotate op bodies (hand-written): the D0/D1/D2/D3 group (ROL/ROR/RCL/RCR/SHL/SHR/SAR),
/// by-1 (D0/D1) and by-CL (D2/D3), byte (D0/D2) and word (D1/D3). Dispatched by the generated <c>ExecuteX86</c>
/// to <see cref="ShiftExecute"/>. Reuses the M5.5a EA pipeline (the ReadRmByte/WriteRmByte/ReadRmWord/
/// WriteRmWord helpers in M8086Cpu.Alu.cs) and the M5.5b flag primitives (FlagCF/.../FlagOF, SetFlag, SetSzp).
///
/// <para><b>The 8086 shift/rotate semantics (the TomHarte-pinned subtleties).</b></para>
/// <list type="bullet">
///   <item><b>Count.</b> The 8086 does NOT mask the count to 5 bits (that is a 186+ change) — it uses the FULL
///     8-bit CL (or the literal 1 for D0/D1). A count of 0 performs NO operation and changes NO flags (verified
///     against the corpus: the D2/D3 CL=0 cases leave operand + every flag untouched).</item>
///   <item><b>CF</b> is the LAST bit shifted/rotated out (the high bit for left ops, the low bit for right
///     ops). For RCL/RCR it is the final state of the carry after rotating through the (width+1)-bit register.</item>
///   <item><b>OF.</b> The 8086 sets OF on EVERY non-zero count (NOT just count==1 — that documentation caveat
///     describes the result being "undefined", but the silicon DOES write a deterministic value the corpus
///     pins). For the shifts/rotates OF is computed from the FINAL result bits:
///     SHL/ROL/RCL ⇒ MSB(result) XOR CF; SHR ⇒ MSB(result) XOR next-to-MSB(result) (i.e. the original MSB for
///     count==1, but the corpus value is the top-two-bits XOR of the produced result); SAR ⇒ 0; ROR/RCR ⇒
///     MSB(result) XOR next-to-MSB(result). These were reconciled byte-exact against the D0-D3 corpus (reg 0-3
///     mask 0xFFFF — OF fully defined; reg 4/5/7 mask 0xFFEF — only AF excluded, OF defined).</item>
///   <item><b>SF/ZF/PF.</b> Set from the result for the SHIFTS (SHL/SHR/SAR) — those are arithmetic/logical.
///     The ROTATES (ROL/ROR/RCL/RCR) do NOT touch SF/ZF/PF (only CF/OF) — the 8086 rotate leaves them.</item>
///   <item><b>AF</b> is UNDEFINED for all shift/rotate ops (the metadata mask 0xFFEF excludes it) — left as
///     natural fallout (untouched).</item>
/// </list>
/// </summary>
public sealed partial class M8086Cpu
{
    /// <summary>M5.5c: execute one shift/rotate (D0-D3) instruction. The ModR/M reg field selects the operation
    /// (0=ROL 1=ROR 2=RCL 3=RCR 4=SHL/SAL 5=SHR 6=(undoc, not routed) 7=SAR); the opcode selects width (D0/D2
    /// byte, D1/D3 word) + count source (D0/D1 = 1, D2/D3 = CL).</summary>
    partial void ShiftExecute(uint key, CpuEmulator.Core.Jit.DecodeResult r)
    {
        byte modrm = r.X86.ModRm;
        uint mod = (uint)(modrm >> 6) & 3u;
        uint reg = (uint)(modrm >> 3) & 7u;   // = the operation selector (the group subfield)
        uint rm = (uint)modrm & 7u;
        ushort disp = r.X86.Disp;
        X86SegmentOverride over = OverrideFromByte(r.X86.SegOverride);

        uint opcode = key >> 3;               // D0/D1/D2/D3
        bool width16 = opcode is 0xD1u or 0xD3u;
        bool byCl = opcode is 0xD2u or 0xD3u;
        int count = byCl ? CL : 1;            // full 8-bit CL on the 8086 (no 5-bit mask)

        if (!width16)
        {
            byte value = ReadRmByte(mod, rm, disp, over);
            byte result = ShiftRotateByte(reg, value, count);
            WriteRmByte(mod, rm, disp, over, result);
        }
        else
        {
            ushort value = ReadRmWord(mod, rm, disp, over);
            ushort result = ShiftRotateWord(reg, value, count);
            WriteRmWord(mod, rm, disp, over, result);
        }
    }

    // ── Byte (8-bit) shift/rotate. count is the raw 8-bit count (0..255); a count of 0 is a no-op (no flags). ──
    private byte ShiftRotateByte(uint op, byte value, int count)
    {
        if (count == 0) return value;   // count 0 ⇒ no operation, no flag change (8086-pinned)

        bool cf = (FLAGS & FlagCF) != 0;   // the incoming carry (matters only for RCL/RCR)
        switch (op)
        {
            case 0u: return RolByte(value, count);
            case 1u: return RorByte(value, count);
            case 2u: return RclByte(value, count, cf);
            case 3u: return RcrByte(value, count, cf);
            case 4u: return ShlByte(value, count);
            case 5u: return ShrByte(value, count);
            case 7u: return SarByte(value, count);
            default: return value;   // reg 6 is undocumented — not routed here, but be inert if it ever is
        }
    }

    private ushort ShiftRotateWord(uint op, ushort value, int count)
    {
        if (count == 0) return value;

        bool cf = (FLAGS & FlagCF) != 0;
        switch (op)
        {
            case 0u: return RolWord(value, count);
            case 1u: return RorWord(value, count);
            case 2u: return RclWord(value, count, cf);
            case 3u: return RcrWord(value, count, cf);
            case 4u: return ShlWord(value, count);
            case 5u: return ShrWord(value, count);
            case 7u: return SarWord(value, count);
            default: return value;
        }
    }

    // ── SHL/SAL: shift left, zero-fill. CF = last bit shifted off the top; OF = MSB(result) XOR CF; SF/ZF/PF
    //    from result. A count >= width+1 shifts everything out ⇒ result 0, CF = 0 (the over-shift drops past
    //    the top), but for count == width the last out-bit is bit0 of the original — computed by the loop. ──
    private byte ShlByte(byte value, int count)
    {
        uint v = value;
        bool cf = false;
        for (int i = 0; i < count; i++) { cf = (v & 0x80) != 0; v = (v << 1) & 0xFF; }
        byte result = (byte)v;
        SetFlag(FlagCF, cf);
        SetFlag(FlagOF, ((result & 0x80) != 0) ^ cf);
        SetSzp(result, width16: false);
        return result;
    }

    private ushort ShlWord(ushort value, int count)
    {
        uint v = value;
        bool cf = false;
        for (int i = 0; i < count; i++) { cf = (v & 0x8000) != 0; v = (v << 1) & 0xFFFF; }
        ushort result = (ushort)v;
        SetFlag(FlagCF, cf);
        SetFlag(FlagOF, ((result & 0x8000) != 0) ^ cf);
        SetSzp(result, width16: true);
        return result;
    }

    // ── SHR: shift right, zero-fill. CF = last bit shifted off the bottom; OF = top-two-bits XOR of the result
    //    (= MSB of the ORIGINAL operand for count==1, since the result MSB is 0 after a right shift — but the
    //    corpus value is the produced result's bit7^bit6, which for count==1 == original-bit7). SF/ZF/PF from
    //    result. ──
    private byte ShrByte(byte value, int count)
    {
        uint v = value;
        bool cf = false;
        for (int i = 0; i < count; i++) { cf = (v & 1) != 0; v >>= 1; }
        byte result = (byte)v;
        SetFlag(FlagCF, cf);
        // OF for SHR is defined ONLY for count==1 (= the MSB of the original operand); for count>1 the 8086
        // produces OF=0 (reconciled byte-exact against the D2.5/D3.5 corpus — count>1 always clears OF).
        SetFlag(FlagOF, count == 1 && (value & 0x80) != 0);
        SetSzp(result, width16: false);
        return result;
    }

    private ushort ShrWord(ushort value, int count)
    {
        uint v = value;
        bool cf = false;
        for (int i = 0; i < count; i++) { cf = (v & 1) != 0; v >>= 1; }
        ushort result = (ushort)v;
        SetFlag(FlagCF, cf);
        SetFlag(FlagOF, count == 1 && (value & 0x8000) != 0);
        SetSzp(result, width16: true);
        return result;
    }

    // ── SAR: arithmetic shift right (sign-preserving). CF = last bit shifted off the bottom; OF = 0 (always);
    //    SF/ZF/PF from result. ──
    private byte SarByte(byte value, int count)
    {
        int v = (sbyte)value;   // sign-extend for the arithmetic shift
        bool cf = false;
        for (int i = 0; i < count; i++) { cf = (v & 1) != 0; v >>= 1; }
        byte result = (byte)v;
        SetFlag(FlagCF, cf);
        SetFlag(FlagOF, false);
        SetSzp(result, width16: false);
        return result;
    }

    private ushort SarWord(ushort value, int count)
    {
        int v = (short)value;
        bool cf = false;
        for (int i = 0; i < count; i++) { cf = (v & 1) != 0; v >>= 1; }
        ushort result = (ushort)v;
        SetFlag(FlagCF, cf);
        SetFlag(FlagOF, false);
        SetSzp(result, width16: true);
        return result;
    }

    // ── ROL: rotate left (no carry-through). CF = the bit rotated out of the top into bit0 = LSB(result). OF =
    //    MSB(result) XOR CF. ROTATES do NOT alter SF/ZF/PF. ──
    private byte RolByte(byte value, int count)
    {
        uint v = value;
        for (int i = 0; i < count; i++) { uint top = (v >> 7) & 1; v = ((v << 1) | top) & 0xFF; }
        byte result = (byte)v;
        bool cf = (result & 1) != 0;
        SetFlag(FlagCF, cf);
        SetFlag(FlagOF, ((result & 0x80) != 0) ^ cf);
        return result;
    }

    private ushort RolWord(ushort value, int count)
    {
        uint v = value;
        for (int i = 0; i < count; i++) { uint top = (v >> 15) & 1; v = ((v << 1) | top) & 0xFFFF; }
        ushort result = (ushort)v;
        bool cf = (result & 1) != 0;
        SetFlag(FlagCF, cf);
        SetFlag(FlagOF, ((result & 0x8000) != 0) ^ cf);
        return result;
    }

    // ── ROR: rotate right (no carry-through). CF = the bit rotated out of the bottom into the top = MSB(result).
    //    OF = MSB(result) XOR next-to-MSB(result) (the top two result bits XOR). ──
    private byte RorByte(byte value, int count)
    {
        uint v = value;
        for (int i = 0; i < count; i++) { uint bot = v & 1; v = (v >> 1) | (bot << 7); }
        byte result = (byte)v;
        SetFlag(FlagCF, (result & 0x80) != 0);
        SetFlag(FlagOF, ((result & 0x80) != 0) ^ ((result & 0x40) != 0));
        return result;
    }

    private ushort RorWord(ushort value, int count)
    {
        uint v = value;
        for (int i = 0; i < count; i++) { uint bot = v & 1; v = (v >> 1) | (bot << 15); }
        ushort result = (ushort)v;
        SetFlag(FlagCF, (result & 0x8000) != 0);
        SetFlag(FlagOF, ((result & 0x8000) != 0) ^ ((result & 0x4000) != 0));
        return result;
    }

    // ── RCL: rotate left THROUGH the carry (a (width+1)-bit rotate: bit value | CF<<width). CF = the value
    //    rotated out the top. OF = MSB(result) XOR CF. Does NOT alter SF/ZF/PF. ──
    private byte RclByte(byte value, int count, bool carry)
    {
        uint v = value;
        uint cf = carry ? 1u : 0u;
        for (int i = 0; i < count; i++)
        {
            uint newCf = (v >> 7) & 1;
            v = ((v << 1) | cf) & 0xFF;
            cf = newCf;
        }
        byte result = (byte)v;
        bool outCf = cf != 0;
        SetFlag(FlagCF, outCf);
        SetFlag(FlagOF, ((result & 0x80) != 0) ^ outCf);
        return result;
    }

    private ushort RclWord(ushort value, int count, bool carry)
    {
        uint v = value;
        uint cf = carry ? 1u : 0u;
        for (int i = 0; i < count; i++)
        {
            uint newCf = (v >> 15) & 1;
            v = ((v << 1) | cf) & 0xFFFF;
            cf = newCf;
        }
        ushort result = (ushort)v;
        bool outCf = cf != 0;
        SetFlag(FlagCF, outCf);
        SetFlag(FlagOF, ((result & 0x8000) != 0) ^ outCf);
        return result;
    }

    // ── RCR: rotate right THROUGH the carry. CF = the value rotated out the bottom. OF = MSB(result) XOR
    //    next-to-MSB(result) (the 8086 computes the RCR overflow from the top two result bits). Does NOT alter
    //    SF/ZF/PF. ──
    private byte RcrByte(byte value, int count, bool carry)
    {
        uint v = value;
        uint cf = carry ? 1u : 0u;
        for (int i = 0; i < count; i++)
        {
            uint newCf = v & 1;
            v = (v >> 1) | (cf << 7);
            cf = newCf;
        }
        byte result = (byte)v;
        SetFlag(FlagCF, cf != 0);
        SetFlag(FlagOF, ((result & 0x80) != 0) ^ ((result & 0x40) != 0));
        return result;
    }

    private ushort RcrWord(ushort value, int count, bool carry)
    {
        uint v = value;
        uint cf = carry ? 1u : 0u;
        for (int i = 0; i < count; i++)
        {
            uint newCf = v & 1;
            v = (v >> 1) | (cf << 15);
            cf = newCf;
        }
        ushort result = (ushort)v;
        SetFlag(FlagCF, cf != 0);
        SetFlag(FlagOF, ((result & 0x8000) != 0) ^ ((result & 0x4000) != 0));
        return result;
    }
}
