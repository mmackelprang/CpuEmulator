namespace CpuEmulator.Machines;

/// <summary>Which CPU interrupt input a peripheral's outgoing line drives.</summary>
public enum CpuInterrupt
{
    /// <summary>The maskable interrupt (6502 IRQ, Z80 INT). Machine.IrqLine.</summary>
    Irq,

    /// <summary>The non-maskable interrupt. Machine.NmiLine.</summary>
    Nmi,
}

/// <summary>One device-line -> CPU-interrupt mapping. PeripheralName must match a PeripheralSlot.Name.</summary>
public sealed record PeripheralIrq(string PeripheralName, CpuInterrupt Target);

/// <summary>Which device IRQ lines drive which CPU interrupts. Devices already claim their wired-OR
/// handle via context.IrqLine.Source()/NmiLine.Source() in Realize; this wiring declares, for the
/// validator, that each named line maps to a real peripheral and a real CPU input.</summary>
public sealed record IrqWiring(IReadOnlyList<PeripheralIrq> Lines)
{
    /// <summary>An empty wiring (no peripheral drives an interrupt).</summary>
    public static IrqWiring None { get; } = new([]);
}
