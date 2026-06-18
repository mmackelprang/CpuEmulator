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
            case (JitMode.RegisterIndirect, "Load"):
            {
                string pair = Z80IndirectPair(d);
                if (pair != "HL")                            // WZ = (ushort)(pair + 1)
                {
                    EmitLoadReg16(ctx, pair);
                    il.Emit(OpCodes.Ldc_I4_1);
                    il.Emit(OpCodes.Add);
                    EmitZ80SetWZ(ctx);
                }
                EmitLoadReg16(ctx, pair);                    // address (int) on stack
                il.Emit(OpCodes.Conv_U4);
                LoadByteFromBus(ctx);                        // data; charges 1
                il.Emit(OpCodes.Stloc, ctx.DataLocal);
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
            case (JitMode.ExtendedAddress, "Load"):
                EmitZ80ReadAbsEa(ctx);                       // ea (int) -> ctx.AddrLocal; charges 2 (lo+hi)
                il.Emit(OpCodes.Ldloc, ctx.AddrLocal); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add);
                EmitZ80SetWZ(ctx);                           // WZ = ea + 1
                il.Emit(OpCodes.Ldloc, ctx.AddrLocal);       // (already uint)
                LoadByteFromBus(ctx);                        // data; charges 1
                il.Emit(OpCodes.Stloc, ctx.DataLocal);
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
}
