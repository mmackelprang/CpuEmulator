namespace CpuEmulator.Core;

/// <summary>
/// A device mapped into an address space over an address range. Lifecycle is two-phase:
/// constructor = configure; <see cref="Realize"/> = wire to the machine (claim IRQ lines,
/// schedule events). The Machine maps the device onto the bus before calling Realize.
/// </summary>
public interface IPeripheral
{
    string Name { get; }

    /// <summary>Called exactly once by the Machine, after all bus mappings exist.</summary>
    void Realize(IMachineContext context);

    /// <summary>Read from the device. <paramref name="offset"/> is relative to the mapping base.</summary>
    uint Read(uint offset, AccessWidth width);

    void Write(uint offset, AccessWidth width, uint value);

    /// <summary>
    /// Side-effect-free read for monitors and debuggers. Default: no honest peek — false
    /// with value 0; the caller decides whether to fall back to the (potentially
    /// perturbing) <see cref="Read"/>. Implementations MUST NOT change any device state
    /// here: no queue dequeues, no flag clears, no IRQ level changes.
    /// </summary>
    bool TryPeek(uint offset, out byte value)
    {
        value = 0;
        return false;
    }
}
