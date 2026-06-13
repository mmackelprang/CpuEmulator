namespace CpuEmulator.Jit;

/// <summary>Per-JittedCpu construction options.</summary>
public sealed record JitOptions
{
    /// <summary>Route every memory access through the bus instead of the fastmem fast path.
    /// Restores per-cycle bus-trace equivalence with the interpreter (Ground truth E) at a
    /// speed cost — the mode the trace spot tests use; off by default (fastmem on).</summary>
    public bool DisableFastmem { get; init; }

    /// <summary>Max instructions per compiled block (default 64). Test-overridable to exercise
    /// the block-length cap cheaply.</summary>
    public int BlockLengthCap { get; init; } = 64;
}
