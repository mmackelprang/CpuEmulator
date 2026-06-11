namespace CpuEmulator.Core;

/// <summary>
/// Base type for host-world failures (configuration errors, codegen bugs, framework misuse).
/// Guest-world events (undefined opcodes, open-bus reads) are emulated behavior and never
/// throw unless an opt-in strict policy is enabled.
/// </summary>
public class EmulationException : Exception
{
    public EmulationException(string message) : base(message) { }
    public EmulationException(string message, Exception inner) : base(message, inner) { }
}
