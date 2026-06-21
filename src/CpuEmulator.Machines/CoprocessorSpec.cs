using CpuEmulator.Core;

namespace CpuEmulator.Machines;

/// <summary>An optional second CPU that shares the primary's program RAM under run-one-then-the-other bus
/// arbitration (the Z80 SoftCard; ADR 0015 Decision 2). The coprocessor sees the shared bus THROUGH
/// Translation; it is dormant at reset and activated by a soft-switch write the ControlPortPeripheral
/// observes (it flips the Machine's active CPU via ICoprocessorControl). Single-CPU boards leave
/// BoardSpec.Coprocessor null.</summary>
/// <param name="Cpu">The coprocessor core kind (CpuKind.Z80 for the SoftCard).</param>
/// <param name="Translation">Logical (coprocessor) -> physical (primary) address translation (PR-J ships
/// the concrete SoftCardTranslation 6-branch table).</param>
/// <param name="ControlPortPeripheral">The PeripheralSlot.Name whose access toggles the active CPU.</param>
/// <param name="ClockRatioToPrimary">The coprocessor:primary clock ratio (~2.0 for the SoftCard Z80);
/// converts coprocessor run time into primary-domain scheduler cycles (ADR 0015 Decision 5).</param>
public sealed record CoprocessorSpec(
    CpuKind Cpu,
    IAddressTranslation Translation,
    string ControlPortPeripheral,
    double ClockRatioToPrimary);
