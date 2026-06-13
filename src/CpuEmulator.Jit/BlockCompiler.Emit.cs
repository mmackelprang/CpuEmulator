using System.Reflection;
using System.Reflection.Emit;
using CpuEmulator.Core;
using CpuEmulator.Core.Jit;
using CpuEmulator.Cpus.Mos6502;

namespace CpuEmulator.Jit;

/// <summary>The per-class emit arms. Each mirrors the proven CpuEmitter body one-for-one — the
/// interpreter is the oracle. Operand resolution (the dummy-read + page-cross shape) is shared
/// between Load and Alu, exactly as the interpreter shares EmitOperandResolution.</summary>
internal sealed partial class BlockCompiler
{
    // ── Operand resolution for Load + Alu: reads memory into a byte (int) on the stack ───────
    // Mirrors CpuEmitter.EmitOperandResolution. Leaves the data byte (int) on the IL stack.
    private void EmitOperandRead(EmitContext ctx, OpcodeDescriptor d)
    {
        ILGenerator il = ctx.Il;
        switch (d.Mode)
        {
            case JitMode.Immediate:
                EmitReadAtPC(ctx);                 // data = bus[PC]; +1 cycle
                EmitIncrementPC(ctx, 1);
                break;

            case JitMode.ZeroPage:
                EmitReadAtPC(ctx);                 // addr = bus[PC]
                il.Emit(OpCodes.Stloc, ctx.AddrLocal);
                EmitIncrementPC(ctx, 1);
                il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
                LoadByteFromBus(ctx);              // data = bus[addr]
                break;

            case JitMode.ZeroPageX:
            case JitMode.ZeroPageY:
            {
                FieldInfoIndex idx = d.Mode == JitMode.ZeroPageX ? FieldInfoIndex.X : FieldInfoIndex.Y;
                EmitReadAtPC(ctx);                 // addr = bus[PC]
                il.Emit(OpCodes.Stloc, ctx.AddrLocal);
                EmitIncrementPC(ctx, 1);
                // dummy read at unindexed zp
                il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
                LoadByteFromBus(ctx);
                il.Emit(OpCodes.Pop);
                // data = bus[(addr + index) & 0xFF]
                il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
                EmitLoadRegByte(ctx, idx);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ldc_I4, 0xFF);
                il.Emit(OpCodes.And);
                il.Emit(OpCodes.Conv_U4);
                LoadByteFromBus(ctx);
                break;
            }

            case JitMode.Absolute:
                EmitReadAbsoluteEa(ctx);           // ea on stack (uint)
                LoadByteFromBus(ctx);              // data = bus[ea]
                break;

            case JitMode.AbsoluteX:
            case JitMode.AbsoluteY:
                EmitIndexedAbsoluteRead(ctx, d.Mode == JitMode.AbsoluteX ? FieldInfoIndex.X : FieldInfoIndex.Y);
                break;

            case JitMode.IndirectX:
                EmitIndirectXEa(ctx);              // ea on stack
                LoadByteFromBus(ctx);
                break;

            case JitMode.IndirectY:
                EmitIndirectYRead(ctx);
                break;

            default:
                throw new EmulationException($"operand-read: no arm for mode {d.Mode} (opcode 0x{d.Opcode:X2})");
        }
    }

    // The indexed-addressing-mode → index-register convention (ZeroPageX uses X, AbsoluteY uses Y)
    // is the 6502 DECODE dimension (M3.1b owns generalizing RequiredIndexRegister). It stays
    // 6502-shaped here, but the FieldInfo is now resolved BY NAME from the per-compile map (J2) —
    // no baked FX/FY statics. Recorded: the seam between the register dimension (done) and the
    // decode dimension (deferred).
    private enum FieldInfoIndex { X, Y }

    private void EmitLoadRegByte(EmitContext ctx, FieldInfoIndex idx)
    {
        ctx.Il.Emit(OpCodes.Ldarg_0);
        ctx.Il.Emit(OpCodes.Ldfld, RegField(idx == FieldInfoIndex.X ? "X" : "Y"));
    }

    /// <summary>lo = bus[PC]; PC++; hi = bus[PC]; PC++; push ea = lo | (hi &lt;&lt; 8).</summary>
    private void EmitReadAbsoluteEa(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        EmitReadAtPC(ctx);
        il.Emit(OpCodes.Stloc, ctx.LoLocal);
        EmitIncrementPC(ctx, 1);
        EmitReadAtPC(ctx);
        il.Emit(OpCodes.Stloc, ctx.HiLocal);
        EmitIncrementPC(ctx, 1);
        // ea = lo | (hi << 8)
        il.Emit(OpCodes.Ldloc, ctx.LoLocal);
        il.Emit(OpCodes.Ldloc, ctx.HiLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shl);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Conv_U4);
    }

    /// <summary>The indexed-absolute Load/Alu read (AbsoluteX/Y), mirroring the interpreter:
    /// always a dummy read at the wrong address, then a real read at ea on page cross.</summary>
    private void EmitIndexedAbsoluteRead(EmitContext ctx, FieldInfoIndex idx)
    {
        ILGenerator il = ctx.Il;
        // lo = bus[PC]; PC++; hi = bus[PC]; PC++
        EmitReadAtPC(ctx);
        il.Emit(OpCodes.Stloc, ctx.LoLocal);
        EmitIncrementPC(ctx, 1);
        EmitReadAtPC(ctx);
        il.Emit(OpCodes.Stloc, ctx.HiLocal);
        EmitIncrementPC(ctx, 1);

        // ea = ((lo | (hi << 8)) + index) & 0xFFFF   -> EaLocal (final addr)
        il.Emit(OpCodes.Ldloc, ctx.LoLocal);
        il.Emit(OpCodes.Ldloc, ctx.HiLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shl);
        il.Emit(OpCodes.Or);
        EmitLoadRegByte(ctx, idx);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldc_I4, 0xFFFF);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Conv_U4);
        il.Emit(OpCodes.Stloc, ctx.AddrLocal);   // AddrLocal = final ea

        // dummy read at (hi << 8) | ((lo + index) & 0xFF)
        il.Emit(OpCodes.Ldloc, ctx.HiLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shl);
        il.Emit(OpCodes.Ldloc, ctx.LoLocal);
        EmitLoadRegByte(ctx, idx);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldc_I4, 0xFF);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Conv_U4);
        LoadByteFromBus(ctx);
        il.Emit(OpCodes.Stloc, ctx.DataLocal);   // data = dummy read result

        // if (((lo + index) & 0x100) != 0) data = bus[ea]   (page cross -> real read)
        Label noCross = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, ctx.LoLocal);
        EmitLoadRegByte(ctx, idx);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldc_I4, 0x100);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Brfalse, noCross);
        il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
        LoadByteFromBus(ctx);
        il.Emit(OpCodes.Stloc, ctx.DataLocal);
        il.MarkLabel(noCross);

        il.Emit(OpCodes.Ldloc, ctx.DataLocal);   // push data
    }

    /// <summary>IndirectX effective address: ptr = bus[PC]; PC++; dummy read at ptr;
    /// lo = bus[(ptr+X)&amp;0xFF]; hi = bus[(ptr+X+1)&amp;0xFF]; push ea.</summary>
    private void EmitIndirectXEa(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        EmitReadAtPC(ctx);
        il.Emit(OpCodes.Stloc, ctx.AddrLocal);   // ptr
        EmitIncrementPC(ctx, 1);
        // dummy read at ptr
        il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
        LoadByteFromBus(ctx);
        il.Emit(OpCodes.Pop);
        // lo = bus[(ptr + X) & 0xFF]
        il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
        EmitLoadRegByte(ctx, FieldInfoIndex.X);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldc_I4, 0xFF);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Conv_U4);
        LoadByteFromBus(ctx);
        il.Emit(OpCodes.Stloc, ctx.LoLocal);
        // hi = bus[(ptr + X + 1) & 0xFF]
        il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
        EmitLoadRegByte(ctx, FieldInfoIndex.X);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldc_I4, 0xFF);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Conv_U4);
        LoadByteFromBus(ctx);
        il.Emit(OpCodes.Stloc, ctx.HiLocal);
        // ea = lo | (hi << 8)
        il.Emit(OpCodes.Ldloc, ctx.LoLocal);
        il.Emit(OpCodes.Ldloc, ctx.HiLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shl);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Conv_U4);
    }

    /// <summary>IndirectY Load/Alu read: ptr = bus[PC]; PC++; lo = bus[ptr]; hi = bus[(ptr+1)&amp;0xFF];
    /// ea = ((lo|hi&lt;&lt;8)+Y)&amp;0xFFFF; dummy read at (hi&lt;&lt;8)|((lo+Y)&amp;0xFF); +1 real read on cross.</summary>
    private void EmitIndirectYRead(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        EmitReadAtPC(ctx);
        il.Emit(OpCodes.Stloc, ctx.AddrLocal);   // ptr
        EmitIncrementPC(ctx, 1);
        // lo = bus[ptr]
        il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
        LoadByteFromBus(ctx);
        il.Emit(OpCodes.Stloc, ctx.LoLocal);
        // hi = bus[(ptr + 1) & 0xFF]
        il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldc_I4, 0xFF);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Conv_U4);
        LoadByteFromBus(ctx);
        il.Emit(OpCodes.Stloc, ctx.HiLocal);
        // ea = ((lo | (hi << 8)) + Y) & 0xFFFF -> AddrLocal
        il.Emit(OpCodes.Ldloc, ctx.LoLocal);
        il.Emit(OpCodes.Ldloc, ctx.HiLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shl);
        il.Emit(OpCodes.Or);
        EmitLoadRegByte(ctx, FieldInfoIndex.Y);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldc_I4, 0xFFFF);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Conv_U4);
        il.Emit(OpCodes.Stloc, ctx.AddrLocal);
        // dummy read at (hi << 8) | ((lo + Y) & 0xFF)
        il.Emit(OpCodes.Ldloc, ctx.HiLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shl);
        il.Emit(OpCodes.Ldloc, ctx.LoLocal);
        EmitLoadRegByte(ctx, FieldInfoIndex.Y);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldc_I4, 0xFF);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Conv_U4);
        LoadByteFromBus(ctx);
        il.Emit(OpCodes.Stloc, ctx.DataLocal);
        // if (((lo + Y) & 0x100) != 0) data = bus[ea]
        Label noCross = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, ctx.LoLocal);
        EmitLoadRegByte(ctx, FieldInfoIndex.Y);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldc_I4, 0x100);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Brfalse, noCross);
        il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
        LoadByteFromBus(ctx);
        il.Emit(OpCodes.Stloc, ctx.DataLocal);
        il.MarkLabel(noCross);
        il.Emit(OpCodes.Ldloc, ctx.DataLocal);
    }

    // ── Load class ───────────────────────────────────────────────────────────────────────────
    private void EmitLoad(EmitContext ctx, OpcodeDescriptor d)
    {
        ILGenerator il = ctx.Il;
        // Ops: Load(target) [+ SetNZ(target)]
        string target = d.Ops[0].RegA;
        EmitOperandRead(ctx, d);                 // data (int) on stack
        il.Emit(OpCodes.Stloc, ctx.DataLocal);
        // target = data
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, ctx.DataLocal);
        il.Emit(OpCodes.Conv_U1);
        il.Emit(OpCodes.Stfld, RegField(target));
        // remaining ops (SetNZ)
        for (int i = 1; i < d.Ops.Length; i++)
            EmitRegisterOp(ctx, d.Ops[i]);
        // (opcode-fetch cycle charged up-front in EmitInstruction — see GT-F(a) note there)
    }

    // ── Store class ────────────────────────────────────────────────────────────────────────
    private void EmitStore(EmitContext ctx, OpcodeDescriptor d)
    {
        // Ops: Store(source). Resolve the effective address (with store dummy reads), push the
        // source register byte, then store.
        string source = d.Ops[0].RegA;
        EmitStoreAddress(ctx, d);                // ea on stack
        EmitLoadRegOrA(ctx, source);             // value on stack
        EmitStoreByte(ctx);                      // charges 1 cycle, writes (fastmem/bus)
        // (opcode-fetch cycle charged up-front in EmitInstruction — see GT-F(a) note there)
    }

    private void EmitLoadRegOrA(EmitContext ctx, string regName)
    {
        ctx.Il.Emit(OpCodes.Ldarg_0);
        ctx.Il.Emit(OpCodes.Ldfld, RegField(regName));
    }

    /// <summary>Resolve a Store/Rmw effective address onto the stack, mirroring the interpreter's
    /// store dummy-read shape exactly.</summary>
    private void EmitStoreAddress(EmitContext ctx, OpcodeDescriptor d)
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
            case JitMode.ZeroPageY:
            {
                FieldInfoIndex idx = d.Mode == JitMode.ZeroPageX ? FieldInfoIndex.X : FieldInfoIndex.Y;
                EmitReadAtPC(ctx);
                il.Emit(OpCodes.Stloc, ctx.AddrLocal);
                EmitIncrementPC(ctx, 1);
                // dummy read at unindexed zp
                il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
                LoadByteFromBus(ctx);
                il.Emit(OpCodes.Pop);
                // (addr + index) & 0xFF
                il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
                EmitLoadRegByte(ctx, idx);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ldc_I4, 0xFF);
                il.Emit(OpCodes.And);
                il.Emit(OpCodes.Conv_U4);
                break;
            }

            case JitMode.Absolute:
                EmitReadAbsoluteEa(ctx);
                break;

            case JitMode.AbsoluteX:
            case JitMode.AbsoluteY:
            {
                FieldInfoIndex idx = d.Mode == JitMode.AbsoluteX ? FieldInfoIndex.X : FieldInfoIndex.Y;
                EmitReadAtPC(ctx);
                il.Emit(OpCodes.Stloc, ctx.LoLocal);
                EmitIncrementPC(ctx, 1);
                EmitReadAtPC(ctx);
                il.Emit(OpCodes.Stloc, ctx.HiLocal);
                EmitIncrementPC(ctx, 1);
                // ea = ((lo | hi<<8) + index) & 0xFFFF
                il.Emit(OpCodes.Ldloc, ctx.LoLocal);
                il.Emit(OpCodes.Ldloc, ctx.HiLocal);
                il.Emit(OpCodes.Ldc_I4_8);
                il.Emit(OpCodes.Shl);
                il.Emit(OpCodes.Or);
                EmitLoadRegByte(ctx, idx);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ldc_I4, 0xFFFF);
                il.Emit(OpCodes.And);
                il.Emit(OpCodes.Conv_U4);
                il.Emit(OpCodes.Stloc, ctx.AddrLocal);
                // dummy read at (hi<<8) | ((lo + index) & 0xFF)
                il.Emit(OpCodes.Ldloc, ctx.HiLocal);
                il.Emit(OpCodes.Ldc_I4_8);
                il.Emit(OpCodes.Shl);
                il.Emit(OpCodes.Ldloc, ctx.LoLocal);
                EmitLoadRegByte(ctx, idx);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ldc_I4, 0xFF);
                il.Emit(OpCodes.And);
                il.Emit(OpCodes.Or);
                il.Emit(OpCodes.Conv_U4);
                LoadByteFromBus(ctx);
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
                break;
            }

            case JitMode.IndirectX:
                EmitIndirectXEa(ctx);
                break;

            case JitMode.IndirectY:
            {
                EmitReadAtPC(ctx);
                il.Emit(OpCodes.Stloc, ctx.AddrLocal);   // ptr
                EmitIncrementPC(ctx, 1);
                il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
                LoadByteFromBus(ctx);
                il.Emit(OpCodes.Stloc, ctx.LoLocal);
                il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ldc_I4, 0xFF);
                il.Emit(OpCodes.And);
                il.Emit(OpCodes.Conv_U4);
                LoadByteFromBus(ctx);
                il.Emit(OpCodes.Stloc, ctx.HiLocal);
                // ea = ((lo | hi<<8) + Y) & 0xFFFF
                il.Emit(OpCodes.Ldloc, ctx.LoLocal);
                il.Emit(OpCodes.Ldloc, ctx.HiLocal);
                il.Emit(OpCodes.Ldc_I4_8);
                il.Emit(OpCodes.Shl);
                il.Emit(OpCodes.Or);
                EmitLoadRegByte(ctx, FieldInfoIndex.Y);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ldc_I4, 0xFFFF);
                il.Emit(OpCodes.And);
                il.Emit(OpCodes.Conv_U4);
                il.Emit(OpCodes.Stloc, ctx.AddrLocal);
                // dummy read at (hi<<8) | ((lo + Y) & 0xFF)
                il.Emit(OpCodes.Ldloc, ctx.HiLocal);
                il.Emit(OpCodes.Ldc_I4_8);
                il.Emit(OpCodes.Shl);
                il.Emit(OpCodes.Ldloc, ctx.LoLocal);
                EmitLoadRegByte(ctx, FieldInfoIndex.Y);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ldc_I4, 0xFF);
                il.Emit(OpCodes.And);
                il.Emit(OpCodes.Or);
                il.Emit(OpCodes.Conv_U4);
                LoadByteFromBus(ctx);
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
                break;
            }

            default:
                throw new EmulationException($"store-addr: no arm for mode {d.Mode} (opcode 0x{d.Opcode:X2})");
        }
    }

    // ── Register class (Implied) — includes transfers, inc/dec, set/clear flag, stack ops, NOP ─
    private void EmitRegister(EmitContext ctx, OpcodeDescriptor d)
    {
        ILGenerator il = ctx.Il;
        string firstKind = d.Ops.Length > 0 ? d.Ops[0].Kind : string.Empty;

        // Stack ops (PHA/PLA/PHP/PLP) have their own bus/cycle shape; the others share the
        // "dummy read at PC, then apply the ops" Implied shape.
        if (firstKind is "Push" or "Pull" or "PushP" or "PullP")
        {
            EmitStackOp(ctx, d.Ops[0]);
            // (opcode-fetch cycle charged up-front in EmitInstruction)
            return;
        }

        // Implied: opcode fetch (charged up-front) + dummy read at PC (no increment) + the ops.
        EmitLoadPC(ctx);
        il.Emit(OpCodes.Conv_U4);
        LoadByteFromBus(ctx);
        il.Emit(OpCodes.Pop);
        foreach (var op in d.Ops)
            EmitRegisterOp(ctx, op);
        // (opcode-fetch cycle charged up-front in EmitInstruction)
    }

    private void EmitRegisterOp(EmitContext ctx, JitOp op)
    {
        ILGenerator il = ctx.Il;
        switch (op.Kind)
        {
            case "Transfer":
            {
                // target = source
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, RegField(op.RegA));   // source
                il.Emit(OpCodes.Stfld, RegField(op.RegB));   // target
                break;
            }
            case "Increment":
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, RegField(op.RegA));
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Conv_U1);
                il.Emit(OpCodes.Stfld, RegField(op.RegA));
                break;
            }
            case "Decrement":
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, RegField(op.RegA));
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Sub);
                il.Emit(OpCodes.Conv_U1);
                il.Emit(OpCodes.Stfld, RegField(op.RegA));
                break;
            }
            case "SetNZ":
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, RegField(op.RegA));
                EmitSetNZFromStack(ctx);
                break;
            }
            case "SetFlag":
            {
                // P = value ? (P | mask) : (P & ~mask)
                byte mask = (byte)(1 << op.FlagBit);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, FP);
                if (op.BoolArg)
                {
                    il.Emit(OpCodes.Ldc_I4, (int)mask);
                    il.Emit(OpCodes.Or);
                }
                else
                {
                    il.Emit(OpCodes.Ldc_I4, (int)(~mask & 0xFF));
                    il.Emit(OpCodes.And);
                }
                il.Emit(OpCodes.Conv_U1);
                il.Emit(OpCodes.Stfld, FP);
                break;
            }
            default:
                throw new EmulationException($"register-op: no arm for kind '{op.Kind}'");
        }
    }

    private void EmitStackOp(EmitContext ctx, JitOp op)
    {
        ILGenerator il = ctx.Il;
        switch (op.Kind)
        {
            case "Push":
            {
                // dummy read at PC (no increment); WriteBus(0x100 + S, source); S--
                EmitLoadPC(ctx);
                il.Emit(OpCodes.Conv_U4);
                LoadByteFromBus(ctx);
                il.Emit(OpCodes.Pop);
                EmitStackAddress(ctx);          // 0x100 + S
                EmitLoadRegOrA(ctx, op.RegA);   // source
                EmitStoreByte(ctx);
                EmitDecrementS(ctx);
                break;
            }
            case "PushP":
            {
                EmitLoadPC(ctx);
                il.Emit(OpCodes.Conv_U4);
                LoadByteFromBus(ctx);
                il.Emit(OpCodes.Pop);
                EmitStackAddress(ctx);
                // (P | 0x30)
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, FP);
                il.Emit(OpCodes.Ldc_I4, 0x30);
                il.Emit(OpCodes.Or);
                EmitStoreByte(ctx);
                EmitDecrementS(ctx);
                break;
            }
            case "Pull":
            {
                // dummy read at PC; dummy read at 0x100+S; S++; target = bus[0x100+S]; SetNZ
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
                il.Emit(OpCodes.Stloc, ctx.DataLocal);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldloc, ctx.DataLocal);
                il.Emit(OpCodes.Conv_U1);
                il.Emit(OpCodes.Stfld, RegField(op.RegA));
                il.Emit(OpCodes.Ldloc, ctx.DataLocal);
                EmitSetNZFromStack(ctx);
                break;
            }
            case "PullP":
            {
                EmitLoadPC(ctx);
                il.Emit(OpCodes.Conv_U4);
                LoadByteFromBus(ctx);
                il.Emit(OpCodes.Pop);
                EmitStackAddress(ctx);
                LoadByteFromBus(ctx);
                il.Emit(OpCodes.Pop);
                EmitIncrementS(ctx);
                // P = (bus[0x100+S] | 0x20) & 0xEF
                EmitStackAddress(ctx);
                LoadByteFromBus(ctx);
                il.Emit(OpCodes.Ldc_I4, 0x20);
                il.Emit(OpCodes.Or);
                il.Emit(OpCodes.Ldc_I4, 0xEF);
                il.Emit(OpCodes.And);
                il.Emit(OpCodes.Stloc, ctx.DataLocal);   // stash the computed P
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldloc, ctx.DataLocal);
                il.Emit(OpCodes.Conv_U1);
                il.Emit(OpCodes.Stfld, FP);
                break;
            }
            default:
                throw new EmulationException($"stack-op: no arm for kind '{op.Kind}'");
        }
    }

    /// <summary>Push the stack address 0x100 + S (uint). S is the StackPointer-role register,
    /// resolved by name from the J2 map (the stack templates are 6502-baked on the 'S' name —
    /// generalizing which register is the stack pointer is the decode dimension, M3.1b).</summary>
    private void EmitStackAddress(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Ldc_I4, 0x100);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, RegField("S"));
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Conv_U4);
    }

    private void EmitDecrementS(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        FieldInfo fs = RegField("S");
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, fs);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Conv_U1);
        il.Emit(OpCodes.Stfld, fs);
    }

    private void EmitIncrementS(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        FieldInfo fs = RegField("S");
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, fs);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Conv_U1);
        il.Emit(OpCodes.Stfld, fs);
    }

    // ── Port class (M3.2) — the Io-bus callout (Ground truth D: NEVER fastmem) ────────────────
    /// <summary>Emit a PortIn/PortOut as an UNCONDITIONAL callout to the SECOND IAddressSpace
    /// (the Io bus — ArgIoBus), never the fastmem'd memory bus. There is NO fastmem branch here by
    /// construction: a port read/write is always an observable device side effect (the load-bearing
    /// never-fastmem rule, proven in emitted IL). Mirrors the interpreter EmitPortBody: the (n)
    /// operand fetch (IoPortImmediate) charges one cycle off the program bus via EmitReadAtPC, then
    /// the Io access charges one more (the interpreter's ReadIo/WriteIo each do _cycles++; the JIT
    /// charges it explicitly since it does not call them). The opcode-fetch cycle is charged up-front
    /// in EmitInstruction.</summary>
    private void EmitPort(EmitContext ctx, OpcodeDescriptor d)
    {
        ILGenerator il = ctx.Il;
        string reg = d.Ops[0].RegA;            // PortIn target / PortOut source — register NAME (J2)

        // Resolve the port number into AddrLocal (uint).
        switch (d.Mode)
        {
            case JitMode.IoPortImmediate:
                EmitReadAtPC(ctx);             // port = bus[PC]; charges 1 (the (n) operand fetch)
                il.Emit(OpCodes.Conv_U4);
                il.Emit(OpCodes.Stloc, ctx.AddrLocal);
                EmitIncrementPC(ctx, 1);
                break;
            case JitMode.IoPortIndirect:
                il.Emit(OpCodes.Ldarg_0);      // port = reg (the (C) form; no operand byte)
                il.Emit(OpCodes.Ldfld, RegField(reg));
                il.Emit(OpCodes.Conv_U4);
                il.Emit(OpCodes.Stloc, ctx.AddrLocal);
                break;
            default:
                throw new EmulationException($"port: no arm for mode {d.Mode} (opcode 0x{d.Opcode:X2})");
        }

        EmitChargeOneCycle(ctx);               // the Io access cycle (the interpreter's ReadIo/WriteIo)

        if (d.Ops[0].Kind == "PortIn")
        {
            // reg = (byte)ioBus.Read8(port)   — the SECOND IAddressSpace, NEVER LoadByteFromBus.
            il.Emit(OpCodes.Ldarg_0);          // cpu (for the Stfld)
            il.Emit(OpCodes.Ldarg_S, ArgIoBus);
            il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
            il.Emit(OpCodes.Callvirt, MRead);  // ioBus.Read8(port) -> byte (int)
            il.Emit(OpCodes.Conv_U1);
            il.Emit(OpCodes.Stfld, RegField(reg));
        }
        else // PortOut
        {
            // ioBus.Write8(port, reg)         — the SECOND IAddressSpace, NEVER EmitStoreByte.
            il.Emit(OpCodes.Ldarg_S, ArgIoBus);
            il.Emit(OpCodes.Ldloc, ctx.AddrLocal);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, RegField(reg));
            il.Emit(OpCodes.Conv_U1);
            il.Emit(OpCodes.Callvirt, MWrite); // ioBus.Write8(port, value)
        }
    }
}
