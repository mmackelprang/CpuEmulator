using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;

namespace CpuEmulator.Jit;

/// <summary>Tier-1 IL-JIT CPU: wraps an interpreter Mos6502Cpu (the oracle + fallback +
/// state owner) and runs cached, compiled blocks for Run. Step always delegates to the inner
/// interpreter (per-instruction fidelity for the monitor + harness). Implements the same
/// ICpuCore + IMonitorSupport surface, so a Machine, a MonitorEngine, and the TomHarte runner
/// drive it identically to the interpreter.</summary>
public sealed class JittedCpu : ICpuCore, IMonitorSupport
{
    /// <summary>The gate message — extracted to a const so the gate test references it directly
    /// (rather than re-typing the string). Names the interpreter fallback and the doc.</summary>
    internal const string DynamicCodeRequiredMessage =
        "The IL-JIT tier requires a runtime JIT. This process is NativeAOT or otherwise "
      + "dynamic-code-disabled; use the interpreter (Mos6502Cpu) directly. See docs/user-guide/jit.md.";

    private readonly Mos6502Cpu _inner;
    private readonly AddressSpace _bus;
    private readonly IAddressSpace _calloutBus;
    private readonly Fastmem _fastmem;
    private readonly BlockCache _cache;
    private readonly BlockCompiler _compiler;
    private readonly JitOptions _opts;
    private long _chainStepCount;   // test seam: chain edges taken without a dispatcher round-trip

    // The chain-edge callback + its mutable scratch, allocated ONCE per JittedCpu (not per chain
    // step). RunChain writes _chainPredecessor before each emitted block runs; the emitted chain
    // edge calls _chainDispatch, which stashes the resolved successor in _chainNext. Hoisting these
    // out of the RunChain loop avoids a per-chain-step delegate + display-class allocation (millions
    // of short-lived GC objects on a tight Klaus/loop run would partially undercut the speedup).
    private CompiledBlock? _chainPredecessor;
    private CompiledBlock? _chainNext;
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
    public JittedCpu(Mos6502Cpu inner, AddressSpace bus, JitOptions? options = null,
        IAddressSpace? traceBus = null)
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            throw new System.PlatformNotSupportedException(DynamicCodeRequiredMessage);
        System.ArgumentNullException.ThrowIfNull(inner);
        System.ArgumentNullException.ThrowIfNull(bus);
        _inner = inner;
        _bus = bus;
        var opts = options ?? new JitOptions();
        _opts = opts;
        // The bus the emitted callouts use: the trace bus when supplied with DisableFastmem
        // (so a TracingAddressSpace sees every access), else the concrete AddressSpace.
        _calloutBus = (opts.DisableFastmem && traceBus is not null) ? traceBus : bus;
        _fastmem = new Fastmem(bus, opts);
        _cache = new BlockCache(bus.PageCount);
        _compiler = new BlockCompiler(_inner, _bus, _fastmem, opts);
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
            _cache.InvalidateIfDirty();          // SMC: discard cache if a code page was written
            CompiledBlock block = _cache.GetOrCompile((ushort)_inner.PC, _compiler);
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
    private void RunChain(CompiledBlock block, ref long budget)
    {
        // The chain callback is allocated once (lazily) and reused; it reads _chainPredecessor and
        // writes _chainNext, so no per-step allocation happens in this hot loop.
        ChainDispatch chain = _chainDispatch ??= ChainEdge;
        CompiledBlock current = block;
        while (true)
        {
            _chainPredecessor = current;            // the inbound-link record (read by ChainEdge)
            _chainNext = null;
            current.Run(_inner, _calloutBus, _fastmem, _cache.Dirty, chain, ref budget, out BlockExit exit);
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
    public string ProgramCounterName => ((IMonitorSupport)_inner).ProgramCounterName;
    public int RegisterBits(string n) => ((IMonitorSupport)_inner).RegisterBits(n);
}
