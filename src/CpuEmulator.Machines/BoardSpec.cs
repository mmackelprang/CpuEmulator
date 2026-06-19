namespace CpuEmulator.Machines;

/// <summary>A declarative description of an emulated computer: which CPU, the address width, the
/// memory map (RAM/ROM/MMIO), the peripheral slots, the IRQ wiring, and the reset inputs. Data, not
/// code (mirroring the CPU-spec philosophy): BoardSpecValidator checks it; BoardMachineFactory
/// instantiates it into the existing runnable Machine. AddressBits is the CPU's bus width (16 for
/// the 6502/Z80; 8-24 per AddressSpace).</summary>
public sealed record BoardSpec(
    string Name,
    CpuKind Cpu,
    int AddressBits,
    IReadOnlyList<MemoryRegion> Memory,
    IReadOnlyList<PeripheralSlot> Peripherals,
    IrqWiring Irq,
    ResetConfig Reset);
