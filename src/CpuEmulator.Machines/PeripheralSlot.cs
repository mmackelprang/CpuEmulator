using CpuEmulator.Core;

namespace CpuEmulator.Machines;

/// <summary>A device attached at [Base, Base+Length) of a CPU bus. Space selects which bus: Program
/// (default — the memory map, must land in/over an Mmio region) or Io (the I/O port space, must land
/// in/over an IoMmio region). Base must be page-aligned and Length a positive multiple of 256. Name is
/// the wiring key the IrqWiring references.</summary>
public sealed record PeripheralSlot(
    string Name, IPeripheral Device, uint Base, uint Length,
    PeripheralSpace Space = PeripheralSpace.Program);
