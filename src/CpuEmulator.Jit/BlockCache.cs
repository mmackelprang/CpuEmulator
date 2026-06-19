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
internal sealed class BlockCache<TCpu>(int pageCount, JitOptions opts) where TCpu : class
{
    private readonly int _pageCount = pageCount;
    private readonly JitOptions _opts = opts;
    private readonly System.Collections.Generic.Dictionary<ushort, CompiledBlock<TCpu>> _blocks = new();
    private readonly System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<CompiledBlock<TCpu>>> _blocksByPage = new();
    public DirtyMap Dirty { get; } = new(pageCount);

    // M6 PR-S: the SMC/recompile-cost lever state. _recompiles[pc] counts how many times the block at
    // pc has been EVICTED-then-recompiled (a first compile is not a recompile). When it exceeds the
    // cap, the PC is SMC-hot: _cooldown[pc] is set to the cooldown window and ShouldInterpret(pc)
    // returns true until the window drains (the dispatcher runs inner.Step for it instead of compiling).
    private readonly System.Collections.Generic.Dictionary<ushort, int> _recompiles = new();
    private readonly System.Collections.Generic.Dictionary<ushort, int> _cooldown = new();
    // M6 PR-S: committed instrumentation (the §3.4 "quantify first" asserted artifact). TotalRecompiles
    // is every evict-then-recompile across the run; TotalEvictions is every block drop; SmcHotPcCount is
    // how many distinct PCs ever tripped the cap. A test asserts the lever drops TotalRecompiles sharply
    // on an SMC-thrash program (the recompile-count-drop gate).
    public long TotalRecompiles { get; private set; }
    public long TotalEvictions { get; private set; }
    public int SmcHotPcCount => _everHotPcs.Count;
    private readonly System.Collections.Generic.HashSet<ushort> _everHotPcs = new();

    /// <summary>The chain link/unlink table (M2-ii): successor PC -> the predecessors that chain
    /// into it, so invalidation can sever every inbound link (Ground truth A).</summary>
    public ChainTable<TCpu> Chains { get; } = new();

    /// <summary>M6 PR-S: should the dispatcher run this PC through the interpreter (because it is
    /// SMC-hot and in its cooldown window) instead of compiling it? False when the lever is disabled.</summary>
    public bool ShouldInterpret(ushort pc) =>
        !_opts.DisableSmcLever && _cooldown.TryGetValue(pc, out int n) && n > 0;

    /// <summary>M6 PR-S: account one interpreter-dispatch of an SMC-hot PC — decrement its cooldown.
    /// When the window drains the entry is removed, so the next dispatch retries the JIT (self-healing:
    /// a PC that stopped being hot returns to full JIT speed; a still-hot PC re-trips the cap cheaply).</summary>
    public void NoteInterpretedDispatch(ushort pc)
    {
        if (_cooldown.TryGetValue(pc, out int n))
        {
            if (n <= 1) { _cooldown.Remove(pc); _recompiles.Remove(pc); }   // window drained: re-arm the JIT
            else _cooldown[pc] = n - 1;
        }
    }

    public CompiledBlock<TCpu> GetOrCompile(ushort pc, BlockCompiler<TCpu> compiler)
    {
        if (_blocks.TryGetValue(pc, out var hit)) return hit;
        // A miss here is either a first compile or a recompile (the block was evicted). Count the
        // recompile + arm the cooldown if this PC has now thrashed past the cap.
        if (_recompiles.TryGetValue(pc, out int prior))
        {
            int now = prior + 1;
            _recompiles[pc] = now;
            TotalRecompiles++;
            if (!_opts.DisableSmcLever && now > _opts.SmcRecompileCap)
            {
                _cooldown[pc] = _opts.SmcCooldownDispatches;   // SMC-hot: cool down via the interpreter
                _everHotPcs.Add(pc);
            }
        }
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
        _recompiles.Clear();    // M6 PR-S: reset the lever state on reuse
        _cooldown.Clear();
        // TotalRecompiles / TotalEvictions / _everHotPcs are run-lifetime instrumentation — NOT reset
        // by FlushAll (a reuse boundary), so a per-worker reuse run still accumulates honest totals.
    }

    /// <summary>Remove a block from the PC map + the per-page index, and sever its chain links:
    /// drop inbound links INTO it (predecessors recompile it by PC on their next chain edge) and
    /// drop it FROM any inbound set it appears in (so a future eviction does not chase a dead ref).
    /// Predecessors are NOT recursively evicted — they resolve-by-PC (Ground truth A/C).</summary>
    private void Evict(CompiledBlock<TCpu> block)
    {
        TotalEvictions++;
        // M6 PR-S: seed the recompile counter so the NEXT GetOrCompile of this PC is counted as a
        // recompile (a first-ever compile of a never-evicted PC is not). Use a sentinel 0 entry; the
        // recompile increment in GetOrCompile turns the first post-evict compile into count 1.
        if (!_recompiles.ContainsKey(block.EntryPc))
            _recompiles[block.EntryPc] = 0;
        _blocks.Remove(block.EntryPc);
        foreach (int page in block.SpannedPages)
            if (_blocksByPage.TryGetValue(page, out var list))
                list.Remove(block);
        Chains.Sever(block.EntryPc);   // drop inbound links INTO this block
        Chains.Forget(block);          // drop this block FROM any inbound set it appears in
    }
}
