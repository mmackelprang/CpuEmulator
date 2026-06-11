namespace CpuEmulator.Core;

/// <summary>Per-space bus behavior policy (spec §7).</summary>
public sealed class AddressSpaceOptions
{
    /// <summary>Value returned by reads from unmapped addresses.</summary>
    public byte OpenBusValue { get; init; } = 0xFF;

    /// <summary>When true, unmapped reads/writes and ROM writes throw
    /// <see cref="StrictBusViolationException"/> instead of using open-bus semantics.</summary>
    public bool Strict { get; init; }
}
