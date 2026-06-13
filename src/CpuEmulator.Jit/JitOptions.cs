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

    /// <summary>Disable block chaining (every block returns to the dispatcher, the M2-i behavior).
    /// Default false (chaining on). The differential fuzzer (Task 7) runs BOTH on and off; a board
    /// or test that wants the simplest dispatch path sets this true. Off-by-default is the speed
    /// posture; the flag exists for differential isolation + the Task 9 dispatch micro-bench.</summary>
    public bool DisableChaining { get; init; }
}
