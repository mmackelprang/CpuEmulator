using CpuEmulator.Core;
using CpuEmulator.Core.Jit;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;

namespace CpuEmulator.Tests.Jit;

/// <summary>Task 2: block discovery, per-block DynamicMethod compilation, the load-class emit
/// core, the budget exit, and the PC-keyed cache. The interpreter is the oracle — every JIT
/// run is diffed against a fresh interpreter run of the same program (state + CycleCount).</summary>
public class BlockCompilerTests
{
    // ── Fixture: a 16-bit full-RAM bus + a program poked at $0200 ─────────────────────────────
    private static AddressSpace NewRamSpace()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        return space;
    }

    private static void Poke(AddressSpace space, ushort at, params byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
            space.Write8((uint)(at + i), bytes[i]);
    }

    /// <summary>Build a fresh interpreter + a JIT-wrapped interpreter over identical programs,
    /// run the given budget through each, and assert the JIT matches the interpreter on the
    /// public registers, the cycle count, and the RAM image. Returns the JittedCpu for cache
    /// introspection.</summary>
    private static JittedCpu<Mos6502Cpu> AssertJitMatchesInterpreter(
        Action<AddressSpace> poke, ushort startPc, long budget, JitOptions? options = null)
    {
        // Interpreter oracle
        var refSpace = NewRamSpace();
        poke(refSpace);
        var refCpu = new Mos6502Cpu(refSpace);
        refCpu.PC = startPc; refCpu.S = 0xFD; refCpu.P = 0x24;
        long refBudget = budget;
        refCpu.Run(ref refBudget);

        // JIT under test
        var jitSpace = NewRamSpace();
        poke(jitSpace);
        var inner = new Mos6502Cpu(jitSpace);
        inner.PC = startPc; inner.S = 0xFD; inner.P = 0x24;
        var jit = new JittedCpu<Mos6502Cpu>(inner, Mos6502Cpu.JitTarget, jitSpace, options: options);
        long jitBudget = budget;
        jit.Run(ref jitBudget);

        Assert.Equal(refCpu.A, inner.A);
        Assert.Equal(refCpu.X, inner.X);
        Assert.Equal(refCpu.Y, inner.Y);
        Assert.Equal(refCpu.S, inner.S);
        Assert.Equal(refCpu.P, inner.P);
        Assert.Equal(refCpu.PC, inner.PC);
        Assert.Equal(refCpu.CycleCount, inner.CycleCount);
        Assert.Equal(budget - refBudget, budget - jitBudget); // same decrement
        for (uint a = 0; a <= 0xFFFF; a++)
            Assert.Equal(refSpace.Read8(a), jitSpace.Read8(a));
        return jit;
    }

    private static BlockCompiler<Mos6502Cpu> NewCompiler(AddressSpace space, JitOptions? options = null)
    {
        var opts = options ?? new JitOptions();
        var inner = new Mos6502Cpu(space);
        return new BlockCompiler<Mos6502Cpu>(inner, Mos6502Cpu.JitTarget, space, new Fastmem(space, opts), opts);
    }

    // ── Discovery ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Discover_stops_at_an_unconditional_jump()
    {
        var space = NewRamSpace();
        Poke(space, 0x0200, 0xA9, 0x01, 0xA9, 0x02, 0x4C, 0x00, 0x02); // LDA #1 / LDA #2 / JMP $0200
        var run = NewCompiler(space).Discover(0x0200);
        Assert.Equal(3, run.Count);
        Assert.Equal(JitOpClass.Jump, run[^1].D.Class);
    }

    [Fact]
    public void Discover_stops_at_a_branch()
    {
        var space = NewRamSpace();
        Poke(space, 0x0200, 0xA9, 0x01, 0xD0, 0xFC); // LDA #1 / BNE *-2
        var run = NewCompiler(space).Discover(0x0200);
        Assert.Equal(2, run.Count);
        Assert.Equal(JitOpClass.Branch, run[^1].D.Class);
    }

    [Fact]
    public void Discover_stops_at_a_fallback_opcode()
    {
        // BRK (0x00) is still a fallback (M2-ii recorded decision: BRK/RTI stay interpreter
        // fallbacks). A fallback opcode ends the block — the original intent of this pin, now
        // expressed with BRK since ADC is no longer a fallback (Task 5 emits it; see the sibling).
        var space = NewRamSpace();
        Poke(space, 0x0200, 0xA9, 0x01, 0x00, 0xA9, 0x02); // LDA #1 / BRK / LDA #2
        var run = NewCompiler(space).Discover(0x0200);
        Assert.Equal(2, run.Count);             // ends AFTER the BRK (fallback)
        Assert.True(run[^1].D.NeedsFallback);
        Assert.True(run[^1].D.EndsBlock);
    }

    [Fact]
    public void Discover_does_not_stop_at_an_ADC_now_that_it_emits()
    {
        // Task 5: ADC is emitted (both binary + decimal arms), so it is straight-line Alu-class and
        // no longer ends a block. LDA #1 / ADC #1 / LDA #2 / JMP-self now discovers a 4-instruction
        // block (M2-i discovered 2, ending after the ADC). The discovery pin flips.
        var space = NewRamSpace();
        Poke(space, 0x0200, 0xA9, 0x01, 0x69, 0x01, 0xA9, 0x02, 0x4C, 0x06, 0x02);
        var run = NewCompiler(space).Discover(0x0200);
        Assert.Equal(4, run.Count);             // LDA, ADC, LDA, JMP — the ADC does NOT end the block
        Assert.False(run[1].D.NeedsFallback);   // the ADC
        Assert.False(run[1].D.EndsBlock);
        Assert.Equal(JitOpClass.Jump, run[^1].D.Class);  // the block ends at the JMP
    }

    [Fact]
    public void Discover_caps_a_long_straight_run_at_the_block_length_cap()
    {
        var space = NewRamSpace();
        var nops = new byte[65];
        Array.Fill(nops, (byte)0xEA);            // 65 NOPs
        Poke(space, 0x0200, nops);
        var run = NewCompiler(space).Discover(0x0200);
        Assert.Equal(64, run.Count);             // capped at 64; the 65th is a new block
    }

    // ── The canonical emit pin ──────────────────────────────────────────────────────────────
    [Fact]
    public void Single_LDA_zp_block_matches_the_interpreter_on_state_and_cycles()
    {
        // A5 10 = LDA $10 with $10 = $42; then a JMP-to-self to end the block deterministically.
        var jit = AssertJitMatchesInterpreter(
            space =>
            {
                space.Write8(0x10, 0x42);
                Poke(space, 0x0200, 0xA5, 0x10, 0x4C, 0x02, 0x02); // LDA $10 / JMP $0202
            },
            startPc: 0x0200, budget: 3); // exactly the LDA's 3 cycles

        // The dedicated assertions from the plan, on the inner CPU directly.
        var space = NewRamSpace();
        space.Write8(0x10, 0x42);
        Poke(space, 0x0200, 0xA5, 0x10, 0x4C, 0x02, 0x02);
        var inner = new Mos6502Cpu(space);
        inner.PC = 0x0200; inner.S = 0xFD; inner.P = 0x24;
        var j = new JittedCpu<Mos6502Cpu>(inner, Mos6502Cpu.JitTarget, space);
        long budget = 3;
        j.Run(ref budget);
        Assert.Equal(0x42, inner.A);
        Assert.Equal(0, inner.P & 0x02);         // Z clear
        Assert.Equal(0, inner.P & 0x80);         // N clear
        Assert.Equal(0x0202, inner.PC);
        Assert.Equal(3, inner.CycleCount);
    }

    // ── Fastmem read of a RAM byte ──────────────────────────────────────────────────────────
    [Fact]
    public void Fastmem_read_of_a_RAM_byte_matches_the_interpreter()
    {
        AssertJitMatchesInterpreter(
            space =>
            {
                space.Write8(0x1234, 0x7F);
                Poke(space, 0x0200, 0xAD, 0x34, 0x12, 0x4C, 0x03, 0x02); // LDA $1234 / JMP $0203
            },
            startPc: 0x0200, budget: 4);
    }

    // ── Block cache hit on PC re-entry ──────────────────────────────────────────────────────
    [Fact]
    public void Block_cache_hits_on_PC_re_entry()
    {
        var space = NewRamSpace();
        Poke(space, 0x0200, 0xEA, 0x4C, 0x00, 0x02);  // NOP / JMP $0200 (self-loop)
        var inner = new Mos6502Cpu(space);
        inner.PC = 0x0200; inner.S = 0xFD; inner.P = 0x24;
        var jit = new JittedCpu<Mos6502Cpu>(inner, Mos6502Cpu.JitTarget, space);

        // Run two full loop iterations' worth of budget. The self-looping block at $0200 is
        // compiled once and re-executed on every re-entry.
        long budget = 100;
        jit.Run(ref budget);
        Assert.Equal(1, jit.CompileCount);            // compiled exactly once despite many re-entries
    }

    // ── Inner interpreter owns state; the JIT shares it ─────────────────────────────────────
    [Fact]
    public void Inner_interpreter_owns_state_the_JIT_shares_it()
    {
        var space = NewRamSpace();
        var inner = new Mos6502Cpu(space);
        var jit = new JittedCpu<Mos6502Cpu>(inner, Mos6502Cpu.JitTarget, space);
        jit.SetRegister("A", 0x5A);
        Assert.Equal(0x5A, inner.A);
        Assert.Equal(0x5Aul, jit.GetRegister("A"));
    }
}
