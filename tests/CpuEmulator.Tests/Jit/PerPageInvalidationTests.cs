using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;

namespace CpuEmulator.Tests.Jit;

/// <summary>Task 4 — the per-page block index + precise per-page invalidation (Ground truth C),
/// replacing M2-i's whole-cache-coarse flush. With chaining, a whole-cache flush on every RAM store
/// would tear down every chain link — unacceptable thrash. M2-ii evicts ONLY the blocks on dirtied
/// pages and severs their inbound chain links, preserving the M2-i carry-forward #1 invariant. These
/// drive the BlockCache primitives directly so the assertions are precisely the eviction +
/// unlink-on-evict contract.</summary>
public class PerPageInvalidationTests
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

    private static (BlockCompiler Compiler, BlockCache Cache) NewCacheAndCompiler(AddressSpace space)
    {
        var inner = new Mos6502Cpu(space);
        var opts = new JitOptions();
        var compiler = new BlockCompiler(inner, space, new Fastmem(space, opts), opts);
        return (compiler, new BlockCache(space.PageCount));
    }

    [Fact]
    public void Store_to_a_page_evicts_only_that_pages_blocks()
    {
        var space = NewRamSpace();
        Poke(space, 0x0300, 0xE8, 0x60);   // INX / RTS on page $03
        Poke(space, 0x0500, 0xC8, 0x60);   // INY / RTS on page $05
        var (compiler, cache) = NewCacheAndCompiler(space);

        cache.GetOrCompile(0x0300, compiler);
        cache.GetOrCompile(0x0500, compiler);
        Assert.Equal(2, compiler.CompileCount);

        // A store dirties page $03 only.
        cache.Dirty.Mark(0x03);
        cache.InvalidateIfDirty();

        // Page $03's block must recompile; page $05's block is still a cache hit.
        cache.GetOrCompile(0x0300, compiler);   // recompiles
        cache.GetOrCompile(0x0500, compiler);   // hit (no recompile)
        Assert.Equal(3, compiler.CompileCount);  // +1 only, not the whole cache
    }

    [Fact]
    public void Inbound_chain_links_to_an_evicted_block_are_severed()
    {
        var space = NewRamSpace();
        Poke(space, 0x0200, 0x4C, 0x00, 0x03);   // P: JMP $0300
        Poke(space, 0x0300, 0xE8, 0x4C, 0x00, 0x03); // S: INX / JMP $0300
        var (compiler, cache) = NewCacheAndCompiler(space);

        CompiledBlock p = cache.GetOrCompile(0x0200, compiler);
        cache.ResolveChain(0x0300, p, compiler);   // P chains into S
        Assert.Contains(p, cache.Chains.InboundTo(0x0300));

        cache.Dirty.Mark(0x03);                    // S's page dirtied
        cache.InvalidateIfDirty();                 // evict S + sever
        Assert.Empty(cache.Chains.InboundTo(0x0300));
    }

    [Fact]
    public void Store_to_a_non_code_page_evicts_nothing()
    {
        var space = NewRamSpace();
        Poke(space, 0x0300, 0xE8, 0x60);
        var (compiler, cache) = NewCacheAndCompiler(space);

        cache.GetOrCompile(0x0300, compiler);
        Assert.Equal(1, compiler.CompileCount);

        cache.Dirty.Mark(0x40);     // a data page with no cached block
        cache.InvalidateIfDirty();
        cache.GetOrCompile(0x0300, compiler);   // still a hit
        Assert.Equal(1, compiler.CompileCount);  // nothing evicted
    }

    [Fact]
    public void Block_spanning_two_pages_is_evicted_by_a_write_to_either()
    {
        static int CompilesAfterStoreToPage(int dirtyPage)
        {
            var space = NewRamSpace();
            // A block straddling the $02FF/$0300 boundary: NOP at $02FE, then a 2-byte op crossing,
            // then RTS. Simplest: place a JMP at $02FE (3 bytes: $02FE,$02FF,$0300) so the block's
            // bytes span pages $02 and $03.
            Poke(space, 0x02FE, 0x4C, 0xFE, 0x02);  // JMP $02FE (bytes at $02FE,$02FF,$0300)
            var (compiler, cache) = NewCacheAndCompiler(space);
            cache.GetOrCompile(0x02FE, compiler);
            Assert.Equal(1, compiler.CompileCount);
            cache.Dirty.Mark(dirtyPage);
            cache.InvalidateIfDirty();
            cache.GetOrCompile(0x02FE, compiler);
            return compiler.CompileCount;
        }

        Assert.Equal(2, CompilesAfterStoreToPage(0x02)); // write to page $02 evicts it
        Assert.Equal(2, CompilesAfterStoreToPage(0x03)); // write to page $03 (the spanned page) too
    }

    [Fact]
    public void Per_page_index_is_rebuilt_on_recompile()
    {
        var space = NewRamSpace();
        Poke(space, 0x0300, 0xE8, 0x60);
        var (compiler, cache) = NewCacheAndCompiler(space);

        cache.GetOrCompile(0x0300, compiler);
        cache.Dirty.Mark(0x03);
        cache.InvalidateIfDirty();               // evicts; the per-page index entry is dropped
        cache.GetOrCompile(0x0300, compiler);    // recompiles -> re-adds to _blocksByPage

        // A second store to page $03 must again evict (the index was rebuilt).
        cache.Dirty.Mark(0x03);
        cache.InvalidateIfDirty();
        cache.GetOrCompile(0x0300, compiler);
        Assert.Equal(3, compiler.CompileCount);  // compiled at first, after evict-1, after evict-2
    }

    [Fact]
    public void MMIO_store_marks_nothing()
    {
        // An MMIO store routes to the bus and must not set any dirty bit (M2-i contract preserved):
        // re-running the same self-looping program that stores to MMIO does NOT scale the compile
        // count with iterations.
        static int Compiles(byte iterations)
        {
            var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
            space.MapMemory(0x0000, new byte[0xD000], writable: true);
            var uart = new CpuEmulator.Peripherals.SimpleUart();
            space.MapPeripheral(0xD000, 0x0100, uart);
            space.MapMemory(0xD100, new byte[0x10000 - 0xD100], writable: true);
            // $0200 loop: STA $D000 / DEX / BNE $0200 / JMP $0206 (park)
            Poke(space, 0x0200, 0x8D, 0x00, 0xD0, 0xCA, 0xD0, 0xFA, 0x4C, 0x06, 0x02);
            var inner = new Mos6502Cpu(space) { PC = 0x0200, S = 0xFD, P = 0x24, X = iterations, A = 0x41 };
            var jit = new JittedCpu(inner, space);
            long budget = 500;
            jit.Run(ref budget);
            return jit.CompileCount;
        }

        Assert.Equal(Compiles(2), Compiles(5));  // MMIO writes marked nothing -> no per-page eviction
    }

    [Fact]
    public void Mark_on_a_not_yet_cached_page_does_not_strand_a_later_block()
    {
        // The M2-i carry-forward #1 invariant, re-run against the per-page path: a store marks a
        // page before any block is cached there; a later block compiled on that page reads post-write
        // bytes, and a subsequent store + invalidate still flushes it. No mark strands a stale block.
        var space = NewRamSpace();
        Poke(space, 0x0300, 0xE8, 0x60);
        var (compiler, cache) = NewCacheAndCompiler(space);

        cache.Dirty.Mark(0x03);          // store to page $03 before any block is cached there
        cache.InvalidateIfDirty();        // no eviction (page $03 owns no block); mark cleared harmlessly
        cache.GetOrCompile(0x0300, compiler);  // NOW a block is cached on $03 (fresh bytes)
        Assert.Equal(1, compiler.CompileCount);

        cache.Dirty.Mark(0x03);          // a later store to the now-cached code page
        cache.InvalidateIfDirty();        // must evict the cached block
        cache.GetOrCompile(0x0300, compiler);
        Assert.Equal(2, compiler.CompileCount); // recompiled — no stale block stranded
    }
}
