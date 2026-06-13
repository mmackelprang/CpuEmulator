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
    private readonly Fastmem _fastmem;
    private readonly BlockCache _cache;
    private readonly BlockCompiler _compiler;

    public JittedCpu(Mos6502Cpu inner, AddressSpace bus, JitOptions? options = null)
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            throw new System.PlatformNotSupportedException(DynamicCodeRequiredMessage);
        System.ArgumentNullException.ThrowIfNull(inner);
        System.ArgumentNullException.ThrowIfNull(bus);
        _inner = inner;
        _bus = bus;
        var opts = options ?? new JitOptions();
        _fastmem = new Fastmem(bus, opts);
        _cache = new BlockCache(bus.PageCount);
        _compiler = new BlockCompiler(_inner, _bus, _fastmem, opts);
    }

    /// <summary>Test seam: how many blocks have been compiled (the cache-hit pin reads this).</summary>
    internal int CompileCount => _compiler.CompileCount;

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
            block.Run(_inner, _bus, _fastmem, _cache.Dirty, ref cycleBudget, out _);
            // Normal/Budget: loop or fall out (cycleBudget drives the while). Irq cannot occur
            // mid-block in M2-i (checked only at entry); the enum carries it for M2-ii chaining.
        }
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
