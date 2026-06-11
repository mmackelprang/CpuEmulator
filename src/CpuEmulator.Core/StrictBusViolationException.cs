namespace CpuEmulator.Core;

/// <summary>Thrown only when AddressSpaceOptions.Strict is enabled — the
/// opt-in firmware-debugging posture for unmapped or read-only accesses.</summary>
public sealed class StrictBusViolationException : EmulationException
{
    public StrictBusViolationException(string message) : base(message) { }
}
