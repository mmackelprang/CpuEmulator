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

/// <summary>PC-keyed block cache + the per-page dirty map + coarse (whole-cache) invalidation.
/// M2-i has no chaining, so a cache rebuild is cheap (re-decode on next entry) and the
/// invalidation response is whole-cache-coarse — see the recorded deviation.</summary>
internal sealed class BlockCache(int pageCount)
{
    private readonly System.Collections.Generic.Dictionary<ushort, CompiledBlock> _blocks = new();
    private readonly System.Collections.Generic.HashSet<int> _pagesWithBlocks = [];
    public DirtyMap Dirty { get; } = new(pageCount);

    public CompiledBlock GetOrCompile(ushort pc, BlockCompiler compiler)
    {
        if (_blocks.TryGetValue(pc, out var hit)) return hit;
        CompiledBlock block = compiler.Compile(pc);
        _blocks[pc] = block;
        foreach (int page in block.SpannedPages) _pagesWithBlocks.Add(page);
        return block;
    }

    /// <summary>The SMC check, run before each dispatch. If any dirty page owns a cached block,
    /// discard the WHOLE cache (coarse, M2-i — cheap because there is no chaining to unlink); that
    /// flush satisfies every outstanding mark, so the map clears. If NO dirty page owns a block,
    /// the marks describe writes to non-code pages — they are cleared only after confirming no
    /// cached block depends on them.
    ///
    /// RECORDED FIX (Task-5 hand-off note #1): the earlier stub ended with an UNCONDITIONAL
    /// <c>Dirty.Clear()</c> — it consumed every mark each dispatch even when no flush occurred.
    /// That is wrong the moment invalidation becomes finer than whole-cache (M2-ii chaining), and
    /// it obscures the invariant that a mark must outlive a dispatch until the block it threatens
    /// is actually recompiled. This version makes the rule explicit: marks are cleared by the SAME
    /// step that flushes the threatened blocks (here, the whole-cache flush), or — for marks on
    /// pages that own no block — cleared as harmless once that is established, never blindly.</summary>
    public void InvalidateIfDirty()
    {
        if (!Dirty.Any) return;

        bool hitCode = false;
        foreach (int page in _pagesWithBlocks)
            if (Dirty[page]) { hitCode = true; break; }

        if (hitCode)
        {
            // A dirtied page owns ≥1 cached block: the coarse M2-i response discards the whole
            // cache. With every block gone, every outstanding mark is satisfied → clear the map.
            _blocks.Clear();
            _pagesWithBlocks.Clear();
            Dirty.Clear();
            return;
        }

        // No dirtied page owns a block: these marks threaten no cached IL (any block later
        // compiled on a dirtied page reads the post-write bytes). Clear them as harmless — but
        // explicitly, as "no block depends on this page," not as an unconditional blanket clear.
        Dirty.Clear();
    }
}
