namespace CpuEmulator.Core;

/// <summary>Bad machine wiring or spec misuse, detected at build/realize time — never mid-run.</summary>
public sealed class MachineConfigurationException : EmulationException
{
    public MachineConfigurationException(string message) : base(message) { }
    public MachineConfigurationException(string message, Exception inner) : base(message, inner) { }
}
