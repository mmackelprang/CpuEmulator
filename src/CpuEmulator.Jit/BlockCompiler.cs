using System.Reflection;
using System.Reflection.Emit;
using CpuEmulator.Core;
using CpuEmulator.Core.Jit;
using CpuEmulator.Cpus.Mos6502;

namespace CpuEmulator.Jit;

/// <summary>The CPU-agnostic block compiler: walks the generated <see cref="OpcodeDescriptor"/>
/// table from an entry PC into a straight-line run, then emits one <see cref="DynamicMethod"/>
/// per block (the descriptor-interpreter-that-emits-IL — the Pydgin "walk the IR → emit" arm).
/// The interpreter is the oracle: every emitted instruction mirrors the proven CpuEmitter body
/// one-for-one, and any NeedsFallback opcode emits a callout to the inner interpreter's Step.</summary>
internal sealed partial class BlockCompiler
{
    private readonly Mos6502Cpu _cpu;
    private readonly AddressSpace _bus;
    private readonly Fastmem _fastmem;
    private readonly JitOptions _opts;
    internal int CompileCount { get; private set; }   // test seam (Block_cache_hits pin)

    // Baked field/method handles — resolved once, reused across every block.
    private static readonly FieldInfo FA = typeof(Mos6502Cpu).GetField("A")!;
    private static readonly FieldInfo FX = typeof(Mos6502Cpu).GetField("X")!;
    private static readonly FieldInfo FY = typeof(Mos6502Cpu).GetField("Y")!;
    private static readonly FieldInfo FS = typeof(Mos6502Cpu).GetField("S")!;
    private static readonly FieldInfo FP = typeof(Mos6502Cpu).GetField("P")!;
    private static readonly FieldInfo FPC = typeof(Mos6502Cpu).GetField("PC")!;

    private static readonly MethodInfo MRead = typeof(AddressSpace).GetMethod("Read8")!;
    private static readonly MethodInfo MWrite = typeof(AddressSpace).GetMethod("Write8")!;
    private static readonly MethodInfo MAdvance =
        typeof(Mos6502Cpu).GetMethod("AdvanceCycles", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo MStep = typeof(Mos6502Cpu).GetMethod("Step")!;
    private static readonly MethodInfo MCycleCount = typeof(Mos6502Cpu).GetProperty("CycleCount")!.GetGetMethod()!;
    // Pre-positioned for the M2-ii emitted block-entry interrupt check. M2-i does NOT emit an
    // entry check — the dispatcher (JittedCpu.Run) checks InterruptPending before each block,
    // which is authoritative without chaining (plan Task 4 note). This handle is intentionally
    // unused until chaining lands; it is NOT a wired-up emitted check.
    private static readonly MethodInfo MInterruptPending =
        typeof(Mos6502Cpu).GetProperty("InterruptPending")!.GetGetMethod()!;

    private static readonly MethodInfo MPageBacking = typeof(Fastmem).GetProperty("PageBacking")!.GetGetMethod()!;
    private static readonly MethodInfo MPageOffset = typeof(Fastmem).GetProperty("PageOffset")!.GetGetMethod()!;
    private static readonly MethodInfo MPageWritable = typeof(Fastmem).GetProperty("PageWritable")!.GetGetMethod()!;
    private static readonly MethodInfo MDirtyMark = typeof(DirtyMap).GetMethod("Mark")!;

    public BlockCompiler(Mos6502Cpu cpu, AddressSpace bus, Fastmem fastmem, JitOptions opts)
        => (_cpu, _bus, _fastmem, _opts) = (cpu, bus, fastmem, opts);

    /// <summary>Decode from pc until an EndsBlock opcode or the block-length cap. Reads opcode
    /// bytes through the bus (a debugger-view decode; never executes). The discovered run is a
    /// list of (pc, descriptor) pairs.</summary>
    public System.Collections.Generic.List<(ushort Pc, OpcodeDescriptor D)> Discover(ushort pc)
    {
        var run = new System.Collections.Generic.List<(ushort, OpcodeDescriptor)>();
        for (int i = 0; i < _opts.BlockLengthCap; i++)
        {
            byte opcode = _bus.Read8(pc);
            OpcodeDescriptor d = Mos6502Cpu.JitDescriptors[opcode];
            run.Add((pc, d));
            if (d.EndsBlock) break;
            pc = unchecked((ushort)(pc + d.Length));
        }
        return run;
    }

    public CompiledBlock Compile(ushort entryPc)
    {
        CompileCount++;
        var run = Discover(entryPc);
        var spannedPages = PagesSpanned(run);
        var dm = new DynamicMethod(
            $"block_{entryPc:X4}", typeof(void),
            [typeof(Mos6502Cpu), typeof(AddressSpace), typeof(Fastmem),
             typeof(DirtyMap), typeof(long).MakeByRefType(), typeof(BlockExit).MakeByRefType()],
            typeof(BlockCompiler).Module, skipVisibility: true);   // reach the AdvanceCycles seam
        ILGenerator il = dm.GetILGenerator();
        var ctx = new EmitContext(il, spannedPages);

        foreach (var (pc, d) in run)
        {
            EmitInstruction(ctx, pc, d);
            if (!d.EndsBlock)
                EmitBudgetCheck(ctx, (ushort)(pc + d.Length));   // the cc_interrupt-style budget exit
        }
        EmitNormalExit(ctx);
        var del = (BlockDelegate)dm.CreateDelegate(typeof(BlockDelegate));
        return new CompiledBlock(entryPc, del, spannedPages);
    }

    private static System.Collections.Generic.HashSet<int> PagesSpanned(
        System.Collections.Generic.List<(ushort Pc, OpcodeDescriptor D)> run)
    {
        var pages = new System.Collections.Generic.HashSet<int>();
        foreach (var (pc, d) in run)
            for (int b = 0; b < d.Length; b++)
                pages.Add(((pc + b) & 0xFFFF) >> 8);
        return pages;
    }

    /// <summary>Emit one instruction. NeedsFallback rows emit a callout to inner.Step() (the
    /// safety valve). Each emit arm mirrors the proven CpuEmitter body.</summary>
    private void EmitInstruction(EmitContext ctx, ushort pc, OpcodeDescriptor d)
    {
        if (d.NeedsFallback) { EmitFallbackStep(ctx); return; }
        // Mirror the interpreter Step's opcode fetch EXACTLY: charge the opcode-fetch cycle FIRST
        // (the interpreter does `ReadBus(PC)` — which does `_cycles++` — then `PC++` in Step,
        // then `Execute` resolves operands). Charging the fetch cycle up-front (rather than as a
        // trailing per-arm charge) is load-bearing for Ground truth F(a): a mid-instruction MMIO
        // store must see a CycleCount that already counts the opcode fetch, so the device's view
        // is byte-identical to the interpreter's WriteBus ordering. The per-arm operand/access
        // charges follow, in order, after this.
        EmitChargeOneCycle(ctx);   // opcode-fetch cycle (was: trailing in each arm — moved up for GT-F(a))
        EmitIncrementPC(ctx, 1);
        // Reset the SMC "wrote page" marker before any instruction that might write RAM, so the
        // intra-block guard only trips on this instruction's own writable-RAM store.
        bool mayWriteRam = d.Class is JitOpClass.Store or JitOpClass.Rmw;
        if (mayWriteRam)
        {
            ctx.Il.Emit(OpCodes.Ldc_I4_M1);
            ctx.Il.Emit(OpCodes.Stloc, ctx.SmcPageLocal);
        }
        switch (d.Class)
        {
            case JitOpClass.Load: EmitLoad(ctx, d); break;
            case JitOpClass.Store: EmitStore(ctx, d); break;
            case JitOpClass.Register: EmitRegister(ctx, d); break;
            case JitOpClass.Alu: EmitAlu(ctx, d); break;
            case JitOpClass.Rmw: EmitRmw(ctx, d); break;
            case JitOpClass.Branch: EmitBranch(ctx, d); break;
            case JitOpClass.Jump: EmitJump(ctx, d); break;
            case JitOpClass.Jsr: EmitJsr(ctx, d); break;
            case JitOpClass.Rts: EmitRts(ctx, d); break;
            default:
                throw new EmulationException(
                    $"BlockCompiler has no emit arm for class '{d.Class}' (opcode 0x{d.Opcode:X2}); "
                  + "a JIT bug — the descriptor said it was emittable but no arm exists.");
        }
        // Intra-block SMC guard (Task-5 hand-off note #2 + controller directive #2): if this
        // store/RMW wrote a byte on one of THIS block's own pages, the compiled IL ahead of PC may
        // now be stale — end the block (exit Normal, PC already at the next instruction) so the
        // dispatcher's InvalidateIfDirty flushes the cache and the next GetOrCompile re-decodes the
        // modified bytes. Conservative + runtime-precise: only fires when the actual written page
        // is one the block occupies.
        if (mayWriteRam)
            EmitSmcGuard(ctx);
    }

    /// <summary>Emit the intra-block self-modifying-code guard: if <c>ctx.SmcPageLocal</c> (the page
    /// a writable-RAM store just wrote, or -1) is one of the block's own SpannedPages, set
    /// exit=Normal and return — ending the block so the next dispatch re-decodes the modified bytes.
    /// PC is already at the next instruction (operand reads advanced it), so no PC fix-up is needed.
    /// Pages are baked as constants from the block's static page span.</summary>
    private static void EmitSmcGuard(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        Label noSmc = il.DefineLabel();
        Label endBlock = il.DefineLabel();
        // if (SmcPageLocal == P) goto endBlock; for each spanned page P.
        foreach (int page in ctx.SpannedPages)
        {
            il.Emit(OpCodes.Ldloc, ctx.SmcPageLocal);
            il.Emit(OpCodes.Ldc_I4, page);
            il.Emit(OpCodes.Beq, endBlock);
        }
        il.Emit(OpCodes.Br, noSmc);
        il.MarkLabel(endBlock);
        EmitNormalExit(ctx);            // exit = Normal; ret (PC already at the next instruction)
        il.MarkLabel(noSmc);
    }

    // ── Cycle bookkeeping (both counters move together: budget-=1 AND cpu._cycles+=1) ──────
    private static void EmitChargeOneCycle(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        // cpu.AdvanceCycles(1)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Call, MAdvance);
        // budget -= 1
        il.Emit(OpCodes.Ldarg_S, (byte)4);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldind_I8);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stind_I8);
    }

    // ── PC helpers ─────────────────────────────────────────────────────────────────────────
    /// <summary>Push the current PC (as uint) onto the stack.</summary>
    private static void EmitLoadPC(EmitContext ctx)
    {
        ctx.Il.Emit(OpCodes.Ldarg_0);
        ctx.Il.Emit(OpCodes.Ldfld, FPC);   // ushort -> I4 (zero-extended)
    }

    /// <summary>PC = (ushort)(PC + n).</summary>
    private static void EmitIncrementPC(EmitContext ctx, int n)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, FPC);
        il.Emit(OpCodes.Ldc_I4, n);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Stfld, FPC);
    }

    /// <summary>Read the byte at the current PC (operand/code fetch), charging 1 cycle; pushes
    /// the byte (as int). Code/operand bytes live in RAM/ROM, so the fastmem-or-bus branch is
    /// correct and charges the same cycle ReadBus(PC) would.</summary>
    private void EmitReadAtPC(EmitContext ctx)
    {
        EmitLoadPC(ctx);                 // push (uint)PC
        ctx.Il.Emit(OpCodes.Conv_U4);
        LoadByteFromBus(ctx);            // pops addr, pushes byte; charges 1 cycle
    }

    // ── Read a byte at the address on the stack (the fastmem/bus branch — Ground truth G) ────
    /// <summary>Emit a byte read of the (uint) address on the IL stack. Charges 1 cycle, then
    /// branches: fastmem PageBacking[page] null -> bus.Read8 (MMIO/unmapped); else direct array
    /// load <c>backing[PageOffset[page] + (addr &amp; 0xFF)]</c>. Leaves the byte (as int) on the
    /// stack. Under DisableFastmem every PageBacking[p] is null, so the bus arm always runs.</summary>
    private void LoadByteFromBus(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Stloc, ctx.EaLocal);   // ea = address
        EmitChargeOneCycle(ctx);               // charge BEFORE the access (matches ReadBus)

        Label mmio = il.DefineLabel(), done = il.DefineLabel();

        // backing = fastmem.PageBacking[ea >> 8]
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, MPageBacking);     // byte[]?[]
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shr_Un);                     // page = ea >> 8
        il.Emit(OpCodes.Ldelem_Ref);                 // backing (byte[] or null)
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brfalse, mmio);              // null -> MMIO

        // direct: backing[PageOffset[page] + (ea & 0xFF)]
        // stack: backing
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, MPageOffset);      // int[]
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shr_Un);                     // page
        il.Emit(OpCodes.Ldelem_I4);                  // PageOffset[page]
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Ldc_I4, 0xFF);
        il.Emit(OpCodes.And);                        // ea & 0xFF
        il.Emit(OpCodes.Add);                        // index
        il.Emit(OpCodes.Ldelem_U1);                  // backing[index] -> byte (int)
        il.Emit(OpCodes.Br, done);

        il.MarkLabel(mmio);
        il.Emit(OpCodes.Pop);                        // drop the null backing
        il.Emit(OpCodes.Ldarg_1);                    // bus
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Callvirt, MRead);            // bus.Read8(ea) -> byte (int)
        il.MarkLabel(done);
    }

    /// <summary>Emit a byte store: writes the (int) value on the stack to the (uint) address
    /// below it on the stack (stack order: ..., address, value). Charges 1 cycle BEFORE the
    /// access (Ground truth F (a): the device sees a write-cycle-inclusive CycleCount). Branches:
    /// PageBacking[page] null -> bus.Write8 (MMIO/unmapped/ROM-as-bus); else, for a WRITABLE
    /// RAM page, direct array store + dirty.Mark(page); a non-writable (ROM) backing page drops
    /// the write (the interpreter drops it too). Under DisableFastmem PageBacking[p] is null, so
    /// every store routes through the bus (+ dirty.Mark for writable pages, so SMC still works).</summary>
    private void EmitStoreByte(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        // stack: address, value  -> stash both (value on top)
        il.Emit(OpCodes.Stloc, ctx.DataLocal);   // value
        il.Emit(OpCodes.Stloc, ctx.EaLocal);     // address
        EmitChargeOneCycle(ctx);                 // charge BEFORE the access (MMIO ordering)

        Label mmio = il.DefineLabel(), drop = il.DefineLabel(), done = il.DefineLabel();

        // backing = fastmem.PageBacking[page]
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, MPageBacking);
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shr_Un);
        il.Emit(OpCodes.Ldelem_Ref);             // backing or null
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brfalse, mmio);          // null -> bus callout

        // writable? fastmem.PageWritable[page]
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, MPageWritable);
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shr_Un);
        il.Emit(OpCodes.Ldelem_U1);              // PageWritable[page] (bool as int)
        il.Emit(OpCodes.Brfalse, drop);          // ROM page -> drop the write (stack: backing)

        // direct RAM store: backing[PageOffset[page] + (ea & 0xFF)] = value
        // stack: backing
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, MPageOffset);
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shr_Un);
        il.Emit(OpCodes.Ldelem_I4);
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Ldc_I4, 0xFF);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Add);                    // index
        il.Emit(OpCodes.Ldloc, ctx.DataLocal);
        il.Emit(OpCodes.Conv_U1);
        il.Emit(OpCodes.Stelem_I1);              // backing[index] = (byte)value
        // dirty.Mark(page)
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shr_Un);
        il.Emit(OpCodes.Callvirt, MDirtyMark);
        // record the written page for the intra-block SMC guard (EmitSmcGuard, after the instr).
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Ldc_I4_8);
        il.Emit(OpCodes.Shr_Un);
        il.Emit(OpCodes.Stloc, ctx.SmcPageLocal);
        il.Emit(OpCodes.Br, done);

        il.MarkLabel(drop);
        il.Emit(OpCodes.Pop);                    // drop backing; ROM write is a no-op
        il.Emit(OpCodes.Br, done);

        il.MarkLabel(mmio);
        il.Emit(OpCodes.Pop);                    // drop the null backing
        il.Emit(OpCodes.Ldarg_1);               // bus
        il.Emit(OpCodes.Ldloc, ctx.EaLocal);
        il.Emit(OpCodes.Ldloc, ctx.DataLocal);
        il.Emit(OpCodes.Conv_U1);
        il.Emit(OpCodes.Callvirt, MWrite);       // bus.Write8(ea, value)
        il.MarkLabel(done);
    }

    // ── SetNZ: P = (P & 0x7D) | (src==0 ? 2 : 0) | (src & 0x80) ──────────────────────────────
    /// <summary>Set the N and Z flags from the (int) source byte on the stack (consumes it).</summary>
    private static void EmitSetNZFromStack(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Stloc, ctx.DataLocal);   // src
        il.Emit(OpCodes.Ldarg_0);                // cpu (for the final Stfld)
        // (P & 0x7D)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, FP);
        il.Emit(OpCodes.Ldc_I4, 0x7D);
        il.Emit(OpCodes.And);
        // | (src==0 ? 2 : 0)
        Label nz = il.DefineLabel(), zdone = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, ctx.DataLocal);
        il.Emit(OpCodes.Ldc_I4, 0xFF);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Brtrue, nz);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Br, zdone);
        il.MarkLabel(nz);
        il.Emit(OpCodes.Ldc_I4_0);
        il.MarkLabel(zdone);
        il.Emit(OpCodes.Or);
        // | (src & 0x80)
        il.Emit(OpCodes.Ldloc, ctx.DataLocal);
        il.Emit(OpCodes.Ldc_I4, 0x80);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Conv_U1);
        il.Emit(OpCodes.Stfld, FP);
    }

    private static FieldInfo RegField(byte regIndex) => regIndex switch
    {
        0 => FA, 1 => FX, 2 => FY, 3 => FS,
        _ => throw new EmulationException($"unknown register index {regIndex}"),
    };

    // (Emit arms continue in BlockCompiler.Emit.cs)

    private static void EmitNormalExit(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        il.Emit(OpCodes.Ldarg_S, (byte)5);                 // out BlockExit exit
        il.Emit(OpCodes.Ldc_I4, (int)BlockExit.Normal);
        il.Emit(OpCodes.Stind_I4);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>After an instruction, if budget &lt;= 0 set PC = nextPc and exit Budget; else
    /// continue the block.</summary>
    private static void EmitBudgetCheck(EmitContext ctx, ushort nextPc)
    {
        ILGenerator il = ctx.Il;
        Label keepGoing = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_S, (byte)4);
        il.Emit(OpCodes.Ldind_I8);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Bgt, keepGoing);                   // budget > 0 -> continue
        // budget exhausted: PC = nextPc; exit = Budget; ret
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, (int)nextPc);
        il.Emit(OpCodes.Conv_U2);
        il.Emit(OpCodes.Stfld, FPC);
        il.Emit(OpCodes.Ldarg_S, (byte)5);
        il.Emit(OpCodes.Ldc_I4, (int)BlockExit.Budget);
        il.Emit(OpCodes.Stind_I4);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(keepGoing);
    }

    /// <summary>The interpreter-Step fallback for a NeedsFallback opcode (ADC/SBC/BRK/RTI/
    /// undefined). Runs one authentic interpreter Step (which charges its own cycles via
    /// ReadBus/WriteBus and advances PC), subtracts the consumed cycles from budget, then exits
    /// the block (the post-Step PC is dynamic — the block cannot statically continue).</summary>
    private static void EmitFallbackStep(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        // long before = cpu.CycleCount;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, MCycleCount);
        il.Emit(OpCodes.Stloc, ctx.TmpLong);
        // cpu.Step();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, MStep);
        // budget -= (cpu.CycleCount - before);
        il.Emit(OpCodes.Ldarg_S, (byte)4);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldind_I8);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, MCycleCount);
        il.Emit(OpCodes.Ldloc, ctx.TmpLong);
        il.Emit(OpCodes.Sub);                // consumed
        il.Emit(OpCodes.Sub);                // budget - consumed
        il.Emit(OpCodes.Stind_I8);
        EmitNormalExit(ctx);                 // PC already set by Step
    }
}
