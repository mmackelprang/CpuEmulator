using System.Reflection;
using System.Reflection.Emit;
using CpuEmulator.Core;
using CpuEmulator.Core.Jit;

namespace CpuEmulator.Jit;

/// <summary>M6 PR-1: the Z80 LD-family emit arm — the first STRUCTURED-CPU (non-6502) family to emit
/// real IL instead of an interpreter fallback. Each branch mirrors the generated Op-body oracle
/// one-for-one: operands through the fastmem split (LoadByteFromBus / EmitStoreByte), register writes
/// via the 8-bit RegField idiom or PR-0's EmitLoadReg16 / EmitStoreReg16, the Z80 T-state residual
/// charged after the up-front fetch + the per-access charges, <c>Q = 0</c> always, and the WZ (MEMPTR)
/// side-effects exactly where the oracle sets them. LD touches NO flags (that is PR-2's ALU core), so
/// this proves the structured-CPU IL-emit path end-to-end without the Q/flag tax.
///
/// CYCLE MODEL (DECISION A): the descriptor's BaseCycles is now CORRECT for every emitted LD form
/// (Task 1b fixed LD r,n 2 -> 7 at the generation source), so this arm charges the residual to reach
/// d.BaseCycles — it owns NO private cycle table. Each branch's residual constant is
/// <c>BaseCycles - 1 (fetch, charged up-front in EmitInstruction) - (per-access charges)</c>; every
/// LoadByteFromBus / EmitStoreByte charges 1. The residuals are cross-checked by the TomHarte JIT
/// cycle-parity gate (the final CycleCount delta must equal the interpreter's).
///
/// EA CLOBBER INVARIANT (the load-bearing correctness fix): LoadByteFromBus stashes its access address
/// in ctx.EaLocal as its FIRST act, and EmitStoreByte clobbers BOTH ctx.EaLocal AND ctx.DataLocal. So a
/// 16-bit absolute EA that must SURVIVE one or more bus accesses lives in ctx.AddrLocal (uint, which the
/// bus helpers never touch) — EmitZ80ReadAbsEa stores the EA there, and every (nn) / 16-bit-absolute
/// branch re-reads ctx.AddrLocal (NOT ctx.EaLocal) for the address and the WZ math.
///
/// DECISION B: the 16-bit-absolute LD (nn),HL / LD HL,(nn) (0x22 / 0x2A, 16 T) have their own branches
/// here (no longer deferred); the gate (IsEmittableZ80Family) admits them in lockstep.</summary>
internal sealed partial class BlockCompiler<TCpu> where TCpu : class
{
    /// <summary>Emit the Z80 LD form keyed by (descriptor mode, op-kind). Only reached when TargetIsZ80
    /// and the row is an "LD" (the three EmitLoad/EmitStore/EmitRegister guards route here). The default
    /// throws so the gate and the arm stay in lockstep — if the whitelist ever admits a form with no
    /// branch, it fails loudly at compile time rather than silently mis-emitting.</summary>
    private void EmitZ80Ld(EmitContext ctx, OpcodeDescriptor d)
    {
        ILGenerator il = ctx.Il;
        JitOp op = d.Ops[0];

        switch (d.Mode, op.Kind)
        {
            // LD r,r'  (Register, Transfer source->target) — 4 T (fetch 1 + residual 3). 8-bit halves.
            // (LD SP,HL — the 16-bit Register/Transfer 0xF9 — is excluded by the gate; it never reaches here.)
            case (JitMode.Register, "Transfer"):
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, RegField(op.RegA));   // source byte
                il.Emit(OpCodes.Stfld, RegField(op.RegB));   // target = source
                EmitChargeCycles(ctx, 3);
                EmitZ80ClearQ(ctx);
                break;

            // LD r,n  (Immediate, Load target) — 7 T (fetch 1 + imm read 1 + residual 5). No WZ.
            case (JitMode.Immediate, "Load"):
                EmitLoadPC(ctx);
                il.Emit(OpCodes.Conv_U4);
                LoadByteFromBus(ctx);                        // data (int); charges 1
                il.Emit(OpCodes.Stloc, ctx.DataLocal);
                EmitIncrementPC(ctx, 1);                     // consume the immediate byte
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldloc, ctx.DataLocal);
                il.Emit(OpCodes.Conv_U1);
                il.Emit(OpCodes.Stfld, RegField(op.RegA));   // target = data
                EmitChargeCycles(ctx, 5);
                EmitZ80ClearQ(ctx);
                break;

            // LD r,(HL) / LD A,(BC) / LD A,(DE)  (RegisterIndirect, Load) — 7 T (fetch 1 + read 1 + 5).
            // The (HL) forms set NO WZ; the accumulator forms set WZ = pair + 1 (oracle Op0A/Op1A).
            // Oracle ORDERING (review H1): data = ReadBus(pair); WZ = pair + 1; reg = data — the bus read
            // precedes the WZ write. The final state is identical either way (WZ is an internal register no
            // bus access can observe mid-instruction), but emit read -> WZ -> store to mirror the oracle
            // one-for-one (the plan's stated discipline).
            case (JitMode.RegisterIndirect, "Load"):
            {
                string pair = Z80IndirectPair(d);
                EmitLoadReg16(ctx, pair);                    // address (int) on stack
                il.Emit(OpCodes.Conv_U4);
                LoadByteFromBus(ctx);                        // data; charges 1 (clobbers EaLocal)
                il.Emit(OpCodes.Stloc, ctx.DataLocal);
                if (pair != "HL")                            // WZ = (ushort)(pair + 1), AFTER the read
                {
                    EmitLoadReg16(ctx, pair);
                    il.Emit(OpCodes.Ldc_I4_1);
                    il.Emit(OpCodes.Add);
                    EmitZ80SetWZ(ctx);
                }
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldloc, ctx.DataLocal);
                il.Emit(OpCodes.Conv_U1);
                il.Emit(OpCodes.Stfld, RegField(op.RegA));   // target = (pair)
                EmitChargeCycles(ctx, 5);
                EmitZ80ClearQ(ctx);
                break;
            }

            // LD (HL),r / LD (BC),A / LD (DE),A  (RegisterIndirect, Store) — 7 T (fetch 1 + write 1 + 5).
            // The (HL) forms set NO WZ; the accumulator forms set WZ = (A<<8) | ((pair+1)&0xFF) (Op02/Op12).
            case (JitMode.RegisterIndirect, "Store"):
            {
                string pair = Z80IndirectPair(d);
                if (pair != "HL")                            // WZ = (A << 8) | ((pair + 1) & 0xFF)
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, RegField("A"));
                    il.Emit(OpCodes.Ldc_I4_8);
                    il.Emit(OpCodes.Shl);
                    EmitLoadReg16(ctx, pair);
                    il.Emit(OpCodes.Ldc_I4_1);
                    il.Emit(OpCodes.Add);
                    il.Emit(OpCodes.Ldc_I4, 0xFF);
                    il.Emit(OpCodes.And);
                    il.Emit(OpCodes.Or);
                    EmitZ80SetWZ(ctx);
                }
                EmitLoadReg16(ctx, pair);                    // address on stack
                il.Emit(OpCodes.Conv_U4);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, RegField(op.RegA));   // source byte (A for BC/DE; r for (HL))
                EmitStoreByte(ctx);                          // writes; charges 1; marks dirty
                EmitChargeCycles(ctx, 5);
                EmitZ80ClearQ(ctx);
                break;
            }

            // LD (HL),n  (Immediate, StoreImm8) — 10 T (fetch 1 + imm read 1 + write 1 + residual 7). No WZ.
            case (JitMode.Immediate, "StoreImm8"):
                EmitLoadReg16(ctx, "HL");                    // address
                il.Emit(OpCodes.Conv_U4);
                EmitLoadPC(ctx);                             // immediate byte address
                il.Emit(OpCodes.Conv_U4);
                LoadByteFromBus(ctx);                        // data; charges 1
                EmitIncrementPC(ctx, 1);
                EmitStoreByte(ctx);                          // stack: address, data -> write; charges 1
                EmitChargeCycles(ctx, 7);
                EmitZ80ClearQ(ctx);
                break;

            // LD rr,nn  (ImmediateExtended, Load16 pair) — 10 T (fetch 1 + lo 1 + hi 1 + residual 7). No WZ.
            case (JitMode.ImmediateExtended, "Load16"):
                EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx);   // lo; charges 1
                EmitIncrementPC(ctx, 1);
                il.Emit(OpCodes.Stloc, ctx.LoLocal);
                EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx);   // hi; charges 1
                EmitIncrementPC(ctx, 1);
                il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shl);
                il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Or);          // hi<<8 | lo
                EmitStoreReg16(ctx, op.RegA);                                      // BC/DE/HL/SP = value
                EmitChargeCycles(ctx, 7);
                EmitZ80ClearQ(ctx);
                break;

            // LD A,(nn)  (ExtendedAddress, Load A) — 13 T (fetch 1 + 2 addr reads + 1 data read + residual 9).
            // WZ = ea + 1 (oracle Op3A). EA survives in ctx.AddrLocal (the bus helpers clobber EaLocal).
            // Oracle ORDERING (review H1): data = ReadBus(ea); WZ = ea + 1; A = data — read precedes the WZ
            // write; emit read -> WZ -> store to mirror the oracle (final state is identical regardless).
            case (JitMode.ExtendedAddress, "Load"):
                EmitZ80ReadAbsEa(ctx);                       // ea (int) -> ctx.AddrLocal; charges 2 (lo+hi)
                il.Emit(OpCodes.Ldloc, ctx.AddrLocal);       // (already uint)
                LoadByteFromBus(ctx);                        // data; charges 1 (clobbers EaLocal, not AddrLocal)
                il.Emit(OpCodes.Stloc, ctx.DataLocal);
                il.Emit(OpCodes.Ldloc, ctx.AddrLocal); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add);
                EmitZ80SetWZ(ctx);                           // WZ = ea + 1, AFTER the data read
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldloc, ctx.DataLocal); il.Emit(OpCodes.Conv_U1);
                il.Emit(OpCodes.Stfld, RegField("A"));       // A = (nn)
                EmitChargeCycles(ctx, 9);
                EmitZ80ClearQ(ctx);
                break;

            // LD (nn),A  (ExtendedAddress, Store A) — 13 T (fetch 1 + 2 ea + 1 write + residual 9).
            // WZ = (A<<8) | ((ea+1) & 0xFF) — the A-high MEMPTR quirk (oracle Op32).
            case (JitMode.ExtendedAddress, "Store"):
                EmitZ80ReadAbsEa(ctx);                       // ea -> ctx.AddrLocal; charges 2
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField("A"));
                il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shl);
                il.Emit(OpCodes.Ldloc, ctx.AddrLocal); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ldc_I4, 0xFF); il.Emit(OpCodes.And);
                il.Emit(OpCodes.Or);
                EmitZ80SetWZ(ctx);                           // WZ = (A<<8) | ((ea+1)&0xFF)
                il.Emit(OpCodes.Ldloc, ctx.AddrLocal);       // address (uint)
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField("A"));
                EmitStoreByte(ctx);                          // (ea) = A; charges 1
                EmitChargeCycles(ctx, 9);
                EmitZ80ClearQ(ctx);
                break;

            // LD (nn),HL  (ExtendedAddress, Store16) — 16 T (fetch 1 + 2 ea + 2 writes + residual 11).
            // WZ = ea + 1 (the SIMPLE WZ — NO A-high quirk). Oracle Op22.
            case (JitMode.ExtendedAddress, "Store16"):
                EmitZ80ReadAbsEa(ctx);                       // ea -> ctx.AddrLocal; charges 2
                il.Emit(OpCodes.Ldloc, ctx.AddrLocal); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add);
                EmitZ80SetWZ(ctx);                           // WZ = ea + 1
                // WriteBus(ea, (byte)HL)  — low byte of the pair
                il.Emit(OpCodes.Ldloc, ctx.AddrLocal);       // address (uint)
                EmitLoadReg16(ctx, op.RegA);                 // HL (int) on stack
                il.Emit(OpCodes.Conv_U1);                    // (byte)HL = low
                EmitStoreByte(ctx);                          // (ea) = lo; charges 1
                // WriteBus((ea+1)&0xFFFF, (byte)(HL>>8)) — high byte
                il.Emit(OpCodes.Ldloc, ctx.AddrLocal); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ldc_I4, 0xFFFF); il.Emit(OpCodes.And); il.Emit(OpCodes.Conv_U4);
                EmitLoadReg16(ctx, op.RegA);
                il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shr_Un);
                il.Emit(OpCodes.Conv_U1);                    // (byte)(HL>>8) = high
                EmitStoreByte(ctx);                          // (ea+1) = hi; charges 1
                EmitChargeCycles(ctx, 11);
                EmitZ80ClearQ(ctx);
                break;

            // LD HL,(nn)  (ExtendedAddress, LoadMem16) — 16 T (fetch 1 + 2 ea + 2 reads + residual 11).
            // WZ = ea + 1. Oracle Op2A.
            case (JitMode.ExtendedAddress, "LoadMem16"):
                EmitZ80ReadAbsEa(ctx);                       // ea -> ctx.AddrLocal; charges 2
                il.Emit(OpCodes.Ldloc, ctx.AddrLocal); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add);
                EmitZ80SetWZ(ctx);                           // WZ = ea + 1
                // vlo = ReadBus(ea)
                il.Emit(OpCodes.Ldloc, ctx.AddrLocal);       // address (uint)
                LoadByteFromBus(ctx);                        // vlo; charges 1
                il.Emit(OpCodes.Stloc, ctx.LoLocal);
                // vhi = ReadBus((ea+1)&0xFFFF)
                il.Emit(OpCodes.Ldloc, ctx.AddrLocal); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ldc_I4, 0xFFFF); il.Emit(OpCodes.And); il.Emit(OpCodes.Conv_U4);
                LoadByteFromBus(ctx);                        // vhi; charges 1
                // HL = vlo | (vhi << 8)
                il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shl);
                il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Or);
                EmitStoreReg16(ctx, op.RegA);                // HL = vlo | vhi<<8 (PR-0 helper)
                EmitChargeCycles(ctx, 11);
                EmitZ80ClearQ(ctx);
                break;

            default:
                throw new EmulationException(
                    $"EmitZ80Ld: unhandled LD form (mode={d.Mode}, kind={op.Kind}, opcode=0x{d.Opcode:X2}). "
                  + "The whitelist (IsEmittableZ80Family) admitted a row with no emit branch — keep it in "
                  + "the gate's fallback set until an arm exists.");
        }
    }

    /// <summary>M6 PR-1: replicate the Z80 memory-refresh (R) bump the interpreter's OnInstructionFetched
    /// does once per opcode fetch: <c>R = (byte)((R &amp; 0x80) | ((R + keyBytes) &amp; 0x7F))</c> — bit 7 is
    /// preserved, bits 0..6 wrap mod 128. keyBytes is the opcode-byte count (1 for base-plane rows, which
    /// is every emitted PR-1 LD). Emitted once in EmitInstruction for every emitted Z80 instruction.</summary>
    private void EmitZ80RefreshR(EmitContext ctx, int keyBytes)
    {
        ILGenerator il = ctx.Il;
        // cpu.R = (byte)( (R & 0x80) | ((R + keyBytes) & 0x7F) )
        il.Emit(OpCodes.Ldarg_0);                    // cpu (receiver for the Stfld)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _z80R!);              // R (byte -> int)
        il.Emit(OpCodes.Ldc_I4, 0x80);
        il.Emit(OpCodes.And);                        // R & 0x80  (bit 7)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _z80R!);              // R
        il.Emit(OpCodes.Ldc_I4, keyBytes);
        il.Emit(OpCodes.Add);                        // R + keyBytes
        il.Emit(OpCodes.Ldc_I4, 0x7F);
        il.Emit(OpCodes.And);                        // (R + keyBytes) & 0x7F  (bits 0..6, mod 128)
        il.Emit(OpCodes.Or);                         // (R & 0x80) | ((R + keyBytes) & 0x7F)
        il.Emit(OpCodes.Conv_U1);
        il.Emit(OpCodes.Stfld, _z80R!);
    }

    /// <summary>cpu.Q = 0 — every base-plane LD clears Q (the oracle sets <c>Q = 0</c>).</summary>
    private void EmitZ80ClearQ(EmitContext ctx)
    {
        ctx.Il.Emit(OpCodes.Ldarg_0);
        ctx.Il.Emit(OpCodes.Ldc_I4_0);
        ctx.Il.Emit(OpCodes.Conv_U1);
        ctx.Il.Emit(OpCodes.Stfld, _z80Q!);
    }

    /// <summary>cpu.WZ = (ushort)value, where value (int) is on top of the IL stack. The MEMPTR
    /// side-effect of the (nn) and (BC)/(DE) indirect LD forms. Stages through ctx.TmpInt (self-contained:
    /// write then read with no intervening call) to keep the cpu receiver below the value for Stfld.</summary>
    private void EmitZ80SetWZ(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Stloc, ctx.TmpInt);          // stash value
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, ctx.TmpInt);
        il.Emit(OpCodes.Conv_U2);                    // (ushort)value
        il.Emit(OpCodes.Stfld, _z80WZ!);
    }

    /// <summary>The 16-bit register pair a RegisterIndirect LD addresses: (BC)/(DE) for the accumulator
    /// forms (0x02/0x0A -> BC, 0x12/0x1A -> DE); (HL) for everything else (the 0x46-block LD r,(HL) and
    /// the 0x70-block LD (HL),r). Keyed on the raw opcode (unambiguous on the base plane).</summary>
    private static string Z80IndirectPair(OpcodeDescriptor d) => d.Opcode switch
    {
        0x02 or 0x0A => "BC",
        0x12 or 0x1A => "DE",
        _ => "HL",
    };

    /// <summary>Read a 2-byte little-endian absolute address from PC into ctx.AddrLocal (NOT ctx.EaLocal,
    /// which the bus helpers clobber); charges 2 cycles (lo + hi reads). The lo byte is staged in
    /// ctx.LoLocal across the hi read (LoadByteFromBus clobbers only EaLocal, so LoLocal survives).</summary>
    private void EmitZ80ReadAbsEa(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx);   // lo; charges 1
        EmitIncrementPC(ctx, 1);
        il.Emit(OpCodes.Stloc, ctx.LoLocal);
        EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx);   // hi; charges 1
        EmitIncrementPC(ctx, 1);
        il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shl);
        il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Or);          // ea = lo | hi<<8
        il.Emit(OpCodes.Stloc, ctx.AddrLocal);                             // survives the bus accesses
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────
    // M6 PR-2: the Z80 ALU + flag core. The shared flag-word helpers (DECISION D — one helper per shape,
    // mirroring the generator's own EmitZ80FlagWord factoring) + the Q lifecycle + the opcode→source
    // helper + the EmitZ80Alu arm. Each helper transcribes the generated oracle (EmitZ80Alu8 /
    // EmitZ80IncDec8 / EmitZ80Add16) one-for-one; the TomHarte JIT sweep proves all three exhaustively.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>M6 PR-2: cpu.Q = cpu.F — every flag-writing ALU op sets Q to the new flag word (the oracle's
    /// <c>Q = F;</c>). (PR-1's EmitZ80ClearQ does the Q=0 case for the no-flag LD family; this is the
    /// flag-writing case.)</summary>
    private void EmitZ80SetQFromF(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Ldarg_0);               // cpu (receiver for the Stfld)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _z80F!);         // cpu.F
        il.Emit(OpCodes.Stfld, _z80Q!);         // cpu.Q = cpu.F
    }

    // M6 PR-2: System.Numerics.BitOperations.PopCount(uint) — the Z80 logic-op parity source. Resolved once.
    private static readonly MethodInfo _popCount =
        typeof(System.Numerics.BitOperations).GetMethod("PopCount", new[] { typeof(uint) })!;

    /// <summary>M6 PR-2: push 1 (int) if res (the byte on top of the stack, as int) has EVEN parity, else 0 —
    /// the Z80 P flag for the logic ops. Mirrors <c>(System.Numerics.BitOperations.PopCount((uint)res) &amp; 1)
    /// == 0</c>. Expects: res (int, 0..255) on top of the stack. Leaves: 1 if even parity else 0 (int).</summary>
    private void EmitZ80EvenParity(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Conv_U4);               // (uint)res
        il.Emit(OpCodes.Call, _popCount);       // PopCount
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.And);                   // & 1
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ceq);                   // == 0  → 1 if even, 0 if odd
    }

    /// <summary>M6 PR-2: the source register of an 8-bit ALU Register-mode op (ADD A,r / SUB A,r / ...). The
    /// r-field is the opcode's low 3 bits: 000=B 001=C 010=D 011=E 100=H 101=L 110=(HL) 111=A. The 110 case is
    /// the (HL) form, which the importer keys as JitMode.RegisterIndirect (a separate arm branch), so this is
    /// only called for the Register-mode rows (the seven register sources). Returns the 8-bit register name for
    /// RegField.</summary>
    private static string Z80AluSource(int opcode) => (opcode & 0x07) switch
    {
        0 => "B", 1 => "C", 2 => "D", 3 => "E", 4 => "H", 5 => "L", 7 => "A",
        _ => throw new EmulationException(   // 6 = (HL): RegisterIndirect, handled by a different branch.
            $"Z80AluSource: opcode 0x{opcode:X2} low-3-bits=6 is the (HL) form — should route to the "
          + "RegisterIndirect branch, not Z80AluSource."),
    };

    /// <summary>M6 PR-2: emit the Z80 add/sub-shaped F flag word (ADD/ADC/SUB/SBC/CP). Mirrors the generated
    /// EmitZ80FlagWord one-for-one. Pre-staged by the caller:
    ///   ctx.NibLocal = A (original accumulator, int)   ctx.DataLocal = data (operand, int)
    ///   ctx.SumLocal = sum/diff (SIGNED int)           ctx.TmpInt    = half (SIGNED int)
    ///   ctx.LoLocal  = res = (byte)(sum/diff) (int 0..255)
    /// subtract: false=ADD/ADC (N=0, ov=~(A^data)&amp;(A^res), C=sum&gt;0xFF), true=SUB/SBC/CP (N=1,
    /// ov=(A^data)&amp;(A^res), C=diff&lt;0). xyFromData: false=Y/X from res (ADD/ADC/SUB/SBC), true=from data
    /// (CP — the operand-XY quirk).</summary>
    private void EmitZ80AddSubFlags(EmitContext ctx, bool subtract, bool xyFromData)
    {
        ILGenerator il = ctx.Il;
        // xy source local: res (LoLocal) for ADD/ADC/SUB/SBC, data (DataLocal) for CP (the operand-XY quirk).
        LocalBuilder xyLoc = xyFromData ? ctx.DataLocal : ctx.LoLocal;
        // Build F = S|Z|Y|H|X|P/V|N|C by OR-ing int terms on the stack, then Stfld via EmitStoreF.
        // S: (res & 0x80)
        il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Ldc_I4, 0x80); il.Emit(OpCodes.And);
        // Z: (res == 0) ? 0x40 : 0
        EmitSelectMask(ctx, () => { il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); }, 0x40);
        il.Emit(OpCodes.Or);
        // Y: (xy & 0x20)   — xy = res (LoLocal) or data (DataLocal); the bit position equals the Y flag bit.
        il.Emit(OpCodes.Ldloc, xyLoc); il.Emit(OpCodes.Ldc_I4, 0x20); il.Emit(OpCodes.And); il.Emit(OpCodes.Or);
        // H: ((half & 0x10) != 0) ? 0x10 : 0
        il.Emit(OpCodes.Ldloc, ctx.TmpInt); il.Emit(OpCodes.Ldc_I4, 0x10); il.Emit(OpCodes.And); il.Emit(OpCodes.Or);
        // X: ((xy & 0x08) != 0) ? 0x08 : 0
        il.Emit(OpCodes.Ldloc, xyLoc); il.Emit(OpCodes.Ldc_I4, 0x08); il.Emit(OpCodes.And); il.Emit(OpCodes.Or);
        // P/V: overflow — ov = (subtract ? (A^data) : ~(A^data)) & (A^res) & 0x80
        EmitZ80Overflow(ctx, subtract);                       // pushes 0x04 or 0x00
        il.Emit(OpCodes.Or);
        // N: subtract ? 0x02 : 0x00
        if (subtract) { il.Emit(OpCodes.Ldc_I4, 0x02); il.Emit(OpCodes.Or); }
        // C: subtract ? (diff < 0) : (sum > 0xFF)  → 0x01
        il.Emit(OpCodes.Ldloc, ctx.SumLocal);
        if (subtract) { il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Clt); }                 // diff < 0
        else          { il.Emit(OpCodes.Ldc_I4, 0xFF); il.Emit(OpCodes.Cgt); }             // sum > 0xFF
        il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.And);                                    // bool→{1,0}; *0x01
        il.Emit(OpCodes.Or);
        // F = (byte)(accumulated OR)
        EmitStoreF(ctx);                                       // Conv_U1 + Stfld _z80F
    }

    /// <summary>push: <c>cond ? mask : 0</c>. Caller emits the condition (evaluated to 1/0) via emitCond.</summary>
    private void EmitSelectMask(EmitContext ctx, Action emitCond, int mask)
    {
        emitCond();                              // 1 or 0 (int)
        ctx.Il.Emit(OpCodes.Ldc_I4, mask);
        ctx.Il.Emit(OpCodes.Mul);                // (1|0) * mask
    }

    /// <summary>push the P/V term (0x04 or 0x00): <c>ov = (subtract ? (A^data) : ~(A^data)) &amp; (A^res) &amp;
    /// 0x80; term = ov != 0 ? 0x04 : 0</c>. Reads ctx.NibLocal=A, ctx.DataLocal=data, ctx.LoLocal=res (all
    /// int).</summary>
    private void EmitZ80Overflow(EmitContext ctx, bool subtract)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Ldloc, ctx.NibLocal); il.Emit(OpCodes.Ldloc, ctx.DataLocal); il.Emit(OpCodes.Xor); // A^data
        if (!subtract) { il.Emit(OpCodes.Ldc_I4_M1); il.Emit(OpCodes.Xor); }                                // ~(A^data)
        il.Emit(OpCodes.Ldloc, ctx.NibLocal); il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Xor);    // A^res
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldc_I4, 0x80); il.Emit(OpCodes.And);    // & 0x80
        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Cgt_Un);     // ov != 0 → 1/0  (Cgt_Un: any nonzero > 0)
        il.Emit(OpCodes.Ldc_I4, 0x04); il.Emit(OpCodes.Mul);    // * 0x04 (P bit)
    }

    /// <summary>cpu.F = (byte)(value-on-stack). The accumulated F word is the only operand on the stack with NO
    /// cpu receiver below it, so stash it to ctx.TmpInt, load cpu, reload, truncate, Stfld (mirrors
    /// EmitZ80SetWZ's idiom).</summary>
    private void EmitStoreF(EmitContext ctx)
    {
        ctx.Il.Emit(OpCodes.Conv_U1);
        // restructure to put cpu below the value: stash, load cpu, load value, Stfld (mirror EmitZ80SetWZ's idiom)
        ctx.Il.Emit(OpCodes.Stloc, ctx.TmpInt);
        ctx.Il.Emit(OpCodes.Ldarg_0);
        ctx.Il.Emit(OpCodes.Ldloc, ctx.TmpInt);
        ctx.Il.Emit(OpCodes.Conv_U1);
        ctx.Il.Emit(OpCodes.Stfld, _z80F!);
    }

    /// <summary>M6 PR-2: the AND/OR/XOR F flag word (shape 3): S Z Y X from res; H = (isAnd ? 0x10 : 0x00);
    /// P = even parity; N = 0; C = 0. Pre-staged: ctx.LoLocal = res (int).</summary>
    private void EmitZ80LogicFlags(EmitContext ctx, bool isAnd)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Ldc_I4, 0x80); il.Emit(OpCodes.And);   // S
        EmitSelectMask(ctx, () => { il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); }, 0x40); il.Emit(OpCodes.Or); // Z
        il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Ldc_I4, 0x20); il.Emit(OpCodes.And); il.Emit(OpCodes.Or);  // Y
        il.Emit(OpCodes.Ldc_I4, isAnd ? 0x10 : 0x00); il.Emit(OpCodes.Or);                                              // H
        il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Ldc_I4, 0x08); il.Emit(OpCodes.And); il.Emit(OpCodes.Or);  // X
        il.Emit(OpCodes.Ldloc, ctx.LoLocal); EmitZ80EvenParity(ctx); il.Emit(OpCodes.Ldc_I4, 0x04); il.Emit(OpCodes.Mul); il.Emit(OpCodes.Or); // P
        EmitStoreF(ctx);                                                                                                 // N=0,C=0 implicitly
    }

    /// <summary>M6 PR-2: the INC/DEC 8-bit F flag word (shape 4): C PRESERVED (F &amp; 0x01); S Z Y X from res;
    /// H from before; V from before boundary; N = inc?0:2. Pre-staged: ctx.DataLocal = before (int),
    /// ctx.LoLocal = res (int). Reads old cpu.F for the preserved C.</summary>
    private void EmitZ80IncDecFlags(EmitContext ctx, bool increment)
    {
        ILGenerator il = ctx.Il;
        // C preserved: (old F & 0x01)
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, _z80F!); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Ldc_I4, 0x80); il.Emit(OpCodes.And); il.Emit(OpCodes.Or);  // S
        EmitSelectMask(ctx, () => { il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); }, 0x40); il.Emit(OpCodes.Or); // Z
        il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Ldc_I4, 0x20); il.Emit(OpCodes.And); il.Emit(OpCodes.Or);  // Y
        // H: inc → (before & 0x0F)==0x0F ; dec → (before & 0x0F)==0x00
        EmitSelectMask(ctx, () => {
            il.Emit(OpCodes.Ldloc, ctx.DataLocal); il.Emit(OpCodes.Ldc_I4, 0x0F); il.Emit(OpCodes.And);
            il.Emit(OpCodes.Ldc_I4, increment ? 0x0F : 0x00); il.Emit(OpCodes.Ceq);
        }, 0x10); il.Emit(OpCodes.Or);                                                                                   // H
        il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Ldc_I4, 0x08); il.Emit(OpCodes.And); il.Emit(OpCodes.Or);  // X
        // V: inc → before==0x7F ; dec → before==0x80
        EmitSelectMask(ctx, () => {
            il.Emit(OpCodes.Ldloc, ctx.DataLocal); il.Emit(OpCodes.Ldc_I4, increment ? 0x7F : 0x80); il.Emit(OpCodes.Ceq);
        }, 0x04); il.Emit(OpCodes.Or);                                                                                   // P/V
        if (!increment) { il.Emit(OpCodes.Ldc_I4, 0x02); il.Emit(OpCodes.Or); }                                         // N (dec)
        EmitStoreF(ctx);
    }

    /// <summary>M6 PR-2/PR-2b: the Z80 ALU emit arm. Mirrors EmitZ80Alu8 / EmitZ80IncDec8 / EmitZ80Add16 /
    /// EmitZ80IncDec16 / EmitZ80EdAdcSbc16 (the generated oracle) one-for-one: result + the full SZ5H3PNC F word
    /// inline, Q = F (flag writers; Q = 0 for the flagless Inc16/Dec16), WZ = HL+1 (ADD HL,rr and ED ADC/SBC HL,rr),
    /// the Z80 T-state residual to d.BaseCycles. Operands via the fastmem split (LoadByteFromBus) or RegField.
    /// PR-2b adds the no-flag 16-bit Inc16/Dec16 and the ED-prefixed ADC/SBC HL,rr (the PR-2 DECISION E
    /// deferrals).</summary>
    private void EmitZ80Alu(EmitContext ctx, OpcodeDescriptor d)
    {
        JitOp op = d.Ops[0];

        switch (op.Kind)
        {
            case "Add8": case "Adc8": case "Sub8": case "Sbc8":
            case "And8": case "Or8":  case "Xor8": case "Cp8":
                EmitZ80Alu8(ctx, d, op);
                break;
            case "IncReg": case "DecReg": case "IncMem8": case "DecMem8":
                EmitZ80IncDec(ctx, d, op);
                break;
            case "Add16":
                EmitZ80Add16(ctx, d, op);
                break;
            case "Inc16": case "Dec16":
                EmitZ80IncDec16(ctx, d, op);
                break;
            case "EdAdcSbc16":
                EmitZ80EdAdcSbc16(ctx, d, op);
                break;
            default:
                throw new EmulationException(
                    $"EmitZ80Alu: unhandled ALU kind '{op.Kind}' (opcode=0x{d.Opcode:X2}). The whitelist "
                  + "(IsEmittableZ80Family) admitted a kind with no emit branch — keep it fallback until armed.");
        }
    }

    /// <summary>M6 PR-2: ADD/ADC/SUB/SBC/AND/OR/XOR/CP A,(r|(HL)|n). Read the operand into ctx.DataLocal, stage A
    /// into ctx.NibLocal, compute the result + the full F word via the shared flag helpers, write A (unless CP),
    /// Q = F, charge the residual.</summary>
    private void EmitZ80Alu8(EmitContext ctx, OpcodeDescriptor d, JitOp op)
    {
        ILGenerator il = ctx.Il;
        bool isLogic = op.Kind is "And8" or "Or8" or "Xor8";
        bool isCp = op.Kind == "Cp8";
        bool carryIn = op.Kind is "Adc8" or "Sbc8";

        // 1) data → ctx.DataLocal (int), and charge the operand-access cycle where applicable.
        int residual;
        switch (d.Mode)
        {
            case JitMode.Register:                                     // ADD A,r — source = opcode low 3 bits
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, RegField(Z80AluSource(d.Opcode)));
                il.Emit(OpCodes.Stloc, ctx.DataLocal);
                residual = 3;                                          // 4 T = fetch 1 + 3
                break;
            case JitMode.RegisterIndirect:                            // ADD A,(HL)
                EmitLoadReg16(ctx, "HL"); il.Emit(OpCodes.Conv_U4);
                LoadByteFromBus(ctx);                                  // charges 1
                il.Emit(OpCodes.Stloc, ctx.DataLocal);
                residual = 5;                                          // 7 T = fetch 1 + read 1 + 5
                break;
            case JitMode.Immediate:                                   // ADD A,n
                EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4);
                LoadByteFromBus(ctx);                                  // charges 1
                il.Emit(OpCodes.Stloc, ctx.DataLocal);
                EmitIncrementPC(ctx, 1);
                residual = 5;                                          // 7 T = fetch 1 + imm 1 + 5
                break;
            default:
                throw new EmulationException($"EmitZ80Alu8: bad mode {d.Mode} for 0x{d.Opcode:X2}");
        }

        // 2) A → ctx.NibLocal (int, the original accumulator the flag math + write-back read).
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField("A")); il.Emit(OpCodes.Stloc, ctx.NibLocal);

        if (isLogic)
        {
            // res = A op data → ctx.LoLocal
            il.Emit(OpCodes.Ldloc, ctx.NibLocal); il.Emit(OpCodes.Ldloc, ctx.DataLocal);
            il.Emit(op.Kind == "And8" ? OpCodes.And : op.Kind == "Or8" ? OpCodes.Or : OpCodes.Xor);
            il.Emit(OpCodes.Ldc_I4, 0xFF); il.Emit(OpCodes.And);              // (byte)
            il.Emit(OpCodes.Stloc, ctx.LoLocal);
            EmitZ80LogicFlags(ctx, isAnd: op.Kind == "And8");
            // A = res
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Conv_U1);
            il.Emit(OpCodes.Stfld, RegField("A"));
        }
        else
        {
            bool subtract = op.Kind is "Sub8" or "Sbc8" or "Cp8";
            // cin (int) on stack for sum/diff and half.
            // sum/diff = A ± data ± cin → ctx.SumLocal (SIGNED int)
            il.Emit(OpCodes.Ldloc, ctx.NibLocal); il.Emit(OpCodes.Ldloc, ctx.DataLocal);
            il.Emit(subtract ? OpCodes.Sub : OpCodes.Add);
            EmitZ80CarryIn(ctx, carryIn, subtract);                          // ± cin (or nothing)
            il.Emit(OpCodes.Stloc, ctx.SumLocal);
            // half = (A & 0x0F) ± (data & 0x0F) ± cin → ctx.TmpInt
            il.Emit(OpCodes.Ldloc, ctx.NibLocal); il.Emit(OpCodes.Ldc_I4, 0x0F); il.Emit(OpCodes.And);
            il.Emit(OpCodes.Ldloc, ctx.DataLocal); il.Emit(OpCodes.Ldc_I4, 0x0F); il.Emit(OpCodes.And);
            il.Emit(subtract ? OpCodes.Sub : OpCodes.Add);
            EmitZ80CarryIn(ctx, carryIn, subtract);
            il.Emit(OpCodes.Stloc, ctx.TmpInt);
            // res = (byte)(sum/diff) → ctx.LoLocal
            il.Emit(OpCodes.Ldloc, ctx.SumLocal); il.Emit(OpCodes.Ldc_I4, 0xFF); il.Emit(OpCodes.And);
            il.Emit(OpCodes.Stloc, ctx.LoLocal);
            EmitZ80AddSubFlags(ctx, subtract, xyFromData: isCp);
            if (!isCp)                                                       // CP writes NO result
            {
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Conv_U1);
                il.Emit(OpCodes.Stfld, RegField("A"));
            }
        }

        EmitZ80SetQFromF(ctx);
        EmitChargeCycles(ctx, residual);
    }

    /// <summary>M6 PR-2: push and apply ± cin: ADC/SBC read the OLD C flag (cpu.F &amp; 0x01); ADD/SUB add/sub
    /// nothing. The op (Add/Sub) matches the running accumulation's direction (cin is ADDED for ADC, SUBTRACTED
    /// for SBC — same sign as data). Read BEFORE the new F is written (the oracle's ordering).</summary>
    private void EmitZ80CarryIn(EmitContext ctx, bool carryIn, bool subtract)
    {
        if (!carryIn) return;
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, _z80F!); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.And); // cin
        il.Emit(subtract ? OpCodes.Sub : OpCodes.Add);
    }

    /// <summary>M6 PR-2: INC/DEC over a register (op.RegA) or (HL). before → ctx.DataLocal, res → ctx.LoLocal,
    /// the inc/dec flag word, write back (reg or (HL)), Q = F, residual.</summary>
    private void EmitZ80IncDec(EmitContext ctx, OpcodeDescriptor d, JitOp op)
    {
        ILGenerator il = ctx.Il;
        bool increment = op.Kind is "IncReg" or "IncMem8";
        bool isMem = op.Kind is "IncMem8" or "DecMem8";

        if (isMem)
        {
            // before = ReadBus(HL) → DataLocal ; res = (byte)(before ± 1) → LoLocal ; flags ; WriteBus(HL,res)
            EmitLoadReg16(ctx, "HL"); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx);   // charges 1
            il.Emit(OpCodes.Stloc, ctx.DataLocal);
            il.Emit(OpCodes.Ldloc, ctx.DataLocal); il.Emit(increment ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_M1);
            il.Emit(OpCodes.Add); il.Emit(OpCodes.Ldc_I4, 0xFF); il.Emit(OpCodes.And);
            il.Emit(OpCodes.Stloc, ctx.LoLocal);
            EmitZ80IncDecFlags(ctx, increment);
            // WriteBus(HL, res)
            EmitLoadReg16(ctx, "HL"); il.Emit(OpCodes.Conv_U4);
            il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Conv_U1);
            EmitStoreByte(ctx);                                                         // charges 1; marks dirty
            EmitZ80SetQFromF(ctx);
            EmitChargeCycles(ctx, 8);                                                   // 11 T = fetch1 + read1 + write1 + 8
        }
        else
        {
            // before = reg → DataLocal ; res = (byte)(before ± 1) → LoLocal ; flags ; reg = res
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField(op.RegA)); il.Emit(OpCodes.Stloc, ctx.DataLocal);
            il.Emit(OpCodes.Ldloc, ctx.DataLocal); il.Emit(increment ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_M1);
            il.Emit(OpCodes.Add); il.Emit(OpCodes.Ldc_I4, 0xFF); il.Emit(OpCodes.And);
            il.Emit(OpCodes.Stloc, ctx.LoLocal);
            EmitZ80IncDecFlags(ctx, increment);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Conv_U1);
            il.Emit(OpCodes.Stfld, RegField(op.RegA));
            EmitZ80SetQFromF(ctx);
            EmitChargeCycles(ctx, 3);                                                   // 4 T = fetch1 + 3
        }
    }

    /// <summary>M6 PR-2: ADD HL,rr. WZ = HL+1 FIRST (MEMPTR), then sum16/half16/res16, the partial flag word
    /// (preserve S/Z/P via F &amp; 0xC4; set H/C/Y/X; N implicitly 0), HL = res16, Q = F, residual 10. The addend
    /// pair is op.RegB (op.RegA is "HL").</summary>
    private void EmitZ80Add16(EmitContext ctx, OpcodeDescriptor d, JitOp op)
    {
        ILGenerator il = ctx.Il;
        // WZ = (ushort)(HL + 1)  — MEMPTR, set BEFORE the add (oracle Op09).
        EmitLoadReg16(ctx, "HL"); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add);
        EmitZ80SetWZ(ctx);                                                  // PR-1 helper
        // hl → NibLocal (int), rr → DataLocal (int)
        EmitLoadReg16(ctx, "HL"); il.Emit(OpCodes.Stloc, ctx.NibLocal);
        EmitLoadReg16(ctx, op.RegB); il.Emit(OpCodes.Stloc, ctx.DataLocal);
        // sum16 = hl + rr → SumLocal ; res16 = (ushort)sum16 → LoLocal ; half16 = (hl&0xFFF)+(rr&0xFFF) → TmpInt
        il.Emit(OpCodes.Ldloc, ctx.NibLocal); il.Emit(OpCodes.Ldloc, ctx.DataLocal); il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, ctx.SumLocal);
        il.Emit(OpCodes.Ldloc, ctx.SumLocal); il.Emit(OpCodes.Ldc_I4, 0xFFFF); il.Emit(OpCodes.And);
        il.Emit(OpCodes.Stloc, ctx.LoLocal);                                // res16
        il.Emit(OpCodes.Ldloc, ctx.NibLocal); il.Emit(OpCodes.Ldc_I4, 0x0FFF); il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldloc, ctx.DataLocal); il.Emit(OpCodes.Ldc_I4, 0x0FFF); il.Emit(OpCodes.And);
        il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, ctx.TmpInt);           // half16
        // F = (oldF & 0xC4) | H | C | Y | X  (S/Z/P preserved; N implicitly 0)
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, _z80F!); il.Emit(OpCodes.Ldc_I4, 0xC4); il.Emit(OpCodes.And);
        EmitSelectMask(ctx, () => { il.Emit(OpCodes.Ldloc, ctx.TmpInt); il.Emit(OpCodes.Ldc_I4, 0x1000); il.Emit(OpCodes.And); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Cgt_Un); }, 0x10); il.Emit(OpCodes.Or); // H
        EmitSelectMask(ctx, () => { il.Emit(OpCodes.Ldloc, ctx.SumLocal); il.Emit(OpCodes.Ldc_I4, 0xFFFF); il.Emit(OpCodes.Cgt); }, 0x01); il.Emit(OpCodes.Or); // C: sum16 > 0xFFFF
        // Y/X from res16 high byte: ((res16 >> 8) & 0x20) and ((res16 >> 8) & 0x08)
        il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shr_Un); il.Emit(OpCodes.Ldc_I4, 0x20); il.Emit(OpCodes.And); il.Emit(OpCodes.Or); // Y
        il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shr_Un); il.Emit(OpCodes.Ldc_I4, 0x08); il.Emit(OpCodes.And); il.Emit(OpCodes.Or); // X
        EmitStoreF(ctx);
        // HL = res16
        il.Emit(OpCodes.Ldloc, ctx.LoLocal);
        EmitStoreReg16(ctx, "HL");
        EmitZ80SetQFromF(ctx);
        EmitChargeCycles(ctx, 10);                                          // 11 T = fetch1 + 10
    }

    /// <summary>M6 PR-2b: INC rr / DEC rr (16-bit, base-plane Z80Alu). Flagless — NO F write, Q = 0 (the Z80
    /// quirk: 16-bit INC/DEC touch no flags). Mirrors EmitZ80IncDec16 (the oracle): target = (ushort)(target ± 1).
    /// op.RegA is the pair-view ("BC"/"DE"/"HL"/"SP"; "SP" is a real ushort field — both round-trip through the
    /// PR-0 wide-register helper). 6 T = fetch 1 + residual 5. NO WZ change (the oracle does not touch WZ).</summary>
    private void EmitZ80IncDec16(EmitContext ctx, OpcodeDescriptor d, JitOp op)
    {
        ILGenerator il = ctx.Il;
        bool increment = op.Kind == "Inc16";
        // pair = (ushort)(pair ± 1)
        EmitLoadReg16(ctx, op.RegA);                  // value (int) on stack
        il.Emit(increment ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_M1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldc_I4, 0xFFFF);
        il.Emit(OpCodes.And);                         // (ushort) wrap
        EmitStoreReg16(ctx, op.RegA);                 // pair = result (PR-0 helper)
        EmitZ80ClearQ(ctx);                           // Q = 0 (no flag write — PR-1 helper)
        EmitChargeCycles(ctx, 5);                     // 6 T = fetch 1 + 5
    }

    /// <summary>M6 PR-2b: ED ADC HL,rr / SBC HL,rr (prefixed, 15 T). Mirrors EmitZ80EdAdcSbc16 (the oracle)
    /// one-for-one: WZ = HL+1 (pre-op), the 16-bit add/sub with carry-in, the full SZ5H3PNC F word RECOMPUTED on
    /// the 16-bit result (S=bit15, Z=full16-zero, Y/X from the high byte, H from bit-11 carry, P/V 16-bit
    /// overflow, N per op, C from bit 16), HL = res16, Q = F. The pair is op.RegB; the sense is op.BoolArg
    /// (true = SBC). The 2-byte ED fetch is charged by EmitInstruction (DECISION G); this arm charges the
    /// residual 13 (15 − 2 fetches). NO bus access.</summary>
    private void EmitZ80EdAdcSbc16(EmitContext ctx, OpcodeDescriptor d, JitOp op)
    {
        ILGenerator il = ctx.Il;
        bool subtract = op.BoolArg;                   // true = SBC

        // WZ = (ushort)(HL + 1)  — MEMPTR, set BEFORE the add/sub (oracle pre-op HL).
        EmitLoadReg16(ctx, "HL"); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add);
        EmitZ80SetWZ(ctx);                            // PR-1 helper

        // hl → NibLocal (int), rr → DataLocal (int), cin (int) staged into the sum.
        EmitLoadReg16(ctx, "HL");      il.Emit(OpCodes.Stloc, ctx.NibLocal);
        EmitLoadReg16(ctx, op.RegB);   il.Emit(OpCodes.Stloc, ctx.DataLocal);

        // full = hl ± rr ± cin → SumLocal (SIGNED int)
        il.Emit(OpCodes.Ldloc, ctx.NibLocal); il.Emit(OpCodes.Ldloc, ctx.DataLocal);
        il.Emit(subtract ? OpCodes.Sub : OpCodes.Add);
        EmitZ80CarryIn(ctx, carryIn: true, subtract);     // ± (F & 0x01) — PR-2 helper
        il.Emit(OpCodes.Stloc, ctx.SumLocal);
        // half = (hl & 0x0FFF) ± (rr & 0x0FFF) ± cin → TmpInt (SIGNED int)
        il.Emit(OpCodes.Ldloc, ctx.NibLocal); il.Emit(OpCodes.Ldc_I4, 0x0FFF); il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldloc, ctx.DataLocal); il.Emit(OpCodes.Ldc_I4, 0x0FFF); il.Emit(OpCodes.And);
        il.Emit(subtract ? OpCodes.Sub : OpCodes.Add);
        EmitZ80CarryIn(ctx, carryIn: true, subtract);
        il.Emit(OpCodes.Stloc, ctx.TmpInt);
        // res16 = (ushort)full → LoLocal (int 0..0xFFFF)
        il.Emit(OpCodes.Ldloc, ctx.SumLocal); il.Emit(OpCodes.Ldc_I4, 0xFFFF); il.Emit(OpCodes.And);
        il.Emit(OpCodes.Stloc, ctx.LoLocal);

        EmitZ80Ed16Flags(ctx, subtract);              // the 16-bit flag word → F

        // HL = res16
        il.Emit(OpCodes.Ldloc, ctx.LoLocal);
        EmitStoreReg16(ctx, "HL");
        EmitZ80SetQFromF(ctx);                        // Q = F (PR-2 helper)
        EmitChargeCycles(ctx, 13);                    // 15 T = 2 fetches (EmitInstruction) + residual 13
    }

    /// <summary>M6 PR-2b: the ED ADC/SBC HL,rr 16-bit F flag word. Mirrors EmitZ80Ed16FlagWord one-for-one.
    /// Pre-staged: ctx.NibLocal=hl, ctx.DataLocal=rr (int); ctx.SumLocal=full (SIGNED int); ctx.TmpInt=half
    /// (SIGNED int); ctx.LoLocal=res16 (int 0..0xFFFF). subtract: false=ADC (N=0, ov=~(hl^rr)&(hl^full),
    /// C=full>0xFFFF), true=SBC (N=1, ov=(hl^rr)&(hl^full), C=full<0). All bits recomputed (no preservation).
    /// S=bit15 of res16 does NOT line up with F's S bit, so it needs an explicit Cgt_Un select; Y/X come from
    /// the high byte ((res16>>8)&0x20/0x08), which DOES line up. ctx.TmpInt holds half until the H term reads
    /// it; EmitStoreF then reuses TmpInt as staging (same ordering as EmitZ80AddSubFlags — confirmed safe).</summary>
    private void EmitZ80Ed16Flags(EmitContext ctx, bool subtract)
    {
        ILGenerator il = ctx.Il;
        // S: ((res16 & 0x8000) != 0) ? 0x80 : 0
        EmitSelectMask(ctx, () => { il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Ldc_I4, 0x8000); il.Emit(OpCodes.And); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Cgt_Un); }, 0x80);
        // Z: (res16 == 0) ? 0x40 : 0
        EmitSelectMask(ctx, () => { il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); }, 0x40); il.Emit(OpCodes.Or);
        // Y: ((res16 >> 8) & 0x20)   — high byte bit5 lines up with the Y flag bit
        il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shr_Un); il.Emit(OpCodes.Ldc_I4, 0x20); il.Emit(OpCodes.And); il.Emit(OpCodes.Or);
        // H: ((half & 0x1000) != 0) ? 0x10 : 0   — bit-11 carry/borrow
        EmitSelectMask(ctx, () => { il.Emit(OpCodes.Ldloc, ctx.TmpInt); il.Emit(OpCodes.Ldc_I4, 0x1000); il.Emit(OpCodes.And); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Cgt_Un); }, 0x10); il.Emit(OpCodes.Or);
        // X: ((res16 >> 8) & 0x08)   — high byte bit3 lines up with the X flag bit
        il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shr_Un); il.Emit(OpCodes.Ldc_I4, 0x08); il.Emit(OpCodes.And); il.Emit(OpCodes.Or);
        // P/V: 16-bit overflow → 0x04. ov = (subtract ? (hl^rr) : ~(hl^rr)) & (hl^full) & 0x8000.
        il.Emit(OpCodes.Ldloc, ctx.NibLocal); il.Emit(OpCodes.Ldloc, ctx.DataLocal); il.Emit(OpCodes.Xor);   // hl^rr
        if (!subtract) { il.Emit(OpCodes.Ldc_I4_M1); il.Emit(OpCodes.Xor); }                                  // ~(hl^rr)
        il.Emit(OpCodes.Ldloc, ctx.NibLocal); il.Emit(OpCodes.Ldloc, ctx.SumLocal); il.Emit(OpCodes.Xor);     // hl^full
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldc_I4, 0x8000); il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Cgt_Un);                                                   // ov != 0 → 1/0
        il.Emit(OpCodes.Ldc_I4, 0x04); il.Emit(OpCodes.Mul); il.Emit(OpCodes.Or);                             // * 0x04
        // N: subtract ? 0x02 : 0
        if (subtract) { il.Emit(OpCodes.Ldc_I4, 0x02); il.Emit(OpCodes.Or); }
        // C: subtract ? (full < 0) : (full > 0xFFFF) → 0x01
        il.Emit(OpCodes.Ldloc, ctx.SumLocal);
        if (subtract) { il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Clt); }
        else          { il.Emit(OpCodes.Ldc_I4, 0xFFFF); il.Emit(OpCodes.Cgt); }
        il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.And); il.Emit(OpCodes.Or);
        EmitStoreF(ctx);                                  // Conv_U1 + Stfld _z80F (PR-2 helper)
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────
    // M6 PR-3: the Z80 branch / call / stack emit arms. The control-flow family (JP/JR/CALL/RET/DJNZ/RST)
    // is the Z80 analogue of the 6502 EmitJump/EmitJsr/EmitRts/EmitBranch arms (BlockCompiler.Flow.cs):
    // a STATIC successor chains (EmitChainOrExit, compile-time-constant target read from the bus); a DYNAMIC
    // (popped) successor exits (EmitNormalExit). The stack family (PUSH/POP) rides JitOpClass.Register and
    // emits inline (block-continuing). Each arm mirrors the generated oracle (CpuEmitter.cs:2438-2778)
    // one-for-one: operand reads via the fastmem split, the WZ side-effect (DECISION K), stack push/pop via
    // EmitStoreByte/LoadByteFromBus + SP, the Z80 T-state model with the taken/not-taken split (DECISION J),
    // Q = 0 (no flow/stack op writes flags).
    // ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>M6 PR-3: PUSH rr / POP rr (Z80Stack, JitOpClass.Register — block-continuing). PUSH: SP−=1, write
    /// (byte)(pair>>8); SP−=1, write (byte)pair. POP: lo=ReadBus(SP), SP+=1; hi=ReadBus(SP), SP+=1; pair=lo|hi&lt;&lt;8
    /// (POP AF writes A=hi, F=lo via the AF pair-view). NO flags, NO WZ; Q=0. PUSH 11 T = fetch1 + 2 writes + 8;
    /// POP 10 T = fetch1 + 2 reads + 7. The pair is op.RegA.</summary>
    private void EmitZ80Stack(EmitContext ctx, OpcodeDescriptor d)
    {
        ILGenerator il = ctx.Il;
        JitOp op = d.Ops[0];
        bool push = op.Kind == "Push16";

        if (push)
        {
            // SP -= 1; WriteBus(SP, (byte)(pair >> 8))
            EmitDecrementSp(ctx);
            EmitLoadReg16(ctx, "SP"); il.Emit(OpCodes.Conv_U4);          // address
            EmitLoadReg16(ctx, op.RegA); il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shr_Un); il.Emit(OpCodes.Conv_U1);
            EmitStoreByte(ctx);                                          // charges 1; marks dirty
            // SP -= 1; WriteBus(SP, (byte)pair)
            EmitDecrementSp(ctx);
            EmitLoadReg16(ctx, "SP"); il.Emit(OpCodes.Conv_U4);
            EmitLoadReg16(ctx, op.RegA); il.Emit(OpCodes.Conv_U1);
            EmitStoreByte(ctx);                                          // charges 1
            EmitZ80ClearQ(ctx);
            EmitChargeCycles(ctx, 8);                                    // 11 T = fetch1 + 2 writes + 8
        }
        else // POP
        {
            // lo = ReadBus(SP) → LoLocal; SP += 1
            EmitLoadReg16(ctx, "SP"); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx); il.Emit(OpCodes.Stloc, ctx.LoLocal);
            EmitIncrementSp(ctx);
            // hi = ReadBus(SP); SP += 1   (the second SP++ precedes the pair write, mirroring the oracle
            // ordering CpuEmitter.cs:2456-2459 one-for-one: lo,SP++,hi,SP++,pair — pre-merge review Finding 1)
            EmitLoadReg16(ctx, "SP"); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx);   // hi (int) on stack
            il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shl);
            il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Or);                   // hi<<8 | lo
            EmitIncrementSp(ctx);                                                       // SP += 1 (before the pair write)
            EmitStoreReg16(ctx, op.RegA);                                               // pair = value (AF → A=hi,F=lo)
            EmitZ80ClearQ(ctx);
            EmitChargeCycles(ctx, 7);                                    // 10 T = fetch1 + 2 reads + 7
        }
    }

    /// <summary>M6 PR-3: SP = (ushort)(SP − 1). SP is a real ushort field (the PR-0 _regWideFields path).</summary>
    private void EmitDecrementSp(EmitContext ctx)
    {
        EmitLoadReg16(ctx, "SP"); ctx.Il.Emit(OpCodes.Ldc_I4_M1); ctx.Il.Emit(OpCodes.Add);
        ctx.Il.Emit(OpCodes.Ldc_I4, 0xFFFF); ctx.Il.Emit(OpCodes.And);
        EmitStoreReg16(ctx, "SP");
    }

    /// <summary>M6 PR-3: SP = (ushort)(SP + 1). SP is a real ushort field (the PR-0 _regWideFields path).</summary>
    private void EmitIncrementSp(EmitContext ctx)
    {
        EmitLoadReg16(ctx, "SP"); ctx.Il.Emit(OpCodes.Ldc_I4_1); ctx.Il.Emit(OpCodes.Add);
        ctx.Il.Emit(OpCodes.Ldc_I4, 0xFFFF); ctx.Il.Emit(OpCodes.And);
        EmitStoreReg16(ctx, "SP");
    }

    /// <summary>M6 PR-3: the Z80 control-flow emit arm (JP/JR/CALL/RET/DJNZ/RST), the Z80 analogue of the 6502
    /// EmitJump/EmitJsr/EmitRts/EmitBranch arms. Each mirrors the generated oracle (CpuEmitter.cs:2650-2778)
    /// one-for-one: operand read via the fastmem split, the WZ side-effect (DECISION K), stack push/pop via
    /// EmitStoreByte/LoadByteFromBus + SP, PC = target, the Z80 T-state model with the taken/not-taken split
    /// (DECISION J), Q = 0. Block-ending: a STATIC target chains (EmitChainOrExit); a DYNAMIC (popped) target
    /// exits (EmitNormalExit). `pc` is the instruction's PC (for the compile-time static-target read); `length`
    /// is the walk's computed length (for the conditional not-taken fall-through).</summary>
    private void EmitZ80Flow(EmitContext ctx, ushort pc, OpcodeDescriptor d, int length)
    {
        JitOp op = d.Ops[0];
        switch (op.Kind)
        {
            case "JumpAbs":   EmitZ80JpAbs(ctx, pc); return;
            case "RelJump":   EmitZ80Jr(ctx, pc, length); return;
            case "CallAbs":   EmitZ80Call(ctx, pc); return;
            case "Rst":       EmitZ80Rst(ctx, d); return;
            case "Ret":       EmitZ80Ret(ctx); return;
            // conditional forms
            case "JumpIf":    EmitZ80JpCc(ctx, pc, op); return;
            case "RelJumpIf": EmitZ80JrCc(ctx, pc, length, op); return;
            case "Djnz":      EmitZ80Djnz(ctx, pc, length, op); return;
            case "CallIf":    EmitZ80CallCc(ctx, pc, length, op); return;
            case "RetCc":     EmitZ80RetCc(ctx, pc, length, op); return;
            default:
                throw new EmulationException(
                    $"EmitZ80Flow: unhandled flow kind '{op.Kind}' (opcode=0x{d.Opcode:X2}). The whitelist "
                  + "(IsEmittableZ80Family) admitted a kind with no emit branch — keep it fallback until armed.");
        }
    }

    // JP nn — read jl,jh; PC = nn; WZ = nn; chain to the static target. 10 T = fetch1 + 2 reads + residual 7.
    private void EmitZ80JpAbs(EmitContext ctx, ushort pc)
    {
        ILGenerator il = ctx.Il;
        ushort target = (ushort)(_bus.Read8((ushort)(pc + 1)) | (_bus.Read8((ushort)(pc + 2)) << 8));
        // jl = ReadBus(PC); PC++; jh = ReadBus(PC); PC = jl | jh<<8
        EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx); il.Emit(OpCodes.Stloc, ctx.LoLocal);  // jl; +1 cyc
        EmitIncrementPC(ctx, 1);
        EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx);                                       // jh; +1 cyc
        il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shl); il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Stloc, ctx.HiLocal);                          // stash the new PC (int) for PC + WZ
        // PC = nn
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloc, ctx.HiLocal); il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Stfld, _fpc);
        il.Emit(OpCodes.Ldloc, ctx.HiLocal); EmitZ80SetWZ(ctx);        // WZ = nn
        EmitZ80ClearQ(ctx);
        EmitChargeCycles(ctx, 7);                                      // 10 T = fetch1 + 2 reads + 7
        EmitChainOrExit(ctx, target);                                 // STATIC target — chainable
    }

    // JR d — read d (signed); PC = (PC after operand) + d; WZ = dest; chain. 12 T = fetch1 + 1 read + residual 10.
    private void EmitZ80Jr(EmitContext ctx, ushort pc, int length)
    {
        ILGenerator il = ctx.Il;
        sbyte off = (sbyte)_bus.Read8((ushort)(pc + 1));
        ushort target = (ushort)((pc + length) + off);                // length == 2 (opcode + displacement)
        // d = (sbyte)ReadBus(PC); PC++   (then PC += d)
        EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx); // d; +1 cyc
        il.Emit(OpCodes.Conv_I1); il.Emit(OpCodes.Stloc, ctx.LoLocal);   // (sbyte)d → LoLocal
        EmitIncrementPC(ctx, 1);
        // PC = (ushort)(PC + d)
        il.Emit(OpCodes.Ldarg_0); EmitLoadPC(ctx); il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Stfld, _fpc);
        EmitLoadPC(ctx); EmitZ80SetWZ(ctx);                           // WZ = dest (the new PC)
        EmitZ80ClearQ(ctx);
        EmitChargeCycles(ctx, 10);                                    // 12 T = fetch1 + 1 read + 10
        EmitChainOrExit(ctx, target);                                 // STATIC target — chainable
    }

    // CALL nn — read nn; push (PC after operand); PC = nn; WZ = nn; chain to the entry. 17 T.
    private void EmitZ80Call(EmitContext ctx, ushort pc)
    {
        ILGenerator il = ctx.Il;
        ushort target = (ushort)(_bus.Read8((ushort)(pc + 1)) | (_bus.Read8((ushort)(pc + 2)) << 8));
        // cl = ReadBus(PC); PC++; ch = ReadBus(PC); PC++   (PC now = the RETURN address)
        EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx); il.Emit(OpCodes.Stloc, ctx.LoLocal);  // cl; +1
        EmitIncrementPC(ctx, 1);
        EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx); il.Emit(OpCodes.Stloc, ctx.HiLocal);  // ch; +1
        EmitIncrementPC(ctx, 1);
        EmitZ80PushPc(ctx);                                           // SP-=1,write PCH; SP-=1,write PCL (2 writes)
        // PC = cl | ch<<8
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, ctx.HiLocal); il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shl);
        il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Or); il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Stfld, _fpc);
        EmitLoadPC(ctx); EmitZ80SetWZ(ctx);                           // WZ = nn (the new PC)
        EmitZ80ClearQ(ctx);
        EmitChargeCycles(ctx, 12);                                    // 17 T = fetch1 + 2 reads + 2 writes + 12
        EmitChainOrExit(ctx, target);                                // STATIC call entry — chainable
    }

    // RST n — push PC (already past the 1-byte opcode); PC = vec; WZ = vec; chain. 11 T = fetch1 + 2 writes + 8.
    private void EmitZ80Rst(EmitContext ctx, OpcodeDescriptor d)
    {
        ILGenerator il = ctx.Il;
        int vec = d.Opcode & 0x38;                                    // 0x00/0x08/.../0x38 — compile-time constant
        EmitZ80PushPc(ctx);                                           // SP-=1,write PCH; SP-=1,write PCL
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4, vec); il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Stfld, _fpc);
        il.Emit(OpCodes.Ldc_I4, vec); EmitZ80SetWZ(ctx);             // WZ = vec
        EmitZ80ClearQ(ctx);
        EmitChargeCycles(ctx, 8);                                     // 11 T = fetch1 + 2 writes + 8
        EmitChainOrExit(ctx, (ushort)vec);                           // STATIC vector — chainable
    }

    // RET — pop PC; WZ = popped; DYNAMIC target → exit. 10 T = fetch1 + 2 reads + residual 7.
    private void EmitZ80Ret(EmitContext ctx)
    {
        EmitZ80PopPc(ctx);                                           // PC = pop (2 reads); leaves PC set
        EmitLoadPC(ctx); EmitZ80SetWZ(ctx);                          // WZ = popped PC
        EmitZ80ClearQ(ctx);
        EmitChargeCycles(ctx, 7);                                    // 10 T = fetch1 + 2 reads + 7
        EmitNormalExit(ctx);                                         // DYNAMIC (popped) target — NOT chainable
    }

    /// <summary>M6 PR-3: SP-=1, WriteBus(SP,(byte)(PC>>8)); SP-=1, WriteBus(SP,(byte)PC). Two writes (each +1 cyc).
    /// Mirrors the oracle's CALL/RST push order (PCH then PCL). PC must already be the return address.</summary>
    private void EmitZ80PushPc(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        EmitDecrementSp(ctx);
        EmitLoadReg16(ctx, "SP"); il.Emit(OpCodes.Conv_U4);
        EmitLoadPC(ctx); il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shr_Un); il.Emit(OpCodes.Conv_U1);
        EmitStoreByte(ctx);                                          // write PCH; +1 cyc; marks dirty
        EmitDecrementSp(ctx);
        EmitLoadReg16(ctx, "SP"); il.Emit(OpCodes.Conv_U4);
        EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U1);
        EmitStoreByte(ctx);                                          // write PCL; +1 cyc
    }

    /// <summary>M6 PR-3: lo=ReadBus(SP),SP+=1; hi=ReadBus(SP),SP+=1; PC = lo|hi&lt;&lt;8. Two reads (each +1 cyc).</summary>
    private void EmitZ80PopPc(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        EmitLoadReg16(ctx, "SP"); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx); il.Emit(OpCodes.Stloc, ctx.LoLocal);
        EmitIncrementSp(ctx);
        EmitLoadReg16(ctx, "SP"); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx);
        il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shl); il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Stloc, ctx.HiLocal);
        EmitIncrementSp(ctx);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloc, ctx.HiLocal); il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Stfld, _fpc);   // PC = popped (Conv_U2 for _fpc-store consistency — pre-merge review Finding 2)
    }

    /// <summary>M6 PR-3: push 1 (int) if the Z80 condition code holds, else 0. cc = (((F >> bit) &amp; 1) == sense).
    /// bit = op.FlagBit (the flag's bit position), sense = op.BoolArg (the expected bit value). Mirrors the
    /// oracle's CondExpr() (CpuEmitter.cs:2643-2648).</summary>
    private void EmitZ80Cond(EmitContext ctx, JitOp op)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, _z80F!);
        il.Emit(OpCodes.Ldc_I4, (int)op.FlagBit); il.Emit(OpCodes.Shr_Un);
        il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldc_I4, op.BoolArg ? 1 : 0);
        il.Emit(OpCodes.Ceq);                                        // == sense → 1/0
    }

    // JP cc,nn — WZ = nn UNCONDITIONALLY (DECISION K); if taken PC = nn; always 10 T. Two static edges
    // (taken target + fall-through), both chainable.
    private void EmitZ80JpCc(EmitContext ctx, ushort pc, JitOp op)
    {
        ILGenerator il = ctx.Il;
        ushort target = (ushort)(_bus.Read8((ushort)(pc + 1)) | (_bus.Read8((ushort)(pc + 2)) << 8));
        ushort fallThrough = (ushort)(pc + 3);                       // JP cc is 3 bytes
        // jl,jh read; WZ = nn UNCONDITIONALLY; both stashed for the taken PC set.
        EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx); il.Emit(OpCodes.Stloc, ctx.LoLocal); // jl;+1
        EmitIncrementPC(ctx, 1);
        EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx); il.Emit(OpCodes.Stloc, ctx.HiLocal); // jh;+1
        EmitIncrementPC(ctx, 1);
        // WZ = jl | jh<<8  (unconditional)
        il.Emit(OpCodes.Ldloc, ctx.HiLocal); il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shl);
        il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Or); EmitZ80SetWZ(ctx);
        EmitZ80ClearQ(ctx);
        EmitChargeCycles(ctx, 7);                                    // 10 T = fetch1 + 2 reads + 7 (always 10)
        // if (cc) { PC = nn; chain target } else { PC already at fall-through; chain fall-through }
        Label notTaken = il.DefineLabel();
        EmitZ80Cond(ctx, op); il.Emit(OpCodes.Brfalse, notTaken);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, ctx.HiLocal); il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shl);
        il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Or); il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Stfld, _fpc);                               // PC = nn
        EmitChainOrExit(ctx, target);
        il.MarkLabel(notTaken);
        EmitChainOrExit(ctx, fallThrough);                          // PC already at pc+3 (both reads advanced it)
    }

    // JR cc,d — WZ = dest ONLY when taken (DECISION K); +5 taken penalty; both edges static.
    private void EmitZ80JrCc(EmitContext ctx, ushort pc, int length, JitOp op)
    {
        ILGenerator il = ctx.Il;
        sbyte off = (sbyte)_bus.Read8((ushort)(pc + 1));
        ushort target = (ushort)((pc + length) + off);              // length == 2
        ushort fallThrough = (ushort)(pc + length);
        // d read; PC++   (PC now at fall-through)
        EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx); il.Emit(OpCodes.Conv_I1); il.Emit(OpCodes.Stloc, ctx.LoLocal); // (sbyte)d; +1
        EmitIncrementPC(ctx, 1);
        EmitZ80ClearQ(ctx);
        EmitChargeCycles(ctx, 5);                                   // 7 T not-taken = fetch1 + 1 read + 5
        Label notTaken = il.DefineLabel();
        EmitZ80Cond(ctx, op); il.Emit(OpCodes.Brfalse, notTaken);
        // taken: PC += d; WZ = PC; +5; chain target
        il.Emit(OpCodes.Ldarg_0); EmitLoadPC(ctx); il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Stfld, _fpc);
        EmitLoadPC(ctx); EmitZ80SetWZ(ctx);                        // WZ = dest (taken only)
        EmitChargeCycles(ctx, 5);                                   // taken penalty 7→12
        EmitChainOrExit(ctx, target);
        il.MarkLabel(notTaken);
        EmitChainOrExit(ctx, fallThrough);                         // PC already at pc+2
    }

    // DJNZ d — B = (byte)(B-1); if (B != 0) taken. WZ = dest ONLY when taken; +5 taken penalty.
    private void EmitZ80Djnz(EmitContext ctx, ushort pc, int length, JitOp op)
    {
        ILGenerator il = ctx.Il;
        sbyte off = (sbyte)_bus.Read8((ushort)(pc + 1));
        ushort target = (ushort)((pc + length) + off);
        ushort fallThrough = (ushort)(pc + length);
        // B = (byte)(B - 1)   (op.RegA == "B")
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField(op.RegA));
        il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Conv_U1); il.Emit(OpCodes.Stfld, RegField(op.RegA));
        // d read; PC++
        EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx); il.Emit(OpCodes.Conv_I1); il.Emit(OpCodes.Stloc, ctx.LoLocal); // +1
        EmitIncrementPC(ctx, 1);
        EmitZ80ClearQ(ctx);
        EmitChargeCycles(ctx, 6);                                   // 8 T not-taken = fetch1 + 1 read + 6
        Label notTaken = il.DefineLabel();
        // if (B != 0) taken
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, RegField(op.RegA)); il.Emit(OpCodes.Brfalse, notTaken);
        il.Emit(OpCodes.Ldarg_0); EmitLoadPC(ctx); il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Conv_U2); il.Emit(OpCodes.Stfld, _fpc);
        EmitLoadPC(ctx); EmitZ80SetWZ(ctx);                        // WZ = dest (taken only)
        EmitChargeCycles(ctx, 5);                                   // taken penalty 8→13
        EmitChainOrExit(ctx, target);
        il.MarkLabel(notTaken);
        EmitChainOrExit(ctx, fallThrough);
    }

    // CALL cc,nn — WZ = nn UNCONDITIONALLY; the push is INSIDE the taken branch; the taken penalty is +5 PLUS
    // the 2 inline push writes (DECISION J). 10 T not-taken; 17 T taken.
    private void EmitZ80CallCc(EmitContext ctx, ushort pc, int length, JitOp op)
    {
        ILGenerator il = ctx.Il;
        ushort target = (ushort)(_bus.Read8((ushort)(pc + 1)) | (_bus.Read8((ushort)(pc + 2)) << 8));
        ushort fallThrough = (ushort)(pc + 3);
        // cl,ch read; PC past operand; WZ = nn UNCONDITIONALLY.
        EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx); il.Emit(OpCodes.Stloc, ctx.LoLocal); // cl;+1
        EmitIncrementPC(ctx, 1);
        EmitLoadPC(ctx); il.Emit(OpCodes.Conv_U4); LoadByteFromBus(ctx); il.Emit(OpCodes.Stloc, ctx.HiLocal); // ch;+1
        EmitIncrementPC(ctx, 1);
        il.Emit(OpCodes.Ldloc, ctx.HiLocal); il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shl);
        il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Or); EmitZ80SetWZ(ctx);   // WZ = nn (unconditional)
        EmitZ80ClearQ(ctx);
        EmitChargeCycles(ctx, 7);                                   // 10 T not-taken = fetch1 + 2 reads + 7
        Label notTaken = il.DefineLabel();
        EmitZ80Cond(ctx, op); il.Emit(OpCodes.Brfalse, notTaken);
        // taken: push PC (return addr, already past operand) — 2 writes +1 each; PC = nn; +5
        EmitZ80PushPc(ctx);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, ctx.HiLocal); il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shl);
        il.Emit(OpCodes.Ldloc, ctx.LoLocal); il.Emit(OpCodes.Or); il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Stfld, _fpc);
        EmitChargeCycles(ctx, 5);                                   // taken penalty (10→17 minus the 2 writes charged inline)
        EmitChainOrExit(ctx, target);                              // STATIC call entry
        il.MarkLabel(notTaken);
        EmitChainOrExit(ctx, fallThrough);                         // PC at pc+3
    }

    // RET cc — WZ = popped ONLY when taken; the pop is INSIDE the taken branch; the taken penalty is +4 PLUS
    // the 2 inline pop reads (DECISION J). 5 T not-taken; 11 T taken. Not-taken chains (pc+1 static); taken exits.
    private void EmitZ80RetCc(EmitContext ctx, ushort pc, int length, JitOp op)
    {
        ILGenerator il = ctx.Il;
        ushort fallThrough = (ushort)(pc + length);                // RET cc is 1 byte; fall-through = pc+1
        EmitZ80ClearQ(ctx);
        EmitChargeCycles(ctx, 4);                                   // 5 T not-taken = fetch1 + 0 bus + 4
        Label notTaken = il.DefineLabel();
        EmitZ80Cond(ctx, op); il.Emit(OpCodes.Brfalse, notTaken);
        // taken: pop PC — 2 reads +1 each; WZ = popped; +4; DYNAMIC target → exit
        EmitZ80PopPc(ctx);
        EmitLoadPC(ctx); EmitZ80SetWZ(ctx);                        // WZ = popped (taken only)
        EmitChargeCycles(ctx, 4);                                   // taken penalty (5→11 minus the 2 reads charged inline)
        EmitNormalExit(ctx);                                       // DYNAMIC popped target — NOT chainable
        il.MarkLabel(notTaken);
        EmitChainOrExit(ctx, fallThrough);                         // not-taken fall-through IS static — chainable
    }
}
