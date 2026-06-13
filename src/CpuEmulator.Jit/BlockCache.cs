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

/// <summary>PC-keyed block cache + the per-page dirty map + the chain unlink table + coarse
/// (whole-cache) invalidation. Tasks 1-2 hold the M2-i coarse flush (Task 4 replaces it with the
/// per-page-precise eviction); the chaining layer is already safe here because every chain edge is
/// gated on <c>!Dirty.Any</c>, so a coarse flush still runs before any chained successor.</summary>
internal sealed class BlockCache(int pageCount)
{
    private readonly System.Collections.Generic.Dictionary<ushort, CompiledBlock> _blocks = new();
    private readonly System.Collections.Generic.HashSet<int> _pagesWithBlocks = [];
    public DirtyMap Dirty { get; } = new(pageCount);

    /// <summary>The chain link/unlink table (M2-ii): successor PC -> the predecessors that chain
    /// into it, so invalidation can sever every inbound link (Ground truth A).</summary>
    public ChainTable Chains { get; } = new();

    public CompiledBlock GetOrCompile(ushort pc, BlockCompiler compiler)
    {
        if (_blocks.TryGetValue(pc, out var hit)) return hit;
        CompiledBlock block = compiler.Compile(pc);
        _blocks[pc] = block;
        foreach (int page in block.SpannedPages) _pagesWithBlocks.Add(page);
        return block;
    }

    /// <summary>Chain-edge resolver (M2-ii): fetch (compiling on first reach) the block at
    /// <paramref name="targetPc"/> and record an inbound link from <paramref name="predecessor"/>.
    /// The emitted chain-resolution call invokes this once it has cleared the chain-break gates
    /// (budget / Dirty.Any / InterruptPending — Ground truth A steps 2-4). Resolves BY PC through
    /// the live cache on every edge, so a severed (evicted) successor recompiles here on the next
    /// reach — no baked delegate, no IL patching.</summary>
    public CompiledBlock ResolveChain(ushort targetPc, CompiledBlock predecessor, BlockCompiler compiler)
    {
        CompiledBlock target = GetOrCompile(targetPc, compiler);
        Chains.Link(targetPc, predecessor);
        return target;
    }

    /// <summary>The SMC check, run before each dispatch (coarse, M2-i — Task 4 makes it per-page).
    /// If any dirty page owns a cached block, discard the WHOLE cache (cheap because a chain edge
    /// only proceeds when !Dirty.Any, so the flush runs before any chained successor). A mark on a
    /// page that owns no block is cleared as harmless.</summary>
    public void InvalidateIfDirty()
    {
        if (!Dirty.Any) return;

        bool hitCode = false;
        foreach (int page in _pagesWithBlocks)
            if (Dirty[page]) { hitCode = true; break; }

        if (hitCode)
        {
            _blocks.Clear();
            _pagesWithBlocks.Clear();
            Chains.Clear();
            Dirty.Clear();
            return;
        }
        Dirty.Clear();
    }
}
