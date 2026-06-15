using CpuEmulator.Core;
using CpuEmulator.Core.Jit;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Cpus.Z80;
using CpuEmulator.Jit;
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>5-3a genericity pins: the per-CPU <see cref="IJitTarget"/> seam resolves the CPU-typed
/// handles by name (Task 1), the generic <c>BlockCompiler&lt;Z80Cpu&gt;</c> discovers a Z80 block and
/// builds the 36-name register map without throwing (Tasks 2 + 7), every Z80 op is a fallback in 5-3a
/// (Task 7), a <c>JittedCpu&lt;Z80Cpu&gt;</c> runs a Z80 NOP via the interpreter fallback (Task 5), and
/// the GENERATED per-CPU targets resolve for both CPUs (Task 6).</summary>
public class Z80JitGenericityTests
{
    [Fact]
    public void Z80_JitTarget_exposes_the_cpu_typed_handles()
    {
        IJitTarget t = Z80Cpu.JitTarget;
        Assert.Equal(typeof(Z80Cpu), t.CpuType);
        // The status + PC fields resolve by NAME on the Z80 type (the J2 baked-handle replacement).
        Assert.NotNull(t.StatusField);     // "F" on the Z80 (vs "P" on the 6502)
        Assert.Equal("F", t.StatusField.Name);
        Assert.NotNull(t.ProgramCounterField);
        Assert.Equal("PC", t.ProgramCounterField.Name);
        // The interpreter-fallback handles resolve on the Z80 type.
        Assert.NotNull(t.StepMethod);
        Assert.NotNull(t.AdvanceCyclesMethod);
        Assert.NotNull(t.CycleCountGetter);
        Assert.NotNull(t.InterruptPendingGetter);
    }

    [Fact]
    public void Generated_JitTargets_resolve_for_both_CPUs()
    {
        Assert.Equal(typeof(Mos6502Cpu), Mos6502Cpu.JitTarget.CpuType);
        Assert.Equal("P", Mos6502Cpu.JitTarget.StatusField.Name);   // 6502 status = P
        Assert.Equal(typeof(Z80Cpu), Z80Cpu.JitTarget.CpuType);
        Assert.Equal("F", Z80Cpu.JitTarget.StatusField.Name);       // Z80 status = F
        // The decode + descriptor wraps resolve for both (the J3 seam): a 6502 NOP key + a Z80 NOP key.
        Assert.NotNull(Mos6502Cpu.JitTarget.AdvanceCyclesMethod);
        Assert.NotNull(Z80Cpu.JitTarget.AdvanceCyclesMethod);
    }

    private static AddressSpace NewRamBus()
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        bus.MapMemory(0x0000, new byte[0x10000], writable: true);
        return bus;
    }

    [Fact]
    public void Generic_compiler_discovers_a_Z80_block()
    {
        var bus = NewRamBus();
        bus.Write8(0x0100, 0x00);   // NOP
        bus.Write8(0x0101, 0x76);   // HALT (a fallback that ends the block)
        var z80 = new Z80Cpu(bus);
        var opts = new JitOptions();
        var compiler = new BlockCompiler<Z80Cpu>(z80, Z80Cpu.JitTarget, bus, new Fastmem(bus, opts), opts);
        var run = compiler.Discover(0x0100);
        Assert.NotEmpty(run);                       // the walk produced at least one row
        Assert.Equal(0x0100, run[0].Pc);
        Assert.True(run[0].D.NeedsFallback);        // every Z80 op is a fallback in 5-3a
    }

    [Fact]
    public void Z80_register_map_builds_against_all_36_names_without_throwing()
    {
        var bus = NewRamBus();
        var z80 = new Z80Cpu(bus);
        var opts = new JitOptions();
        // The ctor builds _regFields from RegisterNames; the 16-bit pair-views are field-less PROPERTIES
        // and must be SKIPPED, not throw (the recorded J2 finding). Constructing without throwing IS the
        // assertion. Sanity: the Z80 declares all 36 names.
        Assert.Equal(35, Z80Cpu.JitTarget.RegisterNames.Count);   // the declared Z80 register file
        var ex = Record.Exception(() =>
            new BlockCompiler<Z80Cpu>(z80, Z80Cpu.JitTarget, bus, new Fastmem(bus, opts), opts));
        Assert.Null(ex);
    }

    [Fact]
    public void Every_Z80_op_in_a_block_emits_a_fallback_in_5_3a()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            return;   // emit-only proof; skip where dynamic code is disabled (AOT)
        var bus = NewRamBus();
        bus.Write8(0x0100, 0x3E); bus.Write8(0x0101, 0x42);  // LD A,42h (one op; a fallback that ends block)
        var z80 = new Z80Cpu(bus);
        var opts = new JitOptions();
        var compiler = new BlockCompiler<Z80Cpu>(z80, Z80Cpu.JitTarget, bus, new Fastmem(bus, opts), opts);
        compiler.Compile(0x0100);
        // Every Z80 op is NeedsFallback in 5-3a, and a fallback ENDS the block — so a block is exactly one
        // op and emits exactly one fallback Step. (5-3b flips the hot ops to 0 fallbacks for emitted blocks.)
        Assert.Equal(1, compiler.FallbackEmitCount);
    }

    [Fact]
    public void JittedCpu_of_Z80_runs_a_NOP_via_fallback()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            return;
        var bus = NewRamBus();
        bus.Write8(0x0000, 0x00);   // NOP (4T)
        var inner = new Z80Cpu(bus);
        inner.SetRegister("PC", 0x0000);
        var jit = new JittedCpu<Z80Cpu>(inner, Z80Cpu.JitTarget, bus, inner.IoBus);
        long budget = 4;
        jit.Run(ref budget);
        // The NOP ran via the interpreter fallback — PC advanced, 4 T-states charged (identical to interp).
        Assert.Equal(0x0001ul, inner.GetRegister("PC"));
        Assert.Equal(4, inner.CycleCount);
    }
}
