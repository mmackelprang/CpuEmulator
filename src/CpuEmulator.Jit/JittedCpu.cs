using CpuEmulator.Core;
using CpuEmulator.Core.Jit;

namespace CpuEmulator.Jit;

/// <summary>Tier-1 IL-JIT CPU, generic over the interpreter CPU type (J1): wraps an interpreter
/// TCpu (the oracle + fallback + state owner) and runs cached, compiled blocks for Run. Step always
/// delegates to the inner interpreter (per-instruction fidelity for the monitor + harness).
/// Implements the same ICpuCore + IMonitorSupport surface, so a Machine, a MonitorEngine, and the
/// TomHarte runner drive it identically to the interpreter. The CPU-specific reflection + decode is
/// resolved through the injected <see cref="IJitTarget"/> (the per-CPU seam) — the JIT assembly no
/// longer references any concrete CPU assembly (a structural genericity proof).</summary>
public sealed class JittedCpu<TCpu> : ICpuCore, IMonitorSupport
    where TCpu : class, ICpuCore, IMonitorSupport
{
    /// <summary>The gate message — extracted to a const so the gate test references it directly
    /// (rather than re-typing the string). Names the interpreter fallback and the doc.</summary>
    internal const string DynamicCodeRequiredMessage =
        "The IL-JIT tier requires a runtime JIT. This process is NativeAOT or otherwise "
      + "dynamic-code-disabled; use the interpreter directly. See docs/user-guide/jit.md.";

    private readonly TCpu _inner;
    private readonly IJitTarget _target;
    private readonly AddressSpace _bus;
    private readonly IAddressSpace _calloutBus;
    private readonly IAddressSpace _ioBus;          // the Port-op callout bus (the Z80's Io space; the
                                                    // 6502 passes a harmless placeholder — no port op)
    private readonly Fastmem _fastmem;
    private readonly BlockCache<TCpu> _cache;
    private readonly BlockCompiler<TCpu> _compiler;
    private readonly JitOptions _opts;
    private readonly string _pcName;   // the ProgramCounter-role register name (the dispatcher reads
                                       // the live PC via _inner.GetRegister(_pcName) — interface-only,
                                       // no concrete CPU field; resolved once at construction)
    private long _chainStepCount;   // test seam: chain edges taken without a dispatcher round-trip

    // The chain-edge callback + its mutable scratch, allocated ONCE per JittedCpu (not per chain
    // step). RunChain writes _chainPredecessor before each emitted block runs; the emitted chain
    // edge calls _chainDispatch, which stashes the resolved successor in _chainNext. Hoisting these
    // out of the RunChain loop avoids a per-chain-step delegate + display-class allocation (millions
    // of short-lived GC objects on a tight Klaus/loop run would partially undercut the speedup).
    private CompiledBlock<TCpu>? _chainPredecessor;
    private CompiledBlock<TCpu>? _chainNext;
    private ChainDispatch? _chainDispatch;

    /// <summary>Construct a Tier-1 JIT over an interpreter and its concrete bus.</summary>
    /// <param name="inner">The wrapped interpreter — the oracle, fallback, and state owner.</param>
    /// <param name="bus">The concrete <see cref="AddressSpace"/> fastmem binds to (page table +
    /// backing arrays + writability). Also the bus MMIO callouts route through by default.</param>
    /// <param name="options">Construction options (DisableFastmem, BlockLengthCap).</param>
    /// <param name="traceBus">Optional: when <c>DisableFastmem</c> is set, route every emitted bus
    /// callout through this <see cref="IAddressSpace"/> instead of <paramref name="bus"/> — the
    /// trace-equivalence seam (Ground truth E / Task 6 Step 3). A <c>TracingAddressSpace</c> wrapping
    /// <paramref name="bus"/> then records an identical access trace to the interpreter's. Ignored
    /// when fastmem is on (RAM/ROM go direct to the backing array, bypassing any bus). Production
    /// code never sets this; it is the trace spot tests' wiring.</param>
    public JittedCpu(TCpu inner, IJitTarget target, AddressSpace bus, IAddressSpace? ioBus = null,
        JitOptions? options = null, IAddressSpace? traceBus = null)
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            throw new System.PlatformNotSupportedException(DynamicCodeRequiredMessage);
        System.ArgumentNullException.ThrowIfNull(inner);
        System.ArgumentNullException.ThrowIfNull(target);
        System.ArgumentNullException.ThrowIfNull(bus);
        _inner = inner;
        _target = target;
        _bus = bus;
        var opts = options ?? new JitOptions();
        _opts = opts;
        // The bus the emitted callouts use: the trace bus when supplied with DisableFastmem
        // (so a TracingAddressSpace sees every access), else the concrete AddressSpace.
        _calloutBus = (opts.DisableFastmem && traceBus is not null) ? traceBus : bus;
        // The Port-op callout target (the Z80's Io space). A CPU with no port op (the 6502) never
        // references arg 7, so passing the memory bus is a harmless placeholder.
        _ioBus = ioBus ?? bus;
        _fastmem = new Fastmem(bus, opts);
        _cache = new BlockCache<TCpu>(bus.PageCount);
        _compiler = new BlockCompiler<TCpu>(_inner, target, _bus, _fastmem, opts);
        _pcName = ((IMonitorSupport)_inner).ProgramCounterName;
    }

    /// <summary>Test seam: how many blocks have been compiled (the cache-hit pin reads this).</summary>
    internal int CompileCount => _compiler.CompileCount;

    /// <summary>Test seam: how many chain edges have been taken without a dispatcher round-trip
    /// (the chaining pins read this; 0 with <see cref="JitOptions.DisableChaining"/> or M2-i).</summary>
    internal long ChainStepCount => _chainStepCount;

    public string Architecture => _inner.Architecture;
    public long CycleCount => _inner.CycleCount;
    public void Reset() => _inner.Reset();

    /// <summary>One instruction — ALWAYS the interpreter (recorded: Step is the monitor +
    /// harness primitive; per-instruction fidelity is the interpreter's job).</summary>
    public void Step() => _inner.Step();

    /// <summary>Run a cycle budget through cached, compiled blocks. The budget decrement equals
    /// the CycleCount delta; overshoot is bounded by one instruction (the block exits at the
    /// boundary where budget &lt;= 0). On a pending interrupt at a block boundary, the inner
    /// interpreter services it (the 7-cycle sequence, one place).</summary>
    public void Run(ref long cycleBudget)
    {
        while (cycleBudget > 0)
        {
            if (_inner.InterruptPending)         // block-entry interrupt check (dispatcher side)
            {
                long before = _inner.CycleCount;
                _inner.Step();                   // services it authentically
                cycleBudget -= _inner.CycleCount - before;
                continue;
            }
            if (_inner.Halted)                   // M3.2 (Ground truth B.3): halted fast path
            {
                // A halted CPU does no memory access, sets no flags, follows no chain — there is
                // nothing to emit. Delegate the idle cycle to _inner.Step (the interpreter's halted
                // guard, B.1) — keeping the halted path in ONE place and NOT busy-compiling a
                // degenerate block. Re-checks InterruptPending next iteration (the wake). For the
                // 6502 _inner.Halted is always false, so this branch is dead (byte-identical JIT).
                long before = _inner.CycleCount;
                _inner.Step();                   // one idle cycle
                cycleBudget -= _inner.CycleCount - before;
                continue;
            }
            _cache.InvalidateIfDirty();          // SMC: discard cache if a code page was written
            CompiledBlock<TCpu> block = _cache.GetOrCompile((ushort)_inner.GetRegister(_pcName), _compiler);
            RunChain(block, ref cycleBudget);    // run the block + follow its static chain edges
            // Normal/Budget/Recompile all return here for a dispatcher round-trip: the loop tops
            // back to InvalidateIfDirty (flushing a self-modified block on a Recompile/dirty exit)
            // + GetOrCompile re-decodes at the (already-set) PC. cycleBudget drives the while.
        }
    }

    /// <summary>Run a block and follow its statically-known chain edges WITHOUT a dispatcher
    /// round-trip, stack-safely (a LOOP, not emitted recursion — Ground truth A step 7). The
    /// emitted block's chain call stashes the resolved successor in 'next' and returns; the emitted
    /// block 'ret's; this loop runs the successor in the SAME frame, so host stack depth across an
    /// arbitrarily long chain is bounded. Returns when the chain breaks (no link / a dynamic exit /
    /// a chain-break gate) or must round-trip (Budget / Recompile).</summary>
    private void RunChain(CompiledBlock<TCpu> block, ref long budget)
    {
        // The chain callback is allocated once (lazily) and reused; it reads _chainPredecessor and
        // writes _chainNext, so no per-step allocation happens in this hot loop.
        ChainDispatch chain = _chainDispatch ??= ChainEdge;
        CompiledBlock<TCpu> current = block;
        while (true)
        {
            _chainPredecessor = current;            // the inbound-link record (read by ChainEdge)
            _chainNext = null;
            // The Port emit arm (5-3b) routes its callout to the SECOND IAddressSpace (arg 7 — the
            // Z80's Io space). In 5-3a every Z80 op falls back to inner.Step (which writes the ports
            // directly), but the dispatcher passes the real _ioBus so the arm 5-3b adds lands on it.
            // The 6502 has no port op and never references arg 7 (6502 blocks are byte-identical).
            current.Run(_inner, _calloutBus, _fastmem, _cache.Dirty, chain, ref budget, out BlockExit exit,
                _ioBus);
            if (exit is BlockExit.Budget or BlockExit.Recompile) return; // round-trip required
            if (_chainNext is null) return;         // chain broke (gates/flag) or a dynamic exit
            current = _chainNext;                   // continue the chain in THIS frame
            _chainStepCount++;                      // test seam (ChainStepCount pin)
        }
    }

    /// <summary>The single chain-edge callback (bound once, reused every step — see RunChain). The
    /// emitted block calls this at a statically-known exit after clearing the chain-break gates;
    /// it links the predecessor and resolves the successor BY PC (compiling on first reach), unless
    /// chaining is disabled, in which case it leaves _chainNext null so RunChain rounds back to the
    /// dispatcher. exit = Normal: a chain edge is a clean block end (the gates already passed).</summary>
    private void ChainEdge(ushort targetPc, ref long budget, out BlockExit exit)
    {
        exit = BlockExit.Normal;
        if (_opts.DisableChaining) return;          // flag -> no chaining; round-trip
        _chainNext = _cache.ResolveChain(targetPc, _chainPredecessor!, _compiler); // link + resolve
    }

    // ICpuCore introspection + IMonitorSupport: ALL delegate to the inner interpreter, so the
    // monitor's disassembler/assembler/InterruptPending/register views are byte-identical.
    public void SetIrqLine(bool a) => _inner.SetIrqLine(a);
    public void SetNmiLine(bool a) => _inner.SetNmiLine(a);
    public System.Collections.Generic.IReadOnlyList<string> RegisterNames => _inner.RegisterNames;
    public ulong GetRegister(string n) => _inner.GetRegister(n);
    public void SetRegister(string n, ulong v) => _inner.SetRegister(n, v);
    string IMonitorSupport.Disassemble(byte o, byte lo, byte hi)
        => ((IMonitorSupport)_inner).Disassemble(o, lo, hi);
    int IMonitorSupport.InstructionLength(byte o)
        => ((IMonitorSupport)_inner).InstructionLength(o);
    bool IMonitorSupport.TryAssemble(string m, string t, out byte[] b, out string? e)
        => ((IMonitorSupport)_inner).TryAssemble(m, t, out b, out e);
    public bool InterruptPending => ((IMonitorSupport)_inner).InterruptPending;
    public bool Halted => ((IMonitorSupport)_inner).Halted;
    public string ProgramCounterName => ((IMonitorSupport)_inner).ProgramCounterName;
    public int RegisterBits(string n) => ((IMonitorSupport)_inner).RegisterBits(n);
}
