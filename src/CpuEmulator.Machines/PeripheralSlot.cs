using CpuEmulator.Core;

namespace CpuEmulator.Machines;

/// <summary>A device attached at [Base, Base+Length) of the program space. Base must be
/// page-aligned and Length a positive multiple of 256; the slot must land in/over an Mmio region.
/// Name is the wiring key the IrqWiring references.</summary>
public sealed record PeripheralSlot(string Name, IPeripheral Device, uint Base, uint Length);
