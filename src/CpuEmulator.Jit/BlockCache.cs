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
    /// discard the WHOLE cache (coarse, M2-i — cheap because there is no chaining to unlink) and
    /// clear the marks. A write to a page with no cached block clears its mark without a flush.</summary>
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
        }
        Dirty.Clear();   // marks are consumed each dispatch cycle
    }
}
