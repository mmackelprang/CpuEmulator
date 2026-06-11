namespace CpuEmulator.Core;

/// <summary>
/// Identifies one of the up-to-three buses a CPU can own (MAME-style).
/// Von Neumann parts use only <see cref="Program"/>; Harvard parts (8051) add
/// <see cref="Data"/>; port-I/O parts (Z80, 8086) add <see cref="Io"/>.
/// </summary>
public enum AddressSpaceKind
{
    Program,
    Data,
    Io,
}
