namespace CpuEmulator.Jit;

/// <summary>The chain link/unlink table. Maps a successor's entry PC to the set of predecessor
/// blocks that chain INTO it, so invalidating (evicting) a successor can sever every inbound link.
/// Chaining resolves successors BY PC through the live cache on every chain edge (Ground truth A,
/// "resolve-by-PC, not bake-the-delegate"), so severing is just dropping the inbound set + evicting
/// the successor from the cache — no emitted IL is patched.</summary>
internal sealed class ChainTable<TCpu> where TCpu : class
{
    private readonly System.Collections.Generic.Dictionary<ushort, System.Collections.Generic.HashSet<CompiledBlock<TCpu>>> _inbound = new();

    /// <summary>Record that <paramref name="predecessor"/> chains into the block at
    /// <paramref name="successorPc"/>. Idempotent (a set).</summary>
    public void Link(ushort successorPc, CompiledBlock<TCpu> predecessor)
    {
        if (!_inbound.TryGetValue(successorPc, out var set))
            _inbound[successorPc] = set = [];
        set.Add(predecessor);
    }

    /// <summary>The predecessors that chain into <paramref name="successorPc"/> (empty if none).</summary>
    public System.Collections.Generic.IReadOnlyCollection<CompiledBlock<TCpu>> InboundTo(ushort successorPc)
        => _inbound.TryGetValue(successorPc, out var set)
            ? set
            : System.Array.Empty<CompiledBlock<TCpu>>();

    /// <summary>Sever all inbound links to <paramref name="successorPc"/> (called when that block is
    /// evicted). The predecessors are NOT touched — they resolve-by-PC and will recompile the
    /// successor on their next chain edge.</summary>
    public void Sever(ushort successorPc) => _inbound.Remove(successorPc);

    /// <summary>Drop a predecessor from every inbound set it appears in (called when the
    /// PREDECESSOR is evicted — so a later Sever of a successor does not retain a dead block).</summary>
    public void Forget(CompiledBlock<TCpu> predecessor)
    {
        foreach (var set in _inbound.Values)
            set.Remove(predecessor);
    }

    /// <summary>Drop ALL inbound links — the per-worker REUSE reset (lever 4). After this the chain table is
    /// empty, as if freshly constructed; the next run rebuilds links by PC on its chain edges.</summary>
    public void Clear() => _inbound.Clear();
}
