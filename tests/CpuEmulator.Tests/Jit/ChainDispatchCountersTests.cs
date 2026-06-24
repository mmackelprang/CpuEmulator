using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;

namespace CpuEmulator.Tests.Jit;

/// <summary>ADR 0022 item C: the four FREE run-lifetime counters surfaced on the public
/// <see cref="IJitMetrics"/> seam — ChainEdgesTaken, DispatcherEntries, BlockCacheHits, BlockCacheMisses.
/// These pins are the red→green gate the Builder requires: each asserts the counter reads a REAL value
/// (&gt; 0 after a JITted run, 0 / correct on the un-run or chaining-disabled case), so each test FAILS if
/// its counter were left hardwired or unwired. The point of this class is the counters themselves, not a
/// state/cycle parity check (ChainingTests already pins parity).</summary>
public class ChainDispatchCountersTests
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

    /// <summary>A genuine backward-branch countdown loop spanning two chained blocks (the canonical
    /// chaining shape from ChainingTests.Multi_block_loop): the loop both compiles + chains AND re-enters
    /// the same block PCs many times, so it exercises chain edges, dispatcher entries, and cache hits.</summary>
    private static void PokeLoop(AddressSpace space)
    {
        // $0200 LDX #$05
        // $0202 loop: DEX / JMP $0206 (chain edge)
        // $0206 CPX #$00 / BNE $0202 (chain back to the loop top) / $020A JMP $020A (park)
        Poke(space, 0x0200,
            0xA2, 0x05,             // LDX #$05
            0xCA,                   // $0202 DEX
            0x4C, 0x06, 0x02,       // JMP $0206
            0xE0, 0x00,             // $0206 CPX #$00
            0xD0, 0xF7,             // BNE $0202
            0x4C, 0x0A, 0x02);      // $020A JMP $020A (park)
    }

    private static JittedCpu<Mos6502Cpu> RunLoop(long budget, JitOptions? options = null)
    {
        var space = NewRamSpace();
        PokeLoop(space);
        var inner = new Mos6502Cpu(space) { PC = 0x0200, S = 0xFD, P = 0x24 };
        var jit = new JittedCpu<Mos6502Cpu>(inner, Mos6502Cpu.JitTarget, space, options: options);
        long b = budget;
        jit.Run(ref b);
        return jit;
    }

    [Fact]
    public void Dispatcher_entries_and_chain_edges_are_positive_after_a_jitted_run()
    {
        // The load-bearing "reads real values, red→green vs unwired" gate: a program that genuinely
        // compiles + chains must show BOTH counters > 0. Fails if either is hardwired to 0 or unwired
        // (DispatcherEntries if _dispatcherEntries++ is missing; ChainEdgesTaken if _chainStepCount is
        // not surfaced).
        var jit = (IJitMetrics)RunLoop(budget: 500);
        Assert.True(jit.DispatcherEntries > 0, "the dispatcher entered at least one compiled block");
        Assert.True(jit.ChainEdgesTaken > 0, "control transferred via the chain, not only the dispatcher");
    }

    [Fact]
    public void Chaining_disabled_takes_zero_chain_edges_but_still_dispatches()
    {
        // Same program with chaining OFF: every block round-trips through the dispatcher, so there are
        // NO in-frame chain hops. Proves the two counters measure DISTINCT things (the floor signal) —
        // fails if DispatcherEntries weren't incremented (it would read 0 even though blocks ran), and
        // fails if ChainEdgesTaken leaked a chain hop with chaining disabled.
        var jit = (IJitMetrics)RunLoop(budget: 500, options: new JitOptions { DisableChaining = true });
        Assert.Equal(0, jit.ChainEdgesTaken);                 // chaining off ⇒ no in-frame hops
        Assert.True(jit.DispatcherEntries > 0, "every block round-trips through the dispatcher");
    }

    [Fact]
    public void Block_cache_hits_grow_on_repeated_dispatch_of_the_same_pc()
    {
        // The loop re-enters the same block PCs many times. With chaining OFF every re-entry is a fresh
        // dispatcher GetOrCompile, so the first compiles MISS and the many re-entries HIT — both counters
        // must be > 0. (Chaining off so the re-entries actually round-trip to GetOrCompile rather than
        // staying inside the emitted chain.)
        var jit = (IJitMetrics)RunLoop(budget: 500, options: new JitOptions { DisableChaining = true });
        Assert.True(jit.BlockCacheMisses > 0, "the first compile(s) missed the cache");
        Assert.True(jit.BlockCacheHits > 0, "re-entries of the same PC hit the compiled block");
    }

    [Fact]
    public void Counters_are_exposed_through_the_IJitMetrics_seam()
    {
        // Compile-time proof the seam carries all four members (read every one through the interface).
        JittedCpu<Mos6502Cpu> jit = RunLoop(budget: 500);
        Assert.True(jit is IJitMetrics);
        var m = (IJitMetrics)jit;
        long chain = m.ChainEdgesTaken;
        long dispatch = m.DispatcherEntries;
        long hits = m.BlockCacheHits;
        long misses = m.BlockCacheMisses;
        // All non-negative run-lifetime totals; the positivity gates above pin the real values.
        Assert.True(chain >= 0 && dispatch >= 0 && hits >= 0 && misses >= 0);
    }
}
