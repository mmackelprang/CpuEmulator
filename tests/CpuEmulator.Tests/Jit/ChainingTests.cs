using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;

namespace CpuEmulator.Tests.Jit;

/// <summary>Tasks 1-2: the chaining scaffolding (BlockExit.Recompile, the ChainTable unlink table,
/// the DisableChaining flag, the dispatcher's Recompile handling) and — once Task 2 emits the
/// chain-resolution calls — direct block-to-block transitions at statically-known exits, pinned
/// against the interpreter on state + cycles. The interpreter is the oracle.</summary>
public class ChainingTests
{
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

    private static BlockCompiler NewCompiler(AddressSpace space, JitOptions? options = null)
    {
        var opts = options ?? new JitOptions();
        var inner = new Mos6502Cpu(space);
        return new BlockCompiler(inner, space, new Fastmem(space, opts), opts);
    }

    // ── Task 1: scaffolding — the enum, the table, the flag, the dispatcher ──────────────────────
    [Fact]
    public void BlockExit_has_a_Recompile_member()
    {
        Assert.True(Enum.IsDefined(typeof(BlockExit), BlockExit.Recompile));
        // Append-only ordering: the M2-i emitted Ldc_I4 constants for Normal/Budget/Irq are unchanged.
        Assert.Equal(0, (int)BlockExit.Normal);
        Assert.Equal(1, (int)BlockExit.Budget);
        Assert.Equal(2, (int)BlockExit.Irq);
        Assert.Equal(3, (int)BlockExit.Recompile);
    }

    [Fact]
    public void ChainTable_records_an_inbound_link()
    {
        var chains = new ChainTable();
        var pred = MakeStubBlock(0x0200);
        chains.Link(0x0300, pred);
        Assert.Contains(pred, chains.InboundTo(0x0300));
    }

    [Fact]
    public void ChainTable_sever_clears_inbound_links()
    {
        var chains = new ChainTable();
        chains.Link(0x0300, MakeStubBlock(0x0200));
        chains.Sever(0x0300);
        Assert.Empty(chains.InboundTo(0x0300));
    }

    [Fact]
    public void ChainTable_link_is_idempotent()
    {
        var chains = new ChainTable();
        var pred = MakeStubBlock(0x0200);
        chains.Link(0x0300, pred);
        chains.Link(0x0300, pred);
        Assert.Single(chains.InboundTo(0x0300));
    }

    [Fact]
    public void ChainTable_forget_drops_a_predecessor_from_every_inbound_set()
    {
        var chains = new ChainTable();
        var pred = MakeStubBlock(0x0200);
        chains.Link(0x0300, pred);
        chains.Link(0x0400, pred);
        chains.Forget(pred);
        Assert.Empty(chains.InboundTo(0x0300));
        Assert.Empty(chains.InboundTo(0x0400));
    }

    [Fact]
    public void DisableChaining_default_is_false()
    {
        Assert.False(new JitOptions().DisableChaining);
    }

    [Fact]
    public void Dispatcher_handles_a_Recompile_exit_by_invalidating()
    {
        // A real intra-block self-modifying program (the M2-i intra-block class): a store overwrites
        // a LATER opcode in the same block. The SMC guard ends the block (Task 3 makes that exit
        // Recompile); the dispatcher must InvalidateIfDirty + re-decode the modified bytes rather
        // than running the stale IL. Diffed against the interpreter oracle on state + cycles + RAM.
        //   $0200 LDA #$C6   ; A = $C6 (DEC-zp opcode value)
        //   $0202 STA $0207  ; SMC: overwrite the opcode at $0207 (was INC $30 = $E6 -> DEC = $C6)
        //   $0205 LDA #$05
        //   $0207 INC $30    ; <- patched to DEC $30; interpreter runs DEC, stale JIT would run INC
        //   $0209 STA $31    ; witness the block continued
        //   $020B JMP $020B  ; park
        var refSpace = NewRamSpace();
        var jitSpace = NewRamSpace();
        Action<AddressSpace> poke = space =>
        {
            space.Write8(0x30, 0x01);
            Poke(space, 0x0200,
                0xA9, 0xC6, 0x8D, 0x07, 0x02, 0xA9, 0x05,
                0xE6, 0x30, 0x85, 0x31, 0x4C, 0x0B, 0x02);
        };
        poke(refSpace);
        var refCpu = new Mos6502Cpu(refSpace) { PC = 0x0200, S = 0xFD, P = 0x24 };
        long rb = 200; refCpu.Run(ref rb);

        poke(jitSpace);
        var inner = new Mos6502Cpu(jitSpace) { PC = 0x0200, S = 0xFD, P = 0x24 };
        var jit = new JittedCpu(inner, jitSpace);
        long jb = 200; jit.Run(ref jb);

        Assert.Equal(refCpu.A, inner.A);
        Assert.Equal(refCpu.PC, inner.PC);
        Assert.Equal(refCpu.CycleCount, inner.CycleCount);
        for (uint a = 0; a <= 0xFFFF; a++)
            Assert.Equal(refSpace.Read8(a), jitSpace.Read8(a));
    }

    /// <summary>A real compiled block keyed at <paramref name="pc"/> — the ChainTable stores
    /// CompiledBlock references, so the unlink-table tests use a genuine compiled block.</summary>
    private static CompiledBlock MakeStubBlock(ushort pc)
    {
        var space = NewRamSpace();
        Poke(space, pc, 0x4C, (byte)(pc & 0xFF), (byte)(pc >> 8)); // JMP-self
        return NewCompiler(space).Compile(pc);
    }

    // ── Task 2: emitted chain-resolution at statically-known exits ───────────────────────────────
    private static JittedCpu AssertJitMatchesInterpreter(
        Action<AddressSpace> poke, ushort startPc, long budget,
        Action<Mos6502Cpu>? seedRegs = null, JitOptions? options = null)
    {
        var refSpace = NewRamSpace();
        poke(refSpace);
        var refCpu = new Mos6502Cpu(refSpace) { PC = startPc, S = 0xFD, P = 0x24 };
        seedRegs?.Invoke(refCpu);
        long refBudget = budget;
        refCpu.Run(ref refBudget);

        var jitSpace = NewRamSpace();
        poke(jitSpace);
        var inner = new Mos6502Cpu(jitSpace) { PC = startPc, S = 0xFD, P = 0x24 };
        seedRegs?.Invoke(inner);
        var jit = new JittedCpu(inner, jitSpace, options);
        long jitBudget = budget;
        jit.Run(ref jitBudget);

        Assert.Equal(refCpu.A, inner.A);
        Assert.Equal(refCpu.X, inner.X);
        Assert.Equal(refCpu.Y, inner.Y);
        Assert.Equal(refCpu.S, inner.S);
        Assert.Equal(refCpu.P, inner.P);
        Assert.Equal(refCpu.PC, inner.PC);
        Assert.Equal(refCpu.CycleCount, inner.CycleCount);
        Assert.Equal(budget - refBudget, budget - jitBudget);
        for (uint a = 0; a <= 0xFFFF; a++)
            Assert.Equal(refSpace.Read8(a), jitSpace.Read8(a));
        return jit;
    }

    [Fact]
    public void Jmp_abs_chains_to_its_target_without_a_dispatcher_round_trip()
    {
        var space = NewRamSpace();
        // $0200: LDA #$01 / JMP $0300        (block A, chains to $0300)
        // $0300: LDX #$02 / JMP $0300        (block B, self-loops via chain)
        Poke(space, 0x0200, 0xA9, 0x01, 0x4C, 0x00, 0x03);
        Poke(space, 0x0300, 0xA2, 0x02, 0x4C, 0x00, 0x03);
        var inner = new Mos6502Cpu(space) { PC = 0x0200, S = 0xFD, P = 0x24 };
        var jit = new JittedCpu(inner, space);
        long budget = 200;
        jit.Run(ref budget);

        Assert.Equal(2, jit.CompileCount);          // both blocks compiled exactly once
        Assert.True(jit.ChainStepCount > 0, "control transferred via the chain, not the dispatcher");
        Assert.Equal(0x01, inner.A);
        Assert.Equal(0x02, inner.X);
    }

    [Fact]
    public void Fall_through_past_the_cap_chains_to_the_continuation()
    {
        // 65 NOPs then JMP-self, with BlockLengthCap = 64 — the first block (64 NOPs) ends by
        // fall-through past the cap and chains to the 65th instruction's block.
        var opts = new JitOptions { BlockLengthCap = 64 };
        var jit = AssertJitMatchesInterpreter(
            space =>
            {
                var nops = new byte[65];
                Array.Fill(nops, (byte)0xEA);            // 65 NOPs
                Poke(space, 0x0200, nops);
                Poke(space, (ushort)(0x0200 + 65),
                    0x4C, (byte)((0x0200 + 65) & 0xFF), (byte)((0x0200 + 65) >> 8)); // JMP-self park
            },
            startPc: 0x0200, budget: 200, options: opts);

        Assert.True(jit.ChainStepCount > 0, "the cap fall-through chained to the continuation");
    }

    [Fact]
    public void Taken_branch_chains_to_its_target()
    {
        // BNE taken (Z clear): LDA #$01 sets Z=0; BNE forward to a static target.
        var jit = AssertJitMatchesInterpreter(
            space =>
                Poke(space, 0x0200,
                    0xA9, 0x01,         // LDA #$01 (Z clear)
                    0xD0, 0x02,         // BNE $0206 (taken)
                    0xA9, 0xFF,         // LDA #$FF (skipped)
                    0xA2, 0x07,         // $0206 LDX #$07
                    0x4C, 0x06, 0x02),  // JMP $0206 (park)
            startPc: 0x0200, budget: 200);

        Assert.True(jit.ChainStepCount > 0);
    }

    [Fact]
    public void Untaken_branch_chains_to_fall_through()
    {
        // BNE NOT taken (Z set): LDA #$00 sets Z=1; BNE falls through to the next instruction.
        var jit = AssertJitMatchesInterpreter(
            space =>
                Poke(space, 0x0200,
                    0xA9, 0x00,         // LDA #$00 (Z set)
                    0xD0, 0x02,         // BNE $0206 (NOT taken)
                    0xA2, 0x09,         // $0204 LDX #$09 (fall-through path)
                    0x4C, 0x04, 0x02),  // JMP $0204 (park)
            startPc: 0x0200, budget: 200);

        Assert.True(jit.ChainStepCount > 0);
    }

    [Fact]
    public void Jmp_indirect_does_not_chain()
    {
        // A JMP-(ind) block alone: its target is read from memory at run time (dynamic), so it does
        // NOT chain — it exits to the dispatcher.
        var space = NewRamSpace();
        Poke(space, 0x0300, 0x00, 0x04);          // vector $0300 -> $0400
        Poke(space, 0x0200, 0x6C, 0x00, 0x03);    // JMP ($0300)
        Poke(space, 0x0400, 0xA9, 0x01, 0x00);    // LDA #$01 / BRK (fallback ends the $0400 block)
        var inner = new Mos6502Cpu(space) { PC = 0x0200, S = 0xFD, P = 0x24 };
        var jit = new JittedCpu(inner, space);
        long budget = 1;                            // one block only — the JMP-(ind)
        jit.Run(ref budget);
        Assert.Equal(0x0400, inner.PC);             // the indirect jump landed
        Assert.Equal(0, jit.ChainStepCount);        // but it did NOT chain
    }

    [Fact]
    public void Rts_does_not_chain()
    {
        var space = NewRamSpace();
        // JSR $0300 (chains to $0300); $0300 RTS (dynamic target — does NOT chain).
        Poke(space, 0x0200, 0x20, 0x00, 0x03, 0x4C, 0x03, 0x02); // JSR $0300 / JMP $0203 (park)
        Poke(space, 0x0300, 0x60);                                // RTS
        var inner = new Mos6502Cpu(space) { PC = 0x0200, S = 0xFD, P = 0x24 };
        var jit = new JittedCpu(inner, space);
        long budget = 1;        // run the JSR block — it chains to $0300 (static), then runs RTS
        jit.Run(ref budget);
        long afterJsrChains = jit.ChainStepCount;   // includes the JSR->$0300 chain
        budget = 1;             // run the next block — must NOT add a chain step (RTS is dynamic)
        jit.Run(ref budget);
        Assert.Equal(afterJsrChains, jit.ChainStepCount);
    }

    [Fact]
    public void Multi_block_loop_matches_the_interpreter_on_state_and_cycles()
    {
        // The canonical chaining-parity pin: a countdown loop spanning two chained blocks.
        var jit = AssertJitMatchesInterpreter(
            space =>
                // $0200 LDX #$05
                // $0202 loop: DEX / JMP $0206 (chain edge)
                // $0206 CPX #$00 / BNE $0202 (chain back to the loop top) / $020A JMP $020A (park)
                Poke(space, 0x0200,
                    0xA2, 0x05,             // LDX #$05
                    0xCA,                   // $0202 DEX
                    0x4C, 0x06, 0x02,       // JMP $0206
                    0xE0, 0x00,             // $0206 CPX #$00
                    0xD0, 0xF7,             // BNE $0202
                    0x4C, 0x0A, 0x02),      // $020A JMP $020A (park)
            startPc: 0x0200, budget: 500);

        Assert.True(jit.ChainStepCount > 0);
    }

    [Fact]
    public void Chaining_is_transparent_to_CycleCount()
    {
        // The same program run chaining-on and chaining-off reaches the identical CycleCount.
        static long RunCycles(JitOptions opts)
        {
            var space = NewRamSpace();
            Poke(space, 0x0200,
                0xA2, 0x05, 0xCA, 0x4C, 0x06, 0x02,
                0xE0, 0x00, 0xD0, 0xF7, 0x4C, 0x0A, 0x02);
            var inner = new Mos6502Cpu(space) { PC = 0x0200, S = 0xFD, P = 0x24 };
            var jit = new JittedCpu(inner, space, opts);
            long budget = 500;
            jit.Run(ref budget);
            return inner.CycleCount;
        }

        Assert.Equal(RunCycles(new JitOptions { DisableChaining = true }), RunCycles(new JitOptions()));
    }

    [Fact]
    public void Long_chain_does_not_overflow_the_stack()
    {
        // A self-looping JMP-abs block run for ~5M cycles in ONE Run call. The chain edge must NOT
        // recurse (it returns to the JittedCpu-side loop), so the host stack stays bounded.
        var space = NewRamSpace();
        Poke(space, 0x0200, 0x4C, 0x00, 0x02);  // JMP $0200 (self-loop, 3 cycles each)
        var inner = new Mos6502Cpu(space) { PC = 0x0200, S = 0xFD, P = 0x24 };
        var jit = new JittedCpu(inner, space);
        long budget = 5_000_000;
        jit.Run(ref budget);                     // no StackOverflowException
        Assert.True(jit.ChainStepCount > 1_000_000);
        Assert.Equal(0x0200, inner.PC);
    }
}
