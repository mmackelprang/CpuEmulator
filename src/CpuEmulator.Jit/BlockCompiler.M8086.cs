using System.Reflection.Emit;
using CpuEmulator.Core;
using CpuEmulator.Core.Jit;

namespace CpuEmulator.Jit;

/// <summary>M6 PR-B: the 8086 MOV-family emit arm + the ModR/M effective-address resolver — the 8086's
/// analogue of the 68000's dual-EA resolver (BlockCompiler.M68000.cs). The EA matrix is decoded at EMIT
/// TIME from the ModR/M byte (a code-stream constant read via M8086CodePhys — the SEGMENTED (CS&lt;&lt;4)+IP
/// origin, DECISION B-0); the 16-bit segment-relative offset + the (seg&lt;&lt;4)+offset 20-bit physical mirror
/// ComputeX86Ea + DefaultSegmentForX86Rm + ResolveSegment + Physical one-for-one. MOV sets NO flags (PR-C
/// owns the flag core). The WORD access wraps the SECOND byte's OFFSET at 16 bits within the segment (the
/// 8086 wrap quirk, ReadEaWordWrapped) — NOT the physical, so each byte's physical re-forms from the
/// surviving (seg, offset) pair. Reused by PR-C (ALU) + PR-D (branch) for every memory operand.
///
/// <para>PREFIX / PC-ACCOUNTING crux: the run-tuple `pc` is the INSTRUCTION START (the segment-override
/// prefix byte, if any, lives there). EmitInstruction charged the first fetch + advanced PC by 1; this arm
/// advances PC by the REMAINING footprint (length - 1) so the block's nextPc matches r.Length EXACTLY for
/// every prefix combination. The emit-time const reads (ModR/M / disp / imm) start at `operandPc` = pc +
/// (prefix bytes) + 1 (opcode), scanned the same way the generated Decode walk consumes prefixes.</para>
///
/// <para>EMIT-TIME ADDRESS NOTE: M8086CodePhys returns the FULL 20-bit physical (CS&lt;&lt;4)+pc; it is passed
/// to _bus.Read8(uint) DIRECTLY (NOT truncated to ushort — that would drop the segment and read the wrong
/// flat-IP byte). The IP wrap at 16 bits is already applied INSIDE M8086CodePhys.</para></summary>
internal sealed partial class BlockCompiler<TCpu> where TCpu : class
{
    // The 8086 ModR/M register-index → name tables (a small fixed ISA fact, NOT a generator output —
    // M8086Cpu.Mov.cs:20-22). reg8 interleaves low/high halves; reg16/sreg are the standard orders.
    private static readonly string[] M8086Reg8  = ["AL", "CL", "DL", "BL", "AH", "CH", "DH", "BH"];
    private static readonly string[] M8086Reg16 = ["AX", "CX", "DX", "BX", "SP", "BP", "SI", "DI"];
    private static readonly string[] M8086Sreg  = ["ES", "CS", "SS", "DS"];

    // The 8086 prefix bytes (segment-override 26/2E/36/3E, LOCK F0/F1, REP F2/F3) — mirrors the generated
    // walk's s_x86Prefixes (M8086Cpu.g.cs). The arm scans these at emit time to find the opcode position past
    // any prefix(es), so the const reads land on the real ModR/M / disp / imm bytes.
    private static bool M8086IsPrefixByte(byte b) =>
        b is 0x26 or 0x2E or 0x36 or 0x3E or 0xF0 or 0xF1 or 0xF2 or 0xF3;

    /// <summary>The override-prefix enum (mirrors M8086Cpu.X86SegmentOverride) — the compile-time-decoded
    /// segment override the arm threads into EmitM8086SegValue. None=no override.</summary>
    private enum M8086Cpu_Override { None, Es, Cs, Ss, Ds }

    /// <summary>Turn the raw segment-override prefix byte (26/2E/36/3E) the decode captured into the enum
    /// (M8086Cpu.Mov.cs:91 OverrideFromByte). 0 / any other byte ⇒ None.</summary>
    private static M8086Cpu_Override M8086OverrideFromByte(byte b) => b switch
    {
        0x26 => M8086Cpu_Override.Es,
        0x2E => M8086Cpu_Override.Cs,
        0x36 => M8086Cpu_Override.Ss,
        0x3E => M8086Cpu_Override.Ds,
        _    => M8086Cpu_Override.None,
    };

    /// <summary>The resolved segment-register NAME for an override (None ⇒ the caller's default). All inputs are
    /// compile-time constants, so the chosen segment register NAME is decided here and only the VALUE is a runtime
    /// load. Mirrors ResolveSegment (M8086Cpu.Ea.cs:58): the override REPLACES the default.</summary>
    private static string M8086SegName(M8086Cpu_Override over, string defaultSeg) => over switch
    {
        M8086Cpu_Override.Es => "ES",
        M8086Cpu_Override.Cs => "CS",
        M8086Cpu_Override.Ss => "SS",
        M8086Cpu_Override.Ds => "DS",
        _ => defaultSeg,
    };

    /// <summary>Push the 16-bit segment-relative OFFSET (as int) for a (mod, rm, disp) memory operand —
    /// ComputeX86Ea (g.cs:781). base+index per rm (runtime reg loads), + (short)disp (compile-time const),
    /// wrapped to 16 bits. The mod=00,rm=110 disp16-direct exception zeroes the base (offset = disp).</summary>
    private void EmitM8086EaOffset(EmitContext ctx, uint mod, uint rm, ushort disp)
    {
        ILGenerator il = ctx.Il;
        bool disp16Direct = mod == 0u && rm == 6u;
        if (disp16Direct)
        {
            il.Emit(OpCodes.Ldc_I4, (int)disp);                    // offset = disp (no base/index)
            il.Emit(OpCodes.Conv_U2);
            return;
        }
        // base+index sum (each term a runtime 16-bit register load, added as ints):
        switch (rm)
        {
            case 0u: EmitLoadReg16(ctx, "BX"); EmitLoadReg16(ctx, "SI"); il.Emit(OpCodes.Add); break; // BX+SI
            case 1u: EmitLoadReg16(ctx, "BX"); EmitLoadReg16(ctx, "DI"); il.Emit(OpCodes.Add); break; // BX+DI
            case 2u: EmitLoadReg16(ctx, "BP"); EmitLoadReg16(ctx, "SI"); il.Emit(OpCodes.Add); break; // BP+SI
            case 3u: EmitLoadReg16(ctx, "BP"); EmitLoadReg16(ctx, "DI"); il.Emit(OpCodes.Add); break; // BP+DI
            case 4u: EmitLoadReg16(ctx, "SI"); break;                                                 // SI
            case 5u: EmitLoadReg16(ctx, "DI"); break;                                                 // DI
            case 6u: EmitLoadReg16(ctx, "BP"); break;                                                 // BP (not disp16Direct here)
            default: EmitLoadReg16(ctx, "BX"); break;                                                 // BX
        }
        // + (short)disp, then wrap to 16 bits (the offset within the 64 KB segment).
        il.Emit(OpCodes.Ldc_I4, unchecked((int)(short)disp));      // sign-extended disp (int overload)
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Conv_U2);                                  // (ushort)(base + index + disp)
    }

    /// <summary>Push the SEGMENT VALUE (as int, 0..0xFFFF) for a (mod, rm) memory operand, threaded with the
    /// override — DefaultSegmentForX86Rm (g.cs:805) + ResolveSegment (Ea.cs:58). The default is SS for BP-based
    /// forms (rm ∈ {2,3,6} except the disp16-direct rm=6,mod=0), DS otherwise; an override REPLACES it.</summary>
    private void EmitM8086SegValue(EmitContext ctx, uint mod, uint rm, M8086Cpu_Override over)
    {
        bool disp16Direct = mod == 0u && rm == 6u;
        bool bpBased = !disp16Direct && (rm == 2u || rm == 3u || rm == 6u);
        string defaultSeg = bpBased ? "SS" : "DS";
        EmitLoadReg16(ctx, M8086SegName(over, defaultSeg));   // the resolved segment register VALUE (runtime load)
    }

    /// <summary>Push the 20-bit PHYSICAL address (as uint) for a (seg-value, offset) pair: (seg &lt;&lt; 4 + offset)
    /// &amp; 0xFFFFF — Physical (Ea.cs:48). Stack on entry: ..., segValue(int), offset(int); on exit: ..., phys(uint).</summary>
    private void EmitM8086PhysicalFromSegOffset(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        // stack: segValue, offset  ->  ((segValue << 4) + offset) & 0xFFFFF
        il.Emit(OpCodes.Stloc, ctx.DataLocal);     // offset (reuse DataLocal as scratch; clobbered next anyway)
        il.Emit(OpCodes.Ldc_I4_4);
        il.Emit(OpCodes.Shl);                       // segValue << 4
        il.Emit(OpCodes.Ldloc, ctx.DataLocal);
        il.Emit(OpCodes.Add);                       // + offset
        il.Emit(OpCodes.Ldc_I4, 0xFFFFF);
        il.Emit(OpCodes.And);                       // & 20-bit mask
    }

    /// <summary>M6 PR-B: emit one 8086 MOV-family instruction (DECISION B-1/B-2). Reached only when
    /// TargetIsM8086 &amp;&amp; d.Mnemonic == "MOV". Decodes the ModR/M / disp / imm at emit time from the SEGMENTED
    /// code stream (M8086CodePhys), resolves the EA via the Task-1 helpers, and transcribes the matching
    /// MovExecute case one-for-one. The default throws (the gate↔arm lockstep tripwire). MOV sets no flags.
    /// <paramref name="length"/> is the FULL instruction footprint (incl. any prefix); PC advances by length-1
    /// (EmitInstruction already advanced by 1). <paramref name="x86Seg"/> is the captured override prefix byte.</summary>
    private void EmitM8086Mov(EmitContext ctx, ushort pc, OpcodeDescriptor d, int length, byte x86Seg)
    {
        ILGenerator il = ctx.Il;
        byte opcode = (byte)d.Opcode;
        uint key = d.Opcode;   // the plain opcode byte; C6/C7 group rows carry Opcode 0xC6/0xC7 (key normalized below)
        M8086Cpu_Override over = M8086OverrideFromByte(x86Seg);

        // The run-tuple `pc` is the INSTRUCTION START. Scan past any prefix byte(s) at the SEGMENTED physical to
        // find the opcode position; the const reads (ModR/M / disp / imm) start at operandPc = (opcode pos)+1.
        // (The generated walk consumes the same prefixes into r.Length, so this aligns the emit-time reads.)
        int opcodePc = pc;
        while (M8086IsPrefixByte(_bus.Read8(M8086CodePhys((ushort)opcodePc)))) opcodePc++;
        int operandPc = opcodePc + 1;   // the byte AFTER the opcode (ModR/M for 88-8E/C6/C7; imm/moffs for A0-BF)

        bool hasModRm = opcode is 0x88 or 0x89 or 0x8A or 0x8B or 0x8C or 0x8E or 0xC6 or 0xC7;
        uint mod = 0, reg = 0, rm = 0; ushort disp = 0;
        if (hasModRm)
        {
            byte modrm = _bus.Read8(M8086CodePhys((ushort)operandPc)); operandPc++;
            mod = (uint)(modrm >> 6) & 3u;
            reg = (uint)(modrm >> 3) & 7u;
            rm  = (uint)modrm & 7u;
            // the disp length per (mod, rm) — the real 8086 table (g.cs:1781): mod=0,rm=6 ⇒ 2; mod=1 ⇒ 1; mod=2 ⇒ 2.
            int dispLen = mod switch { 0u => rm == 6u ? 2 : 0, 1u => 1, 2u => 2, _ => 0 };
            if (dispLen == 1) { disp = unchecked((ushort)(sbyte)_bus.Read8(M8086CodePhys((ushort)operandPc))); operandPc++; }
            else if (dispLen == 2)
            {
                byte lo = _bus.Read8(M8086CodePhys((ushort)operandPc));
                byte hi = _bus.Read8(M8086CodePhys((ushort)(operandPc + 1)));
                disp = (ushort)(lo | (hi << 8)); operandPc += 2;
            }
        }

        // Normalize the C6/C7 group keys to 0x630/0x638 (the interpreter does the same, Mov.cs:149-150). On the
        // 8086 the reg field of C6/C7 is a don't-care (always MOV r/m,imm).
        if (opcode == 0xC6) key = 0x630u;
        else if (opcode == 0xC7) key = 0x638u;

        switch (key)
        {
            case 0x88u:   // MOV r/m8, r8 — store reg8 to the byte rm
                if (mod == 3u) { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField(M8086Reg8[reg])); il.Emit(OpCodes.Stfld, RegField(M8086Reg8[rm])); }
                else EmitM8086StoreByteEa(ctx, mod, rm, disp, over, () => { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField(M8086Reg8[reg])); });
                break;

            case 0x8Au:   // MOV r8, r/m8 — load the byte rm into reg8
                il.Emit(OpCodes.Ldarg_0);
                if (mod == 3u) { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField(M8086Reg8[rm])); }
                else EmitM8086LoadByteEa(ctx, mod, rm, disp, over);
                il.Emit(OpCodes.Conv_U1);
                il.Emit(OpCodes.Stfld, RegField(M8086Reg8[reg]));
                break;

            case 0x89u:   // MOV r/m16, r16
                if (mod == 3u) { EmitLoadReg16(ctx, M8086Reg16[reg]); EmitStoreReg16(ctx, M8086Reg16[rm]); }
                else EmitM8086StoreWordEa(ctx, mod, rm, disp, over, () => EmitLoadReg16(ctx, M8086Reg16[reg]));
                break;

            case 0x8Bu:   // MOV r16, r/m16
                if (mod == 3u) { EmitLoadReg16(ctx, M8086Reg16[rm]); EmitStoreReg16(ctx, M8086Reg16[reg]); }
                else { EmitM8086LoadWordEa(ctx, mod, rm, disp, over); EmitStoreReg16(ctx, M8086Reg16[reg]); }
                break;

            case 0x8Cu:   // MOV r/m16, Sreg — store segment register to the word rm
                if (mod == 3u) { EmitLoadReg16(ctx, M8086Sreg[reg & 3u]); EmitStoreReg16(ctx, M8086Reg16[rm]); }
                else EmitM8086StoreWordEa(ctx, mod, rm, disp, over, () => EmitLoadReg16(ctx, M8086Sreg[reg & 3u]));
                break;

            case 0x8Eu:   // MOV Sreg, r/m16 — load the word rm into the segment register
                if (mod == 3u) { EmitLoadReg16(ctx, M8086Reg16[rm]); EmitStoreReg16(ctx, M8086Sreg[reg & 3u]); }
                else { EmitM8086LoadWordEa(ctx, mod, rm, disp, over); EmitStoreReg16(ctx, M8086Sreg[reg & 3u]); }
                break;

            // A0-A3: accumulator-direct (moffs). The disp16 rides the IMMEDIATE slot (no ModR/M); default seg DS,
            // override-replaced (the interpreter resolves through ResolveSegment(DS, over) — Mov.cs:206/212/218/224).
            case 0xA0u: case 0xA1u: case 0xA2u: case 0xA3u:
            {
                ushort moffs = (ushort)(_bus.Read8(M8086CodePhys((ushort)operandPc))
                                        | (_bus.Read8(M8086CodePhys((ushort)(operandPc + 1))) << 8));
                string seg = M8086SegName(over, "DS");   // DS default, override-replaced (matches the oracle)
                if (opcode == 0xA0u)   // MOV AL, moffs8
                {
                    il.Emit(OpCodes.Ldarg_0);
                    EmitM8086LoadByteAtSegOff(ctx, seg, moffs);
                    il.Emit(OpCodes.Conv_U1); il.Emit(OpCodes.Stfld, RegField("AL"));
                }
                else if (opcode == 0xA1u)   // MOV AX, moffs16
                {
                    EmitM8086LoadWordAtSegOff(ctx, seg, moffs);
                    EmitStoreReg16(ctx, "AX");
                }
                else if (opcode == 0xA2u)   // MOV moffs8, AL
                    EmitM8086StoreByteAtSegOff(ctx, seg, moffs, () => { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField("AL")); });
                else                          // 0xA3 MOV moffs16, AX
                    EmitM8086StoreWordAtSegOff(ctx, seg, moffs, () => EmitLoadReg16(ctx, "AX"));
                break;
            }

            // B0-B7 MOV r8, imm8 ; B8-BF MOV r16, imm16. reg = opcode & 7; the imm rides the immediate slot.
            case 0xB0u: case 0xB1u: case 0xB2u: case 0xB3u: case 0xB4u: case 0xB5u: case 0xB6u: case 0xB7u:
            {
                byte imm8 = _bus.Read8(M8086CodePhys((ushort)operandPc));
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4, (int)imm8); il.Emit(OpCodes.Conv_U1);
                il.Emit(OpCodes.Stfld, RegField(M8086Reg8[opcode & 7u]));
                break;
            }
            case 0xB8u: case 0xB9u: case 0xBAu: case 0xBBu: case 0xBCu: case 0xBDu: case 0xBEu: case 0xBFu:
            {
                ushort imm16 = (ushort)(_bus.Read8(M8086CodePhys((ushort)operandPc))
                                        | (_bus.Read8(M8086CodePhys((ushort)(operandPc + 1))) << 8));
                il.Emit(OpCodes.Ldc_I4, (int)imm16); EmitStoreReg16(ctx, M8086Reg16[opcode & 7u]);
                break;
            }

            // C6/C7 reg=0: MOV r/m, imm. The imm follows the ModR/M + disp (operandPc already past them).
            case 0x630u:   // MOV r/m8, imm8
            {
                byte imm8 = _bus.Read8(M8086CodePhys((ushort)operandPc));
                if (mod == 3u) { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4, (int)imm8); il.Emit(OpCodes.Conv_U1); il.Emit(OpCodes.Stfld, RegField(M8086Reg8[rm])); }
                else EmitM8086StoreByteEa(ctx, mod, rm, disp, over, () => { il.Emit(OpCodes.Ldc_I4, (int)imm8); });
                break;
            }
            case 0x638u:   // MOV r/m16, imm16
            {
                ushort imm16 = (ushort)(_bus.Read8(M8086CodePhys((ushort)operandPc))
                                        | (_bus.Read8(M8086CodePhys((ushort)(operandPc + 1))) << 8));
                if (mod == 3u) { il.Emit(OpCodes.Ldc_I4, (int)imm16); EmitStoreReg16(ctx, M8086Reg16[rm]); }
                else EmitM8086StoreWordEa(ctx, mod, rm, disp, over, () => il.Emit(OpCodes.Ldc_I4, (int)imm16));
                break;
            }

            default:
                throw new EmulationException(
                    $"BlockCompiler: no 8086 MOV emit branch for key 0x{key:X} (opcode 0x{opcode:X2}); "
                  + "the gate (IsEmittableX86Family) admitted a form the arm does not handle — a lockstep bug.");
        }

        // PC advance: EmitInstruction already advanced PC by 1 (the first/opcode-fetch byte). Advance by the
        // REMAINING footprint (length - 1 = prefix tail + ModR/M + disp + imm) so the block's nextPc == r.Length
        // EXACTLY, for any prefix combination. (length is the walk's full per-instruction stride.)
        int tail = length - 1;
        if (tail > 0) EmitIncrementPC(ctx, tail);
    }

    /// <summary>Push the byte at the (mod, rm, disp, over) memory EA (as int) — resolve the 20-bit physical,
    /// then LoadByteFromBus (the fastmem split; charges 1 cycle). Used by the byte LOAD forms.</summary>
    private void EmitM8086LoadByteEa(EmitContext ctx, uint mod, uint rm, ushort disp, M8086Cpu_Override over)
    {
        EmitM8086SegValue(ctx, mod, rm, over);
        EmitM8086EaOffset(ctx, mod, rm, disp);
        EmitM8086PhysicalFromSegOffset(ctx);   // -> phys (uint)
        LoadByteFromBus(ctx);                   // pops addr, pushes byte; charges 1
    }

    /// <summary>Store the byte produced by <paramref name="pushValue"/> to the (mod, rm, disp, over) memory EA —
    /// resolve the physical, push value, EmitStoreByte (fastmem split; charges 1; marks dirty). pushValue leaves a
    /// byte (int) on the stack.</summary>
    private void EmitM8086StoreByteEa(EmitContext ctx, uint mod, uint rm, ushort disp, M8086Cpu_Override over, System.Action pushValue)
    {
        EmitM8086SegValue(ctx, mod, rm, over);
        EmitM8086EaOffset(ctx, mod, rm, disp);
        EmitM8086PhysicalFromSegOffset(ctx);   // -> phys (uint) [address]
        pushValue();                            // -> value (int)  [stack: address, value]
        EmitStoreByte(ctx);
    }

    /// <summary>Push the WORD at the (mod, rm, disp, over) memory EA (as int) — the offset-wrap form: stash the
    /// resolved (seg, offset), read the low byte at (seg, offset), the high byte at (seg, (offset+1)&amp;0xFFFF),
    /// compose lo | hi&lt;&lt;8. Each byte's physical re-forms from the survivors (ReadEaWordWrapped, Mov.cs:116).</summary>
    private void EmitM8086LoadWordEa(EmitContext ctx, uint mod, uint rm, ushort disp, M8086Cpu_Override over)
    {
        ILGenerator il = ctx.Il;
        // resolve (seg value, offset) into the survivor locals.
        EmitM8086SegValue(ctx, mod, rm, over); il.Emit(OpCodes.Stloc, ctx.M8086SegLocal);
        EmitM8086EaOffset(ctx, mod, rm, disp); il.Emit(OpCodes.Stloc, ctx.M8086OffsetLocal);
        // lo = byte at Physical(seg, offset)
        EmitM8086PushPhysical(ctx, offsetPlusOne: false); LoadByteFromBus(ctx); il.Emit(OpCodes.Stloc, ctx.DataLocal);
        // hi = byte at Physical(seg, (offset+1)&0xFFFF)
        EmitM8086PushPhysical(ctx, offsetPlusOne: true); LoadByteFromBus(ctx);
        il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shl);
        il.Emit(OpCodes.Ldloc, ctx.DataLocal); il.Emit(OpCodes.Or);   // hi<<8 | lo
    }

    /// <summary>Store the word produced by <paramref name="pushValue"/> to the (mod, rm, disp, over) memory EA
    /// — offset-wrap: low byte at (seg, offset), high byte at (seg, (offset+1)&amp;0xFFFF) (WriteEaWordWrapped,
    /// Mov.cs:125). pushValue leaves a word (int) on the stack.</summary>
    private void EmitM8086StoreWordEa(EmitContext ctx, uint mod, uint rm, ushort disp, M8086Cpu_Override over, System.Action pushValue)
    {
        ILGenerator il = ctx.Il;
        EmitM8086SegValue(ctx, mod, rm, over); il.Emit(OpCodes.Stloc, ctx.M8086SegLocal);
        EmitM8086EaOffset(ctx, mod, rm, disp); il.Emit(OpCodes.Stloc, ctx.M8086OffsetLocal);
        // Stage the WORD value in AddrLocal (uint) — EmitStoreByte clobbers DataLocal + EaLocal but NEVER AddrLocal
        // (the Z80/68000 survivor discipline), so the high-byte write below can still read the full word.
        pushValue(); il.Emit(OpCodes.Stloc, ctx.AddrLocal);           // value -> AddrLocal (survivor)
        // write lo: Physical(seg, offset), (byte)value
        EmitM8086PushPhysical(ctx, offsetPlusOne: false);
        il.Emit(OpCodes.Ldloc, ctx.AddrLocal); il.Emit(OpCodes.Conv_U1); EmitStoreByte(ctx);
        // write hi: Physical(seg, (offset+1)&0xFFFF), (byte)(value>>8) — re-reads the SURVIVING word from AddrLocal.
        EmitM8086PushPhysical(ctx, offsetPlusOne: true);
        il.Emit(OpCodes.Ldloc, ctx.AddrLocal); il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shr_Un); il.Emit(OpCodes.Conv_U1);
        EmitStoreByte(ctx);
    }

    /// <summary>Push Physical(M8086SegLocal, M8086OffsetLocal [+1 wrapped]) — ((seg&lt;&lt;4) + ((offset[+1])&amp;0xFFFF))
    /// &amp; 0xFFFFF — from the survivor locals. The +1 wraps the OFFSET at 16 bits (the segment-relative wrap).</summary>
    private void EmitM8086PushPhysical(EmitContext ctx, bool offsetPlusOne)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Ldloc, ctx.M8086SegLocal); il.Emit(OpCodes.Ldc_I4_4); il.Emit(OpCodes.Shl);   // seg<<4
        il.Emit(OpCodes.Ldloc, ctx.M8086OffsetLocal);
        if (offsetPlusOne) { il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Ldc_I4, 0xFFFF); il.Emit(OpCodes.And); }
        il.Emit(OpCodes.Add); il.Emit(OpCodes.Ldc_I4, 0xFFFFF); il.Emit(OpCodes.And);
    }

    // The moffs (A0-A3) byte/word forms reuse the survivor-pair machinery with a CONSTANT segment-register name
    // (DS default, override-threaded) and a CONSTANT offset (the moffs16). Thin wrappers over the above:
    private void EmitM8086LoadByteAtSegOff(EmitContext ctx, string seg, ushort offset)
    { EmitLoadReg16(ctx, seg); ctx.Il.Emit(OpCodes.Ldc_I4, (int)offset); EmitM8086PhysicalFromSegOffset(ctx); LoadByteFromBus(ctx); }
    private void EmitM8086StoreByteAtSegOff(EmitContext ctx, string seg, ushort offset, System.Action pushValue)
    { EmitLoadReg16(ctx, seg); ctx.Il.Emit(OpCodes.Ldc_I4, (int)offset); EmitM8086PhysicalFromSegOffset(ctx); pushValue(); EmitStoreByte(ctx); }
    private void EmitM8086LoadWordAtSegOff(EmitContext ctx, string seg, ushort offset)
    { EmitLoadReg16(ctx, seg); ctx.Il.Emit(OpCodes.Stloc, ctx.M8086SegLocal); ctx.Il.Emit(OpCodes.Ldc_I4, (int)offset); ctx.Il.Emit(OpCodes.Stloc, ctx.M8086OffsetLocal);
      EmitM8086PushPhysical(ctx, false); LoadByteFromBus(ctx); ctx.Il.Emit(OpCodes.Stloc, ctx.DataLocal);
      EmitM8086PushPhysical(ctx, true); LoadByteFromBus(ctx); ctx.Il.Emit(OpCodes.Ldc_I4_8); ctx.Il.Emit(OpCodes.Shl); ctx.Il.Emit(OpCodes.Ldloc, ctx.DataLocal); ctx.Il.Emit(OpCodes.Or); }
    private void EmitM8086StoreWordAtSegOff(EmitContext ctx, string seg, ushort offset, System.Action pushValue)
    { EmitLoadReg16(ctx, seg); ctx.Il.Emit(OpCodes.Stloc, ctx.M8086SegLocal); ctx.Il.Emit(OpCodes.Ldc_I4, (int)offset); ctx.Il.Emit(OpCodes.Stloc, ctx.M8086OffsetLocal);
      pushValue(); ctx.Il.Emit(OpCodes.Stloc, ctx.AddrLocal);   // word value -> AddrLocal (survives EmitStoreByte)
      EmitM8086PushPhysical(ctx, false); ctx.Il.Emit(OpCodes.Ldloc, ctx.AddrLocal); ctx.Il.Emit(OpCodes.Conv_U1); EmitStoreByte(ctx);
      EmitM8086PushPhysical(ctx, true); ctx.Il.Emit(OpCodes.Ldloc, ctx.AddrLocal); ctx.Il.Emit(OpCodes.Ldc_I4_8); ctx.Il.Emit(OpCodes.Shr_Un); ctx.Il.Emit(OpCodes.Conv_U1); EmitStoreByte(ctx); }
}
