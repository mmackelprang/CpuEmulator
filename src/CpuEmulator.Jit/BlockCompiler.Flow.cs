using System.Reflection;
using System.Reflection.Emit;
using CpuEmulator.Core;
using CpuEmulator.Core.Jit;

namespace CpuEmulator.Jit;

/// <summary>The Alu (non-ADC/SBC), Rmw, and control-flow (Branch/Jump/Jsr/Rts) emit arms.
/// Each mirrors the proven CpuEmitter body one-for-one.</summary>
internal sealed partial class BlockCompiler
{
    // ── ALU class (non-ADC/SBC: And/Ora/Eor/Compare/Bit) ─────────────────────────────────────
    private void EmitAlu(EmitContext ctx, OpcodeDescriptor d)
    {
        ILGenerator il = ctx.Il;
        EmitOperandRead(ctx, d);                 // data (int) on stack
        il.Emit(OpCodes.Stloc, ctx.DataLocal);
        string kind = d.Ops[0].Kind;
        switch (kind)
        {
            case "And":
            case "Ora":
            case "Eor":
            {
                // A = A <op> data
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, FA);
                il.Emit(OpCodes.Ldloc, ctx.DataLocal);
                il.Emit(kind switch
                {
                    "And" => OpCodes.And,
                    "Ora" => OpCodes.Or,
                    _ => OpCodes.Xor,
                });
                il.Emit(OpCodes.Conv_U1);
                il.Emit(OpCodes.Stfld, FA);
                // SetNZ(A)
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, FA);
                EmitSetNZFromStack(ctx);
                break;
            }
            case "Compare":
            {
                // temp = reg - data; P = (P & 0x7C) | (reg >= data ? 1 : 0)
                //   | ((temp & 0xFF) == 0 ? 2 : 0) | (temp & 0x80)
                FieldInfo reg = RegField(d.Ops[0].RegA);
                // temp -> LoLocal
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, reg);
                il.Emit(OpCodes.Ldloc, ctx.DataLocal);
                il.Emit(OpCodes.Sub);
                il.Emit(OpCodes.Stloc, ctx.LoLocal);      // temp (int)

                il.Emit(OpCodes.Ldarg_0);
                // (P & 0x7C)
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, FP);
                il.Emit(OpCodes.Ldc_I4, 0x7C);
                il.Emit(OpCodes.And);
                // | (reg >= data ? 1 : 0)
                Label ge = il.DefineLabel(), gedone = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, reg);
                il.Emit(OpCodes.Ldloc, ctx.DataLocal);
                il.Emit(OpCodes.Bge, ge);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Br, gedone);
                il.MarkLabel(ge);
                il.Emit(OpCodes.Ldc_I4_1);
                il.MarkLabel(gedone);
                il.Emit(OpCodes.Or);
                // | ((temp & 0xFF) == 0 ? 2 : 0)
                Label nz = il.DefineLabel(), nzdone = il.DefineLabel();
                il.Emit(OpCodes.Ldloc, ctx.LoLocal);
                il.Emit(OpCodes.Ldc_I4, 0xFF);
                il.Emit(OpCodes.And);
                il.Emit(OpCodes.Brtrue, nz);
                il.Emit(OpCodes.Ldc_I4_2);
                il.Emit(OpCodes.Br, nzdone);
                il.MarkLabel(nz);
                il.Emit(OpCodes.Ldc_I4_0);
                il.MarkLabel(nzdone);
                il.Emit(OpCodes.Or);
                // | (temp & 0x80)
                il.Emit(OpCodes.Ldloc, ctx.LoLocal);
                il.Emit(OpCodes.Ldc_I4, 0x80);
                il.Emit(OpCodes.And);
                il.Emit(OpCodes.Or);
                il.Emit(OpCodes.Conv_U1);
                il.Emit(OpCodes.Stfld, FP);
                break;
            }
            case "Adc":
                EmitAdc(ctx);   // data already in ctx.DataLocal (EmitOperandRead ran above)
                break;
            case "Sbc":
                EmitSbc(ctx);
                break;
            case "Bit":
            {
                // P = (P & 0x3D) | ((A & data) == 0 ? 2 : 0) | (data & 0xC0)
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, FP);
                il.Emit(OpCodes.Ldc_I4, 0x3D);
                il.Emit(OpCodes.And);
                // | ((A & data) == 0 ? 2 : 0)
                Label nz = il.DefineLabel(), nzdone = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, FA);
                il.Emit(OpCodes.Ldloc, ctx.DataLocal);
                il.Emit(OpCodes.And);
                il.Emit(OpCodes.Ldc_I4, 0xFF);
                il.Emit(OpCodes.And);
                il.Emit(OpCodes.Brtrue, nz);
                il.Emit(OpCodes.Ldc_I4_2);
                il.Emit(OpCodes.Br, nzdone);
                il.MarkLabel(nz);
                il.Emit(OpCodes.Ldc_I4_0);
                il.MarkLabel(nzdone);
                il.Emit(OpCodes.Or);
                // | (data & 0xC0)
                il.Emit(OpCodes.Ldloc, ctx.DataLocal);
                il.Emit(OpCodes.Ldc_I4, 0xC0);
                il.Emit(OpCodes.And);
                il.Emit(OpCodes.Or);
                il.Emit(OpCodes.Conv_U1);
                il.Emit(OpCodes.Stfld, FP);
                break;
            }
            default:
                throw new EmulationException($"alu: no arm for kind '{kind}' (opcode 0x{d.Opcode:X2})");
        }
        // (opcode-fetch cycle charged up-front in EmitInstruction — see GT-F(a) note there)
    }

    // ── RMW class (ShiftLeft/Right, RotateLeft/Right, IncrementMem, DecrementMem) ─────────────
    private void EmitRmw(EmitContext ctx, OpcodeDescriptor d)
    {
        ILGenerator il = ctx.Il;
        string kind = d.Ops[0].Kind;

        if (d.Mode == JitMode.Accumulator)
        {
            // dummy read at PC (no increment); operate on A; SetNZ(A)
            EmitLoadPC(ctx);
            il.Emit(OpCodes.Conv_U4);
            LoadByteFromBus(ctx);
            il.Emit(OpCodes.Pop);
            // value = A
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, FA);
            il.Emit(OpCodes.Stloc, ctx.DataLocal);    // value (int)
            EmitRmwCompute(ctx, kind);                // temp -> HiLocal, sets C from value
            // A = temp; SetNZ(A)
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldloc, ctx.HiLocal);
            il.Emit(OpCodes.Conv_U1);
            il.Emit(OpCodes.Stfld, FA);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, FA);
            EmitSetNZFromStack(ctx);
            // (opcode-fetch cycle charged up-front in EmitInstruction)
            return;
        }

        // Memory forms: resolve ea (RMW shape), read value, dummy write of unmodified value,
        // compute, write modified, SetNZ.
        EmitRmwAddress(ctx, d);                       // ea on stack
        il.Emit(OpCodes.Stloc, ctx.EaLocal);          // stash ea (LoadByteFromBus reuses EaLocal,
                                                      // but we re-store before each store below)
        // value = bus[ea]
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        LoadByteFromBus(ctx);
        il.Emit(OpCodes.Stloc, ctx.DataLocal);        // value
        // dummy write of the unmodified value: WriteBus(ea, value)
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Ldloc, ctx.DataLocal);
        EmitStoreByte(ctx);
        // compute temp -> HiLocal (sets C from value)
        EmitRmwCompute(ctx, kind);
        // WriteBus(ea, temp)
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Ldloc, ctx.HiLocal);
        EmitStoreByte(ctx);
        // P = (P & 0x7D) | (temp==0?2:0) | (temp & 0x80)
        il.Emit(OpCodes.Ldloc, ctx.HiLocal);
        EmitSetNZFromStack(ctx);
        // (opcode-fetch cycle charged up-front in EmitInstruction — see GT-F(a) note there)
    }

    /// <summary>Compute the RMW result into HiLocal from the value in DataLocal, setting the C
    /// flag where the op does. Mirrors CpuEmitter.EmitRmwCompute (C from the OLD value).</summary>
    private void EmitRmwCompute(EmitContext ctx, string kind)
    {
        ILGenerator il = ctx.Il;
        switch (kind)
        {
            case "ShiftLeft":
                // temp = value << 1; C = (value >> 7) & 1
                il.Emit(OpCodes.Ldloc, ctx.DataLocal);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Shl);
                il.Emit(OpCodes.Conv_U1);
                il.Emit(OpCodes.Stloc, ctx.HiLocal);
                EmitSetCarry(ctx, () =>
                {
                    il.Emit(OpCodes.Ldloc, ctx.DataLocal);
                    il.Emit(OpCodes.Ldc_I4_7);
                    il.Emit(OpCodes.Shr_Un);
                    il.Emit(OpCodes.Ldc_I4_1);
                    il.Emit(OpCodes.And);
                });
                break;
            case "ShiftRight":
                // temp = value >> 1; C = value & 1
                il.Emit(OpCodes.Ldloc, ctx.DataLocal);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Shr_Un);
                il.Emit(OpCodes.Conv_U1);
                il.Emit(OpCodes.Stloc, ctx.HiLocal);
                EmitSetCarry(ctx, () =>
                {
                    il.Emit(OpCodes.Ldloc, ctx.DataLocal);
                    il.Emit(OpCodes.Ldc_I4_1);
                    il.Emit(OpCodes.And);
                });
                break;
            case "RotateLeft":
                // temp = (value << 1) | (P & 1); C = (value >> 7) & 1
                il.Emit(OpCodes.Ldloc, ctx.DataLocal);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Shl);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, FP);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.And);
                il.Emit(OpCodes.Or);
                il.Emit(OpCodes.Conv_U1);
                il.Emit(OpCodes.Stloc, ctx.HiLocal);
                EmitSetCarry(ctx, () =>
                {
                    il.Emit(OpCodes.Ldloc, ctx.DataLocal);
                    il.Emit(OpCodes.Ldc_I4_7);
                    il.Emit(OpCodes.Shr_Un);
                    il.Emit(OpCodes.Ldc_I4_1);
                    il.Emit(OpCodes.And);
                });
                break;
            case "RotateRight":
                // temp = (value >> 1) | ((P & 1) << 7); C = value & 1
                il.Emit(OpCodes.Ldloc, ctx.DataLocal);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Shr_Un);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, FP);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.And);
                il.Emit(OpCodes.Ldc_I4_7);
                il.Emit(OpCodes.Shl);
                il.Emit(OpCodes.Or);
                il.Emit(OpCodes.Conv_U1);
                il.Emit(OpCodes.Stloc, ctx.HiLocal);
                EmitSetCarry(ctx, () =>
                {
                    il.Emit(OpCodes.Ldloc, ctx.DataLocal);
                    il.Emit(OpCodes.Ldc_I4_1);
                    il.Emit(OpCodes.And);
                });
                break;
            case "IncrementMem":
                il.Emit(OpCodes.Ldloc, ctx.DataLocal);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Conv_U1);
                il.Emit(OpCodes.Stloc, ctx.HiLocal);
                break;
            case "DecrementMem":
                il.Emit(OpCodes.Ldloc, ctx.DataLocal);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Sub);
                il.Emit(OpCodes.Conv_U1);
                il.Emit(OpCodes.Stloc, ctx.HiLocal);
                break;
            default:
                throw new EmulationException($"rmw-compute: no arm for kind '{kind}'");
        }
    }

    /// <summary>P = (P & 0xFE) | (carryBits pushed by the action &amp; 1).</summary>
    private void EmitSetCarry(EmitContext ctx, System.Action pushCarry)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, FP);
        il.Emit(OpCodes.Ldc_I4, 0xFE);
        il.Emit(OpCodes.And);
        pushCarry();
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Conv_U1);
        il.Emit(OpCodes.Stfld, FP);
    }

    /// <summary>Resolve a memory-RMW effective address onto the stack (ZeroPage/ZeroPageX/
    /// Absolute/AbsoluteX), mirroring the interpreter's RMW dummy-read shape.</summary>
    private void EmitRmwAddress(EmitContext ctx, OpcodeDescriptor d)
    {
        ILGenerator il = ctx.Il;
        switch (d.Mode)
        {
            case JitMode.ZeroPage:
                EmitReadAtPC(ctx);
                il.Emit(OpCodes.Conv_U4);
                EmitIncrementPC(ctx, 1);
                break;
            case JitMode.ZeroPageX:
                EmitReadAtPC(ctx);
                il.Emit(OpCodes.Stloc, ctx.AddrLocal);
                EmitIncrementPC(ctx, 1);
                il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
                LoadByteFromBus(ctx);
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
                EmitLoadRegByte(ctx, FieldInfoIndex.X);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ldc_I4, 0xFF);
                il.Emit(OpCodes.And);
                il.Emit(OpCodes.Conv_U4);
                break;
            case JitMode.Absolute:
                EmitReadAbsoluteEa(ctx);
                break;
            case JitMode.AbsoluteX:
                // lo, hi; ea = ((lo|hi<<8)+X)&0xFFFF; dummy read at (hi<<8)|((lo+X)&0xFF) ALWAYS
                EmitReadAtPC(ctx);
                il.Emit(OpCodes.Stloc, ctx.LoLocal);
                EmitIncrementPC(ctx, 1);
                EmitReadAtPC(ctx);
                il.Emit(OpCodes.Stloc, ctx.HiLocal);
                EmitIncrementPC(ctx, 1);
                il.Emit(OpCodes.Ldloc, ctx.LoLocal);
                il.Emit(OpCodes.Ldloc, ctx.HiLocal);
                il.Emit(OpCodes.Ldc_I4_8);
                il.Emit(OpCodes.Shl);
                il.Emit(OpCodes.Or);
                EmitLoadRegByte(ctx, FieldInfoIndex.X);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ldc_I4, 0xFFFF);
                il.Emit(OpCodes.And);
                il.Emit(OpCodes.Conv_U4);
                il.Emit(OpCodes.Stloc, ctx.AddrLocal);
                il.Emit(OpCodes.Ldloc, ctx.HiLocal);
                il.Emit(OpCodes.Ldc_I4_8);
                il.Emit(OpCodes.Shl);
                il.Emit(OpCodes.Ldloc, ctx.LoLocal);
                EmitLoadRegByte(ctx, FieldInfoIndex.X);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ldc_I4, 0xFF);
                il.Emit(OpCodes.And);
                il.Emit(OpCodes.Or);
                il.Emit(OpCodes.Conv_U4);
                LoadByteFromBus(ctx);
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
                break;
            default:
                throw new EmulationException($"rmw-addr: no arm for mode {d.Mode} (opcode 0x{d.Opcode:X2})");
        }
    }

    // ── Branch class (Relative) ──────────────────────────────────────────────────────────────
    private void EmitBranch(EmitContext ctx, ushort pc, OpcodeDescriptor d, int length)
    {
        ILGenerator il = ctx.Il;
        int bit = d.Ops[0].FlagBit;
        int expectedBit = d.Ops[0].BoolArg ? 1 : 0;

        // The two static successors (both compile-time constants from the offset byte in the code
        // stream — Ground truth A): the fall-through PC and the taken target. PC after the operand
        // is pc + the walk's COMPUTED length; the taken target adds the signed offset to it.
        byte offset = _bus.Read8((ushort)(pc + 1));
        ushort fallThroughPc = (ushort)(pc + length);
        ushort takenTargetPc = (ushort)(fallThroughPc + (sbyte)offset);

        // offset = bus[PC]; PC++   (LoLocal holds offset as int)
        EmitReadAtPC(ctx);
        il.Emit(OpCodes.Stloc, ctx.LoLocal);
        EmitIncrementPC(ctx, 1);

        Label notTaken = il.DefineLabel();
        // if (((P >> bit) & 1) == expectedBit)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, FP);
        il.Emit(OpCodes.Ldc_I4, bit);
        il.Emit(OpCodes.Shr_Un);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldc_I4, expectedBit);
        il.Emit(OpCodes.Bne_Un, notTaken);

        // taken: dummy read at PC; target = (ushort)(PC + (sbyte)offset)
        EmitLoadPC(ctx);
        il.Emit(OpCodes.Conv_U4);
        LoadByteFromBus(ctx);
        il.Emit(OpCodes.Pop);

        // target = (ushort)(PC + (sbyte)offset)  -> HiLocal
        EmitLoadPC(ctx);                              // PC (int, 0..65535)
        il.Emit(OpCodes.Ldloc, ctx.LoLocal);          // offset (int 0..255)
        il.Emit(OpCodes.Conv_I1);                     // (sbyte)offset, sign-extended to int
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Conv_U2);                     // (ushort)
        il.Emit(OpCodes.Stloc, ctx.HiLocal);          // target

        // if ((target & 0xFF00) != (PC & 0xFF00)) dummy read at (PC & 0xFF00)|(target & 0xFF)
        Label noPageCross = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, ctx.HiLocal);
        il.Emit(OpCodes.Ldc_I4, 0xFF00);
        il.Emit(OpCodes.And);
        EmitLoadPC(ctx);
        il.Emit(OpCodes.Ldc_I4, 0xFF00);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Beq, noPageCross);
        // dummy read at (PC & 0xFF00) | (target & 0xFF)
        EmitLoadPC(ctx);
        il.Emit(OpCodes.Ldc_I4, 0xFF00);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldloc, ctx.HiLocal);
        il.Emit(OpCodes.Ldc_I4, 0xFF);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Conv_U4);
        LoadByteFromBus(ctx);
        il.Emit(OpCodes.Pop);
        il.MarkLabel(noPageCross);

        // PC = target
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, ctx.HiLocal);
        il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Stfld, FPC);
        // (opcode-fetch cycle charged up-front; the taken +1 / page-cross +1 charged in the body
        //  above, unchanged from M2-i). Chain to the TAKEN static target (Ground truth A).
        EmitChainOrExit(ctx, takenTargetPc);

        il.MarkLabel(notTaken);
        // The untaken arm: PC is already at the fall-through (the operand read advanced it). Chain
        // to the fall-through static target.
        EmitChainOrExit(ctx, fallThroughPc);
    }

    // ── Jump class (JMP Absolute / Indirect) ─────────────────────────────────────────────────
    private void EmitJump(EmitContext ctx, ushort pc, OpcodeDescriptor d)
    {
        ILGenerator il = ctx.Il;
        if (d.Mode == JitMode.Absolute)
        {
            // The static target is the absolute operand (a constant in the code stream) — Ground
            // truth A: JMP-abs is chainable.
            ushort target = (ushort)(_bus.Read8((ushort)(pc + 1)) | (_bus.Read8((ushort)(pc + 2)) << 8));
            // lo = bus[PC]; PC++; hi = bus[PC]; PC++; PC = lo | (hi << 8)
            EmitReadAtPC(ctx);
            il.Emit(OpCodes.Stloc, ctx.LoLocal);
            EmitIncrementPC(ctx, 1);
            EmitReadAtPC(ctx);
            il.Emit(OpCodes.Stloc, ctx.HiLocal);
            EmitIncrementPC(ctx, 1);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldloc, ctx.LoLocal);
            il.Emit(OpCodes.Ldloc, ctx.HiLocal);
            il.Emit(OpCodes.Ldc_I4_8);
            il.Emit(OpCodes.Shl);
            il.Emit(OpCodes.Or);
            il.Emit(OpCodes.Conv_U2);
            il.Emit(OpCodes.Stfld, FPC);
            // (opcode-fetch cycle charged up-front in EmitInstruction)
            EmitChainOrExit(ctx, target);
        }
        else if (d.Mode == JitMode.Indirect)
        {
            // lo = bus[PC]; PC++; hi = bus[PC]; PC++; ptr = lo|hi<<8;
            // target = bus[ptr]; target |= bus[(ptr & 0xFF00)|((ptr+1)&0xFF)] << 8; PC = target
            EmitReadAtPC(ctx);
            il.Emit(OpCodes.Stloc, ctx.LoLocal);
            EmitIncrementPC(ctx, 1);
            EmitReadAtPC(ctx);
            il.Emit(OpCodes.Stloc, ctx.HiLocal);
            EmitIncrementPC(ctx, 1);
            // ptr -> AddrLocal
            il.Emit(OpCodes.Ldloc, ctx.LoLocal);
            il.Emit(OpCodes.Ldloc, ctx.HiLocal);
            il.Emit(OpCodes.Ldc_I4_8);
            il.Emit(OpCodes.Shl);
            il.Emit(OpCodes.Or);
            il.Emit(OpCodes.Conv_U4);
            il.Emit(OpCodes.Stloc, ctx.AddrLocal);
            // target lo = bus[ptr]
            il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
            LoadByteFromBus(ctx);
            il.Emit(OpCodes.Stloc, ctx.DataLocal);
            // target hi = bus[(ptr & 0xFF00) | ((ptr+1) & 0xFF)] << 8
            il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
            il.Emit(OpCodes.Ldc_I4, 0xFF00);
            il.Emit(OpCodes.And);
            il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Ldc_I4, 0xFF);
            il.Emit(OpCodes.And);
            il.Emit(OpCodes.Or);
            il.Emit(OpCodes.Conv_U4);
            LoadByteFromBus(ctx);
            il.Emit(OpCodes.Ldc_I4_8);
            il.Emit(OpCodes.Shl);
            il.Emit(OpCodes.Ldloc, ctx.DataLocal);
            il.Emit(OpCodes.Or);
            // PC = target
            il.Emit(OpCodes.Stloc, ctx.HiLocal);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldloc, ctx.HiLocal);
            il.Emit(OpCodes.Conv_U2);
            il.Emit(OpCodes.Stfld, FPC);
            // (opcode-fetch cycle charged up-front in EmitInstruction). JMP-(ind) reads its target
            // from memory at run time — a DYNAMIC successor, NOT chainable (Ground truth A).
            EmitNormalExit(ctx);
        }
        else
        {
            throw new EmulationException($"jump: no arm for mode {d.Mode} (opcode 0x{d.Opcode:X2})");
        }
    }

    // ── JSR (Absolute) ───────────────────────────────────────────────────────────────────────
    private void EmitJsr(EmitContext ctx, ushort pc, OpcodeDescriptor d)
    {
        ILGenerator il = ctx.Il;
        // The static call target is the absolute operand (a constant in the code stream) — Ground
        // truth A: JSR's target is known (the return address is dynamic, but the entry is static).
        ushort target = (ushort)(_bus.Read8((ushort)(pc + 1)) | (_bus.Read8((ushort)(pc + 2)) << 8));
        // lo = bus[PC]; PC++; dummy read at 0x100+S; push PCH; S--; push PCL; S--; hi = bus[PC]; PC = hi:lo
        EmitReadAtPC(ctx);
        il.Emit(OpCodes.Stloc, ctx.LoLocal);
        EmitIncrementPC(ctx, 1);
        // dummy stack read
        EmitStackAddress(ctx);
        LoadByteFromBus(ctx);
        il.Emit(OpCodes.Pop);
        // push PCH = PC >> 8
        EmitStackAddress(ctx);
        EmitLoadPC(ctx);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shr_Un);
        EmitStoreByte(ctx);
        EmitDecrementS(ctx);
        // push PCL = PC & 0xFF
        EmitStackAddress(ctx);
        EmitLoadPC(ctx);
        EmitStoreByte(ctx);
        EmitDecrementS(ctx);
        // hi = bus[PC]
        EmitReadAtPC(ctx);
        il.Emit(OpCodes.Stloc, ctx.HiLocal);
        // PC = lo | (hi << 8)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, ctx.LoLocal);
        il.Emit(OpCodes.Ldloc, ctx.HiLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shl);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Stfld, FPC);
        // (opcode-fetch cycle charged up-front in EmitInstruction). Chain to the static call target.
        EmitChainOrExit(ctx, target);
    }

    // ── RTS (Implied) ────────────────────────────────────────────────────────────────────────
    private void EmitRts(EmitContext ctx, OpcodeDescriptor d)
    {
        ILGenerator il = ctx.Il;
        // dummy read at PC; dummy read at 0x100+S; S++; lo=bus[0x100+S]; S++; hi=bus[0x100+S];
        // PC = lo|hi<<8; dummy read at new PC; PC++
        EmitLoadPC(ctx);
        il.Emit(OpCodes.Conv_U4);
        LoadByteFromBus(ctx);
        il.Emit(OpCodes.Pop);
        EmitStackAddress(ctx);
        LoadByteFromBus(ctx);
        il.Emit(OpCodes.Pop);
        EmitIncrementS(ctx);
        EmitStackAddress(ctx);
        LoadByteFromBus(ctx);
        il.Emit(OpCodes.Stloc, ctx.LoLocal);
        EmitIncrementS(ctx);
        EmitStackAddress(ctx);
        LoadByteFromBus(ctx);
        il.Emit(OpCodes.Stloc, ctx.HiLocal);
        // PC = lo | (hi << 8)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, ctx.LoLocal);
        il.Emit(OpCodes.Ldloc, ctx.HiLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shl);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Stfld, FPC);
        // dummy read at new PC; PC++
        EmitLoadPC(ctx);
        il.Emit(OpCodes.Conv_U4);
        LoadByteFromBus(ctx);
        il.Emit(OpCodes.Pop);
        EmitIncrementPC(ctx, 1);
        // (opcode-fetch cycle charged up-front in EmitInstruction). RTS pops its target from the
        // stack — a DYNAMIC successor, NOT chainable (Ground truth A): exit to the dispatcher.
        EmitNormalExit(ctx);
    }
}
