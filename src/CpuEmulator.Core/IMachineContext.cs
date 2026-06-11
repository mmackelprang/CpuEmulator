namespace CpuEmulator.Core;

/// <summary>What a peripheral (or CPU factory) may see of the machine during construction.</summary>
public interface IMachineContext
{
    IScheduler Scheduler { get; }
    IAddressSpace Space(AddressSpaceKind kind);
    IInterruptLine IrqLine { get; }
    IInterruptLine NmiLine { get; }
}
