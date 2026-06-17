namespace CpuEmulator.Cpus.M68000;

/// <summary>
/// M4.5c data-movement system-misc (DC4 boundary: data-axis-assertable, no trap/control-transfer). SWAP/EXG/LEA/
/// PEA/MOVEQ/TAS/MOVEM/MOVEP. The stack/control/privileged tail (LINK/UNLK, JMP/JSR/RTS/RTR/RTE, TRAP/TRAPV/
/// CHK, ANDI/ORI/EORI-to-CCR/SR, NOP) is M4.5d. Reuses ComputeEa(pureEa) + the merged layer; seam untouched.
/// </summary>
public sealed partial class M68000Cpu
{
    // ── Task 16: SWAP + MOVEQ ───────────────────────────────────────────────────────────────────────────────
    partial void SwapExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint dn = operword & 7u;
        uint cur = DataReg(dn);
        uint result = (cur >> 16) | (cur << 16);
        SetDataRegPartial(dn, result, 2u);                  // whole-long write
        SR = (ushort)((SR & 0xFF00) | AluCcr.Logic(0, 0, result, 2u, false, (byte)(SR & 0xFF)));  // N/Z, V=C=0, X kept
    }

    partial void MoveQExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint dn = (operword >> 9) & 7u;
        uint result = unchecked((uint)(int)(sbyte)(byte)(operword & 0xFFu));   // sign-extend imm8 -> .l
        SetDataRegPartial(dn, result, 2u);
        SR = (ushort)((SR & 0xFF00) | AluCcr.Logic(0, 0, result, 2u, false, (byte)(SR & 0xFF)));
    }

    // ── Task 17: EXG ────────────────────────────────────────────────────────────────────────────────────────
    partial void ExgExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint rx = (operword >> 9) & 7u;
        uint ry = operword & 7u;
        uint mode = (operword >> 3) & 0x1Fu;   // bits 7-3
        switch (mode)
        {
            case 0x08u: { uint t = DataReg(rx); SetDataRegPartial(rx, DataReg(ry), 2u); SetDataRegPartial(ry, t, 2u); break; } // D-D
            case 0x09u: { uint t = Areg(rx);    SetAreg(rx, Areg(ry));               SetAreg(ry, t);                break; } // A-A
            default:    { uint t = DataReg(rx); SetDataRegPartial(rx, Areg(ry), 2u);  SetAreg(ry, t);                break; } // D-A (0x11): Rx=Dn, Ry=An
        }
        // EXG sets NO CCR.
    }

    // ── Task 18: LEA + PEA ──────────────────────────────────────────────────────────────────────────────────
    partial void LeaExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint an = (operword >> 9) & 7u;                                            // dest An (bits 11-9)
        uint ea = ComputeEa(srcMode, srcReg, 2u, r.ExtensionWords, pureEa: true);  // address only (no write-back)
        SetAreg(an, ea);                                                            // whole An; no CCR
        // M4.5d-2b: LEA is refills-lead (no operand bus access — its reads are all prefetch refills, flushed
        // after the body). The only timing delta is the INDEX addressing mode's address-calc internal cycles:
        // LEA (d8,An,Xn)/(d8,PC,Xn) = 12 vs d16(An)/(d16,PC) = 8 — a +4 internal-cycle idle (the brief-index add;
        // double the data ops' +2 because the address IS the result, computed on the internal ALU). The simple
        // modes ((An)=4, d16=8, abs.W=8, abs.L=12) are pure refills, no idle.
        if (srcMode == 6u || (srcMode == 7u && srcReg == 3u)) Idle(4);
    }

    partial void PeaExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint ea = ComputeEa(srcMode, srcReg, 2u, r.ExtensionWords, pureEa: true);
        uint sp = A7 - 4u;                                                          // push: -(A7)
        A7 = sp;
        WriteLongBus(sp, ea);                                                       // no CCR
    }

    // ── Task 19: TAS ────────────────────────────────────────────────────────────────────────────────────────
    partial void TasExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        AluDest dest = ResolveEaDest(srcMode, srcReg, 0u, r.ExtensionWords, out uint operand);   // .b read (address-once)
        uint b = operand & 0xFFu;
        byte ccr = (byte)(SR & 0xFF);
        ccr = (byte)(ccr & ~0x0F);                          // clear N Z V C; keep X
        if ((b & 0x80u) != 0) ccr |= 0x08;                  // N from bit 7 of the ORIGINAL
        if (b == 0u) ccr |= 0x04;                           // Z
        SR = (ushort)((SR & 0xFF00) | ccr);                 // V=C=0
        WriteResolvedDest(dest, 0u, b | 0x80u);             // write back with bit 7 set
    }

    // ── Task 20: MOVEM ──────────────────────────────────────────────────────────────────────────────────────
    partial void MoveMExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        // r.ExtensionWords[0] = the register-list mask. dr (bit 10): 0 = regs->mem, 1 = mem->regs.
        // sz (bit 6): 0 = .w (2 bytes, sign-extend on load), 1 = .l (4 bytes). The EA (bits 5-0) is the base.
        uint mask16 = r.ExtensionWords[0];
        bool toRegs = (operword & 0x0400u) != 0;
        uint opSize = (operword & 0x0040u) != 0 ? 2u : 1u;          // .l : .w
        int unit = opSize == 2u ? 4 : 2;
        var eaExt = ShiftExt(r.ExtensionWords, 1);                  // the mask word precedes the EA's words

        if (srcMode == 4u && !toRegs)   // -(An) predecrement STORE: mask is A7..D0 (REVERSED), pre-decrement each
        {
            uint addr = Areg(srcReg);
            for (int i = 0; i < 16; i++)
            {
                if ((mask16 & (1u << i)) == 0) continue;
                int regIndex = 15 - i;                              // bit0 -> A7 (15) ... bit15 -> D0 (0)
                uint val = regIndex < 8 ? DataReg((uint)regIndex) : Areg((uint)(regIndex - 8));
                addr -= (uint)unit;
                if (opSize == 2u) WriteLongBus(addr, val); else WriteWordBus(addr, (ushort)val);
            }
            SetAreg(srcReg, addr);                                  // write back the final -(An)
            return;
        }

        // All other modes: mask is D0..A7 (forward); compute the base ONCE (pureEa), walk ascending.
        // PC-relative EAs (mode 7 reg 2/3) base off the FIRST extension word's address — but MOVEM's first
        // extension word is the register-list MASK, so the displacement sits one word LATER. The generated Step
        // sets _eaPcBase = operword+2 (correct for a single-disp insn); bump it by 2 here so ComputeEa's PcForEa
        // points at the displacement word, then restore. (Read-only source: MOVEM PC-relative is mem->regs only.)
        bool pcRel = srcMode == 7u && (srcReg == 2u || srcReg == 3u);
        uint savedPcBase = _eaPcBase;
        if (pcRel && _eaPcBase != 0u) _eaPcBase += 2u;
        uint ea = ComputeEa(srcMode, srcReg, opSize, eaExt, pureEa: true);
        if (pcRel) _eaPcBase = savedPcBase;
        uint cursor = ea;
        for (int i = 0; i < 16; i++)
        {
            if ((mask16 & (1u << i)) == 0) continue;                // bit0 -> D0 ... bit8 -> A0 ... bit15 -> A7
            if (toRegs)
            {
                uint raw = opSize == 2u ? ReadLongBus(cursor) : ReadWordBus(cursor);
                uint val = opSize == 2u ? raw : unchecked((uint)(int)(short)(ushort)raw);   // .w sign-extends to 32
                if (i < 8) SetDataRegPartial((uint)i, val, 2u); else SetAreg((uint)(i - 8), val);
            }
            else
            {
                uint val = i < 8 ? DataReg((uint)i) : Areg((uint)(i - 8));
                if (opSize == 2u) WriteLongBus(cursor, val); else WriteWordBus(cursor, (ushort)val);
            }
            cursor += (uint)unit;
        }
        if (srcMode == 3u && toRegs) SetAreg(srcReg, cursor);       // (An)+ load writes back the advanced An
    }

    // ── Task 20 Step 2b: MOVEP (DC5 — INCLUDED). Byte-lane move over d16(An). ───────────────────────────────
    partial void MovePExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        // 0000 ddd 1 dr sz 001 aaa : ddd=Dn(11-9), dr(bit 7) 0=mem->reg 1=reg->mem, sz(bit 6) 0=.w 1=.l,
        // aaa=An(2-0). The +1 displacement word (signed) forms the base d16(An); bytes land on EVERY OTHER
        // address starting at base, MOST-SIGNIFICANT byte first.
        uint dn = (operword >> 9) & 7u;
        uint an = operword & 7u;
        bool toMem = (operword & 0x0080u) != 0;   // bit 7
        bool isLong = (operword & 0x0040u) != 0;  // bit 6
        int bytes = isLong ? 4 : 2;
        short disp = (short)r.ExtensionWords[0];
        uint baseAddr = Areg(an) + unchecked((uint)(int)disp);

        if (toMem)   // Dn -> memory: high byte of the operand first, every other address
        {
            uint val = DataReg(dn);
            for (int i = 0; i < bytes; i++)
            {
                int shift = (bytes - 1 - i) * 8;
                WriteByteAt(baseAddr + (uint)(i * 2), (byte)(val >> shift));
            }
        }
        else         // memory -> Dn: assemble high byte first; .w replaces low 16, .l replaces all 32
        {
            uint val = 0u;
            for (int i = 0; i < bytes; i++)
            {
                int shift = (bytes - 1 - i) * 8;
                val |= (uint)ReadByteAt(baseAddr + (uint)(i * 2)) << shift;
            }
            SetDataRegPartial(dn, val, isLong ? 2u : 1u);   // .w writes the low word (upper preserved)
        }
        // MOVEP sets NO CCR.
    }
}
