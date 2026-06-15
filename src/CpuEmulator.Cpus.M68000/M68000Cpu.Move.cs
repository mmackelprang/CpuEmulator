namespace CpuEmulator.Cpus.M68000;

/// <summary>
/// M4.5a: the MOVE-family op bodies (hand-written, per D-A (resolved) — the size axis + privileged moves are
/// procedural and do not compress into the existing micro-op vocabulary; M4.5b/c may promote to data once the
/// ALU families reveal the shared shape). The generated FieldGrammar Step dispatches the MOVE opIndices here.
/// These call the live ComputeEa (M4.3b) + the wide cycle-charging bus helpers (Task 2). Semantics: MOVE sets
/// CCR (N/Z, V=C=0) and does a PARTIAL write to a data-register dest at .b/.w; MOVEA writes the whole An
/// (sign-extended at .w) and sets NO CCR; MOVE to/from SR/CCR and MOVE USP are the privileged system moves.
///
/// The *Execute methods are classic `partial void` (implicitly private) implementing parts: the generated
/// partial declares the matching `partial void` (no accessibility modifier — C# requires no implementation
/// part for a classic partial method, which is what let Task 3's Step compile with the bodies absent; this
/// file is the implementation part).
/// </summary>
public sealed partial class M68000Cpu
{
    // size index: 0=.b, 1=.w, 2=.l
    private static uint SizeMask(uint size) => size switch { 0u => 0xFFu, 1u => 0xFFFFu, _ => 0xFFFFFFFFu };

    /// <summary>Read the source operand at the given EA, size-correct, from a data/address register or memory.
    /// For Dn (mode 0) reads the register low bits; for An (mode 1) reads the address register; for #imm
    /// (mode 7 reg 4) reads the extension words; otherwise dereferences the computed EA via the wide bus.</summary>
    private uint ReadEaOperand(uint mode, uint reg, uint size, CpuEmulator.Core.Jit.ExtensionWords ext)
    {
        if (mode == 0u) return DataReg(reg) & SizeMask(size);          // Dn
        if (mode == 1u) return Areg(reg);                              // An (always full 32 — MOVEA source)
        if (mode == 7u && reg == 4u)                                   // #imm — value is the extension words
            return size == 2u ? (((uint)ext[0] << 16) | ext[1]) : (ext[0] & SizeMask(size));
        uint ea = ComputeEa(mode, reg, size, ext, pureEa: false);     // dereference (write-back happens here)
        return size switch { 0u => ReadByteAt(ea), 1u => ReadWordBus(ea), _ => ReadLongBus(ea) };
    }

    // A byte read on the 16-bit bus is a .w transaction whose relevant half is the addressed byte. The 68000
    // reads the word containing the byte; the tracing vector records a .b transaction at the byte address.
    // Use Read8 (the bus composes the byte; a .b transaction is recorded by the byte path). Charge a word cycle.
    private byte ReadByteAt(uint ea) { _cycles += WordAccessCycles; return _bus.Read8(ea); }
    private void WriteByteAt(uint ea, byte v) { _cycles += WordAccessCycles; _bus.Write8(ea, v); }

    private uint DataReg(uint reg) => reg switch { 0u=>D0,1u=>D1,2u=>D2,3u=>D3,4u=>D4,5u=>D5,6u=>D6,_=>D7 };
    private void SetDataRegPartial(uint reg, uint value, uint size)
    {
        uint mask = SizeMask(size);
        uint cur = DataReg(reg);
        uint merged = (cur & ~mask) | (value & mask);   // PARTIAL write (.b/.w preserve the upper bits)
        switch (reg) { case 0u:D0=merged;break; case 1u:D1=merged;break; case 2u:D2=merged;break;
                       case 3u:D3=merged;break; case 4u:D4=merged;break; case 5u:D5=merged;break;
                       case 6u:D6=merged;break; default:D7=merged;break; }
    }

    /// <summary>Write the operand to the destination EA (size-correct). Dn = partial write; memory = wide bus.</summary>
    private void WriteEaOperand(uint mode, uint reg, uint size, uint value,
        CpuEmulator.Core.Jit.ExtensionWords ext)
    {
        if (mode == 0u) { SetDataRegPartial(reg, value, size); return; }   // Dn — partial
        uint ea = ComputeEa(mode, reg, size, ext, pureEa: false);
        switch (size) { case 0u: WriteByteAt(ea, (byte)value); break;
                        case 1u: WriteWordBus(ea, (ushort)value); break;
                        default: WriteLongBus(ea, value); break; }
    }

    /// <summary>Set the CCR for a MOVE result: N = result's sign bit (size-relative), Z = result zero, V = C = 0,
    /// X unchanged (MOVE does not touch X).</summary>
    private void SetMoveCcr(uint result, uint size)
    {
        uint mask = SizeMask(size);
        uint signBit = size switch { 0u => 0x80u, 1u => 0x8000u, _ => 0x80000000u };
        bool n = (result & signBit) != 0;
        bool z = (result & mask) == 0;
        byte ccr = (byte)(SR & 0xFF);
        ccr = (byte)(ccr & ~0x0F);                       // clear N(3) Z(2) V(1) C(0); X(4) preserved
        if (n) ccr |= 0x08;
        if (z) ccr |= 0x04;
        SR = (ushort)((SR & 0xFF00) | ccr);
    }

    // ── The dispatch targets the generator emits (classic partial declarations) ─────────────────────────────
    partial void MoveExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size,
        uint srcMode, uint srcReg)
    {
        // Destination EA at bits 11-6, mode/register SWAPPED: dest register = bits 11-9, dest mode = bits 8-6.
        uint dstReg  = (operword >> 9) & 7u;
        uint dstMode = (operword >> 6) & 7u;
        uint value = ReadEaOperand(srcMode, srcReg, size, r.ExtensionWords);   // source (may write-back)
        // The dest EA's extension words follow the source's in r.ExtensionWords — Task 6 surfaces them in order.
        WriteEaOperand(dstMode, dstReg, size, value, DestExtensionWords(r.ExtensionWords, srcMode, srcReg, size));
        SetMoveCcr(value, size);
    }

    /// <summary>Slice the destination EA's extension words out of the combined buffer (the source EA's words
    /// come first; the dest EA's follow). M4.5a's MOVE forms that need dest extension words are covered by the
    /// Task-6 two-EA decode; this helper picks the dest slice. For dest modes with no extension word it returns
    /// an empty buffer.</summary>
    private static CpuEmulator.Core.Jit.ExtensionWords DestExtensionWords(
        CpuEmulator.Core.Jit.ExtensionWords all, uint srcMode, uint srcReg, uint size)
    {
        int srcCount = SourceExtWordCount(srcMode, srcReg, size);
        // Shift the dest words down to index 0 so ComputeEa(dest) reads ext[0]/ext[1].
        return new CpuEmulator.Core.Jit.ExtensionWords(
            all[srcCount], all[srcCount + 1], all[srcCount + 2], all[srcCount + 3],
            System.Math.Max(0, all.Count - srcCount));
    }

    /// <summary>The source-EA extension-word count (mirrors the generated ExtensionWordCount for a single EA);
    /// shared by the two-EA MOVE length (Task 6) and the dest-slice above so the source/dest split is one rule.</summary>
    private static int SourceExtWordCount(uint mode, uint reg, uint size) => mode switch
    {
        5u or 6u => 1,
        7u => reg switch { 0u => 1, 1u => 2, 2u => 1, 3u => 1, 4u => size == 2u ? 2 : 1, _ => 0 },
        _ => 0,
    };

    partial void MoveAExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size,
        uint srcMode, uint srcReg)
    {
        uint dstReg = (operword >> 9) & 7u;                       // dest An register (bits 11-9)
        uint value = ReadEaOperand(srcMode, srcReg, size, r.ExtensionWords);
        uint extended = size == 1u ? unchecked((uint)(int)(short)(ushort)value) : value;   // .w sign-extends to 32
        SetAreg(dstReg, extended);                                // WHOLE An write; MOVEA sets NO CCR
    }

    partial void MoveToSrExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r,
        uint srcMode, uint srcReg)
    {
        // PRIVILEGED: a user-mode MOVE to SR is a privilege violation (vector 8) — that EXCEPTION is M4.5d.
        // M4.5a honors the bit but does NOT vector (the MOVEtoSR vectors are supervisor-mode cases). If a
        // user-mode case appears, Task 8 flags it; the privilege vector lands in M4.5d.
        uint value = ReadEaOperand(srcMode, srcReg, size: 1u, r.ExtensionWords);   // .w source
        // The 68000 SR has only T(15) S(13) I2..I0(10-8) X N Z V C(4-0) implemented; the unused bits
        // (14, 11, 7-5) read as 0 and a load masks them off. SR_VALID = 0xA71F.
        SR = (ushort)(value & 0xA71Fu);
    }

    partial void MoveToCcrExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r,
        uint srcMode, uint srcReg)
    {
        uint value = ReadEaOperand(srcMode, srcReg, size: 1u, r.ExtensionWords);   // .w source, low byte → CCR
        Ccr = (byte)(value & 0x1F);                               // only bits 0-4 are CCR; high bits ignored
    }

    partial void MoveFromSrExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r,
        uint srcMode, uint srcReg)
    {
        // NOTE: for MOVE-from-SR the operword's low-6 EA field (passed here as srcMode/srcReg) is the
        // DESTINATION, not a source — this op moves SR -> EA. Alias them explicitly so a future maintainer
        // adding a second EA or a privilege path does not misread the direction (review finding, M4.5a).
        uint dstMode = srcMode, dstReg = srcReg;
        WriteEaOperand(dstMode, dstReg, size: 1u, value: SR, ext: r.ExtensionWords);   // SR.w -> dest EA
    }

    partial void MoveUspExecute(uint operword)
    {
        // PRIVILEGED. bit 3 = direction: 1 = USP → An; 0 = An → USP. reg = bits 2-0.
        uint reg = operword & 7u;
        if ((operword & 0x8u) != 0) SetAreg(reg, USP);            // MOVE USP,An (from USP)
        else USP = Areg(reg);                                     // MOVE An,USP (to USP)
    }
}
