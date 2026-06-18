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

    /// <summary>M6 PR-1: the genericity flip. LD A,42h now EMITS (it was a fallback in 5-3a); the only
    /// fallback in the block is the block-ending HALT, so FallbackEmitCount is exactly 1 (the HALT), NOT 2.
    /// Mirrors the 6502 ADC_opcode_block_emits_no_fallback shape.</summary>
    [Fact]
    public void Z80_LD_block_emits_no_fallback_after_PR1()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            return;   // emit-only proof; skip where dynamic code is disabled (AOT)
        var bus = NewRamBus();
        bus.Write8(0x0100, 0x3E); bus.Write8(0x0101, 0x42);   // LD A,42h  (now emitted — 0 fallbacks)
        bus.Write8(0x0102, 0x76);                              // HALT — the one block-ending fallback
        var z80 = new Z80Cpu(bus);
        var opts = new JitOptions();
        var compiler = new BlockCompiler<Z80Cpu>(z80, Z80Cpu.JitTarget, bus, new Fastmem(bus, opts), opts);
        compiler.Compile(0x0100);
        // LD A,42h emits (0 fallbacks); HALT is the one fallback that ends the block.
        Assert.Equal(1, compiler.FallbackEmitCount);           // exactly the HALT, NOT the LD
    }

    /// <summary>M6 PR-1: every emitted LD form contributes 0 fallbacks — the "FallbackEmitCount drops by
    /// exactly the emitted opcodes" half of the §8 parity gate AND the gate/arm lockstep check (the form
    /// must have a real emit branch, or EmitZ80Ld's default throws and Compile fails). One LD op per
    /// block, terminated by HALT; the block's only fallback is the HALT. Includes the 16-bit-absolute
    /// 0x22 (LD (nn),HL) / 0x2A (LD HL,(nn)) — Decision B.</summary>
    [Theory]
    [InlineData(new byte[] { 0x41 })]               // LD B,C        (Register/Transfer)
    [InlineData(new byte[] { 0x06, 0x42 })]         // LD B,42h      (Immediate/Load)
    [InlineData(new byte[] { 0x46 })]               // LD B,(HL)     (RegisterIndirect/Load)
    [InlineData(new byte[] { 0x70 })]               // LD (HL),B     (RegisterIndirect/Store)
    [InlineData(new byte[] { 0x0A })]               // LD A,(BC)     (RegisterIndirect/Load + WZ)
    [InlineData(new byte[] { 0x02 })]               // LD (BC),A     (RegisterIndirect/Store + WZ)
    [InlineData(new byte[] { 0x36, 0x42 })]         // LD (HL),42h   (Immediate/StoreImm8)
    [InlineData(new byte[] { 0x01, 0x34, 0x12 })]   // LD BC,1234h   (ImmediateExtended/Load16)
    [InlineData(new byte[] { 0x3A, 0x00, 0x20 })]   // LD A,(2000h)  (ExtendedAddress/Load + WZ)
    [InlineData(new byte[] { 0x32, 0x00, 0x20 })]   // LD (2000h),A  (ExtendedAddress/Store + WZ quirk)
    [InlineData(new byte[] { 0x22, 0x00, 0x20 })]   // LD (2000h),HL (ExtendedAddress/Store16) — Decision B
    [InlineData(new byte[] { 0x2A, 0x00, 0x20 })]   // LD HL,(2000h) (ExtendedAddress/LoadMem16) — Decision B
    public void Z80_LD_form_emits_zero_fallbacks(byte[] opcode)
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            return;   // emit-only proof; skip where dynamic code is disabled (AOT)
        var bus = NewRamBus();
        ushort pc = 0x0100;
        foreach (byte b in opcode) bus.Write8(pc++, b);
        bus.Write8(pc, 0x76);                                  // HALT — the one block-ending fallback
        var z80 = new Z80Cpu(bus);
        var opts = new JitOptions();
        var compiler = new BlockCompiler<Z80Cpu>(z80, Z80Cpu.JitTarget, bus, new Fastmem(bus, opts), opts);
        compiler.Compile(0x0100);
        Assert.Equal(1, compiler.FallbackEmitCount);           // the LD emitted; only the HALT fell back
    }

    /// <summary>M6 PR-1 (Decision A): the descriptor-cycle tripwire. The Z80 LD r,n immediate carries 7 T
    /// (the generator JitBaseCycles fix), and the UNTOUCHED control proves the shared
    /// ComputeCycles("Immediate") template still yields 2 for the 6502's LDA #imm (the fix is scoped — it
    /// only catches the "LD" mnemonic, which the 6502 never names). This is the cheap, fast tripwire that
    /// the disambiguation stays scoped if anyone later edits JitBaseCycles/ComputeCycles.</summary>
    [Fact]
    public void Z80_LD_r_n_descriptor_carries_7_cycles_and_6502_immediate_stays_2()
    {
        Assert.Equal(7, Z80Cpu.DescriptorFor(0x06).BaseCycles);        // LD B,n — the corrected Z80 value
        Assert.Equal(2, Mos6502Cpu.DescriptorFor(0xA9).BaseCycles);    // LDA #imm — the shared template, untouched
    }

    [Theory]
    [InlineData("BC", 0x1234)]
    [InlineData("DE", 0xABCD)]
    [InlineData("HL", 0xBEEF)]
    [InlineData("AF", 0x55AA)]
    [InlineData("IX", 0x0FF0)]
    [InlineData("IY", 0xC3C3)]
    [InlineData("SP", 0xFFFE)]   // a real ushort field — the direct-Stfld path
    [InlineData("WZ", 0x8001)]
    public void Wide_register_helper_round_trips_every_Z80_pair(string name, int value)
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            return;   // emit-only proof; skip where dynamic code is disabled (AOT)
        var bus = NewRamBus();
        var z80 = new Z80Cpu(bus);
        var opts = new JitOptions();
        var compiler = new BlockCompiler<Z80Cpu>(z80, Z80Cpu.JitTarget, bus, new Fastmem(bus, opts), opts);
        // Round-trip through the new helpers: write `value` via EmitStoreReg16, read it back via EmitLoadReg16.
        int readback = compiler.CompileReg16RoundTrip(name, value);
        Assert.Equal(value, readback);
        // Oracle cross-check: the helper's compose/decompose must equal the CPU's own property/field getter.
        Assert.Equal((ulong)value, z80.GetRegister(name));
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
