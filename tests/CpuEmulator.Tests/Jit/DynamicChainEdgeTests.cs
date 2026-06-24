using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Cpus.M8086;
using CpuEmulator.Jit;

namespace CpuEmulator.Tests.Jit;

/// <summary>ADR 0023 D0 — the <c>EmitDynamicChainOrExit</c> helper in isolation (no arm converted
/// yet). The helper is a near-clone of <c>EmitChainOrExit</c> whose ONLY difference is that it reads
/// the chain target from the RUNTIME <c>cpu.PC</c> field (the value a dynamic arm — RTS/JMP-(ind)/RET
/// — just stored) instead of a baked compile-time constant. These tests drive the genuine emitted IL
/// via the <c>CompileDynamicChainProbe</c> seam and assert: (1) with all gates clear it invokes the
/// ChainDispatch callback with the live PC (chains to a runtime PC); (2) when any of the three gates
/// (budget &lt;= 0 / dirty.Any / InterruptPending) fires it degrades to a plain EmitNormalExit
/// round-trip (the callback is NOT invoked); (3) the 8086 fold projects the live IP to the linear
/// (CS&lt;&lt;4)+IP block key the dispatcher's ProjectBlockKey computes.</summary>
public class DynamicChainEdgeTests
{
    private static AddressSpace NewRamSpace()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        return space;
    }

    private static BlockCompiler<Mos6502Cpu> NewCompiler(AddressSpace space, Mos6502Cpu cpu, JitOptions? options = null)
    {
        var opts = options ?? new JitOptions();
        return new BlockCompiler<Mos6502Cpu>(cpu, Mos6502Cpu.JitTarget, space, new Fastmem(space, opts), opts);
    }

    /// <summary>Invoke a probe delegate with a recording ChainDispatch and return (chainCalled,
    /// targetSeen, exit). The dirty map is sized for a 16-bit address space (256 pages).</summary>
    private static (bool Called, uint Target, BlockExit Exit) InvokeProbe<TCpu>(
        BlockDelegate<TCpu> probe, TCpu cpu, AddressSpace space, JitOptions opts,
        long budget = 100, bool markDirty = false) where TCpu : class
    {
        var dirty = new DirtyMap(256);
        if (markDirty) dirty.Mark(0x02);
        bool called = false;
        uint target = 0xFFFFFFFF;
        ChainDispatch chain = (uint t, ref long b, out BlockExit e) =>
        {
            called = true; target = t; e = BlockExit.Normal;
        };
        probe(cpu, space, new Fastmem(space, opts), dirty, chain, ref budget, out BlockExit exit, space);
        return (called, target, exit);
    }

    // ── (1) the chain path: all gates clear → ChainDispatch invoked with the runtime PC ──────────
    [Fact]
    public void Dynamic_edge_chains_to_the_runtime_PC()
    {
        var space = NewRamSpace();
        var cpu = new Mos6502Cpu(space) { PC = 0x0444 };   // the "runtime" successor an arm would have set
        var compiler = NewCompiler(space, cpu);
        var probe = compiler.CompileDynamicChainProbe();

        var (called, target, exit) = InvokeProbe(probe, cpu, space, new JitOptions());

        Assert.True(called, "all gates clear — the dynamic edge must invoke the chain callback");
        Assert.Equal(0x0444u, target);                     // chained to the LIVE PC, not a constant
        Assert.Equal(BlockExit.Normal, exit);
    }

    [Fact]
    public void Dynamic_edge_reads_the_PC_at_run_time_not_compile_time()
    {
        // The SAME compiled probe chains to whatever PC is live at invoke — proving the target is a
        // runtime field read, not baked. Two invocations, two different PCs, two different targets.
        var space = NewRamSpace();
        var cpu = new Mos6502Cpu(space);
        var compiler = NewCompiler(space, cpu);
        var probe = compiler.CompileDynamicChainProbe();

        cpu.PC = 0x0100;
        var a = InvokeProbe(probe, cpu, space, new JitOptions());
        cpu.PC = 0x0200;
        var b = InvokeProbe(probe, cpu, space, new JitOptions());

        Assert.Equal(0x0100u, a.Target);
        Assert.Equal(0x0200u, b.Target);
    }

    // ── (2) each gate fires → degrade to EmitNormalExit (no chain) ───────────────────────────────
    [Fact]
    public void Budget_exhausted_gate_routes_to_the_dispatcher()
    {
        var space = NewRamSpace();
        var cpu = new Mos6502Cpu(space) { PC = 0x0444 };
        var compiler = NewCompiler(space, cpu);
        var probe = compiler.CompileDynamicChainProbe();

        var (called, _, exit) = InvokeProbe(probe, cpu, space, new JitOptions(), budget: 0);

        Assert.False(called, "budget <= 0 — the dynamic edge must NOT chain");
        Assert.Equal(BlockExit.Normal, exit);              // EmitNormalExit
    }

    [Fact]
    public void Dirty_Any_gate_routes_to_the_dispatcher()
    {
        var space = NewRamSpace();
        var cpu = new Mos6502Cpu(space) { PC = 0x0444 };
        var compiler = NewCompiler(space, cpu);
        var probe = compiler.CompileDynamicChainProbe();

        var (called, _, _) = InvokeProbe(probe, cpu, space, new JitOptions(), markDirty: true);

        Assert.False(called, "dirty.Any (an SMC store happened) — the dynamic edge must NOT chain");
    }

    [Fact]
    public void Interrupt_pending_gate_routes_to_the_dispatcher()
    {
        var space = NewRamSpace();
        var cpu = new Mos6502Cpu(space) { PC = 0x0444 };
        cpu.SetIrqLine(true);                              // raise an IRQ (P.I is clear by default)
        Assert.True(cpu.InterruptPending);                // precondition: the gate's predicate is live
        var compiler = NewCompiler(space, cpu);
        var probe = compiler.CompileDynamicChainProbe();

        var (called, _, _) = InvokeProbe(probe, cpu, space, new JitOptions());

        Assert.False(called, "InterruptPending — the dynamic edge must NOT chain (the irq is sampled at the edge)");
    }

    // ── (3) the 8086 segmented fold: live IP → linear (CS<<4)+IP block key ────────────────────────
    [Fact]
    public void M8086_fold_projects_the_live_IP_to_the_linear_block_key()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        space.MapMemory(0x00000, new byte[0x100000], writable: true);
        var opts = new JitOptions();
        var cpu = new M8086Cpu(space);
        cpu.SetRegister("CS", 0x1000);                     // CS<<4 = 0x10000
        cpu.SetRegister("IP", 0x0444);                     // the runtime successor IP
        var compiler = new BlockCompiler<M8086Cpu>(cpu, M8086Cpu.JitTarget, space, new Fastmem(space, opts), opts);
        compiler.PrimeM8086CodePhysBaseForTest();          // bake CS<<4 from the live CS
        Assert.Equal(0x10000u, compiler.M8086CodePhysBaseForTest);

        var probe = compiler.CompileDynamicChainProbe(foldM8086CodePhysBase: true);
        var (called, target, _) = InvokeProbe(probe, cpu, space, opts);

        Assert.True(called);
        // the dispatcher's ProjectBlockKey for this CS:IP — the un-fakeable cross-check
        uint expected = M8086Cpu.JitTarget.ProjectBlockKey(cpu);
        Assert.Equal(expected, target);                    // (0x10000 + 0x0444) & 0xFFFFF == 0x10444
        Assert.Equal(0x10444u, target);
    }
}
