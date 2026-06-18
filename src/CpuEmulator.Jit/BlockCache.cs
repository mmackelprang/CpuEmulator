namespace CpuEmulator.Jit;

/// <summary>Per-page dirty marks. An emitted RAM store calls Mark(page); the dispatcher
/// consults the marks before each block dispatch (the cc-cheap SMC check).</summary>
public sealed class DirtyMap(int pageCount)
{
    private readonly bool[] _dirty = new bool[pageCount];
    public bool Any { get; private set; }
    public void Mark(int page) { _dirty[page] = true; Any = true; }
    public bool this[int page] => _dirty[page];
    public void Clear() { System.Array.Clear(_dirty); Any = false; }
}

/// <summary>PC-keyed block cache + a per-page block index + the chain unlink table + per-page-precise
/// invalidation (M2-ii, Ground truth C). M2-i discarded the WHOLE cache on any SMC hit to a code
/// page (justified: "no chaining, rebuild is cheap"). With chaining, a whole-cache flush would tear
/// down every chain link on every RAM store — unacceptable thrash. M2-ii evicts ONLY the blocks on
/// dirtied pages (and severs their inbound chain links via the <see cref="ChainTable"/>), preserving
/// the M2-i carry-forward #1 invariant: a page's mark is cleared by the SAME step that evicts that
/// page's blocks; a not-yet-cached page's later block reads post-write bytes (it compiles after the
/// eviction).</summary>
internal sealed class BlockCache<TCpu>(int pageCount) where TCpu : class
{
    private readonly int _pageCount = pageCount;
    private readonly System.Collections.Generic.Dictionary<ushort, CompiledBlock<TCpu>> _blocks = new();
    private readonly System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<CompiledBlock<TCpu>>> _blocksByPage = new();
    public DirtyMap Dirty { get; } = new(pageCount);

    /// <summary>The chain link/unlink table (M2-ii): successor PC -> the predecessors that chain
    /// into it, so invalidation can sever every inbound link (Ground truth A).</summary>
    public ChainTable<TCpu> Chains { get; } = new();

    public CompiledBlock<TCpu> GetOrCompile(ushort pc, BlockCompiler<TCpu> compiler)
    {
        if (_blocks.TryGetValue(pc, out var hit)) return hit;
        CompiledBlock<TCpu> block = compiler.Compile(pc);
        _blocks[pc] = block;
        foreach (int page in block.SpannedPages)
        {
            if (!_blocksByPage.TryGetValue(page, out var list))
                _blocksByPage[page] = list = [];
            list.Add(block);
        }
        return block;
    }

    /// <summary>Chain-edge resolver (M2-ii): fetch (compiling on first reach) the block at
    /// <paramref name="targetPc"/> and record an inbound link from <paramref name="predecessor"/>.
    /// The emitted chain-resolution call invokes this once it has cleared the chain-break gates
    /// (budget / Dirty.Any / InterruptPending — Ground truth A steps 2-4). Resolves BY PC through
    /// the live cache on every edge, so a severed (evicted) successor recompiles here on the next
    /// reach — no baked delegate, no IL patching.</summary>
    public CompiledBlock<TCpu> ResolveChain(ushort targetPc, CompiledBlock<TCpu> predecessor, BlockCompiler<TCpu> compiler)
    {
        CompiledBlock<TCpu> target = GetOrCompile(targetPc, compiler);
        Chains.Link(targetPc, predecessor);
        return target;
    }

    /// <summary>The SMC check, run by the dispatcher before each block dispatch. PRECISE (M2-ii):
    /// evict only the blocks on dirtied pages (and sever their inbound chain links), not the whole
    /// cache. Preserves the M2-i carry-forward #1 invariant: a page's mark is cleared by the SAME
    /// step that evicts that page's blocks; a not-yet-cached page's later block reads post-write
    /// bytes (it compiles after the eviction). A dirtied page that owns no block evicts nothing and
    /// its mark is cleared as harmless.</summary>
    public void InvalidateIfDirty()
    {
        if (!Dirty.Any) return;
        for (int page = 0; page < _pageCount; page++)        // 256 for a 16-bit board; cheap scan
        {
            if (!Dirty[page]) continue;
            if (_blocksByPage.TryGetValue(page, out var list))
                foreach (CompiledBlock<TCpu> block in list.ToArray())   // copy: Evict mutates the list
                    Evict(block);
        }
        Dirty.Clear();
    }

    /// <summary>Evict EVERY block and reset all derived state — the per-worker REUSE reset (lever 4). After this
    /// the cache is byte-equivalent to a freshly constructed BlockCache(pageCount): no compiled blocks, no
    /// per-page index, no inbound chain links, no dirty marks. The next GetOrCompile recompiles from the CURRENT
    /// bus bytes — which is the whole point: the dispatch key is (ushort)PC and the SAME PC carries different bytes
    /// across reused cases, so a stale block would silently run the wrong case's code.</summary>
    public void FlushAll()
    {
        _blocks.Clear();
        _blocksByPage.Clear();
        Chains.Clear();
        Dirty.Clear();
    }

    /// <summary>Remove a block from the PC map + the per-page index, and sever its chain links:
    /// drop inbound links INTO it (predecessors recompile it by PC on their next chain edge) and
    /// drop it FROM any inbound set it appears in (so a future eviction does not chase a dead ref).
    /// Predecessors are NOT recursively evicted — they resolve-by-PC (Ground truth A/C).</summary>
    private void Evict(CompiledBlock<TCpu> block)
    {
        _blocks.Remove(block.EntryPc);
        foreach (int page in block.SpannedPages)
            if (_blocksByPage.TryGetValue(page, out var list))
                list.Remove(block);
        Chains.Sever(block.EntryPc);   // drop inbound links INTO this block
        Chains.Forget(block);          // drop this block FROM any inbound set it appears in
    }
}
