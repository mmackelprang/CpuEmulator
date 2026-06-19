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

    /// <summary>M6 PR-S: the SMC/recompile-cost lever. A block PC that is recompiled more than this
    /// many times (re-evicted then recompiled — the self-modifying-code thrash signature) is treated
    /// as SMC-hot: the dispatcher stops re-JITing it and runs it via the interpreter oracle for
    /// <see cref="SmcCooldownDispatches"/> dispatches, then retries the JIT. Default 16 — high enough
    /// that normal warmup never trips, low enough that Klaus's per-dispatch thrash trips early. A pure
    /// PERFORMANCE policy: the cooldown path is the byte-exact interpreter (the fallback oracle), so
    /// the lever NEVER changes the architectural result, only when to re-JIT vs interpret.</summary>
    public int SmcRecompileCap { get; init; } = 16;

    /// <summary>M6 PR-S: how many dispatches an SMC-hot PC runs via the interpreter before the JIT is
    /// retried (the cooldown window). Default 256 — long enough to amortize the per-dispatch Compile()
    /// cost the thrash was burning, short enough that a PC that stops being hot recovers quickly.</summary>
    public int SmcCooldownDispatches { get; init; } = 256;

    /// <summary>M6 PR-S: turn the SMC/recompile-cost lever fully off (every PC always JITs, the
    /// pre-PR-S behavior). Default false (the lever is ON). The differential fuzzer runs BOTH on and
    /// off, asserting both match the interpreter — so the lever is proven parity-transparent (it is a
    /// scheduling policy, not a correctness change), exactly as the fuzzer runs chaining on AND off.</summary>
    public bool DisableSmcLever { get; init; }
}
