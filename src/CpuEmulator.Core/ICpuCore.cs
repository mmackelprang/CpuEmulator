namespace CpuEmulator.Core;

/// <summary>
/// A CPU core. Implementations are generated from ISA specs; both execution tiers
/// (interpreter, IL-JIT) sit behind this one interface.
/// </summary>
public interface ICpuCore
{
    /// <summary>Architecture identifier, e.g. "mos6502".</summary>
    string Architecture { get; }

    /// <summary>Total cycles executed since construction. Monotonic.</summary>
    long CycleCount { get; }

    void Reset();

    /// <summary>Execute exactly one instruction.</summary>
    void Step();

    /// <summary>
    /// Run instructions until <paramref name="cycleBudget"/> is exhausted, decrementing it
    /// by cycles actually executed. May overshoot by at most one instruction (budget may
    /// go slightly negative). The decrement always equals the increase in <see cref="CycleCount"/>.
    /// </summary>
    void Run(ref long cycleBudget);

    void SetIrqLine(bool asserted);
    void SetNmiLine(bool asserted);

    /// <summary>Register names for generic state introspection (test harness, debugger).</summary>
    IReadOnlyList<string> RegisterNames { get; }

    /// <summary>Get a register's current value, zero-extended to 64 bits.</summary>
    /// <exception cref="ArgumentException">The name is not in <see cref="RegisterNames"/>.</exception>
    ulong GetRegister(string name);

    /// <summary>Set a register. Values are truncated to the register's natural width.</summary>
    /// <exception cref="ArgumentException">The name is not in <see cref="RegisterNames"/>.</exception>
    void SetRegister(string name, ulong value);
}
