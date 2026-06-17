namespace CpuEmulator.Cpus.M8086;

/// <summary>
/// M5.5c — the 8086 misc data-movement + flag-control op bodies (hand-written): XCHG (86/87 r/m,r and 91-97
/// reg,AX), LEA (8D), LDS/LES (C5/C4), XLAT (D7), LAHF/SAHF (9F/9E), CBW/CWD (98/99), the flag-control ops
/// CLC/STC/CMC (F8/F9/F5), CLD/STD (FC/FD), CLI/STI (FA/FB), and the no-state ops NOP (90), HLT (F4), WAIT
/// (9B). Dispatched by the generated <c>ExecuteX86</c> to <see cref="MiscExecute"/>. Reuses the M5.5a EA
/// pipeline + register accessors and the M5.5b FLAGS bit masks.
///
/// <para><b>The flag-byte ops (LAHF/SAHF).</b> The 8086 low FLAGS byte has fixed bits: bit1 reads as 1, bit3
/// and bit5 read as 0 (the others are SF=7 ZF=6 AF=4 PF=2 CF=0). LAHF loads AH with this canonical low byte;
/// SAHF writes SF/ZF/AF/PF/CF back from AH (keeping the fixed bits). The canonical low-byte mask is 0xD5
/// (bits 7,6,4,2,0) ORed with 0x02 (bit1 always set).</para>
/// </summary>
public sealed partial class M8086Cpu
{
    // ── The two extra FLAGS bits M5.5c's flag-control ops touch (the M5.5b Alu partial declares CF/PF/AF/ZF/
    //    SF/OF; DF + IF live here). DF=bit10, IF=bit9 (the M8086Spec layout). ──────────────────────────────
    private const ushort FlagDF = 1 << 10;   // direction (string ops; CLD/STD)
    private const ushort FlagIF = 1 << 9;    // interrupt-enable (CLI/STI)

    /// <summary>The canonical 8086 low-FLAGS-byte bits SF/ZF/AF/PF/CF (mask 0xD5); bit1 is always 1.</summary>
    private const byte LahfSahfMask = 0xD5;

    /// <summary>M5.5c: execute one misc data-movement / flag-control instruction.</summary>
    partial void MiscExecute(uint key, CpuEmulator.Core.Jit.DecodeResult r)
    {
        byte modrm = r.X86.ModRm;
        uint mod = (uint)(modrm >> 6) & 3u;
        uint reg = (uint)(modrm >> 3) & 7u;
        uint rm = (uint)modrm & 7u;
        ushort disp = r.X86.Disp;
        X86SegmentOverride over = OverrideFromByte(r.X86.SegOverride);

        switch (key)
        {
            // ── 86/87: XCHG r/m,r — swap the r/m operand with the reg operand. Sets no flags. ──────────────
            case 0x86u:   // XCHG r/m8, r8
            {
                byte a = ReadRmByte(mod, rm, disp, over);
                byte b = Reg8(reg);
                WriteRmByte(mod, rm, disp, over, b);
                SetReg8(reg, a);
                break;
            }
            case 0x87u:   // XCHG r/m16, r16
            {
                ushort a = ReadRmWord(mod, rm, disp, over);
                ushort b = Reg16(reg);
                WriteRmWord(mod, rm, disp, over, b);
                SetReg16(reg, a);
                break;
            }

            // ── 91-97: XCHG r16, AX (0x90 is NOP = XCHG AX,AX, handled separately). reg = opcode & 7. ───────
            case 0x91u: case 0x92u: case 0x93u: case 0x94u: case 0x95u: case 0x96u: case 0x97u:
            {
                uint regIdx = key & 7u;
                ushort tmp = AX;
                AX = Reg16(regIdx);
                SetReg16(regIdx, tmp);
                break;
            }

            // ── 8D: LEA r16, m — load the EFFECTIVE ADDRESS (the 16-bit offset), NOT the memory content. The
            //    segment + bus are not consulted. mod==3 (a register source) is undefined on the 8086; the
            //    corpus does not exercise it for LEA (the assembler never emits it). ──────────────────────────
            case 0x8Du:
            {
                ushort offset = ComputeX86Ea(mod, rm, disp);
                SetReg16(reg, offset);
                break;
            }

            // ── C5 LDS / C4 LES: load r16 from [mem] and DS/ES from [mem+2] (the far-pointer load). ──────────
            case 0xC5u:   // LDS r16, m16:16
            {
                var (seg, off) = ResolveEaSegOffset(mod, rm, disp, over);
                ushort lo = ReadEaWordWrapped(seg, off);
                ushort hi = ReadEaWordWrapped(seg, (ushort)(off + 2));
                SetReg16(reg, lo);
                DS = hi;
                break;
            }
            case 0xC4u:   // LES r16, m16:16
            {
                var (seg, off) = ResolveEaSegOffset(mod, rm, disp, over);
                ushort lo = ReadEaWordWrapped(seg, off);
                ushort hi = ReadEaWordWrapped(seg, (ushort)(off + 2));
                SetReg16(reg, lo);
                ES = hi;
                break;
            }

            // ── D7: XLAT — AL = [DS:BX + AL] (a byte load; DS is overridable). ────────────────────────────────
            case 0xD7u:
            {
                ushort offset = (ushort)(BX + AL);
                ushort seg = ResolveSegment(DS, over);
                AL = ReadEaByte(Physical(seg, offset));
                break;
            }

            // ── 9F LAHF / 9E SAHF: the AH <-> low-FLAGS-byte transfers. ───────────────────────────────────────
            case 0x9Fu:   // LAHF: AH = canonical low FLAGS byte (SF ZF 0 AF 0 PF 1 CF)
                AH = (byte)((FLAGS & LahfSahfMask) | 0x02);
                break;
            case 0x9Eu:   // SAHF: set SF/ZF/AF/PF/CF from AH (keep the fixed low bits + the whole high byte)
                FLAGS = (ushort)((FLAGS & 0xFF00) | (AH & LahfSahfMask) | 0x02);
                break;

            // ── 98 CBW / 99 CWD: sign-extend AL→AX / AX→DX:AX. No flags. ──────────────────────────────────────
            case 0x98u:   // CBW: AH = (AL bit7) ? 0xFF : 0x00
                AH = (byte)((AL & 0x80) != 0 ? 0xFF : 0x00);
                break;
            case 0x99u:   // CWD: DX = (AX bit15) ? 0xFFFF : 0x0000
                DX = (ushort)((AX & 0x8000) != 0 ? 0xFFFF : 0x0000);
                break;

            // ── The flag-control ops. ────────────────────────────────────────────────────────────────────────
            case 0xF8u: SetFlag(FlagCF, false); break;                          // CLC
            case 0xF9u: SetFlag(FlagCF, true); break;                           // STC
            case 0xF5u: SetFlag(FlagCF, (FLAGS & FlagCF) == 0); break;          // CMC (complement carry)
            case 0xFCu: SetFlag(FlagDF, false); break;                          // CLD
            case 0xFDu: SetFlag(FlagDF, true); break;                           // STD
            case 0xFAu: SetFlag(FlagIF, false); break;                          // CLI
            case 0xFBu: SetFlag(FlagIF, true); break;                           // STI

            // ── No-state ops (data axis): NOP / WAIT do nothing; HLT halts the CPU (no register effect beyond
            //    the IP advance the Step already did — the data-axis corpus checks only regs+ram). ──────────────
            case 0x90u: break;   // NOP
            case 0x9Bu: break;   // WAIT (no coprocessor on the data axis)
            case 0xF4u: break;   // HLT (the halted state is not a data-axis register; M5.5d models the real halt)
        }
    }
}
