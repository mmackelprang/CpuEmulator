namespace CpuEmulator.Cpus.M8086;

/// <summary>
/// M5.5c — the 8086 stack op bodies (hand-written): PUSH/POP (general reg 50-5F, segment reg 06/07/0E/16/17/
/// 1E/1F, the FF /6 group PUSH r/m16, the 8F /0 group POP r/m16) and PUSHF/POPF (9C/9D). Dispatched by the
/// generated <c>ExecuteX86</c> to <see cref="StackExecute"/>.
///
/// <para><b>The stack discipline.</b> The stack lives in <c>SS:SP</c> (the SS segment is NON-overridable for
/// push/pop — a segment-override prefix on a stack op does NOT change the stack segment, only a memory operand's
/// segment). SP is the 16-bit offset, wrapping within the 64 KB SS segment. PUSH pre-decrements SP by 2 then
/// writes the word; POP reads the word then post-increments SP by 2 (both LITTLE-ENDIAN, with the segment-
/// relative offset wrap).</para>
///
/// <para><b>The 8086 quirks the TomHarte corpus pins.</b></para>
/// <list type="bullet">
///   <item><b>PUSH SP (0x54)</b> pushes the value of SP <i>AFTER</i> the decrement (the 8086/8088 behavior;
///     the 80286+ pushes the pre-decrement value). So <c>[SS:SP-2] = SP-2</c>.</item>
///   <item><b>POP SP (0x5C)</b> loads SP from the popped stack word — the popped value WINS over the SP+2 the
///     post-increment would compute (the read sets SP last).</item>
///   <item><b>PUSHF</b> pushes the FLAGS word; bits 12-15 read back as 1 on the 8086 (the undefined high
///     nibble) and bit 1 reads back as 1. The corpus's pushed RAM carries these set bits — but they are part of
///     the FLAGS register state the runner already tracks; the pushed word is exactly the current FLAGS.</item>
///   <item><b>POPF</b> loads FLAGS from the stack; the result FLAGS the corpus expects is mask-aware (the
///     undefined bits vary), so POPF stores the popped word directly into FLAGS.</item>
/// </list>
/// </summary>
public sealed partial class M8086Cpu
{
    /// <summary>The nine DEFINED 8086 FLAGS bits (CF=0, PF=2, AF=4, ZF=6, SF=7, TF=8, IF=9, DF=10, OF=11) —
    /// the bits POPF copies from the popped word. Mask 0x0FD5.</summary>
    private const ushort FlagsDefinedMask = 0x0FD5;

    /// <summary>The 8086 reserved-bit forcing POPF applies: bits 12-15 = 1 and bit 1 = 1 (bits 3 &amp; 5 force to
    /// 0, achieved by them not being in <see cref="FlagsDefinedMask"/>). Value 0xF002.</summary>
    private const ushort FlagsForcedBits = 0xF002;

    /// <summary>Push a 16-bit word onto the stack: SP -= 2 (wrapping at 16 bits), then write the word at SS:SP
    /// little-endian (the segment-relative offset wrap applies to the high byte). The SS segment is fixed.</summary>
    private void PushWord(ushort value)
    {
        SP = (ushort)(SP - 2);
        WriteEaWordWrapped(SS, SP, value);
    }

    /// <summary>Pop a 16-bit word off the stack: read the word at SS:SP little-endian, then SP += 2 (wrapping at
    /// 16 bits). Returns the popped value.</summary>
    private ushort PopWord()
    {
        ushort value = ReadEaWordWrapped(SS, SP);
        SP = (ushort)(SP + 2);
        return value;
    }

    /// <summary>M5.5c: execute one stack instruction.</summary>
    partial void StackExecute(uint key, CpuEmulator.Core.Jit.DecodeResult r)
    {
        // 8F (POP r/m16) is a don't-care group: the 8086 ignores the ModR/M reg field (the corpus hits reg 0
        // AND reg 1). NORMALIZE any 8F group key (0x478-0x47F) to the /0 form 0x478 before the dispatch.
        if (key >= 0x479u && key <= 0x47Fu) key = 0x478u;

        switch (key)
        {
            // ── 50-57: PUSH r16. PUSH SP (0x54) pushes the POST-decrement SP (the 8086 quirk) — since PushWord
            //    decrements SP BEFORE writing and we read Reg16 AFTER the decrement, this falls out naturally. ──
            case 0x50u: case 0x51u: case 0x52u: case 0x53u:
            case 0x54u: case 0x55u: case 0x56u: case 0x57u:
            {
                uint regIdx = key & 7u;
                // Decrement first, THEN read the register — so PUSH SP writes SP-2 (8086), and the other regs
                // are unaffected by the decrement.
                SP = (ushort)(SP - 2);
                WriteEaWordWrapped(SS, SP, Reg16(regIdx));
                break;
            }

            // ── 58-5F: POP r16. POP SP (0x5C) takes the popped value (it overwrites the post-increment). ──
            case 0x58u: case 0x59u: case 0x5Au: case 0x5Bu:
            case 0x5Cu: case 0x5Du: case 0x5Eu: case 0x5Fu:
            {
                uint regIdx = key & 7u;
                ushort value = PopWord();          // reads, then SP += 2
                SetReg16(regIdx, value);           // POP SP ⇒ the popped value overwrites SP+2 (read last wins)
                break;
            }

            // ── Segment-register PUSH/POP (06/0E/16/1E push; 07/17/1F pop; there is no POP CS on the 8086). ──
            case 0x06u: PushWord(ES); break;   // PUSH ES
            case 0x0Eu: PushWord(CS); break;   // PUSH CS
            case 0x16u: PushWord(SS); break;   // PUSH SS
            case 0x1Eu: PushWord(DS); break;   // PUSH DS
            case 0x07u: ES = PopWord(); break; // POP ES
            case 0x17u: SS = PopWord(); break; // POP SS
            case 0x1Fu: DS = PopWord(); break; // POP DS

            // ── FF /6: PUSH r/m16 (group key 0x7FE). The 8086 DECREMENTS SP FIRST, then reads + writes the
            //    operand — so PUSH SP (mod=11,r/m=4) writes the POST-decrement SP value (the same quirk as the
            //    50-57 PUSH SP form), and a memory operand is read with SP already decremented. Reconciled
            //    against the FF.6 `push sp` corpus cases. ──
            case 0x7FEu:
            {
                byte modrm = r.X86.ModRm;
                uint mod = (uint)(modrm >> 6) & 3u;
                uint rm = (uint)modrm & 7u;
                ushort disp = r.X86.Disp;
                X86SegmentOverride over = OverrideFromByte(r.X86.SegOverride);
                SP = (ushort)(SP - 2);                          // decrement FIRST (the 8086 PUSH SP quirk)
                ushort value = ReadRmWord(mod, rm, disp, over); // now reads SP-2 if r/m == SP
                WriteEaWordWrapped(SS, SP, value);
                break;
            }

            // ── 8F /0: POP r/m16 (group key 0x478). Pop the value FIRST (SP += 2), then store to the r/m
            //    destination. A memory destination EA is computed AFTER the pop (the post-increment SP is in
            //    force for an SP-relative EA — but for the common cases the EA does not depend on SP). ──
            case 0x478u:
            {
                byte modrm = r.X86.ModRm;
                uint mod = (uint)(modrm >> 6) & 3u;
                uint rm = (uint)modrm & 7u;
                ushort disp = r.X86.Disp;
                X86SegmentOverride over = OverrideFromByte(r.X86.SegOverride);
                ushort value = PopWord();
                WriteRmWord(mod, rm, disp, over, value);
                break;
            }

            // ── 9C PUSHF / 9D POPF. PUSHF pushes the whole FLAGS word (the corpus seeds the forced high bits, so
            //    pushing FLAGS as-is is byte-exact). POPF loads FLAGS from the stack but the 8086 forces the
            //    reserved bits: bits 12-15 = 1, bit 1 = 1, bits 3 & 5 = 0 — only the nine DEFINED flag bits
            //    (CF/PF/AF/ZF/SF/TF/IF/DF/OF = mask 0x0FD5) take the popped value. Reconciled byte-exact against
            //    the 9D corpus (popped 0x5C9A → FLAGS 0xFC92, etc.). ──
            case 0x9Cu: PushWord(FLAGS); break;
            case 0x9Du: FLAGS = (ushort)((PopWord() & FlagsDefinedMask) | FlagsForcedBits); break;
        }
    }
}
