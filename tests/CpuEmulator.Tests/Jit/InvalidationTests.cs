using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;

namespace CpuEmulator.Tests.Jit;

/// <summary>Task 5: dirty-page invalidation for self-modifying code. The interpreter always
/// re-fetches the opcode byte, so the JIT — which caches compiled blocks — MUST discard a block
/// whose bytes were overwritten. Two classes the Tasks 1-3 reviewer fuzzer found Critical:
/// (1) BETWEEN-BLOCK — a store to a code page must force the owning block to recompile, and the
/// dirty mark for a not-yet-cached page must NOT be dropped by an unconditional clear; and
/// (2) INTRA-BLOCK — a store within a block to the block's OWN page range must take effect on the
/// subsequent execution (M2-i: the emitted store ends the block, forcing a re-dispatch).
/// The interpreter is the oracle: every JIT run is diffed against a fresh interpreter run.</summary>
public class InvalidationTests
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

    /// <summary>A no-op chain callback for direct single-block Run() unit tests (these inspect the
    /// dirty map after one block, not chaining). The block's emitted chain edge calls this when it
    /// reaches a chainable exit; setting exit = Normal mirrors the dispatcher routing to itself.</summary>
    private static readonly ChainDispatch NoChain =
        (ushort _, ref long _, out BlockExit e) => e = BlockExit.Normal;

    /// <summary>Run the same program through a fresh interpreter and a JIT-wrapped interpreter
    /// (cache ON), then assert identical final registers, cycle count, and full RAM image.</summary>
    private static (Mos6502Cpu refCpu, Mos6502Cpu jitInner) AssertJitMatchesInterpreter(
        Action<AddressSpace> poke, ushort startPc, long budget, out JittedCpu<Mos6502Cpu> jit)
    {
        var refSpace = NewRamSpace();
        poke(refSpace);
        var refCpu = new Mos6502Cpu(refSpace);
        refCpu.PC = startPc; refCpu.S = 0xFD; refCpu.P = 0x24;
        long refBudget = budget;
        refCpu.Run(ref refBudget);

        var jitSpace = NewRamSpace();
        poke(jitSpace);
        var inner = new Mos6502Cpu(jitSpace);
        inner.PC = startPc; inner.S = 0xFD; inner.P = 0x24;
        jit = new JittedCpu<Mos6502Cpu>(inner, Mos6502Cpu.JitTarget, jitSpace);
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
        return (refCpu, inner);
    }

    // ── Class-1: between-block SMC — overwriting a cached block's opcode re-decodes ─────────────
    [Fact]
    public void SMC_overwriting_a_cached_blocks_opcode_re_decodes()
    {
        // A loop body at $0300 is cached on its first pass. A driver at $0200 patches the loop
        // body's opcode (a different instruction), then re-enters the loop. The JIT must run the
        // NEW opcode — the interpreter always re-fetches, so a divergence is a stale-cache bug.
        //
        // Program ($0200, X counts down from 2):
        //   $0200 LDX #$02
        //   $0202 loop2: JSR $0300        ; run the patch-target block (cached on pass 1)
        //   $0205 DEX
        //   $0206 BNE loop2 ($0202)
        //   $0208 JMP $0208               ; park
        // Patch target ($0300):
        //   $0300 INC $10                 ; opcode $E6 (will be patched to DEC $10 = $C6)
        //   $0302 LDA #$C6                 ; patch byte
        //   $0304 STA $0300               ; SMC: overwrite the INC opcode with DEC on page $03
        //   $0307 RTS
        AssertJitMatchesInterpreter(
            space =>
            {
                space.Write8(0x10, 0x40);
                Poke(space, 0x0200,
                    0xA2, 0x02,             // LDX #$02
                    0x20, 0x00, 0x03,       // JSR $0300
                    0xCA,                   // DEX
                    0xD0, 0xFA,             // BNE $0202
                    0x4C, 0x08, 0x02);      // JMP $0208 (park)
                Poke(space, 0x0300,
                    0xE6, 0x10,             // INC $10
                    0xA9, 0xC6,             // LDA #$C6
                    0x8D, 0x00, 0x03,       // STA $0300  (patch INC->DEC)
                    0x60);                  // RTS
            },
            startPc: 0x0200, budget: 200, out _);
    }

    // ── Class-1: a cached block on page P, then a store to page P, forces a recompile ───────────
    [Fact]
    public void Cached_block_on_a_page_recompiles_after_a_store_to_that_page()
    {
        // The hand-off note #1 scenario, pinned directly on the cache: a block is cached on page P,
        // then a guest store dirties page P; the next dispatch MUST recompile (the cached block's
        // bytes may have changed). We drive the cache primitives so the assertion is about the
        // invalidation contract, not an end-to-end SMC program.
        var space = NewRamSpace();
        Poke(space, 0x0300, 0xE6, 0x10, 0x60); // INC $10 / RTS on page $03
        var inner = new Mos6502Cpu(space);
        var opts = new JitOptions();
        var fastmem = new Fastmem(space, opts);
        var compiler = new BlockCompiler<Mos6502Cpu>(inner, Mos6502Cpu.JitTarget, space, fastmem, opts);
        var cache = new BlockCache<Mos6502Cpu>(space.PageCount, new JitOptions());

        // 1) Cache a block on page $03.
        cache.GetOrCompile(0x0300, compiler);
        Assert.Equal(1, compiler.CompileCount);

        // 2) A store dirties page $03 (a write somewhere in the block's bytes).
        cache.Dirty.Mark(0x03);

        // 3) The dispatcher's pre-dispatch check sees a dirtied code page → flushes the cache.
        cache.InvalidateIfDirty();

        // 4) Re-dispatch at $0300 must RECOMPILE (the cached stale block was discarded).
        cache.GetOrCompile(0x0300, compiler);
        Assert.Equal(2, compiler.CompileCount); // recompiled — the cache did not serve the stale block
    }

    // ── Class-1: a mark on a not-yet-cached page is NOT silently dropped into a later stale hit ──
    [Fact]
    public void Mark_on_a_not_yet_cached_page_does_not_strand_a_later_block()
    {
        // The hand-off's precise warning: an unconditional Dirty.Clear() consumes a page's mark
        // even when that page owns no cached block yet. This pins that the corrected logic leaves
        // no stale block reachable: mark page $03 (no block there yet), run InvalidateIfDirty
        // (no flush, no cached block on $03), THEN cache a block on $03 — and a subsequent store +
        // InvalidateIfDirty still flushes it. No mark is "lost" in a way that strands a stale block.
        var space = NewRamSpace();
        Poke(space, 0x0300, 0xE6, 0x10, 0x60);
        var inner = new Mos6502Cpu(space);
        var opts = new JitOptions();
        var compiler = new BlockCompiler<Mos6502Cpu>(inner, Mos6502Cpu.JitTarget, space, new Fastmem(space, opts), opts);
        var cache = new BlockCache<Mos6502Cpu>(space.PageCount, new JitOptions());

        cache.Dirty.Mark(0x03);          // store to page $03 before any block is cached there
        cache.InvalidateIfDirty();        // no flush (page $03 owns no block); mark consumed harmlessly
        cache.GetOrCompile(0x0300, compiler);  // NOW a block is cached on $03 (fresh bytes)
        Assert.Equal(1, compiler.CompileCount);

        cache.Dirty.Mark(0x03);          // a later store to the now-cached code page
        cache.InvalidateIfDirty();        // must flush the cached block
        cache.GetOrCompile(0x0300, compiler);
        Assert.Equal(2, compiler.CompileCount); // recompiled — no stale block was stranded
    }

    // ── Class-1 corollary: a write to a non-code page does NOT invalidate (cache survives) ──────
    [Fact]
    public void Write_to_a_non_code_page_does_not_invalidate()
    {
        // A self-looping block at $0200 writes to page $40 (data, no cached block) on each pass.
        // The data write must NOT flush the cache, so the compile count is INDEPENDENT of the
        // iteration count: a 5-iteration run and a 2-iteration run compile the same number of
        // blocks (the loop body + the park block — each compiled once). A per-iteration flush
        // would make the count scale with the iterations.
        static int Compiles(byte iterations)
        {
            var space = NewRamSpace();
            // $0200 loop: STA $4000 / DEX / BNE $0200 / JMP $0206 (park)
            Poke(space, 0x0200, 0x8D, 0x00, 0x40, 0xCA, 0xD0, 0xFA, 0x4C, 0x06, 0x02);
            var inner = new Mos6502Cpu(space);
            inner.PC = 0x0200; inner.S = 0xFD; inner.P = 0x24;
            inner.X = iterations; inner.A = 0x99;
            var jit = new JittedCpu<Mos6502Cpu>(inner, Mos6502Cpu.JitTarget, space);
            long budget = 500;
            jit.Run(ref budget);
            Assert.Equal(0x99, space.Read8(0x4000)); // the store landed
            return jit.CompileCount;
        }

        Assert.Equal(Compiles(2), Compiles(5)); // compile count independent of iterations → no flush
    }

    // ── Class-1 dirty-map pin: a RAM store sets the written page's dirty bit ─────────────────────
    [Fact]
    public void Dirty_map_marks_the_written_page()
    {
        var space = NewRamSpace();
        // STA $4000 then JMP-self
        Poke(space, 0x0200, 0x8D, 0x00, 0x40, 0x4C, 0x03, 0x02);
        var inner = new Mos6502Cpu(space);
        inner.PC = 0x0200; inner.S = 0xFD; inner.P = 0x24; inner.A = 0x12;

        var opts = new JitOptions();
        var fastmem = new Fastmem(space, opts);
        var dirty = new DirtyMap(space.PageCount);
        var compiler = new BlockCompiler<Mos6502Cpu>(inner, Mos6502Cpu.JitTarget, space, fastmem, opts);
        var block = compiler.Compile(0x0200);
        long budget = 4;
        block.Run(inner, space, fastmem, dirty, NoChain, ref budget, out _);

        Assert.True(dirty[0x40]);   // page $40 (address $4000) is marked
        Assert.False(dirty[0x02]);  // the code page $02 was only read, not written
    }

    // ── Class-1 MMIO corollary: an MMIO store does not mark a dirty page ────────────────────────
    [Fact]
    public void MMIO_store_does_not_mark_a_dirty_page()
    {
        // Page $D0 is a peripheral (no backing) — a store there routes to the bus and must NOT
        // set any dirty bit (Ground truth E corollary: MMIO can't hold code).
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0xD000], writable: true);
        var uart = new CpuEmulator.Peripherals.SimpleUart();
        space.MapPeripheral(0xD000, 0x0100, uart);
        space.MapMemory(0xD100, new byte[0x10000 - 0xD100], writable: true);

        var inner = new Mos6502Cpu(space);
        inner.PC = 0x0200; inner.S = 0xFD; inner.P = 0x24; inner.A = 0x41;
        // STA $D000 then JMP-self
        Poke(space, 0x0200, 0x8D, 0x00, 0xD0, 0x4C, 0x03, 0x02);

        var opts = new JitOptions();
        var fastmem = new Fastmem(space, opts);
        var dirty = new DirtyMap(space.PageCount);
        var compiler = new BlockCompiler<Mos6502Cpu>(inner, Mos6502Cpu.JitTarget, space, fastmem, opts);
        var block = compiler.Compile(0x0200);
        long budget = 4;
        block.Run(inner, space, fastmem, dirty, NoChain, ref budget, out _);

        Assert.False(dirty[0xD0]);  // the MMIO write did not mark a dirty page
    }

    // ── Class-2: intra-block SMC — a store overwrites a later OPCODE in the same block ──────────
    [Fact]
    public void Intra_block_SMC_overwriting_a_later_opcode_in_the_same_block_takes_effect()
    {
        // The JIT reads operand BYTES from memory at run time (so operand-SMC is already faithful),
        // but it DECODES OPCODES at compile time into fixed IL. A store that overwrites a later
        // instruction's OPCODE byte within the same straight-line block is the divergence the
        // reviewer found: the compiled block runs the OLD opcode's IL while the interpreter re-
        // fetches the NEW one. M2-i's fix ends the block at the writable-RAM store whose target
        // page is the block's own, forcing a re-dispatch that decodes the modified bytes.
        //
        //   $0200 LDA #$C6        ; A = $C6 (the opcode byte for DEC zp)
        //   $0202 STA $0207       ; SMC: overwrite the OPCODE at $0207 (originally INC $30 = $E6)
        //   $0205 LDA #$05        ; A = $05
        //   $0207 INC $30         ; opcode at $0207 patched $E6 (INC) -> $C6 (DEC). $30 starts $01.
        //                         ;   interpreter runs DEC $30 -> $30 = $00; stale JIT runs INC -> $02
        //   $0209 STA $31         ; store A ($05) to $31 (a witness the block continued)
        //   $020B JMP $020B       ; park
        AssertJitMatchesInterpreter(
            space =>
            {
                space.Write8(0x30, 0x01);
                Poke(space, 0x0200,
                    0xA9, 0xC6,             // LDA #$C6  (DEC-zp opcode value)
                    0x8D, 0x07, 0x02,       // STA $0207 (patch the opcode at $0207)
                    0xA9, 0x05,             // LDA #$05
                    0xE6, 0x30,             // INC $30   <- opcode byte at $0207, patched to DEC
                    0x85, 0x31,             // STA $31
                    0x4C, 0x0B, 0x02);      // JMP $020B (park)
            },
            startPc: 0x0200, budget: 200, out _);
    }
}
