namespace CpuEmulator.Cpus.M8086;

/// <summary>
/// M5.5a: the 8086 MOV-family op bodies (hand-written). This is the read/modify/write EFFECTIVE-ADDRESS
/// execute pipeline the later instruction families (ALU/shift/control — M5.5b-d) reuse: decode → ModR/M →
/// EA → segment → bus. The generated x86 Step arm decodes through the segmented fetch stream, advances IP by
/// the computed length, then dispatches the resolved OperationKey through <c>ExecuteX86</c>, which routes the
/// MOV-family keys (88-8E, A0-A3, B0-BF, and the C6/C7 reg=0 group keys 0x630/0x638) to
/// <see cref="MovExecute"/> here. Everything else routes to <c>HandleUndefinedOpcode</c> (scope is MOV-only).
///
/// <para>MOV SETS NO FLAGS — no body here touches <c>FLAGS</c>. The memory operands flow through the
/// EA/segmentation layer (<see cref="ResolveEaPhysical"/> + the byte/word bus helpers in M8086Cpu.Ea.cs); the
/// register operands use the generated AX/AL/... accessors (writing a half auto-updates the 16-bit view, the
/// partial-write hazard). The accumulator-direct forms (A0-A3) carry their <c>moffs</c> disp16 in the IMMEDIATE
/// slot (no ModR/M), default segment DS, overridable.</para>
/// </summary>
public sealed partial class M8086Cpu
{
    // ── Register encoding tables (8086 ModR/M reg/rm + opcode-embedded reg). ─────────────────────────────────
    //   Reg8 index:  0=AL 1=CL 2=DL 3=BL 4=AH 5=CH 6=DH 7=BH   (the low/high halves interleave by index)
    //   Reg16 index: 0=AX 1=CX 2=DX 3=BX 4=SP 5=BP 6=SI 7=DI
    //   Sreg index:  0=ES 1=CS 2=SS 3=DS

    /// <summary>Read an 8-bit register by its 0-7 encoding (AL/CL/DL/BL/AH/CH/DH/BH).</summary>
    private byte Reg8(uint idx) => idx switch
    {
        0u => AL, 1u => CL, 2u => DL, 3u => BL,
        4u => AH, 5u => CH, 6u => DH, _ => BH,
    };

    /// <summary>Write an 8-bit register by its 0-7 encoding. Writing AL/AH (etc.) auto-updates AX via the
    /// generated computed pair-view — the partial-write hazard the M5.1 state proof pinned.</summary>
    private void SetReg8(uint idx, byte v)
    {
        switch (idx)
        {
            case 0u: AL = v; break;
            case 1u: CL = v; break;
            case 2u: DL = v; break;
            case 3u: BL = v; break;
            case 4u: AH = v; break;
            case 5u: CH = v; break;
            case 6u: DH = v; break;
            default: BH = v; break;
        }
    }

    /// <summary>Read a 16-bit register by its 0-7 encoding (AX/CX/DX/BX/SP/BP/SI/DI).</summary>
    private ushort Reg16(uint idx) => idx switch
    {
        0u => AX, 1u => CX, 2u => DX, 3u => BX,
        4u => SP, 5u => BP, 6u => SI, _ => DI,
    };

    /// <summary>Write a 16-bit register by its 0-7 encoding.</summary>
    private void SetReg16(uint idx, ushort v)
    {
        switch (idx)
        {
            case 0u: AX = v; break;
            case 1u: CX = v; break;
            case 2u: DX = v; break;
            case 3u: BX = v; break;
            case 4u: SP = v; break;
            case 5u: BP = v; break;
            case 6u: SI = v; break;
            default: DI = v; break;
        }
    }

    /// <summary>Read a SEGMENT register by its 0-3 encoding (ES/CS/SS/DS).</summary>
    private ushort Sreg(uint idx) => idx switch
    {
        0u => ES, 1u => CS, 2u => SS, _ => DS,
    };

    /// <summary>Write a SEGMENT register by its 0-3 encoding.</summary>
    private void SetSreg(uint idx, ushort v)
    {
        switch (idx)
        {
            case 0u: ES = v; break;
            case 1u: CS = v; break;
            case 2u: SS = v; break;
            default: DS = v; break;
        }
    }

    /// <summary>Turn the raw segment-override prefix byte the decode walk captured into the EA layer's
    /// <see cref="X86SegmentOverride"/> enum: 26→Es, 2E→Cs, 36→Ss, 3E→Ds, anything else (0) ⇒ None.</summary>
    private static X86SegmentOverride OverrideFromByte(byte b) => b switch
    {
        0x26 => X86SegmentOverride.Es,
        0x2E => X86SegmentOverride.Cs,
        0x36 => X86SegmentOverride.Ss,
        0x3E => X86SegmentOverride.Ds,
        _    => X86SegmentOverride.None,
    };

    /// <summary>M5.5a: execute one MOV-family instruction. The generated <c>ExecuteX86</c> dispatches here with
    /// the resolved OperationKey + the full <see cref="CpuEmulator.Core.Jit.DecodeResult"/> (carrying the
    /// captured ModR/M byte, the sign-extended disp16, the immediate, and the segment-override prefix byte).
    /// MOV sets no flags.</summary>
    partial void MovExecute(uint key, CpuEmulator.Core.Jit.DecodeResult r)
    {
        // Re-derive the ModR/M sub-fields once (valid for the ModR/M forms 88-8E + C6/C7; ignored by the
        // accumulator-direct A0-A3 and the imm→reg B0-BF forms, which have no ModR/M).
        byte modrm = r.X86.ModRm;
        uint mod = (uint)(modrm >> 6) & 3u;
        uint reg = (uint)(modrm >> 3) & 7u;
        uint rm  = (uint)modrm & 7u;
        ushort disp = r.X86.Disp;
        X86SegmentOverride over = OverrideFromByte(r.X86.SegOverride);

        switch (key)
        {
            // ── 88-8B: the d/w-bit register/memory MOVs. ──────────────────────────────────────────────────
            case 0x88u:   // MOV r/m8, r8 — store reg8 to the byte rm
            {
                byte src = Reg8(reg);
                if (mod == 3u) SetReg8(rm, src);
                else WriteEaByte(ResolveEaPhysical(mod, rm, disp, over), src);
                break;
            }
            case 0x89u:   // MOV r/m16, r16 — store reg16 to the word rm
            {
                ushort src = Reg16(reg);
                if (mod == 3u) SetReg16(rm, src);
                else WriteEaWord(ResolveEaPhysical(mod, rm, disp, over), src);
                break;
            }
            case 0x8Au:   // MOV r8, r/m8 — load the byte rm into reg8
            {
                byte src = mod == 3u ? Reg8(rm) : ReadEaByte(ResolveEaPhysical(mod, rm, disp, over));
                SetReg8(reg, src);
                break;
            }
            case 0x8Bu:   // MOV r16, r/m16 — load the word rm into reg16
            {
                ushort src = mod == 3u ? Reg16(rm) : ReadEaWord(ResolveEaPhysical(mod, rm, disp, over));
                SetReg16(reg, src);
                break;
            }

            // ── 8C/8E: the segment-register MOVs (reg field = segment index, only 0-3 meaningful). ─────────
            case 0x8Cu:   // MOV r/m16, Sreg — store the segment register to the word rm
            {
                ushort src = Sreg(reg & 3u);
                if (mod == 3u) SetReg16(rm, src);
                else WriteEaWord(ResolveEaPhysical(mod, rm, disp, over), src);
                break;
            }
            case 0x8Eu:   // MOV Sreg, r/m16 — load the word rm into the segment register
            {
                ushort src = mod == 3u ? Reg16(rm) : ReadEaWord(ResolveEaPhysical(mod, rm, disp, over));
                SetSreg(reg & 3u, src);
                break;
            }

            // ── A0-A3: the accumulator-direct (moffs) MOVs. The moffs disp16 rides the IMMEDIATE slot (no
            //    ModR/M); default segment DS, overridable. ──────────────────────────────────────────────────
            case 0xA0u:   // MOV AL, moffs8
            {
                ushort offset = r.X86.Imm;
                AL = ReadEaByte(Physical(ResolveSegment(DS, over), offset));
                break;
            }
            case 0xA1u:   // MOV AX, moffs16
            {
                ushort offset = r.X86.Imm;
                AX = ReadEaWord(Physical(ResolveSegment(DS, over), offset));
                break;
            }
            case 0xA2u:   // MOV moffs8, AL
            {
                ushort offset = r.X86.Imm;
                WriteEaByte(Physical(ResolveSegment(DS, over), offset), AL);
                break;
            }
            case 0xA3u:   // MOV moffs16, AX
            {
                ushort offset = r.X86.Imm;
                WriteEaWord(Physical(ResolveSegment(DS, over), offset), AX);
                break;
            }

            // ── B0-BF: MOV reg, immediate (reg = opcode & 7; the immediate rides the immediate slot). ───────
            case 0xB0u: case 0xB1u: case 0xB2u: case 0xB3u:
            case 0xB4u: case 0xB5u: case 0xB6u: case 0xB7u:   // MOV r8, imm8
                SetReg8(key & 7u, (byte)r.X86.Imm);
                break;
            case 0xB8u: case 0xB9u: case 0xBAu: case 0xBBu:
            case 0xBCu: case 0xBDu: case 0xBEu: case 0xBFu:   // MOV r16, imm16
                SetReg16(key & 7u, r.X86.Imm);
                break;

            // ── C6/C7 reg=0: MOV r/m, immediate (the group keys (0xC6<<3)|0 = 0x630, (0xC7<<3)|0 = 0x638). ──
            case 0x630u:   // MOV r/m8, imm8
            {
                byte src = (byte)r.X86.Imm;
                if (mod == 3u) SetReg8(rm, src);
                else WriteEaByte(ResolveEaPhysical(mod, rm, disp, over), src);
                break;
            }
            case 0x638u:   // MOV r/m16, imm16
            {
                ushort src = r.X86.Imm;
                if (mod == 3u) SetReg16(rm, src);
                else WriteEaWord(ResolveEaPhysical(mod, rm, disp, over), src);
                break;
            }
        }
    }
}
