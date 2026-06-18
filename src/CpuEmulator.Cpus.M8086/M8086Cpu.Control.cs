namespace CpuEmulator.Cpus.M8086;

/// <summary>
/// M5.5d — the 8086 CONTROL-FLOW op bodies (hand-written): the unconditional jumps + calls (JMP/CALL near +
/// far, direct + indirect), the conditional jumps (70-7F Jcc rel8), the loop family (LOOP/LOOPE/LOOPNE/JCXZ),
/// and the returns (RET/RETF, incl. the imm16 stack-adjust forms). Dispatched by the generated <c>ExecuteX86</c>
/// to <see cref="ControlExecute"/>. Reuses the M5.5a EA pipeline + register accessors and the M5.5c stack
/// helpers (<see cref="PushWord"/>/<see cref="PopWord"/>).
///
/// <para><b>The relative-target base.</b> The generated Step advances <see cref="IP"/> by the instruction's
/// length BEFORE dispatch, so <see cref="IP"/> already holds the address of the NEXT instruction — exactly the
/// base a relative jump/call adds its sign-extended displacement to (<c>IP = IP + (sbyte/short)rel</c>). A near
/// CALL pushes this post-advance return IP, then jumps. A far CALL pushes CS THEN the return IP, then loads
/// CS:IP. The relative displacement rides the IMMEDIATE slot (the rel8/rel16 the decode walk captured); the far
/// DIRECT target rides Imm (offset) + Disp (segment) — the Fixed32 carrier (M5.5d decode walk).</para>
///
/// <para><b>The FF-group indirect forms.</b> The FF /2../5 group keys (<c>(0xFF&lt;&lt;3)|reg</c> = 0x7FA-0x7FD)
/// are CALL/JMP through a ModR/M operand: /2 CALL near (the r/m16 IS the new IP), /3 CALL far (the m16:16 supplies
/// IP then CS), /4 JMP near, /5 JMP far. The far indirect forms read a SECOND word at offset+2 for the segment
/// (the LDS/LES far-pointer shape). A register operand (mod=11) is valid only for the NEAR forms.</para>
/// </summary>
public sealed partial class M8086Cpu
{
    /// <summary>Evaluate an 8086 conditional-jump predicate by its opcode (0x70-0x7F). Returns true when the
    /// branch is TAKEN. The condition reads the current FLAGS (CF/PF/ZF/SF/OF).</summary>
    private bool JccTaken(uint opcode)
    {
        bool cf = (FLAGS & FlagCF) != 0;
        bool pf = (FLAGS & FlagPF) != 0;
        bool zf = (FLAGS & FlagZF) != 0;
        bool sf = (FLAGS & FlagSF) != 0;
        bool of = (FLAGS & FlagOF) != 0;
        return opcode switch
        {
            0x70u => of,                 // JO
            0x71u => !of,                // JNO
            0x72u => cf,                 // JB/JC/JNAE
            0x73u => !cf,                // JAE/JNB/JNC
            0x74u => zf,                 // JE/JZ
            0x75u => !zf,                // JNE/JNZ
            0x76u => cf || zf,           // JBE/JNA
            0x77u => !cf && !zf,         // JA/JNBE
            0x78u => sf,                 // JS
            0x79u => !sf,                // JNS
            0x7Au => pf,                 // JP/JPE
            0x7Bu => !pf,                // JNP/JPO
            0x7Cu => sf != of,           // JL/JNGE
            0x7Du => sf == of,           // JGE/JNL
            0x7Eu => zf || (sf != of),   // JLE/JNG
            _     => !zf && (sf == of),  // 0x7F JG/JNLE
        };
    }

    /// <summary>M5.5d: execute one control-flow instruction.</summary>
    partial void ControlExecute(uint key, CpuEmulator.Core.Jit.DecodeResult r)
    {
        switch (key)
        {
            // ── 70-7F Jcc rel8 — IP already past the 2-byte instruction; on TAKEN add the sign-extended rel8. ─
            case >= 0x70u and <= 0x7Fu:
                if (JccTaken(key)) IP = (ushort)(IP + (short)(sbyte)(byte)r.X86.Imm);
                break;

            // ── EB JMP rel8 (short). ─────────────────────────────────────────────────────────────────────────
            case 0xEBu:
                IP = (ushort)(IP + (short)(sbyte)(byte)r.X86.Imm);
                break;

            // ── E9 JMP rel16 (near). ─────────────────────────────────────────────────────────────────────────
            case 0xE9u:
                IP = (ushort)(IP + (short)r.X86.Imm);
                break;

            // ── E8 CALL rel16 (near) — push the return IP, then add the rel16. ──────────────────────────────
            case 0xE8u:
                PushWord(IP);
                IP = (ushort)(IP + (short)r.X86.Imm);
                break;

            // ── 9A CALL ptr16:16 (far direct) — push CS, push return IP, then load CS:IP from the immediate. ─
            case 0x9Au:
                PushWord(CS);
                PushWord(IP);
                IP = r.X86.Imm;     // offset (low word of the far ptr)
                CS = r.X86.Disp;    // segment (high word — carried on Disp by the Fixed32 decode)
                break;

            // ── EA JMP ptr16:16 (far direct) — load CS:IP from the immediate (no push). ─────────────────────
            case 0xEAu:
                IP = r.X86.Imm;
                CS = r.X86.Disp;
                break;

            // ── C3 RET (near) — pop IP. ──────────────────────────────────────────────────────────────────────
            case 0xC3u:
                IP = PopWord();
                break;

            // ── C2 RET imm16 (near, pop-bytes) — pop IP, then add imm16 to SP (caller-cleanup convention). ──
            case 0xC2u:
                IP = PopWord();
                SP = (ushort)(SP + r.X86.Imm);
                break;

            // ── CB RETF (far) — pop IP then CS. ──────────────────────────────────────────────────────────────
            case 0xCBu:
                IP = PopWord();
                CS = PopWord();
                break;

            // ── CA RETF imm16 (far, pop-bytes) — pop IP, pop CS, then add imm16 to SP. ───────────────────────
            case 0xCAu:
                IP = PopWord();
                CS = PopWord();
                SP = (ushort)(SP + r.X86.Imm);
                break;

            // ── E0 LOOPNE / E1 LOOPE / E2 LOOP / E3 JCXZ. CX-conditioned short jumps (rel8 in Imm). ───────────
            case 0xE2u:   // LOOP: CX -= 1; if CX != 0 jump.
            {
                CX = (ushort)(CX - 1);
                if (CX != 0) IP = (ushort)(IP + (short)(sbyte)(byte)r.X86.Imm);
                break;
            }
            case 0xE1u:   // LOOPE/LOOPZ: CX -= 1; if CX != 0 AND ZF jump.
            {
                CX = (ushort)(CX - 1);
                if (CX != 0 && (FLAGS & FlagZF) != 0) IP = (ushort)(IP + (short)(sbyte)(byte)r.X86.Imm);
                break;
            }
            case 0xE0u:   // LOOPNE/LOOPNZ: CX -= 1; if CX != 0 AND !ZF jump.
            {
                CX = (ushort)(CX - 1);
                if (CX != 0 && (FLAGS & FlagZF) == 0) IP = (ushort)(IP + (short)(sbyte)(byte)r.X86.Imm);
                break;
            }
            case 0xE3u:   // JCXZ: jump if CX == 0 (CX is NOT decremented).
            {
                if (CX == 0) IP = (ushort)(IP + (short)(sbyte)(byte)r.X86.Imm);
                break;
            }

            // ── FF /2../5 indirect group (keys (0xFF<<3)|reg = 0x7FA-0x7FD). ───────────────────────────────────
            case 0x7FAu:   // CALL r/m16 (near indirect): the operand IS the new IP; push the return first.
            {
                byte modrm = r.X86.ModRm;
                uint mod = (uint)(modrm >> 6) & 3u, rm = (uint)modrm & 7u;
                ushort target = ReadRmWord(mod, rm, r.X86.Disp, OverrideFromByte(r.X86.SegOverride));
                PushWord(IP);
                IP = target;
                break;
            }
            case 0x7FCu:   // JMP r/m16 (near indirect).
            {
                byte modrm = r.X86.ModRm;
                uint mod = (uint)(modrm >> 6) & 3u, rm = (uint)modrm & 7u;
                IP = ReadRmWord(mod, rm, r.X86.Disp, OverrideFromByte(r.X86.SegOverride));
                break;
            }
            case 0x7FBu:   // CALL m16:16 (far indirect): IP from [mem], CS from [mem+2]. Push CS then return IP.
            {
                byte modrm = r.X86.ModRm;
                uint mod = (uint)(modrm >> 6) & 3u, rm = (uint)modrm & 7u;
                var (seg, off) = ResolveEaSegOffset(mod, rm, r.X86.Disp, OverrideFromByte(r.X86.SegOverride));
                ushort newIp = ReadEaWordWrapped(seg, off);
                ushort newCs = ReadEaWordWrapped(seg, (ushort)(off + 2));
                PushWord(CS);
                PushWord(IP);
                IP = newIp;
                CS = newCs;
                break;
            }
            case 0x7FDu:   // JMP m16:16 (far indirect): IP from [mem], CS from [mem+2] (no push).
            {
                byte modrm = r.X86.ModRm;
                uint mod = (uint)(modrm >> 6) & 3u, rm = (uint)modrm & 7u;
                var (seg, off) = ResolveEaSegOffset(mod, rm, r.X86.Disp, OverrideFromByte(r.X86.SegOverride));
                ushort newIp = ReadEaWordWrapped(seg, off);
                ushort newCs = ReadEaWordWrapped(seg, (ushort)(off + 2));
                IP = newIp;
                CS = newCs;
                break;
            }
        }
    }
}
