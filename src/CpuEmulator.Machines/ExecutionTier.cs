namespace CpuEmulator.Machines;

/// <summary>Build-time tier selection for BoardMachineFactory. The SAME BoardSpec runs on either
/// tier; the tier is a factory parameter, not part of the board's declarative description.</summary>
public enum ExecutionTier
{
    /// <summary>Tier-0: the bare interpreter core. AOT-clean.</summary>
    Interpreter,

    /// <summary>Tier-1: the interpreter wrapped in JittedCpu&lt;TCpu&gt; (Reflection.Emit; non-AOT).</summary>
    Jit,
}
